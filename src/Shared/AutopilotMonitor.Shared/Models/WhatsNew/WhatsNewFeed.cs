using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AutopilotMonitor.Shared.Models.WhatsNew
{
    /// <summary>
    /// The portal's <c>/whats-new.json</c> as the backend reads it — the same payload the What's new
    /// panel renders (generated at web-deploy time from the docs changelogs by
    /// <c>scripts/whats-new/build-whats-new.js</c>). Entries carry a content-hash id and the docs
    /// commit date; the backend only needs ids (to diff against the last run) plus the display
    /// fields for the digest message.
    /// </summary>
    public sealed class WhatsNewFeed
    {
        public const string PlatformChannel = "platform";
        public const string AgentChannel = "agent";
        public static readonly string[] Channels = { PlatformChannel, AgentChannel };

        public string? GeneratedUtc { get; set; }
        public string? DocsCommit { get; set; }
        public IReadOnlyList<WhatsNewFeedEntry> Entries { get; set; } = Array.Empty<WhatsNewFeedEntry>();

        /// <summary>Channel → docs URL of the full changelog ("View all updates" target).</summary>
        public IReadOnlyDictionary<string, string> DocsUrls { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Parses the payload. Returns null for anything off-shape (wrong schema version, missing
        /// channel, malformed entry) — the caller then skips the run rather than diffing against a
        /// half-read list, which would look like "everything was removed" or "everything is new".
        /// </summary>
        public static WhatsNewFeed? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json!);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;
                if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
                    return null;
                if (!root.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Object)
                    return null;

                var entries = new List<WhatsNewFeedEntry>();
                var docsUrls = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var channelName in Channels)
                {
                    if (!channels.TryGetProperty(channelName, out var channel) || channel.ValueKind != JsonValueKind.Object)
                        return null;
                    if (!channel.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
                        return null;

                    if (channel.TryGetProperty("docsUrl", out var docsUrl) && docsUrl.ValueKind == JsonValueKind.String)
                        docsUrls[channelName] = docsUrl.GetString()!;

                    foreach (var item in list.EnumerateArray())
                    {
                        var entry = ParseEntry(channelName, item);
                        if (entry == null)
                            return null;
                        entries.Add(entry);
                    }
                }

                return new WhatsNewFeed
                {
                    GeneratedUtc = GetString(root, "generatedUtc"),
                    DocsCommit = GetString(root, "docsCommit"),
                    Entries = entries,
                    DocsUrls = docsUrls,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static WhatsNewFeedEntry? ParseEntry(string channel, JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return null;

            var id = GetString(item, "id");
            var addedUtc = GetString(item, "addedUtc");
            var body = GetString(item, "body");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(addedUtc) || body == null)
                return null;
            if (!DateTime.TryParse(addedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var added))
                return null;

            return new WhatsNewFeedEntry
            {
                Channel = channel,
                Id = id!,
                AddedUtc = added,
                Period = GetString(item, "period") ?? string.Empty,
                Title = GetString(item, "title"),
                Body = body,
                Link = GetString(item, "link"),
            };
        }

        private static string? GetString(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>One changelog bullet as published in <c>/whats-new.json</c>.</summary>
    public sealed class WhatsNewFeedEntry
    {
        /// <summary><see cref="WhatsNewFeed.PlatformChannel"/> or <see cref="WhatsNewFeed.AgentChannel"/>.</summary>
        public string Channel { get; set; } = default!;

        /// <summary>Content-hash id (stable across builds while the bullet text is unchanged).</summary>
        public string Id { get; set; } = default!;

        /// <summary>When the bullet was committed to the docs repo.</summary>
        public DateTime AddedUtc { get; set; }

        /// <summary>The changelog period heading, e.g. "September 2026".</summary>
        public string Period { get; set; } = string.Empty;

        /// <summary>Bold lead of platform bullets; agent bullets have none.</summary>
        public string? Title { get; set; }

        /// <summary>Inline markdown (**bold**, `code`, [label](url)).</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>First docs link of the bullet, if any.</summary>
        public string? Link { get; set; }

        /// <summary>Channel-qualified key used for the "already announced" set — ids are per-channel hashes.</summary>
        public string Key => $"{Channel}:{Id}";
    }
}
