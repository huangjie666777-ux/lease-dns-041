using System.Text;

namespace SubnetPlanner.Api.Dns;

public static class DnsConstants
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort ClassIn = 1;

    public const byte RcodeNoError = 0;
    public const byte RcodeNameError = 3; // NXDOMAIN
    public const byte RcodeNotImpl = 4;

    public const int MaxLabelLength = 63;
    public const int MaxNameLength = 255;
    public const int HeaderLength = 12;
}

public enum DnsQuestionKind { A, Ptr, Other }

public sealed record DnsQuestion(
    ushort Id,
    bool IsStandardQuery,
    bool TruncationRequested,
    IReadOnlyList<string> Labels,
    DnsQuestionKind Kind,
    ushort Type,
    ushort Class,
    int QuestionEndOffset,
    byte[] RawQuestion);

public enum ParseFailure { Refuse, NotImpl }

public readonly record struct ParseResult(bool Ok, ParseFailure Failure, DnsQuestion? Question)
{
    public static ParseResult Refuse() => new(false, ParseFailure.Refuse, null);
    public static ParseResult NotImpl() => new(false, ParseFailure.NotImpl, null);
    public static ParseResult Success(DnsQuestion q) => new(true, default, q);
}

public static class DnsPacket
{
    public static ParseResult Parse(byte[] data)
    {
        if (data.Length < DnsConstants.HeaderLength)
            return ParseResult.Refuse();

        var id = (ushort)((data[0] << 8) | data[1]);
        var flags2 = data[2];
        var flags3 = data[3];
        var qr = (flags2 & 0x80) != 0;
        var opcode = (flags2 >> 3) & 0x0F;
        var tc = (flags2 & 0x02) != 0;
        var qdCount = (ushort)((data[4] << 8) | data[5]);
        var anCount = (ushort)((data[6] << 8) | data[7]);
        var nsCount = (ushort)((data[8] << 8) | data[9]);
        var arCount = (ushort)((data[10] << 8) | data[11]);

        if (qr || opcode != 0 || tc)
            return ParseResult.NotImpl();
        if (qdCount != 1)
            return ParseResult.NotImpl();
        if (anCount != 0 || nsCount != 0 || arCount != 0)
            return ParseResult.NotImpl();

        var qStart = DnsConstants.HeaderLength;
        var parse = ReadName(data, qStart);
        if (!parse.Ok)
            return ParseResult.Refuse();
        var afterName = parse.NextOffset;
        if (afterName + 4 > data.Length)
            return ParseResult.Refuse();
        var qType = (ushort)((data[afterName] << 8) | data[afterName + 1]);
        var qClass = (ushort)((data[afterName + 2] << 8) | data[afterName + 3]);
        var qEnd = afterName + 4;

        if (qClass != DnsConstants.ClassIn)
            return ParseResult.NotImpl();

        DnsQuestionKind kind;
        if (qType == DnsConstants.TypeA) kind = DnsQuestionKind.A;
        else if (qType == DnsConstants.TypePtr) kind = DnsQuestionKind.Ptr;
        else kind = DnsQuestionKind.Other;

        var rawQuestion = new byte[qEnd - qStart];
        Array.Copy(data, qStart, rawQuestion, 0, rawQuestion.Length);

        return ParseResult.Success(new DnsQuestion(
            id, true, false, parse.Labels!, kind, qType, qClass, qEnd, rawQuestion));
    }

    private readonly record struct NameRead(bool Ok, IReadOnlyList<string>? Labels, int NextOffset);

