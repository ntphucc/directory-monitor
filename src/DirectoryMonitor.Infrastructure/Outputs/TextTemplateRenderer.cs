using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Infrastructure.Outputs;

public static partial class TextTemplateRenderer
{
    private static readonly HashSet<string> SupportedTokens = ["EventType", "EventTime", "EventTimeUtc", "EntryType", "Path", "OldPath", "FileSize", "UserName", "MachineName", "MachineId", "EventId", "SequenceNumber", "IsRecovered"];

    public static IReadOnlyList<string> Validate(string template)
        => TokenRegex().Matches(template).Select(match => match.Groups[1].Value).Where(token => !SupportedTokens.Contains(token)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Render(MonitorEvent e, TextDestinationOptions options)
        => options.Format switch
        {
            TextOutputFormat.JsonLines => JsonSerializer.Serialize(new { eventType = e.EventType.DisplayName(), eventTime = e.EventTimeUtc, entryType = e.EntryType.ToString(), e.Path, e.OldPath, e.FileSize, e.UserName, e.MachineName, e.MachineId, eventId = e.Id, e.SequenceNumber, e.IsRecovered }),
            TextOutputFormat.Csv => string.Join(',', Csv(e.EventTimeUtc.ToLocalTime().ToString("O")), Csv(e.EventType.DisplayName()), Csv(e.EntryType.ToString()), Csv(e.Path), Csv(e.OldPath ?? string.Empty), Csv(e.FileSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty), Csv(e.UserName), Csv(e.MachineName), Csv(e.MachineId.ToString("D")), Csv(e.Id.ToString("D")), Csv(e.SequenceNumber.ToString(CultureInfo.InvariantCulture)), Csv(e.IsRecovered.ToString())),
            _ => RenderTemplate(e, options.Template)
        };

    private static string RenderTemplate(MonitorEvent e, string template)
        => TokenRegex().Replace(template, match => Resolve(e, match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null));

    private static string Resolve(MonitorEvent e, string token, string? format) => token switch
    {
        "EventType" => e.EventType.DisplayName(),
        "EventTime" => e.EventTimeUtc.ToLocalTime().ToString(format ?? "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        "EventTimeUtc" => e.EventTimeUtc.ToUniversalTime().ToString(format ?? "O", CultureInfo.InvariantCulture),
        "EntryType" => e.EntryType.ToString(), "Path" => e.Path, "OldPath" => e.OldPath ?? string.Empty,
        "FileSize" => e.FileSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, "UserName" => e.UserName,
        "MachineName" => e.MachineName, "MachineId" => e.MachineId.ToString("D"), "EventId" => e.Id.ToString("D"),
        "SequenceNumber" => e.SequenceNumber.ToString(CultureInfo.InvariantCulture), "IsRecovered" => e.IsRecovered.ToString(), _ => matchLiteral(token, format)
    };

    private static string matchLiteral(string token, string? format) => "{" + token + (format is null ? string.Empty : ":" + format) + "}";
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)(?::([^{}]+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
