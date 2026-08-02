using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Outputs;

namespace DirectoryMonitor.Tests;

public sealed class TextTemplateRendererTests
{
    private static readonly MonitorEvent Sample = new()
    {
        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), SequenceNumber = 42,
        EventTimeUtc = new DateTimeOffset(2026, 8, 2, 7, 25, 36, TimeSpan.Zero), EventType = MonitorEventType.Change,
        EntryType = EntryType.File, Path = @"D:\Data\Report.xlsx", OldPath = @"D:\Data\Old.xlsx",
        UserName = @"COMPANY\operator", MachineName = "PC-01"
    };

    [Fact]
    public void Custom_template_replaces_supported_tokens()
    {
        var value = TextTemplateRenderer.Render(Sample, new TextDestinationOptions { Format = TextOutputFormat.CustomTemplate, Template = "{EventType}|{Path}|{OldPath}|{SequenceNumber}" });
        Assert.Equal(@"Change|D:\Data\Report.xlsx|D:\Data\Old.xlsx|42", value);
    }

    [Fact]
    public void Unknown_token_is_reported() => Assert.Contains("Unknown", TextTemplateRenderer.Validate("{Unknown}"));

    [Fact]
    public void Json_lines_is_valid_json()
    {
        var json = TextTemplateRenderer.Render(Sample, new TextDestinationOptions { Format = TextOutputFormat.JsonLines });
        using var document = System.Text.Json.JsonDocument.Parse(json); Assert.Equal("Change", document.RootElement.GetProperty("eventType").GetString());
    }
}
