using System;
using System.Linq;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers <see cref="TableBatchDedup.ByRowKey"/> — the guard that stops an agent replaying an
/// overlapping ordinal/step from failing an entire ingest entity-group-transaction with Azure
/// Tables' HTTP 400 / <c>InvalidDuplicateRow</c>. Exercised through the three real ingest
/// projections (Signals, DecisionTransitions, Index) so the (PK, RowKey) contract is asserted
/// against the keys those repos actually emit.
/// </summary>
public class TableBatchDedupTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static SignalRecord Signal(long ordinal, string kind) => new()
    {
        TenantId             = TenantId,
        SessionId            = SessionId,
        SessionSignalOrdinal = ordinal,
        SessionTraceOrdinal  = ordinal,
        Kind                 = kind,
        KindSchemaVersion    = 1,
        OccurredAtUtc        = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc),
        SourceOrigin         = "test",
        PayloadJson          = "{}",
    };

    [Fact]
    public void ByRowKey_collapses_duplicate_signal_ordinal_keeping_last_write()
    {
        var first  = TableSignalRepository.ToEntity(Signal(6, "first"));
        var second = TableSignalRepository.ToEntity(Signal(6, "second")); // same RowKey as first
        var other  = TableSignalRepository.ToEntity(Signal(7, "other"));

        var (deduped, dropped) = TableBatchDedup.ByRowKey(new[] { first, second, other });

        Assert.Equal(1, dropped);
        Assert.Equal(2, deduped.Count);

        var collapsed = deduped.Single(e => e.RowKey == TableSignalRepository.BuildRowKey(6));
        Assert.Equal("second", collapsed.GetString("Kind")); // last-wins
    }

    [Fact]
    public void ByRowKey_collapses_duplicate_transition_stepIndex()
    {
        DecisionTransitionRecord Transition(int step, string toStage) => new()
        {
            TenantId            = TenantId,
            SessionId           = SessionId,
            StepIndex           = step,
            SessionTraceOrdinal = step,
            SignalOrdinalRef    = step,
            OccurredAtUtc       = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc),
            Trigger             = "t",
            FromStage           = "A",
            ToStage             = toStage,
            ReducerVersion      = "1",
            PayloadJson         = "{}",
        };

        var first  = TableDecisionTransitionRepository.ToEntity(Transition(3, "Stage1"));
        var second = TableDecisionTransitionRepository.ToEntity(Transition(3, "Stage2"));

        var (deduped, dropped) = TableBatchDedup.ByRowKey(new[] { first, second });

        Assert.Equal(1, dropped);
        var collapsed = Assert.Single(deduped);
        Assert.Equal("Stage2", collapsed.GetString("ToStage")); // last-wins
    }

    [Fact]
    public void ByRowKey_is_a_passthrough_when_no_duplicates_and_orders_by_rowkey()
    {
        var ten  = TableSignalRepository.ToEntity(Signal(10, "ten"));
        var nine = TableSignalRepository.ToEntity(Signal(9, "nine"));

        var (deduped, dropped) = TableBatchDedup.ByRowKey(new[] { ten, nine });

        Assert.Equal(0, dropped);
        Assert.Equal(2, deduped.Count);
        // D19 padding → RowKey lex order matches numeric order: 9 before 10.
        Assert.Equal(TableSignalRepository.BuildRowKey(9), deduped[0].RowKey);
        Assert.Equal(TableSignalRepository.BuildRowKey(10), deduped[1].RowKey);
    }

    [Fact]
    public void ByRowKey_handles_empty_input()
    {
        var (deduped, dropped) = TableBatchDedup.ByRowKey(Array.Empty<TableEntity>());

        Assert.Empty(deduped);
        Assert.Equal(0, dropped);
    }
}
