using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Annotations function folder (GetSessionAnnotations,
/// ListSessionAnnotations, ListTenantSessionAnnotations, UpsertSessionAnnotation).
/// The anonymous literals below are copied verbatim from the pre-migration call sites
/// (including the item shapes built by AnnotationWire.ToWire/ToWireWithScope) and act as
/// the independent fixture against the new typed DTOs.
/// </summary>
public class AnnotationsWireParityTests
{
    private static readonly DateTime Created = new DateTime(2026, 8, 29, 9, 15, 42, DateTimeKind.Utc);
    private static readonly DateTime Updated = new DateTime(2026, 8, 30, 11, 22, 33, DateTimeKind.Utc);

    [Fact]
    public void GetSessionAnnotations_success_shape_is_wire_identical()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                sessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                tenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                annotations = new List<object>
                {
                    new
                    {
                        lane = "operator",
                        verdict = (string?)"root_cause_confirmed",
                        note = (string?)"Driver package blocked ESP; matches APP-017.",
                        authorUpn = "ops@contoso.com",
                        authorDisplayName = "Contoso Ops",
                        createdByUpn = "ops@contoso.com",
                        createdAtUtc = Created,
                        updatedAtUtc = Updated,
                        ruleIds = new List<string> { "APP-017", "NET-001" },
                    },
                },
                writableLanes = new List<string> { "operator", "tenantadmin" },
            },
            new GetSessionAnnotationsResponse
            {
                Success = true,
                SessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                TenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                Annotations = new List<SessionAnnotationItem>
                {
                    new SessionAnnotationItem
                    {
                        Lane = "operator",
                        Verdict = "root_cause_confirmed",
                        Note = "Driver package blocked ESP; matches APP-017.",
                        AuthorUpn = "ops@contoso.com",
                        AuthorDisplayName = "Contoso Ops",
                        CreatedByUpn = "ops@contoso.com",
                        CreatedAtUtc = Created,
                        UpdatedAtUtc = Updated,
                        RuleIds = new List<string> { "APP-017", "NET-001" },
                    },
                },
                WritableLanes = new List<string> { "operator", "tenantadmin" },
            });
    }

    [Fact]
    public void GetSessionAnnotations_null_verdict_and_note_vanish_identically()
    {
        // A lane row can be verdict-only or note-only; null slots must drop their keys
        // on both sides (WhenWritingNull). Also covers the empty-annotations page.
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                sessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                tenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                annotations = new List<object>
                {
                    new
                    {
                        lane = "tenantadmin",
                        verdict = (string?)null,
                        note = (string?)null,
                        authorUpn = "admin@fabrikam.com",
                        authorDisplayName = "Fabrikam Admin",
                        createdByUpn = "admin@fabrikam.com",
                        createdAtUtc = Created,
                        updatedAtUtc = Updated,
                        ruleIds = new List<string>(),
                    },
                },
                writableLanes = new List<string>(),
            },
            new GetSessionAnnotationsResponse
            {
                Success = true,
                SessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                TenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                Annotations = new List<SessionAnnotationItem>
                {
                    new SessionAnnotationItem
                    {
                        Lane = "tenantadmin",
                        Verdict = null,
                        Note = null,
                        AuthorUpn = "admin@fabrikam.com",
                        AuthorDisplayName = "Fabrikam Admin",
                        CreatedByUpn = "admin@fabrikam.com",
                        CreatedAtUtc = Created,
                        UpdatedAtUtc = Updated,
                        RuleIds = new List<string>(),
                    },
                },
                WritableLanes = new List<string>(),
            });
    }

    [Fact]
    public void SessionAnnotationList_success_shape_is_wire_identical()
    {
        // Shared by ListSessionAnnotations (global) and ListTenantSessionAnnotations —
        // both sites emit { success, count, annotations(scoped), nextLink }.
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                count = 1,
                annotations = new List<object>
                {
                    new
                    {
                        tenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                        sessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                        lane = "globaladmin",
                        verdict = (string?)"analysis_wrong",
                        note = (string?)"Rule fired on the wrong phase.",
                        authorUpn = "ga@contoso.com",
                        authorDisplayName = "Platform GA",
                        createdByUpn = "ga@contoso.com",
                        createdAtUtc = Created,
                        updatedAtUtc = Updated,
                        ruleIds = new List<string> { "ESP-004" },
                    },
                },
                nextLink = (string?)"/api/global/session-annotations?pageSize=50&continuation=abc123",
            },
            new SessionAnnotationListResponse
            {
                Success = true,
                Count = 1,
                Annotations = new List<SessionAnnotationScopedItem>
                {
                    new SessionAnnotationScopedItem
                    {
                        TenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                        SessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                        Lane = "globaladmin",
                        Verdict = "analysis_wrong",
                        Note = "Rule fired on the wrong phase.",
                        AuthorUpn = "ga@contoso.com",
                        AuthorDisplayName = "Platform GA",
                        CreatedByUpn = "ga@contoso.com",
                        CreatedAtUtc = Created,
                        UpdatedAtUtc = Updated,
                        RuleIds = new List<string> { "ESP-004" },
                    },
                },
                NextLink = "/api/global/session-annotations?pageSize=50&continuation=abc123",
            });
    }

    [Fact]
    public void SessionAnnotationList_null_nextLink_and_null_item_slots_vanish_identically()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                count = 1,
                annotations = new List<object>
                {
                    new
                    {
                        tenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                        sessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                        lane = "operator",
                        verdict = (string?)null,
                        note = (string?)"Note-only annotation.",
                        authorUpn = "ops@fabrikam.com",
                        authorDisplayName = "Fabrikam Ops",
                        createdByUpn = "ops@fabrikam.com",
                        createdAtUtc = Created,
                        updatedAtUtc = Updated,
                        ruleIds = new List<string>(),
                    },
                },
                nextLink = (string?)null,
            },
            new SessionAnnotationListResponse
            {
                Success = true,
                Count = 1,
                Annotations = new List<SessionAnnotationScopedItem>
                {
                    new SessionAnnotationScopedItem
                    {
                        TenantId = "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                        SessionId = "6f0a2d9c-1b3e-4c8f-9d21-7e5a4b6c8d90",
                        Lane = "operator",
                        Verdict = null,
                        Note = "Note-only annotation.",
                        AuthorUpn = "ops@fabrikam.com",
                        AuthorDisplayName = "Fabrikam Ops",
                        CreatedByUpn = "ops@fabrikam.com",
                        CreatedAtUtc = Created,
                        UpdatedAtUtc = Updated,
                        RuleIds = new List<string>(),
                    },
                },
                NextLink = null,
            });
    }

    [Fact]
    public void UpsertSessionAnnotation_deleted_shape_is_wire_identical()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, deleted = true },
            new UpsertSessionAnnotationDeletedResponse { Success = true, Deleted = true });
    }

    [Fact]
    public void UpsertSessionAnnotation_success_shape_is_wire_identical()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                annotation = (object)new
                {
                    lane = "operator",
                    verdict = (string?)"different_problem",
                    note = (string?)"Actually a proxy issue, not the app.",
                    authorUpn = "ops@contoso.com",
                    authorDisplayName = "Contoso Ops",
                    createdByUpn = "admin@contoso.com",
                    createdAtUtc = Created,
                    updatedAtUtc = Updated,
                    ruleIds = new List<string> { "NET-001" },
                },
            },
            new UpsertSessionAnnotationResponse
            {
                Success = true,
                Annotation = new SessionAnnotationItem
                {
                    Lane = "operator",
                    Verdict = "different_problem",
                    Note = "Actually a proxy issue, not the app.",
                    AuthorUpn = "ops@contoso.com",
                    AuthorDisplayName = "Contoso Ops",
                    CreatedByUpn = "admin@contoso.com",
                    CreatedAtUtc = Created,
                    UpdatedAtUtc = Updated,
                    RuleIds = new List<string> { "NET-001" },
                },
            });
    }

    [Fact]
    public void UpsertSessionAnnotation_verdict_only_null_note_vanishes_identically()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                annotation = (object)new
                {
                    lane = "tenantadmin",
                    verdict = (string?)"inconclusive",
                    note = (string?)null,
                    authorUpn = "admin@fabrikam.com",
                    authorDisplayName = "Fabrikam Admin",
                    createdByUpn = "admin@fabrikam.com",
                    createdAtUtc = Created,
                    updatedAtUtc = Created,
                    ruleIds = new List<string>(),
                },
            },
            new UpsertSessionAnnotationResponse
            {
                Success = true,
                Annotation = new SessionAnnotationItem
                {
                    Lane = "tenantadmin",
                    Verdict = "inconclusive",
                    Note = null,
                    AuthorUpn = "admin@fabrikam.com",
                    AuthorDisplayName = "Fabrikam Admin",
                    CreatedByUpn = "admin@fabrikam.com",
                    CreatedAtUtc = Created,
                    UpdatedAtUtc = Created,
                    RuleIds = new List<string>(),
                },
            });
    }
}
