using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>The two transactional mails the product sends.</summary>
public enum EmailTemplateKind
{
    Welcome,
    Farewell,
}

/// <summary>
/// Resolves the HTML body for a transactional mail: the operator override when one is
/// stored, otherwise the built-in template. Consumed by <see cref="EmailService"/>.
/// </summary>
public interface IEmailTemplateProvider
{
    Task<string> GetHtmlAsync(EmailTemplateKind kind, string domainName, CancellationToken ct = default);
}

/// <summary>
/// Operator-editable email templates. Overrides live in the PreviewConfig table (partition
/// "EmailTemplates", row key = kind) and are cached for 5 minutes; the built-in templates in
/// <see cref="EmailTemplates"/> remain the fallback. Subjects are not editable — they are pinned
/// constants.
/// <para>
/// Rendering contract: the only placeholder is <c>{{domainName}}</c>; an empty domain renders as
/// "your organization", exactly like the built-ins. Resolution is fail-soft — a storage error
/// falls back to the built-in template so activation/offboarding mails keep going out.
/// </para>
/// </summary>
public class EmailTemplateService : IEmailTemplateProvider
{
    public const string DomainPlaceholder = "{{domainName}}";
    public const string EmptyDomainLabel = "your organization";
    /// <summary>Azure Table string properties cap at 32K UTF-16 chars; keep headroom.</summary>
    public const int MaxHtmlLength = 30_000;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IConfigRepository _configRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EmailTemplateService> _logger;

    public EmailTemplateService(IConfigRepository configRepo, IMemoryCache cache, ILogger<EmailTemplateService> logger)
    {
        _configRepo = configRepo;
        _cache = cache;
        _logger = logger;
    }

    public static bool TryParseKind(string? value, out EmailTemplateKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "welcome": kind = EmailTemplateKind.Welcome; return true;
            case "farewell": kind = EmailTemplateKind.Farewell; return true;
            default: kind = default; return false;
        }
    }

    public static string KindKey(EmailTemplateKind kind) => kind == EmailTemplateKind.Welcome ? "welcome" : "farewell";

    public static string Subject(EmailTemplateKind kind) => kind == EmailTemplateKind.Welcome
        ? EmailTemplates.PreviewApprovedSubject
        : EmailTemplates.OffboardingFarewellSubject;

    /// <summary>Built-in template with the placeholder left in place (for the editor).</summary>
    public static string BuiltInRaw(EmailTemplateKind kind) => kind == EmailTemplateKind.Welcome
        ? EmailTemplates.GetPreviewApprovedHtml(DomainPlaceholder)
        : EmailTemplates.GetOffboardingFarewellHtml(DomainPlaceholder);

    /// <summary>Substitutes the placeholder; an empty domain becomes "your organization".</summary>
    public static string Render(string rawHtml, string? domainName)
    {
        var display = string.IsNullOrWhiteSpace(domainName) ? EmptyDomainLabel : domainName.Trim();
        return rawHtml.Replace(DomainPlaceholder, display, StringComparison.Ordinal);
    }

    /// <summary>Returns null when the HTML is acceptable, otherwise the validation message.</summary>
    public static string? Validate(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "Template HTML must not be empty.";
        if (html.Length > MaxHtmlLength) return $"Template HTML exceeds {MaxHtmlLength:N0} characters ({html.Length:N0}).";
        return null;
    }

    public async Task<string> GetHtmlAsync(EmailTemplateKind kind, string domainName, CancellationToken ct = default)
    {
        var overrideEntry = await GetOverrideAsync(kind, failSoft: true);
        return overrideEntry is null
            ? Render(BuiltInRaw(kind), domainName)
            : Render(overrideEntry.Html, domainName);
    }

    /// <summary>
    /// The stored override, or null for built-in. <paramref name="failSoft"/> swallows storage
    /// errors (send path must never depend on the override store); the admin endpoints pass
    /// false so a broken store surfaces as an error instead of silently showing the built-in.
    /// </summary>
    public virtual async Task<EmailTemplateOverride?> GetOverrideAsync(EmailTemplateKind kind, bool failSoft = false)
    {
        var key = CacheKey(kind);
        if (_cache.TryGetValue(key, out EmailTemplateOverride? cached))
            return cached;

        try
        {
            var entry = await _configRepo.GetEmailTemplateOverrideAsync(KindKey(kind));
            // null is a valid (and the common) value — cache it too so the send path
            // doesn't hit storage on every mail.
            _cache.Set(key, entry, CacheDuration);
            return entry;
        }
        catch (Exception ex) when (failSoft)
        {
            _logger.LogWarning(ex, "Failed to read {Kind} email template override — using built-in template", kind);
            return null;
        }
    }

    public virtual async Task<EmailTemplateOverride> SaveOverrideAsync(EmailTemplateKind kind, string html, string updatedBy)
    {
        var error = Validate(html);
        if (error is not null) throw new ArgumentException(error, nameof(html));

        var entry = new EmailTemplateOverride
        {
            Kind = KindKey(kind),
            Html = html,
            UpdatedBy = updatedBy,
            UpdatedUtc = DateTime.UtcNow,
        };
        await _configRepo.SaveEmailTemplateOverrideAsync(entry);
        _cache.Remove(CacheKey(kind));
        _logger.LogInformation("Email template override saved: {Kind} by {UpdatedBy} ({Length} chars)", kind, updatedBy, html.Length);
        return entry;
    }

    public virtual async Task DeleteOverrideAsync(EmailTemplateKind kind, string deletedBy)
    {
        await _configRepo.DeleteEmailTemplateOverrideAsync(KindKey(kind));
        _cache.Remove(CacheKey(kind));
        _logger.LogInformation("Email template override reset to built-in: {Kind} by {DeletedBy}", kind, deletedBy);
    }

    private static string CacheKey(EmailTemplateKind kind) => $"email-template-override:{KindKey(kind)}";
}
