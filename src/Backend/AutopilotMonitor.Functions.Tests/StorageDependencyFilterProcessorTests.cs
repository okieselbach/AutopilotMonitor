using AutopilotMonitor.Functions.Telemetry;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Locks in the filtering contract of <see cref="StorageDependencyFilterProcessor"/>: successful
/// Azure Storage dependencies (Table/Queue/Blob) are dropped to curb AppDependencies ingestion
/// cost, while FAILED storage calls and ALL non-storage telemetry (HTTP/Graph/requests) survive.
/// </summary>
public class StorageDependencyFilterProcessorTests
{
    /// <summary>Capturing terminal processor — records everything forwarded to it.</summary>
    private sealed class CapturingProcessor : ITelemetryProcessor
    {
        public List<ITelemetry> Received { get; } = new();
        public void Process(ITelemetry item) => Received.Add(item);
    }

    private static (StorageDependencyFilterProcessor processor, CapturingProcessor next) Build()
    {
        var next = new CapturingProcessor();
        return (new StorageDependencyFilterProcessor(next), next);
    }

    private static DependencyTelemetry Dep(string target, bool? success, string? data = null, string? type = null)
        => new() { Target = target, Success = success, Data = data ?? string.Empty, Type = type ?? string.Empty };

    [Theory]
    [InlineData("myacct.table.core.windows.net")]
    [InlineData("myacct.queue.core.windows.net")]
    [InlineData("myacct.blob.core.windows.net")]
    public void SuccessfulStorageDependency_IsDropped(string target)
    {
        var (processor, next) = Build();
        processor.Process(Dep(target, success: true));
        Assert.Empty(next.Received);
    }

    [Fact]
    public void SuccessfulStorageDependency_WithNullSuccess_IsDropped()
    {
        // Null Success (no explicit outcome) is treated as non-failure → still noise.
        var (processor, next) = Build();
        processor.Process(Dep("myacct.table.core.windows.net", success: null));
        Assert.Empty(next.Received);
    }

