using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The dashboard search-box grammar, parsed once per request for the server-side sweep
/// (<c>q=</c> on <c>/api/search/sessions</c> and the global twin). Mirrors the web's
/// <c>app/dashboard/utils/sessionSearchQuery.ts</c> exactly — both run
/// <c>utils/session-search-syntax.cases.json</c>, because a term that matches on one side
/// and not the other is a ghost result (found by the server, filtered out by the client, or
/// never fetched at all).
/// <para>
/// Grammar: whitespace-separated terms are AND-ed; a leading minus excludes; double quotes
/// protect a phrase; <c>key=value</c> / <c>key:value</c> restricts a term to one field when
/// <c>key</c> is in <see cref="Fields"/> (case-insensitive), otherwise the token stays a
/// literal term (<c>14:30</c>). A qualifier is recognised only when the separator precedes any
/// quote in the token. Every term is a case-insensitive substring match; free terms may hit
/// any field in <see cref="Fields"/>. There is no escape character.
/// </para>
/// </summary>
public sealed class SessionSearchQuery
{
    /// <summary>Qualifier → field reader, in the web's suggestion order. Exactly the set the client searches.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, Func<SessionSummary, string?>>> Fields =
        new List<KeyValuePair<string, Func<SessionSummary, string?>>>
        {
            new("device", s => s.DeviceName),
            new("serial", s => s.SerialNumber),
            new("model", s => s.Model),
            new("manufacturer", s => s.Manufacturer),
            new("status", s => s.Status.ToString()),
            new("session", s => s.SessionId),
            new("country", s => s.GeoCountry),
            new("region", s => s.GeoRegion),
            new("city", s => s.GeoCity),
            new("agent", s => s.AgentVersion),
            new("os", s => s.OsName),
            new("build", s => s.OsBuild),
            new("osversion", s => s.OsDisplayVersion),
            new("edition", s => s.OsEdition),
            new("language", s => s.OsLanguage),
        };

    private static readonly Dictionary<string, Func<SessionSummary, string?>> ReaderByQualifier =
        Fields.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>One search term; <see cref="Field"/> is the qualifier (lowercase) or null for a free term.</summary>
    public readonly record struct Term(string Text, string? Field);

    public IReadOnlyList<Term> Include { get; }
    public IReadOnlyList<Term> Exclude { get; }

    /// <summary>True when the query carries no filter at all (matches every session).</summary>
    public bool IsEmpty => Include.Count == 0 && Exclude.Count == 0;

    private SessionSearchQuery(List<Term> include, List<Term> exclude)
    {
        Include = include;
        Exclude = exclude;
    }

    public static SessionSearchQuery Parse(string? query)
    {
        var include = new List<Term>();
        var exclude = new List<Term>();
        foreach (var token in Tokenize(query ?? string.Empty))
        {
            Term term;
            if (token.Qualifier != null && ReaderByQualifier.ContainsKey(token.Qualifier))
                term = new Term(token.Text.ToString(), token.Qualifier.ToLowerInvariant());
            else
                term = new Term(token.LiteralText, null);
            var target = token.Negated ? exclude : include;
            if (!target.Contains(term)) target.Add(term);
        }
        return new SessionSearchQuery(include, exclude);
    }

    public bool Matches(SessionSummary session)
    {
        if (IsEmpty) return true;
        foreach (var t in Include)
            if (!TermMatches(t, session)) return false;
        foreach (var t in Exclude)
            if (TermMatches(t, session)) return false;
        return true;
    }

    private static bool TermMatches(Term term, SessionSummary session)
    {
        if (term.Field != null)
            return ContainsIgnoreCase(ReaderByQualifier[term.Field](session), term.Text);
        foreach (var field in Fields)
            if (ContainsIgnoreCase(field.Value(session), term.Text)) return true;
        return false;
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack)
           && haystack!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private sealed class Token
    {
        public StringBuilder Text { get; } = new();
        public bool Negated { get; init; }
        public string? Qualifier { get; set; }
        public char Separator { get; set; }
        public bool SawQuote { get; set; }
        public string LiteralText => Qualifier == null ? Text.ToString() : $"{Qualifier}{Separator}{Text}";
    }

    // Character-for-character the web tokenizer (lib/searchQueryTokens.ts).
    private static List<Token> Tokenize(string query)
    {
        var tokens = new List<Token>();
        Token? current = null;
        var inQuotes = false;

        void Flush()
        {
            if (current != null && current.Text.Length > 0) tokens.Add(current);
            current = null;
        }

        foreach (var ch in query)
        {
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                Flush();
                continue;
            }
            if (ch == '"')
            {
                current ??= new Token();
                current.SawQuote = true;
                inQuotes = !inQuotes;
                continue;
            }
            if (current == null)
            {
                if (ch == '-')
                {
                    current = new Token { Negated = true };
                    continue;
                }
                current = new Token();
            }
            if ((ch == '=' || ch == ':') && !inQuotes && !current.SawQuote
                && current.Qualifier == null && current.Text.Length > 0)
            {
                current.Qualifier = current.Text.ToString();
                current.Separator = ch;
                current.Text.Clear();
                continue;
            }
            current.Text.Append(ch);
        }
        Flush();
        return tokens;
    }
}
