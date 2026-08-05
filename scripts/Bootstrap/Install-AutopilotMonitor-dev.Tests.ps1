# Pester tests (Pester 5+) for the bootstrap guard/relax logic in
# Install-AutopilotMonitor-dev.ps1 (v2.3-dev).
#
# The script under test is dot-sourced; its entry guard prevents the bootstrap
# from executing, so loading it here is side-effect free. External state (WMI,
# registry, filesystem, WinRT) is mocked; profile directories are real folders
# under Pester's $TestDrive with a manipulated CreationTimeUtc.
#
# Run under Windows PowerShell 5.1 to match the IME runtime:
#   powershell.exe -NoProfile -Command "& { $c = New-PesterConfiguration; $c.Run.Path = 'scripts\Bootstrap'; $c.Output.Verbosity = 'Detailed'; Invoke-Pester -Configuration $c }"
#
# This file MUST remain pure ASCII (PS 5.1 reads BOM-less files as ANSI).

BeforeAll {
    . (Join-Path $PSScriptRoot 'Install-AutopilotMonitor-dev.ps1')

    function New-FakeProfile {
        param([string]$Name, [double]$AgeMinutes)
        $p = Join-Path $TestDrive $Name
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        (Get-Item $p).CreationTimeUtc = (Get-Date).ToUniversalTime().AddMinutes(-$AgeMinutes)
        return $p
    }
}

Describe 'Get-CloudPcModel' {
    It 'returns the model string for a Cloud PC' {
        Mock Get-CimInstance { [pscustomobject]@{ Manufacturer = 'Microsoft Corporation'; Model = 'Cloud PC Enterprise 2vCPU/8GB/128GB' } }
        Get-CloudPcModel | Should -Be 'Cloud PC Enterprise 2vCPU/8GB/128GB'
    }

    It 'returns null for a physical device' {
        Mock Get-CimInstance { [pscustomobject]@{ Manufacturer = 'Dell Inc.'; Model = 'Latitude 7450' } }
        Get-CloudPcModel | Should -BeNullOrEmpty
    }

    It 'returns null for a non-Microsoft device whose model happens to match' {
        Mock Get-CimInstance { [pscustomobject]@{ Manufacturer = 'Contoso Ltd.'; Model = 'Cloud PC Clone' } }
        Get-CloudPcModel | Should -BeNullOrEmpty
    }

    It 'returns null when the WMI query fails (SKIP-safe)' {
        Mock Get-CimInstance { throw 'RPC server unavailable' }
        Get-CloudPcModel | Should -BeNullOrEmpty
    }
}

Describe 'Get-OobeState' {
    It 'returns a non-empty state string and never throws' {
        Get-OobeState | Should -Not -BeNullOrEmpty
    }
}

Describe 'Get-RealUserProfilePaths' {
    BeforeEach {
        Mock Write-Log { }
    }

    It 'merges WMI and filesystem views, drops special/system profiles, dedupes' {
        Mock Get-CimInstance {
            @(
                [pscustomobject]@{ Special = $false; LocalPath = 'C:\Users\LeonGottschalk' }
                [pscustomobject]@{ Special = $true; LocalPath = 'C:\Users\systemprofile' }
                [pscustomobject]@{ Special = $false; LocalPath = 'D:\Profiles\Elsewhere' }
            )
        }
        Mock Get-ChildItem {
            @(
                [pscustomobject]@{ FullName = 'C:\Users\LeonGottschalk' }
                [pscustomobject]@{ FullName = 'C:\Users\Public' }
                [pscustomobject]@{ FullName = 'C:\Users\defaultuser0' }
                [pscustomobject]@{ FullName = 'C:\Users\WDAGUtilityAccount' }
            )
        }
        $result = @(Get-RealUserProfilePaths)
        $result | Should -Be @('C:\Users\LeonGottschalk')
    }

    It 'falls back to the filesystem view when the WMI query fails' {
        Mock Get-CimInstance { throw 'WMI broken' }
        Mock Get-ChildItem {
            @(
                [pscustomobject]@{ FullName = 'C:\Users\SomeUser' }
                [pscustomobject]@{ FullName = 'C:\Users\Default' }
            )
        }
        $result = @(Get-RealUserProfilePaths)
        $result | Should -Be @('C:\Users\SomeUser')
    }

    It 'returns an empty result when only special/system profiles exist' {
        Mock Get-CimInstance { @() }
        Mock Get-ChildItem {
            @(
                [pscustomobject]@{ FullName = 'C:\Users\Public' }
                [pscustomobject]@{ FullName = 'C:\Users\defaultuser1' }
            )
        }
        @(Get-RealUserProfilePaths).Count | Should -Be 0
    }
}

