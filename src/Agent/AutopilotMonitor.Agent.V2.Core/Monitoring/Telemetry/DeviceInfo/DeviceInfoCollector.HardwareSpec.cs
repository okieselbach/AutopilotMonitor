using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo
{
    /// <summary>
    /// Partial: Static hardware specifications (CPU, RAM, Disk, BIOS, Battery, GPU).
    /// Collected once at agent startup — these values do not change during enrollment.
    /// </summary>
    public partial class DeviceInfoCollector
    {
        private void CollectHardwareSpec()
        {
            var data = new Dictionary<string, object>();

            try
            {
                CollectSystemInfo(data);
                CollectCpuInfo(data);
                CollectMemoryInfo(data);
                CollectDiskInfo(data);
                CollectBiosInfo(data);
                CollectBatteryInfo(data);
                CollectGpuInfo(data);

                var message = BuildHardwareSpecMessage(data);
                EmitDeviceInfoEvent(Constants.EventTypes.HardwareSpec, message, data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect hardware spec: {ex.Message}");
            }
        }

        /// <summary>
        /// Emits system manufacturer/model + a coarse virtual-machine flag onto the
        /// hardware_spec event. Reuses <see cref="DeviceInfoProvider"/> so Lenovo's
        /// quirk (model lives in Win32_ComputerSystemProduct.Version, not
        /// Win32_ComputerSystem.Model) and the VM detection allowlist stay in one place
        /// shared with SessionRegistration / DiagnosticsPackage / GatherRulesMode.
        /// </summary>
        private void CollectSystemInfo(Dictionary<string, object> data)
        {
            try
            {
                var manufacturer = DeviceInfoProvider.GetManufacturer();
                var model = DeviceInfoProvider.GetModel();
                data["systemManufacturer"] = manufacturer;
                data["systemModel"] = model;
                data["isVirtualMachine"] = DeviceInfoProvider.IsVirtualMachine(manufacturer, model);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect system info: {ex.Message}");
            }
        }

        private void CollectCpuInfo(Dictionary<string, object> data)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Architecture, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        data["cpuName"] = obj["Name"]?.ToString()?.Trim();

                        // CPU/device architecture (x86, x64, ARM, ARM64). The WMI service
                        // runs as a 64-bit host, so Win32_Processor.Architecture reports the
                        // true hardware architecture even though this net48 agent runs as a
                        // 32-bit process under ARM64 emulation -- unlike the PROCESSOR_ARCHITECTURE
                        // environment variable, which would report the emulated (x86) value.
                        if (obj["Architecture"] != null)
                            data["cpuArchitecture"] = MapProcessorArchitecture(Convert.ToInt32(obj["Architecture"]));

                        if (obj["NumberOfCores"] != null)
                            data["cpuCores"] = Convert.ToInt32(obj["NumberOfCores"]);

                        if (obj["NumberOfLogicalProcessors"] != null)
                            data["cpuLogicalProcessors"] = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);

                        if (obj["MaxClockSpeed"] != null)
                        {
                            var mhz = Convert.ToInt32(obj["MaxClockSpeed"]);
                            data["cpuMaxClockSpeedGHz"] = Math.Round(mhz / 1000.0, 2);
                        }

                        break; // first processor only
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect CPU info: {ex.Message}");
            }
        }

        private void CollectMemoryInfo(Dictionary<string, object> data)
        {
            try
            {
                // Total physical memory from Win32_ComputerSystem
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["TotalPhysicalMemory"] != null)
                        {
                            var bytes = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                            data["ramTotalGB"] = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 1);
                        }
                        break;
                    }
                }

                // DIMM details from Win32_PhysicalMemory
                var dimms = new List<Dictionary<string, object>>();
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Capacity, Speed, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var dimm = new Dictionary<string, object>();

                        if (obj["Capacity"] != null)
                        {
                            var bytes = Convert.ToInt64(obj["Capacity"]);
                            dimm["capacityGB"] = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 1);
                        }

                        if (obj["Speed"] != null)
                            dimm["speed"] = Convert.ToInt32(obj["Speed"]);

                        if (obj["SMBIOSMemoryType"] != null)
                            dimm["type"] = MapMemoryType(Convert.ToInt32(obj["SMBIOSMemoryType"]));

                        if (obj["FormFactor"] != null)
                            dimm["formFactor"] = MapFormFactor(Convert.ToInt32(obj["FormFactor"]));

                        dimms.Add(dimm);
                    }
                }

                data["ramDimmCount"] = dimms.Count;

                if (dimms.Count > 0)
                {
                    var first = dimms[0];
                    if (first.ContainsKey("speed"))
                        data["ramSpeed"] = first["speed"];
                    if (first.ContainsKey("type"))
                        data["ramType"] = first["type"];
                    if (first.ContainsKey("formFactor"))
                        data["ramFormFactor"] = first["formFactor"];
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect memory info: {ex.Message}");
            }
        }

        private void CollectDiskInfo(Dictionary<string, object> data)
        {
            try
            {
                var disks = new List<Dictionary<string, object>>();
                bool collected = false;

                // Primary: MSFT_PhysicalDisk from Storage namespace (better SSD/NVMe detection)
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        new ManagementScope(@"\\.\root\Microsoft\Windows\Storage"),
                        new ObjectQuery("SELECT FriendlyName, MediaType, Size, BusType FROM MSFT_PhysicalDisk")))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var disk = new Dictionary<string, object>();

                            disk["model"] = obj["FriendlyName"]?.ToString()?.Trim();

                            if (obj["Size"] != null)
                            {
                                var bytes = Convert.ToInt64(obj["Size"]);
                                disk["sizeGB"] = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 0);
                            }

                            var mediaType = obj["MediaType"] != null ? Convert.ToInt32(obj["MediaType"]) : 0;
                            var busType = obj["BusType"] != null ? Convert.ToInt32(obj["BusType"]) : 0;
                            disk["mediaType"] = MapDiskMediaType(mediaType, busType);
                            disk["busType"] = MapDiskBusType(busType);

                            disks.Add(disk);
                        }
                        collected = true;
                    }
                }
                catch
                {
                    // Storage namespace not available — fall back to Win32_DiskDrive
                }

                // Fallback: Win32_DiskDrive
                if (!collected)
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var disk = new Dictionary<string, object>();

                            disk["model"] = obj["Model"]?.ToString()?.Trim();

                            if (obj["Size"] != null)
                            {
                                var bytes = Convert.ToInt64(obj["Size"]);
                                disk["sizeGB"] = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 0);
                            }

                            disk["mediaType"] = obj["MediaType"]?.ToString() ?? "Unknown";
                            disk["busType"] = obj["InterfaceType"]?.ToString() ?? "Unknown";

                            disks.Add(disk);
                        }
                    }
                }

                data["disks"] = disks;
                data["diskCount"] = disks.Count;

                // System drive free space
                try
                {
                    var systemDrive = new DriveInfo("C");
                    if (systemDrive.IsReady)
                    {
                        data["systemDriveFreeGB"] = Math.Round(systemDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                        data["systemDriveTotalGB"] = Math.Round(systemDrive.TotalSize / (1024.0 * 1024.0 * 1024.0), 0);
                    }
                }
                catch { /* C: drive info not available */ }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect disk info: {ex.Message}");
            }
        }

        private void CollectBiosInfo(Dictionary<string, object> data)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        data["biosVersion"] = obj["SMBIOSBIOSVersion"]?.ToString();

                        if (obj["ReleaseDate"] != null)
                        {
                            var wmiDate = obj["ReleaseDate"].ToString();
                            data["biosReleaseDate"] = ParseWmiDateTime(wmiDate);
                        }

                        var major = obj["SMBIOSMajorVersion"]?.ToString();
                        var minor = obj["SMBIOSMinorVersion"]?.ToString();
                        if (major != null && minor != null)
                            data["smbiosVersion"] = $"{major}.{minor}";

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect BIOS info: {ex.Message}");
            }
        }

        private void CollectBatteryInfo(Dictionary<string, object> data)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT DesignCapacity, FullChargeCapacity, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery"))
                {
                    var results = searcher.Get();
                    bool found = false;

                    foreach (ManagementObject obj in results)
                    {
                        found = true;
                        data["batteryPresent"] = true;

                        var designCapacity = obj["DesignCapacity"] != null ? Convert.ToInt32(obj["DesignCapacity"]) : 0;
                        var fullChargeCapacity = obj["FullChargeCapacity"] != null ? Convert.ToInt32(obj["FullChargeCapacity"]) : 0;

                        if (designCapacity > 0)
                            data["batteryDesignCapacityMWh"] = designCapacity;

                        if (fullChargeCapacity > 0)
                            data["batteryFullChargeCapacityMWh"] = fullChargeCapacity;

                        if (designCapacity > 0 && fullChargeCapacity > 0)
                            data["batteryHealthPercent"] = (int)Math.Round(fullChargeCapacity * 100.0 / designCapacity);

                        if (obj["EstimatedChargeRemaining"] != null)
                            data["batteryChargePercent"] = Convert.ToInt32(obj["EstimatedChargeRemaining"]);

                        if (obj["BatteryStatus"] != null)
                            data["batteryStatus"] = MapBatteryStatus(Convert.ToInt32(obj["BatteryStatus"]));

                        break; // first battery only
                    }

                    if (!found)
                        data["batteryPresent"] = false;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect battery info: {ex.Message}");
            }
        }

        private void CollectGpuInfo(Dictionary<string, object> data)
        {
            try
            {
                var gpus = new List<Dictionary<string, object>>();

                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var gpu = new Dictionary<string, object>();

                        gpu["name"] = obj["Name"]?.ToString()?.Trim();

                        if (obj["AdapterRAM"] != null)
                        {
                            var bytes = Convert.ToUInt32(obj["AdapterRAM"]);
                            // WMI AdapterRAM is uint32 — caps at ~4GB. If value equals uint.MaxValue, it's unreliable.
                            if (bytes > 0 && bytes < uint.MaxValue)
                                gpu["adapterRAMGB"] = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 1);
                        }

                        gpu["driverVersion"] = obj["DriverVersion"]?.ToString();

                        gpus.Add(gpu);
                    }
                }

                data["gpus"] = gpus;
                data["gpuCount"] = gpus.Count;
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect GPU info: {ex.Message}");
            }
        }

        #region Hardware Spec Helpers

        private static string BuildHardwareSpecMessage(Dictionary<string, object> data)
        {
            var parts = new List<string>();

            if (data.ContainsKey("cpuName"))
                parts.Add(data["cpuName"].ToString());

            if (data.ContainsKey("ramTotalGB"))
            {
                var ram = $"{data["ramTotalGB"]} GB";
                if (data.ContainsKey("ramType"))
                    ram += $" {data["ramType"]}";
                parts.Add(ram + " RAM");
            }

            if (data.ContainsKey("disks") && data["disks"] is List<Dictionary<string, object>> disks && disks.Count > 0)
            {
                // Prefer non-USB disk for the summary message (USB drives are often boot media, not the actual system disk)
                var d = disks.FirstOrDefault(dk => dk.ContainsKey("busType") && !string.Equals(dk["busType"]?.ToString(), "USB", StringComparison.OrdinalIgnoreCase))
                     ?? disks[0];
                var diskDesc = d.ContainsKey("sizeGB") ? $"{d["sizeGB"]} GB" : "";
                if (d.ContainsKey("mediaType"))
                    diskDesc += $" {d["mediaType"]}";
                parts.Add(diskDesc.Trim());
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "Hardware spec collected";
        }

        internal static string MapProcessorArchitecture(int architecture)
        {
            // Win32_Processor.Architecture (== PROCESSOR_ARCHITECTURE):
            // 0=x86, 5=ARM, 6=ia64, 9=x64, 12=ARM64. See Microsoft docs for Win32_Processor.
            switch (architecture)
            {
                case 0: return "x86";
                case 5: return "ARM";
                case 6: return "ia64";
                case 9: return "x64";
                case 12: return "ARM64";
                default: return "Unknown";
            }
        }

        private static string MapMemoryType(int smbiosType)
        {
            switch (smbiosType)
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 34: return "DDR5";
                default: return smbiosType > 0 ? $"Type {smbiosType}" : "Unknown";
            }
        }

        private static string MapFormFactor(int formFactor)
        {
            switch (formFactor)
            {
                case 8: return "DIMM";
                case 12: return "SODIMM";
                default: return formFactor > 0 ? $"FormFactor {formFactor}" : "Unknown";
            }
        }

        private static string MapDiskMediaType(int mediaType, int busType)
        {
            // MSFT_PhysicalDisk MediaType: 3=HDD, 4=SSD, 5=SCM
            // BusType: 17=NVMe
            switch (mediaType)
            {
                case 3: return "HDD";
                case 4: return busType == 17 ? "NVMe SSD" : "SSD";
                case 5: return "SCM";
                default: return "Unknown";
            }
        }

        private static string MapDiskBusType(int busType)
        {
            switch (busType)
            {
                case 3: return "ATA";
                case 7: return "USB";
                case 11: return "SATA";
                case 17: return "NVMe";
                default: return busType > 0 ? $"BusType {busType}" : "Unknown";
            }
        }

        private static string MapBatteryStatus(int status)
        {
            switch (status)
            {
                case 1: return "Discharging";
                case 2: return "AC Power";
                case 3: return "Fully Charged";
                case 4: return "Low";
                case 5: return "Critical";
                case 6: return "Charging";
                case 7: return "Charging High";
                case 8: return "Charging Low";
                case 9: return "Charging Critical";
                default: return $"Unknown ({status})";
            }
        }

        private static string ParseWmiDateTime(string wmiDate)
        {
            // WMI datetime format: yyyyMMddHHmmss.ffffff+zzz (e.g., "20240115000000.000000+000")
            if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8)
                return wmiDate;

            try
            {
                var year = wmiDate.Substring(0, 4);
                var month = wmiDate.Substring(4, 2);
                var day = wmiDate.Substring(6, 2);
                return $"{year}-{month}-{day}";
            }
            catch
            {
                return wmiDate;
            }
        }

        #endregion
    }
}
