#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// WindowsUpdateClassifier (D-310) — decides which Windows Update client events are listed one
    /// by one (OS updates) and which are only counted (Store, Defender, everything else). The
    /// titles are the shapes the WU client writes on Windows 11 GA and Insider builds.
    /// </summary>
    public sealed class WindowsUpdateClassifierTests
    {
        [Theory]
        [InlineData("2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5099999)")]
        [InlineData("2026-09 Cumulative Update Preview for Windows 11, version 24H2 for x64-based Systems (KB5099998)")]
        [InlineData("2026-09 Dynamic Update for Windows 11, version 25H2 for x64-based Systems (KB5099997)")]
        [InlineData("2026-09 Servicing Stack Update for Windows 11, version 25H2 for x64-based Systems (KB5099996)")]
        [InlineData("Windows 11 Insider Preview Quality Update (26220.9568)")]
        [InlineData("Windows 11 Insider Preview Feature Update (26220.9022)")]
        [InlineData("Windows 11, version 25H2 Upgrade")]
        [InlineData("2026-09 .NET Framework Security Update (KB5099995)")]
        [InlineData("2026-08 .NET Framework Preview Update (KB5099994)")]
        [InlineData("Windows Malicious Software Removal Tool x64 - v5.145 (KB890830)")]
        public void WindowsAndDotNetFrameworkUpdates_AreOs(string title)
        {
            Assert.Equal(WindowsUpdateClassifier.Os, WindowsUpdateClassifier.Classify(title, serviceGuid: null));
        }

        [Theory]
        [InlineData("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.459.505.0) - Current Channel (Broad)")]
        [InlineData("Update for Microsoft Defender Antivirus antimalware platform - KB4052623 (Version 4.18.26080.4) - Current Channel (Staged)")]
        // Names "Windows" and "Update" — Defender must still win.
        [InlineData("Windows 10/11 - Update for Microsoft Defender for Endpoint - KB5005292 (Version 10.8838.26060.15013) - Current Channel (Staged)")]
        public void DefenderUpdates_AreDefender(string title)
        {
            Assert.Equal(WindowsUpdateClassifier.Defender, WindowsUpdateClassifier.Classify(title, serviceGuid: null));
        }

        [Theory]
        [InlineData("9NSTH9KHZDLQ-Microsoft.UI.Xaml.2.8")]
        [InlineData("9NBLGGH4NNS1-Microsoft.DesktopAppInstaller")]
        // Upper-case "WINDOWS" inside the package name must not make it an OS update.
        [InlineData("9WZDNCRFHVN5-MICROSOFT.WINDOWSCALCULATOR")]
        [InlineData("9WZDNCRFHWKN-MICROSOFT.WINDOWSSOUNDRECORDER")]
        [InlineData("ApplicationSet-9WZDNCRFJ3Q2-Microsoft.BingWeather")]
        public void StoreProductTitles_AreStore(string title)
        {
            Assert.Equal(WindowsUpdateClassifier.Store, WindowsUpdateClassifier.Classify(title, serviceGuid: null));
        }

        [Theory]
        [InlineData("{855e8a7c-ecb4-4ca3-b045-1dfa50104289}")]
        [InlineData("{855E8A7C-ECB4-4CA3-B045-1DFA50104289}")]
        [InlineData("855e8a7c-ecb4-4ca3-b045-1dfa50104289")]
        public void StoreServiceGuid_IsStore_WhateverTheTitle(string serviceGuid)
        {
            Assert.Equal(WindowsUpdateClassifier.Store,
                WindowsUpdateClassifier.Classify("2026-09 Cumulative Update for Windows 11 (KB5099999)", serviceGuid));
        }

        [Fact]
        public void OtherServiceGuid_LeavesTheTitleInCharge()
        {
            Assert.Equal(WindowsUpdateClassifier.Os,
                WindowsUpdateClassifier.Classify("2026-09 Cumulative Update for Windows 11 (KB5099999)", "{7971f918-a847-4430-9279-4a52d1efe18d}"));
        }

        [Theory]
        [InlineData("Intel net Driver Update (24.50.0.4)")]
        [InlineData("PowerShell v7.6.6 (x64)")]
        [InlineData("Remote help 5.2.1040.0")]
        [InlineData("Microsoft Edge-Stable Channel Version 140 Update for x64 based Edge-Chromium (Build 140.0.3485.54)")]
        [InlineData("Microsoft .NET 8.0.20 Security Update for x64 Client (KB5099993)")]
        [InlineData("Windows Feature Experience Pack 1000.26100.234.0")]
        [InlineData("KB5099999")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void EverythingElse_IsOther(string? title)
        {
            Assert.Equal(WindowsUpdateClassifier.Other, WindowsUpdateClassifier.Classify(title!, serviceGuid: null));
        }
    }
}