Describe 'Get-RelaxDecision' {
    BeforeEach {
        Mock Write-Log { }
        # Defaults: physical device, OOBE long done. Individual tests override.
        Mock Get-OobeState { 'Completed' }
        Mock Get-CloudPcModel { $null }
    }

    Context 'Windows 365 Cloud PC' {
        BeforeEach {
            Mock Get-CloudPcModel { 'Cloud PC Enterprise 2vCPU/8GB/128GB' }
        }

        It 'relaxes for a single fresh profile although OOBE is Completed (W365 field case)' {
            $p = New-FakeProfile 'LeonGottschalk' 0.6
            $d = Get-RelaxDecision -ProfilePaths @($p) -OobeProfileMaxAgeMinutes 15
            $d.Active | Should -BeTrue
            $d.CloudPcModel | Should -Be 'Cloud PC Enterprise 2vCPU/8GB/128GB'
        }

        It 'does not relax for an old profile (productive Cloud PC)' {
            $p = New-FakeProfile 'LeonGottschalk' 30
            (Get-RelaxDecision -ProfilePaths @($p) -OobeProfileMaxAgeMinutes 15).Active | Should -BeFalse
        }

        It 'does not relax for two profiles' {
            $p1 = New-FakeProfile 'UserA' 2
            $p2 = New-FakeProfile 'UserB' 400
            (Get-RelaxDecision -ProfilePaths @($p1, $p2) -OobeProfileMaxAgeMinutes 15).Active | Should -BeFalse
        }

        It 'relaxes even when the WinRT OOBE API is unavailable (old build)' {
            Mock Get-OobeState { 'Unavailable' }
            $p = New-FakeProfile 'CpcUser' 1
            (Get-RelaxDecision -ProfilePaths @($p) -OobeProfileMaxAgeMinutes 15).Active | Should -BeTrue
        }

        It 'does not relax for a profile with a future creation time (clock skew)' {
            $p = New-FakeProfile 'SkewUser' -10
            (Get-RelaxDecision -ProfilePaths @($p) -OobeProfileMaxAgeMinutes 15).Active | Should -BeFalse
        }
    }

    Context 'OOBE InProgress (Windows Backup for Organizations, v2.2 behaviour)' {
        It 'relaxes for a single fresh profile during OOBE' {
            Mock Get-OobeState { 'InProgress' }
            $p = New-FakeProfile 'RestoreUser' 3
            $d = Get-RelaxDecision -ProfilePaths @($p) -OobeProfileMaxAgeMinutes 15
            $d.Active | Should -BeTrue
        }
    }

    Context 'no trigger' {
        It 'does not relax on a physical device with a fresh profile and OOBE Completed' {
            $p = New-FakeProfile 'SomeUser' 2
            (Get-RelaxDecision -ProfilePaths @($p) -OobeProfileMaxAgeMinutes 15).Active | Should -BeFalse
        }

        It 'stays inactive with no profiles at all' {
            (Get-RelaxDecision -ProfilePaths @() -OobeProfileMaxAgeMinutes 15).Active | Should -BeFalse
        }
    }
}