    [Theory]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("403")]
    [InlineData("")] // unknown outcome (timeout/network) — keep
    public void FailedStorageDependency_IsKept(string resultCode)
    {
        var (processor, next) = Build();
        var dep = Dep("myacct.queue.core.windows.net", success: false);
        dep.ResultCode = resultCode;
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    // Expected outcomes (point-read miss, ETag precondition, insert conflict) are normal control
    // flow — live-verified as 100% of the remaining "failed" storage rows. Dropped.
    [Theory]
    [InlineData("404")]
    [InlineData("412")]
    [InlineData("409")]
    public void ExpectedStorageOutcome_HttpShape_IsDropped(string resultCode)
    {
        var (processor, next) = Build();
        var dep = Dep("myacct.table.core.windows.net", success: false);
        dep.ResultCode = resultCode;
        processor.Process(dep);
        Assert.Empty(next.Received);
    }

    [Theory]
    [InlineData("Azure.RequestFailedException: The specified resource does not exist.\nRequestId:x\nStatus: 404 (Not Found)\nErrorCode: ResourceNotFound")]
    [InlineData("Azure.RequestFailedException: The update condition specified in the request was not satisfied.\nStatus: 412 (Precondition Failed)\nErrorCode: UpdateConditionNotSatisfied")]
    [InlineData("Azure.RequestFailedException: The specified entity already exists.\nStatus: 409 (Conflict)\nErrorCode: EntityAlreadyExists")]
    public void ExpectedStorageOutcome_InProcShape_IsDropped(string error)
    {
        // InProc spans have no ResultCode — only the exception text in Properties["Error"].
        var (processor, next) = Build();
        var dep = Dep(target: "TableClient.GetEntity", success: false, type: "InProc | Microsoft.Tables");
        dep.Properties["Error"] = error;
        processor.Process(dep);
        Assert.Empty(next.Received);
    }

    [Fact]
    public void ThrottledInProcStorageDependency_IsKept()
    {
        var (processor, next) = Build();
        var dep = Dep(target: "TableClient.SubmitTransaction", success: false, type: "InProc | Microsoft.Tables");
        dep.Properties["Error"] = "Azure.RequestFailedException: Too many requests.\nStatus: 429 (Too Many Requests)\nErrorCode: ServerBusy";
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    [Fact]
    public void SuccessfulSignalRRestCall_IsDropped()
    {
        var (processor, next) = Build();
        processor.Process(Dep("autopilotmonitor-eu.service.signalr.net", success: true, data: "POST /api/hubs/autopilotmonitor/groups/t-x/connections/y", type: "HTTP"));
        Assert.Empty(next.Received);
    }

    [Fact]
    public void FailedSignalRRestCall_IsKept()
    {
        var (processor, next) = Build();
        var dep = Dep("autopilotmonitor-eu.service.signalr.net", success: false, type: "HTTP");
        dep.ResultCode = "500";
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    [Theory]
    [InlineData("graph.microsoft.com")]
    [InlineData("login.microsoftonline.com")]
    [InlineData("api.nvd.nist.gov")]
    public void NonStorageDependency_IsKept(string target)
    {
        var (processor, next) = Build();
        var dep = Dep(target, success: true);
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    [Fact]
    public void StorageEndpointInDataField_IsDropped()
    {
        // Some instrumentation modes put the endpoint in Data rather than Target.
        var (processor, next) = Build();
        processor.Process(Dep(target: string.Empty, success: true, data: "GET https://myacct.blob.core.windows.net/container/x"));
        Assert.Empty(next.Received);
    }

    // Azure SDK ActivitySource shape: Type = "InProc | {az.namespace}", Target/Data carry only
    // the SDK operation name — no endpoint host. Live-verified as ~80% of billed dependency rows.
    [Theory]
    [InlineData("InProc | Microsoft.Tables", "TableClient.GetEntity")]
    [InlineData("InProc | Microsoft.Tables", "TableClient.SubmitTransaction")]
    [InlineData("InProc | Microsoft.Tables", "TableServiceClient.CreateTableIfNotExists")]
    [InlineData("InProc | Microsoft.Storage", "QueueClient.ReceiveMessages")]
    [InlineData("InProc | Microsoft.Storage", "QueueClient.SendMessage")]
    [InlineData("InProc | Microsoft.Storage", "BlobClient.Upload")]
    public void SuccessfulInProcStorageDependency_IsDropped(string type, string operationName)
    {
        var (processor, next) = Build();
        processor.Process(Dep(target: operationName, success: true, type: type));
        Assert.Empty(next.Received);
    }

    [Fact]
    public void SuccessfulInProcStorageDependency_WithNullSuccess_IsDropped()
    {
        var (processor, next) = Build();
        processor.Process(Dep(target: "TableClient.Query", success: null, type: "InProc | Microsoft.Tables"));
        Assert.Empty(next.Received);
    }

    [Fact]
    public void FailedInProcStorageDependency_IsKept()
    {
        var (processor, next) = Build();
        var dep = Dep(target: "TableClient.GetEntity", success: false, type: "InProc | Microsoft.Tables");
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    [Theory]
    [InlineData("InProc | Microsoft.AAD", "DefaultAzureCredential.GetToken")]
    [InlineData("InProc | Microsoft.Insights", "MetricsQueryClient.QueryResource")]
    [InlineData("InProc", "Invoke")] // the worker's own invocation span
    public void NonStorageInProcDependency_IsKept(string type, string operationName)
    {
        var (processor, next) = Build();
        var dep = Dep(target: operationName, success: true, type: type);
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    [Fact]
    public void NonDependencyTelemetry_IsAlwaysKept()
    {
        var (processor, next) = Build();
        var request = new RequestTelemetry { Name = "POST /api/agent/telemetry", Success = true };
        var trace = new TraceTelemetry { Message = "hello" };
        processor.Process(request);
        processor.Process(trace);
        Assert.Equal(2, next.Received.Count);
    }
}
