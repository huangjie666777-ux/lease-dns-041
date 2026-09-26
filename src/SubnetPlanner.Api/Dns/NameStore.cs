using System.Text.RegularExpressions;

namespace SubnetPlanner.Api.Dns;

public sealed record NameEntry(string Mac, string Hostname);

public sealed record NameError(string Field, string Message);

public sealed record NameSnapshot(
    IReadOnlyDictionary<string, string> HostnameByMac,
    IReadOnlyDictionary<string, string> MacByHostnameLower);

public sealed partial class NameStore
{
    private readonly object _gate = new();
    private readonly List<NameEntry> _entries = new();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex HostnameRegex();

    public static bool IsValidMac(string? mac) =>
        mac is not null && mac.Length == 12 && mac.All(Uri.IsHexDigit);

    public static bool IsValidHostname(string? hostname) =>
        hostname is not null && hostname.Length >= 1 && hostname.Length <= 63
        && HostnameRegex().IsMatch(hostname);

    public static string NormalizeMac(string mac) => mac.ToUpperInvariant();
    public static string HostnameKey(string hostname) => hostname.ToLowerInvariant();

    public IReadOnlyList<NameEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    public NameEntry? FindByMac(string mac)
    {
        var key = NormalizeMac(mac);
        lock (_gate)
        {
            foreach (var entry in _entries)
                if (entry.Mac == key) return entry;
            return null;
        }
    }

    public NameSnapshot Snapshot()
    {
        lock (_gate)
        {
            var byMac = new Dictionary<string, string>(_entries.Count);
            var byHost = new Dictionary<string, string>(_entries.Count);
            foreach (var entry in _entries)
            {
                byMac[entry.Mac] = entry.Hostname;
                byHost[HostnameKey(entry.Hostname)] = entry.Mac;
            }
            return new NameSnapshot(byMac, byHost);
        }
    }

    public static bool TryValidate(
        IEnumerable<NameEntryInput> inputs, out List<NameEntry> entries, out List<NameError> errors)
    {
        entries = new List<NameEntry>();
        errors = new List<NameError>();
        var seenMac = new HashSet<string>();
        var seenHost = new HashSet<string>();
        var index = 0;
        foreach (var input in inputs)
        {
            var mac = input.Mac ?? string.Empty;
            var hostname = input.Hostname ?? string.Empty;
            if (!IsValidMac(mac))
                errors.Add(new NameError($"entries[{index}].mac",
                    "MAC必须为12位十六进制字符"));
            else if (!seenMac.Add(NormalizeMac(mac)))
                errors.Add(new NameError($"entries[{index}].mac",
                    $"MAC {NormalizeMac(mac)} 重复"));

            if (!IsValidHostname(hostname))
                errors.Add(new NameError($"entries[{index}].hostname",
                    "主机名长度须为1至63位ASCII字母、数字或连字符，且首尾为字母或数字"));
            else if (!seenHost.Add(HostnameKey(hostname)))
                errors.Add(new NameError($"entries[{index}].hostname",
                    $"主机名 {hostname} 重复（忽略大小写）"));

            if (IsValidMac(mac) && IsValidHostname(hostname))
                entries.Add(new NameEntry(NormalizeMac(mac), hostname));
            index++;
        }
        return errors.Count == 0;
    }

    public void Replace(IReadOnlyList<NameEntry> next)
    {
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(next);
        }
    }
}

public sealed record NameEntryInput(string? Mac, string? Hostname);
