using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// In-memory <see cref="ISelfServiceAssignees"/> for the function tests: records every call, and — paired with a
/// <see cref="FakeSignalRNotificationService"/> — how many disconnects had happened when it was called, which is
/// the proof that a handler ends the assignment BEFORE it cuts the live streams.
/// </summary>
public class FakeSelfServiceAssignees : ISelfServiceAssignees
{
    private readonly FakeSignalRNotificationService? _signalR;

    public FakeSelfServiceAssignees(FakeSignalRNotificationService? signalR = null) => _signalR = signalR;

    public List<(string HomeTenantId, string Upn, string Actor, string Reason, int DisconnectsBefore)> Ended { get; } = new();
    public List<(string HomeTenantId, string Upn, string Actor, string Reason)> Assigned { get; } = new();

    /// <summary>When set, <see cref="EndAssignmentAsync"/> throws it (fail-soft path of the caller).</summary>
    public Exception? EndThrows { get; set; }

    /// <summary>When set, <see cref="AssignMemberAsync"/> returns it instead of a success.</summary>
    public DelegationFailure? AssignFails { get; set; }

    public Task<bool> EndAssignmentAsync(string homeTenantId, string upn, string actor, string reason)
    {
        Ended.Add((homeTenantId, upn, actor, reason, _signalR?.DisconnectedUsers.Count ?? 0));
        if (EndThrows != null)
            throw EndThrows;
        return Task.FromResult(true);
    }

    public Task<DelegationResult<TenantGroupAssignment>> AssignMemberAsync(string homeTenantId, string upn, string actor, string reason)
    {
        Assigned.Add((homeTenantId, upn, actor, reason));
        return Task.FromResult(AssignFails is { } failure
            ? DelegationResult<TenantGroupAssignment>.Fail(failure.Status, failure.Code, failure.Message)
            : DelegationResult<TenantGroupAssignment>.Success(new TenantGroupAssignment { Upn = upn, GroupId = homeTenantId }));
    }
}
