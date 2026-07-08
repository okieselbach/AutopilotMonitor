using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.DeviceInfo;

/// <summary>
/// Pins the VM-detection allowlist used by the rule engine's preconditions feature
/// (see ANALYZE-SEC-001 "skip on virtual machines"). Conservative-bias: false negatives
/// (a misclassified physical box → rule still fires) are preferred over false positives
/// (a misclassified VM → rule silently skipped, user-visible regression).
/// </summary>
public class DeviceInfoProviderVmDetectionTests
{
    [Theory]
    // ===== Hyper-V =====
    // Hyper-V VMs (incl. Cloud PC, Azure Virtual Desktop) report manufacturer
    // "Microsoft Corporation" + model "Virtual Machine". The model string is what
    // disambiguates them from physical Surface devices that share the manufacturer.
    [InlineData("Microsoft Corporation", "Virtual Machine", true)]
    [InlineData("microsoft corporation", "virtual machine", true)]   // case-insensitive
    // ===== Surface (the tricky physical case sharing manufacturer with Hyper-V) =====
    [InlineData("Microsoft Corporation", "Surface Laptop 5", false)]
    [InlineData("Microsoft Corporation", "Surface Pro 9", false)]
    [InlineData("Microsoft Corporation", "Surface Book 3", false)]
    // ===== VMware =====
    [InlineData("VMware, Inc.", "VMware Virtual Platform", true)]
    [InlineData("Other Vendor", "VMware Virtual Platform", true)]    // detected by model (defensive)
    // ===== VirtualBox =====
    [InlineData("innotek GmbH", "VirtualBox", true)]
    [InlineData("Other Vendor", "VirtualBox", true)]                 // detected by model (defensive)
    // ===== Other hypervisors =====
    [InlineData("Xen", "HVM domU", true)]
    [InlineData("QEMU", "Standard PC", true)]
    [InlineData("Parallels Software International Inc.", "Parallels Virtual Platform", true)]
    [InlineData("Red Hat", "KVM", true)]
    // ===== Common physical OEMs (must stay false) =====
    [InlineData("LENOVO", "20XW00JEMZ", false)]
    [InlineData("Dell Inc.", "Latitude 7430", false)]
    [InlineData("HP", "EliteBook 840 G9", false)]
    // ===== Edge cases =====
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void IsVirtualMachine_classifies(string? manufacturer, string? model, bool expected) =>
        Assert.Equal(expected, DeviceInfoProvider.IsVirtualMachine(manufacturer!, model!));
}
