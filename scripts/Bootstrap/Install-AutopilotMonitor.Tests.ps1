# Pester tests (Pester 5+) for the bootstrap guard/relax logic in
# Install-AutopilotMonitor.ps1.
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
    . (Join-Path $PSScriptRoot 'Install-AutopilotMonitor.ps1')

    function New-FakeSignature {
        param([string]$Status, [string]$Subject)
        $cert = if ($Subject) {
            [pscustomobject]@{ Subject = $Subject; Thumbprint = '0123456789ABCDEF' }
        } else { $null }
        return [pscustomobject]@{ Status = $Status; SignerCertificate = $cert }
    }

    function New-FakePayload {
        param([string[]]$Files)
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        foreach ($f in $Files) { Set-Content -Path (Join-Path $dir $f) -Value 'x' }
        return $dir
    }

    function New-FakeProfile {
        param([string]$Name, [double]$AgeMinutes)
        $p = Join-Path $TestDrive $Name
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        (Get-Item $p).CreationTimeUtc = (Get-Date).ToUniversalTime().AddMinutes(-$AgeMinutes)
        return $p
    }
}

Describe 'Test-IsCloudPc' {
    # Local WMI on a Cloud PC is generic ('Virtual Machine'), so detection rests on
    # the Windows365 registry key AND the CloudManagedDesktopExtension service
    # (both required).
    BeforeEach {
        Mock Write-Log { }
        Mock Get-CimInstance { [pscustomobject]@{ Manufacturer = 'Microsoft Corporation'; Model = 'Virtual Machine' } }
    }

    It 'detects a Cloud PC when both markers are present' {
        Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { [pscustomobject]@{ Name = 'CloudManagedDesktopExtension'; Status = 'Running' } }
        Test-IsCloudPc | Should -BeTrue
    }

    It 'does not detect with the registry key alone (e.g. W365-Boot physical client)' {
        Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { $null }
        Test-IsCloudPc | Should -BeFalse
    }

    It 'does not detect with the service alone' {
        Mock Test-Path { $false } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { [pscustomobject]@{ Name = 'CloudManagedDesktopExtension'; Status = 'Running' } }
        Test-IsCloudPc | Should -BeFalse
    }

    It 'does not detect a plain device (no markers)' {
        Mock Test-Path { $false } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { $null }
        Test-IsCloudPc | Should -BeFalse
    }

    It 'still evaluates markers when the WMI identity query fails' {
        Mock Get-CimInstance { throw 'RPC server unavailable' }
        Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { [pscustomobject]@{ Name = 'CloudManagedDesktopExtension'; Status = 'Running' } }
        Test-IsCloudPc | Should -BeTrue
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
                [pscustomobject]@{ Special = $false; LocalPath = 'C:\Users\UserOne' }
                [pscustomobject]@{ Special = $true; LocalPath = 'C:\Users\systemprofile' }
                [pscustomobject]@{ Special = $false; LocalPath = 'D:\Profiles\Elsewhere' }
            )
        }
        Mock Get-ChildItem {
            @(
                [pscustomobject]@{ FullName = 'C:\Users\UserOne' }
                [pscustomobject]@{ FullName = 'C:\Users\Public' }
                [pscustomobject]@{ FullName = 'C:\Users\defaultuser0' }
                [pscustomobject]@{ FullName = 'C:\Users\WDAGUtilityAccount' }
            )
        }
        $result = @(Get-RealUserProfilePaths)
        $result | Should -Be @('C:\Users\UserOne')
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
        Mock Test-IsCloudPc { $false }
    }

    Context 'Windows 365 Cloud PC' {
        BeforeEach {
            Mock Test-IsCloudPc { $true }
        }

        It 'relaxes for a single fresh profile although OOBE is Completed (first connect)' {
            $p = New-FakeProfile 'CloudPcUser' 0.6
            $d = Get-RelaxDecision -ProfilePaths @($p) -OobeProfileMaxAgeMinutes 15
            $d.Active | Should -BeTrue
            $d.IsCloudPc | Should -BeTrue
        }

        It 'does not relax for an old profile (productive Cloud PC)' {
            $p = New-FakeProfile 'CloudPcUser' 30
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

    Context 'OOBE InProgress (Windows Backup for Organizations)' {
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
        Mock Test-IsCloudPc { $false }
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

    It 'installs on a freshly provisioned Cloud PC with one fresh profile' {
        $script:fakeProfile = New-FakeProfile 'CloudPcUser' 0.6
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        Mock Test-IsCloudPc { $true }
        # First-connect user is already visible in LogonUI on W365 -- must not block either.
        Mock Get-ItemProperty { [pscustomobject]@{ LastLoggedOnUser = 'AzureAD\CloudPcUser' } } -ParameterFilter { $Path -like '*LogonUI*' }
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

    It 'installs on a Cloud PC despite uptime over the window when the relax is active (guard 4 exemption)' {
        # W365 reality: provisioned days ago, running headless ever since; the user's
        # first connect just created the profile. Uptime is meaningless here.
        $script:fakeProfile = New-FakeProfile 'FirstConnectUser' 0.6
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        Mock Test-IsCloudPc { $true }
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-90) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeTrue
        $d.RelaxActive | Should -BeTrue
    }

    It 'still skips an over-uptime Cloud PC when the relax is NOT active (no fresh profile)' {
        Mock Test-IsCloudPc { $true }
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-90) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15 -AgentBinPath $script:cleanAgentBin
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'UptimeExceeded'
    }

    It 'keeps guard 4 for the OOBE-restore relax (physical device, uptime is meaningful)' {
        $script:fakeProfile = New-FakeProfile 'RestoreUser' 3
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        Mock Get-OobeState { 'InProgress' }
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

Describe 'Get-UnverifiedAgentBinaries' {
    # The publisher gate on the extracted payload. What matters is the DECISION: which
    # payload is accepted, which is refused, and which files are looked at at all.
    BeforeEach {
        Mock Write-Log { }
        $script:ours = 'CN=glueckkanja AG, O=glueckkanja AG, C=DE'
    }

    It 'accepts a payload where every binary of ours is validly signed by us' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe', 'AutopilotMonitor.Shared.dll')
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'Valid' -Subject $script:ours }
        Get-UnverifiedAgentBinaries -Path $dir | Should -BeNullOrEmpty
    }

    It 'checks the binaries we author and leaves third-party assemblies alone' {
        $dir = New-FakePayload -Files @(
            'AutopilotMonitor.Agent.exe',
            'AutopilotMonitor.Agent.exe.config',
            'AutopilotMonitor.Shared.dll',
            'Newtonsoft.Json.dll',
            'System.Management.Automation.dll'
        )
        $script:checked = @()
        Mock Get-AuthenticodeSignature {
            $script:checked += (Split-Path $FilePath -Leaf)
            New-FakeSignature -Status 'Valid' -Subject $script:ours
        }

        Get-UnverifiedAgentBinaries -Path $dir | Should -BeNullOrEmpty
        $script:checked | Should -Be @('AutopilotMonitor.Agent.exe', 'AutopilotMonitor.Shared.dll')
    }

    It 'refuses an unsigned agent executable' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe')
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'NotSigned' -Subject $null }
        (Get-UnverifiedAgentBinaries -Path $dir).Count | Should -Be 1
    }

    It 'refuses a tampered binary (hash mismatch)' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe')
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'HashMismatch' -Subject $script:ours }
        (Get-UnverifiedAgentBinaries -Path $dir).Count | Should -Be 1
    }

    It 'refuses a signature whose chain does not validate' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe')
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'UnknownError' -Subject $script:ours }
        (Get-UnverifiedAgentBinaries -Path $dir).Count | Should -Be 1
    }

    It 'refuses a validly signed binary from a different publisher' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe')
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'Valid' -Subject 'CN=Somebody Else, O=Somebody Else Ltd, C=US' }
        (Get-UnverifiedAgentBinaries -Path $dir).Count | Should -Be 1
    }

    It 'refuses the whole payload when a single DLL of ours is swapped' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe', 'AutopilotMonitor.Shared.dll')
        Mock Get-AuthenticodeSignature {
            if ((Split-Path $FilePath -Leaf) -eq 'AutopilotMonitor.Shared.dll') {
                New-FakeSignature -Status 'Valid' -Subject 'CN=Somebody Else, O=Somebody Else Ltd, C=US'
            } else {
                New-FakeSignature -Status 'Valid' -Subject $script:ours
            }
        }
        $rejected = Get-UnverifiedAgentBinaries -Path $dir
        $rejected.Count | Should -Be 1
        $rejected[0] | Should -BeLike 'AutopilotMonitor.Shared.dll*'
    }

    It 'refuses when the signature check itself throws' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe')
        Mock Get-AuthenticodeSignature { throw 'access denied' }
        (Get-UnverifiedAgentBinaries -Path $dir).Count | Should -Be 1
    }

    It 'refuses an empty payload instead of passing it' {
        $dir = New-FakePayload -Files @('Newtonsoft.Json.dll')
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'Valid' -Subject $script:ours }
        (Get-UnverifiedAgentBinaries -Path $dir).Count | Should -Be 1
    }
}

Describe 'publisher pin' {
    It 'pins the expected publisher by subject, not by thumbprint' {
        $ExpectedPublisher | Should -Be '*O=glueckkanja AG*'
        $ExpectedPublisher | Should -Not -Match '^[0-9A-Fa-f]{40}$'
    }
}
