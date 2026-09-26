using System.Text.RegularExpressions;

namespace SubnetPlanner.Api.Dns;

public sealed record NameEntry(string Mac, string Hostname);

public sealed class NameValidationException(IReadOnlyList<string> Errors) : Exception
{
    public IReadOnlyList<string> Errors { get; } = Errors;
}

/// <summary>
/// MAC -> 主机名配置。MAC为12位十六进制（忽略大小写，内部大写存储），
/// 主机名为1~63位ASCII字母/数字/连字符且首尾为字母或数字（忽略大小写）。
/// 改名仅替换名称表，不触碰租约；整表校验通过后原子替换，失败保留旧表。
/// </summary>
public sealed partial class NameMap
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _macToName = new();

    public object SyncRoot => _gate;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex HostnamePattern();

    /// <summary>校验并原子替换整表；任一条目不合法或存在重复MAC/主机名时保留旧表。</summary>
    public void ReplaceAll(IEnumerable<NameEntry> entries)
    {
        var parsed = Validate(entries);
        lock (_gate)
        {
            _macToName.Clear();
            foreach (var entry in parsed)
                _macToName[entry.Mac] = entry.Hostname;
        }
    }

    public static List<NameEntry> Validate(IEnumerable<NameEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var errors = new List<string>();
        var parsed = new List<NameEntry>();
        var seenMac = new HashSet<string>();
        var seenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var entry in entries)
        {
            var field = $"entries[{index}]";
            var mac = entry?.Mac?.Trim() ?? string.Empty;
            var hostname = entry?.Hostname?.Trim() ?? string.Empty;
            var macOk = mac.Length == 12 && mac.All(c => char.IsAsciiHexDigit(c));
            var nameOk = hostname.Length >= 1 && hostname.Length <= 63 && HostnamePattern().IsMatch(hostname);

            if (!macOk)
                errors.Add($"{field}.mac: MAC必须为12位十六进制字符");
            if (!nameOk)
                errors.Add($"{field}.hostname: 主机名须为1~63位ASCII字母、数字或连字符且首尾为字母或数字");

            if (macOk)
            {
                var macKey = mac.ToUpperInvariant();
                if (!seenMac.Add(macKey))
                    errors.Add($"{field}.mac: MAC {macKey} 重复");
                if (nameOk)
                {
                    if (!seenName.Add(hostname))
                        errors.Add($"{field}.hostname: 主机名 {hostname} 重复");
                    parsed.Add(new NameEntry(macKey, hostname.ToLowerInvariant()));
                }
            }
            index++;
        }
        if (errors.Count > 0)
            throw new NameValidationException(errors);
        return parsed;
    }

    /// <summary>查询某MAC的主机名（MAC忽略大小写），未配置返回null。</summary>
    public string? TryGetHostname(string mac)
    {
        var key = NormalizeMac(mac);
        if (key is null) return null;
        lock (_gate)
            return _macToName.TryGetValue(key, out var name) ? name : null;
    }

    public IReadOnlyList<NameEntry> Snapshot()
    {
        lock (_gate)
            return _macToName.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new NameEntry(kv.Key, kv.Value)).ToList();
    }

    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var trimmed = mac.Trim();
        return trimmed.Length == 12 && trimmed.All(char.IsAsciiHexDigit)
            ? trimmed.ToUpperInvariant() : null;
    }
}