Describe 'Get-BootstrapDecision' {
    BeforeEach {
        Mock Write-Log { }
        # Baseline: clean device during Device ESP -- no marker, no profiles, no
        # logged-on user, 1h uptime, agent not installed. Tests override selectively.
        Mock Get-ItemProperty { $null }
        Mock Get-RealUserProfilePaths { @() }
        Mock Get-OobeState { 'Completed' }
        Mock Get-CloudPcModel { $null }
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-1) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
        $script:cleanAgentBin = Join-Path $TestDrive 'AgentBin'
    }

    It 'installs on a clean ESP device' {
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeTrue
        $d.ReasonCode | Should -BeNullOrEmpty
        $d.RelaxActive | Should -BeFalse
    }

    It 'skips when the Deployed marker exists (guard 1)' {
        Mock Get-ItemProperty { [pscustomobject]@{ Deployed = '2026-08-01T10:00:00Z' } } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\AutopilotMonitor' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'AlreadyDeployed'
    }

    It 'skips a productive device with an old profile (guard 2)' {
        $script:fakeProfile = New-FakeProfile 'OldUser' 5000
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'ProfilesFound'
    }

    It 'installs on a freshly provisioned Cloud PC with one fresh profile (W365 field case)' {
        $script:fakeProfile = New-FakeProfile 'LeonGottschalk' 0.6
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        Mock Get-CloudPcModel { 'Cloud PC Enterprise 2vCPU/8GB/128GB' }
        # First-connect user is already visible in LogonUI on W365 -- must not block either.
        Mock Get-ItemProperty { [pscustomobject]@{ LastLoggedOnUser = 'AzureAD\LeonGottschalk' } } -ParameterFilter { $Path -like '*LogonUI*' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeTrue
        $d.RelaxActive | Should -BeTrue
    }

    It 'skips when a real user has logged on and no relax applies (guard 3)' {
        Mock Get-ItemProperty { [pscustomobject]@{ LastLoggedOnUser = 'AzureAD\SomeUser' } } -ParameterFilter { $Path -like '*LogonUI*' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'LastLoggedOnUser'
    }

    It 'does not skip for the defaultuser0 OOBE account (guard 3)' {
        Mock Get-ItemProperty { [pscustomobject]@{ LastLoggedOnUser = 'defaultuser0' } } -ParameterFilter { $Path -like '*LogonUI*' }
        (Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin).Install | Should -BeTrue
    }

    It 'skips when uptime exceeds the bootstrap window (guard 4)' {
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-20) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'UptimeExceeded'
    }

    It 'skips when the agent binary is already present (guard 5)' {
        $binPath = Join-Path $TestDrive 'AgentBinExisting'
        New-Item -ItemType Directory -Path $binPath -Force | Out-Null
        Set-Content -Path (Join-Path $binPath 'AutopilotMonitor.Agent.exe') -Value 'stub'
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $binPath
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'AgentPresent'
    }
}

Describe 'logging purity' {
    # Regression guard: Write-Log runs inside functions that RETURN decision
    # objects. If it ever emits to the output stream again (Write-Output), the
    # log lines leak into the return values and turn them into arrays. This
    # test deliberately does NOT mock Write-Log.
    It 'Get-BootstrapDecision returns exactly one object with real logging' {
        Mock Get-ItemProperty { [pscustomobject]@{ Deployed = '2026-08-01T10:00:00Z' } } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\AutopilotMonitor' }
        # Plain local assignment: PowerShell's dynamic scoping makes Write-Log
        # (called from Get-BootstrapDecision) resolve this $LogFile instead of
        # the ProgramData path defined by the dot-sourced script.
        $LogFile = Join-Path $TestDrive 'bootstrap_test.log'
        $d = @(Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath (Join-Path $TestDrive 'NoAgent'))
        $d.Count | Should -Be 1
        $d[0].ReasonCode | Should -Be 'AlreadyDeployed'
        Get-Content $LogFile -Raw | Should -Match 'SKIP: Agent was previously deployed'
    }
}
