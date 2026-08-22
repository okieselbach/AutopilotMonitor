using System.Text.Json;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Discord embed rendering: payload shape, color mapping, and the Discord embed limits
/// (25 fields, 256/1024 name/value, 4096 description, 6000 total).
/// </summary>
public class DiscordRendererTests
{
    private static JsonElement RenderEmbed(NotificationAlert alert)
    {
        var json = new DiscordRenderer().RenderToJson(alert);
        var doc = JsonDocument.Parse(json);
        var embeds = doc.RootElement.GetProperty("embeds");
        Assert.Equal(1, embeds.GetArrayLength());
        return embeds[0];
    }

    [Fact]
    public void RendersSingleEmbed_WithTitleColorFieldsAndLinks()
    {
        var alert = new NotificationAlert
        {
            Title = "Enrollment Succeeded",
            Summary = "Enrollment Succeeded: DESK-001",
            Severity = NotificationSeverity.Success,
            ThemeColor = "00B050",
            Facts = { new NotificationFact { Name = "Device", Value = "DESK-001" } },
            Sections = { new NotificationSection { Title = "Details", Text = "All apps installed." } },
            Actions = { new NotificationAction { Type = "openUrl", Title = "Open session", Url = "https://portal.example.com/sessions/abc" } },
        };

        var embed = RenderEmbed(alert);

        Assert.Equal("✅ Enrollment Succeeded", embed.GetProperty("title").GetString());
        Assert.Equal(0x00B050, embed.GetProperty("color").GetInt32());

        var description = embed.GetProperty("description").GetString()!;
        Assert.StartsWith("Enrollment Succeeded: DESK-001", description);
        Assert.Contains("**Details**\nAll apps installed.", description);
        // Webhooks cannot post buttons — actions become markdown links.
        Assert.Contains("[Open session](https://portal.example.com/sessions/abc)", description);

        var fields = embed.GetProperty("fields");
        Assert.Equal(1, fields.GetArrayLength());
        Assert.Equal("Device", fields[0].GetProperty("name").GetString());
        Assert.Equal("DESK-001", fields[0].GetProperty("value").GetString());
        Assert.True(fields[0].GetProperty("inline").GetBoolean());
    }

    [Theory]
    [InlineData(NotificationSeverity.Success, 0x2ECC71)]
    [InlineData(NotificationSeverity.Error, 0xE74C3C)]
    [InlineData(NotificationSeverity.Warning, 0xF1C40F)]
    [InlineData(NotificationSeverity.Info, 0x3498DB)]
    public void Color_FallsBackToSeverity_WhenThemeColorMissingOrInvalid(NotificationSeverity severity, int expected)
    {
        foreach (var themeColor in new[] { "", "not-hex" })
        {
            var embed = RenderEmbed(new NotificationAlert { Title = "T", Severity = severity, ThemeColor = themeColor });
            Assert.Equal(expected, embed.GetProperty("color").GetInt32());
        }
    }

    [Fact]
    public void Fields_CappedAt25_AndEmptyFactsSkipped()
    {
        var alert = new NotificationAlert { Title = "T", Summary = "S" };
        alert.Facts.Add(new NotificationFact { Name = "", Value = "dropped" });
        alert.Facts.Add(new NotificationFact { Name = "dropped", Value = " " });
        for (var i = 0; i < 30; i++)
            alert.Facts.Add(new NotificationFact { Name = $"f{i}", Value = "v" });

        var fields = RenderEmbed(alert).GetProperty("fields");

        Assert.Equal(25, fields.GetArrayLength());
        Assert.Equal("f0", fields[0].GetProperty("name").GetString()); // empty facts skipped, not counted
    }

    [Fact]
    public void FieldValue_TruncatedTo1024_WithEllipsis()
    {
        var alert = new NotificationAlert { Title = "T" };
        alert.Facts.Add(new NotificationFact { Name = "long", Value = new string('x', 2000) });

        var value = RenderEmbed(alert).GetProperty("fields")[0].GetProperty("value").GetString()!;

        Assert.Equal(1024, value.Length);
        Assert.EndsWith("…", value);
    }

    [Fact]
    public void TotalEmbedLength_CappedAt6000_ByDroppingTrailingFields()
    {
        var alert = new NotificationAlert { Title = "T", Summary = "S" };
        for (var i = 0; i < 25; i++)
            alert.Facts.Add(new NotificationFact { Name = $"f{i}", Value = new string('v', 1000) });

        var embed = RenderEmbed(alert);
        var fields = embed.GetProperty("fields");

        var total = embed.GetProperty("title").GetString()!.Length
            + embed.GetProperty("description").GetString()!.Length;
        for (var i = 0; i < fields.GetArrayLength(); i++)
            total += fields[i].GetProperty("name").GetString()!.Length + fields[i].GetProperty("value").GetString()!.Length;

        Assert.True(fields.GetArrayLength() < 25);
        Assert.True(total <= 6000);
        Assert.Equal("f0", fields[0].GetProperty("name").GetString()); // leading fields kept
    }

    [Fact]
    public void EmptyDescription_Omitted_AndProviderTypeIsDiscord()
    {
        var embed = RenderEmbed(new NotificationAlert { Title = "T", Severity = NotificationSeverity.Info });

        Assert.False(embed.TryGetProperty("description", out _));
        Assert.False(embed.TryGetProperty("fields", out _));
        Assert.Equal(WebhookProviderType.Discord, new DiscordRenderer().ProviderType);
    }
}