    private static NameRead ReadName(byte[] data, int start)
    {
        var labels = new List<string>();
        var totalEncoded = 0;
        var totalExpanded = 0;
        var offset = start;
        var jumped = false;
        var nextAfterName = 0;
        var visited = new HashSet<int>();

        while (true)
        {
            if (offset >= data.Length)
                return new NameRead(false, null, 0);
            if (!visited.Add(offset))
                return new NameRead(false, null, 0); // 指针循环
            var length = data[offset];
            if (length == 0)
            {
                offset++;
                if (!jumped) nextAfterName = offset;
                return new NameRead(true, labels, jumped ? nextAfterName : offset);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= data.Length)
                    return new NameRead(false, null, 0);
                var pointer = ((length & 0x3F) << 8) | data[offset + 1];
                if (pointer >= offset || pointer >= data.Length)
                    return new NameRead(false, null, 0); // 不允许前向/越界指针
                if (!jumped) nextAfterName = offset + 2;
                offset = pointer;
                jumped = true;
                totalEncoded += 2;
                if (totalEncoded > DnsConstants.MaxNameLength)
                    return new NameRead(false, null, 0);
                continue;
            }
            if ((length & 0xC0) != 0)
                return new NameRead(false, null, 0); // 保留标签类型
            if (length > DnsConstants.MaxLabelLength)
                return new NameRead(false, null, 0);
            var labelStart = offset + 1;
            if (labelStart + length > data.Length)
                return new NameRead(false, null, 0);
            var label = Encoding.ASCII.GetString(data, labelStart, length);
            labels.Add(label);
            totalExpanded += length + 1;
            if (totalExpanded > DnsConstants.MaxNameLength)
                return new NameRead(false, null, 0);
            offset = labelStart + length;
        }
    }

    public static byte[] BuildResponse(DnsQuestion question, byte rcode,
        IReadOnlyList<(string Name, ushort Type, ushort Class, uint Ttl, byte[] Rdata)>? answers = null)
    {
        var answerList = answers ?? Array.Empty<(string, ushort, ushort, uint, byte[])>();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(ByteOrderHigh(question.Id));
        writer.Write(ByteOrderHigh((ushort)(0x8480 | rcode))); // QR=1 AA=1 RD=0 RA=1
        writer.Write(ByteOrderHigh((ushort)1));
        writer.Write(ByteOrderHigh((ushort)answerList.Count));
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        var questionStart = (int)ms.Position;
        writer.Write(question.RawQuestion);
        var questionNameEnd = questionStart + question.QuestionEndOffset
            - DnsConstants.HeaderLength - 4;

        foreach (var (name, type, klass, ttl, rdata) in answerList)
        {
            if (LabelsEqual(name, question.Labels) && question.Labels.Count > 0)
            {
                var pointer = (ushort)(0xC000 | questionStart);
                writer.Write(ByteOrderHigh(pointer));
            }
            else
            {
                WriteName(writer, name);
            }
            writer.Write(ByteOrderHigh(type));
            writer.Write(ByteOrderHigh(klass));
            writer.Write(ByteOrderHigh(ttl));
            writer.Write(ByteOrderHigh((ushort)rdata.Length));
            writer.Write(rdata);
        }
        return ms.ToArray();
    }

    public static bool LabelsEqual(string name, IReadOnlyList<string> labels)
    {
        var parts = name.TrimEnd('.').Split('.');
        if (parts.Length != labels.Count) return false;
        for (var i = 0; i < parts.Length; i++)
            if (!string.Equals(parts[i], labels[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    public static void WriteName(BinaryWriter writer, string name)
    {
        var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length == 0 || bytes.Length > DnsConstants.MaxLabelLength)
                throw new ArgumentException("非法标签", nameof(name));
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        writer.Write((byte)0);
    }

    public static byte[] ARecordRdata(uint address) => new[]
    {
        (byte)(address >> 24), (byte)(address >> 16),
        (byte)(address >> 8), (byte)address
    };

    public static byte[] PtrRdata(string name)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        WriteName(writer, name);
        return ms.ToArray();
    }

    private static byte[] ByteOrderHigh(ushort value) => new[]
    {
        (byte)(value >> 8), (byte)value
    };

    private static byte[] ByteOrderHigh(uint value) => new[]
    {
        (byte)(value >> 24), (byte)(value >> 16),
        (byte)(value >> 8), (byte)value
    };
}
