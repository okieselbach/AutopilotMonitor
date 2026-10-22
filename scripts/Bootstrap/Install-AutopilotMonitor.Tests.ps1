# Pester tests (Pester 5+) for Install-AutopilotMonitor.ps1.
#
# The script under test is dot-sourced; its entry guard prevents the bootstrap
# from executing, so loading it here is side-effect free. External state (WMI,
# registry, scheduled tasks, WinRT) is mocked; folders, files and junctions are real,
# under Pester's $TestDrive.
#
# Folders created here belong to the test account. Run locally without elevation, that
# account may not make Administrators the owner, and its folders are never "ours". Tests
# that need folders we own mock Test-IsOwnedByUs and New-ProtectedDirectorySecurity.
#
# Run under Windows PowerShell 5.1 to match the IME runtime:
#   powershell.exe -NoProfile -Command "& { $c = New-PesterConfiguration; $c.Run.Path = 'scripts\Bootstrap'; $c.Output.Verbosity = 'Detailed'; Invoke-Pester -Configuration $c }"
#
# This file MUST remain pure ASCII (PS 5.1 reads BOM-less files as ANSI).

BeforeAll {
    $script:InstallerPath = Join-Path $PSScriptRoot 'Install-AutopilotMonitor.ps1'
    . $script:InstallerPath

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

    # What Test-FileCatalog -Detailed returns, reduced to the fields the check reads.
    function New-FakeCatalogResult {
        param(
            [string]$Status = 'Valid',
            [object]$Signature,
            [hashtable]$CatalogItems = @{ 'AutopilotMonitor.Agent.exe' = 'AA' },
            [hashtable]$PathItems = @{ 'AutopilotMonitor.Agent.exe' = 'AA' }
        )
        return [pscustomobject]@{ Status = $Status; Signature = $Signature; CatalogItems = $CatalogItems; PathItems = $PathItems }
    }

    function New-FakeProfile {
        param([string]$Name, [double]$AgeMinutes)
        $p = Join-Path $TestDrive $Name
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        (Get-Item $p).CreationTimeUtc = (Get-Date).ToUniversalTime().AddMinutes(-$AgeMinutes)
        return $p
    }

    function New-CategoryError {
        param([System.Management.Automation.ErrorCategory]$Category)
        return New-Object System.Management.Automation.ErrorRecord((New-Object System.Exception 'simulated'), 'Simulated', $Category, $null)
    }

    # Stand-in for the protected DACL: protected like the real one, but full control for the
    # test account and no owner change, so a non-elevated run can create and clean it up.
    function New-TestDirectorySecurity {
        $security = New-Object System.Security.AccessControl.DirectorySecurity
        $security.SetAccessRuleProtection($true, $false)
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
        return $security
    }

    function New-TestFolder {
        param([string]$Name)
        $path = Join-Path $TestDrive ("$Name-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    # A directory junction pointing at a folder with a sentinel file: whatever the code under
    # test does, the sentinel must survive.
    function New-SentinelJunction {
        param([string]$Link)
        $target = New-TestFolder 'junction-target'
        Set-Content -Path (Join-Path $target 'sentinel.txt') -Value 'must survive'
        cmd.exe /c mklink /J "$Link" "$target" | Out-Null
        return $target
    }

    function Get-ErrorRecords {
        param([scriptblock]$Script)
        return @(& $Script 2>&1 | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
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
        Mock Test-RegistryKey64 { $true } -ParameterFilter { $SubKey -eq 'SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { [pscustomobject]@{ Name = 'CloudManagedDesktopExtension'; Status = 'Running' } }
        Test-IsCloudPc | Should -BeTrue
    }

    It 'does not detect with the registry key alone (e.g. W365-Boot physical client)' {
        Mock Test-RegistryKey64 { $true } -ParameterFilter { $SubKey -eq 'SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { $null }
        Test-IsCloudPc | Should -BeFalse
    }

    It 'does not detect with the service alone' {
        Mock Test-RegistryKey64 { $false }
        Mock Get-Service { [pscustomobject]@{ Name = 'CloudManagedDesktopExtension'; Status = 'Running' } }
        Test-IsCloudPc | Should -BeFalse
    }

    It 'does not detect a plain device (no markers)' {
        Mock Test-RegistryKey64 { $false }
        Mock Get-Service { $null }
        Test-IsCloudPc | Should -BeFalse
    }

    It 'still evaluates markers when the WMI identity query fails' {
        Mock Get-CimInstance { throw 'RPC server unavailable' }
        Mock Test-RegistryKey64 { $true } -ParameterFilter { $SubKey -eq 'SOFTWARE\Microsoft\Windows365' }
        Mock Get-Service { [pscustomobject]@{ Name = 'CloudManagedDesktopExtension'; Status = 'Running' } }
        Test-IsCloudPc | Should -BeTrue
    }
}

Describe 'Get-OobeState' {
    It 'returns a non-empty state string and never throws' {
        Get-OobeState | Should -Not -BeNullOrEmpty
    }
}

Describe 'Get-RegistryValue64 and Test-RegistryKey64' {
    # The guards must read what the 64-bit agent wrote, also from a 32-bit host. These run
    # against keys every Windows has.
    It 'reads a value from the 64-bit view' {
        Get-RegistryValue64 -SubKey 'SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name 'CurrentBuild' | Should -Not -BeNullOrEmpty
    }

    # ProgramFilesDir is "Program Files" in the 64-bit view and "Program Files (x86)" in the
    # 32-bit one; Intune starts scripts in a 32-bit host by default.
    It 'reads the 64-bit view from a 32-bit host' {
        $host32 = Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
        $command = ". '{0}'; [Environment]::Is64BitProcess; Get-RegistryValue64 -SubKey 'SOFTWARE\Microsoft\Windows\CurrentVersion' -Name 'ProgramFilesDir'" -f $script:InstallerPath
        $out = @(& $host32 -NoProfile -ExecutionPolicy Bypass -Command $command)
        $out[0] | Should -Be 'False'
        $out[1] | Should -Be $env:ProgramW6432
    }

    It 'returns null for a missing key or value' {
        Get-RegistryValue64 -SubKey 'SOFTWARE\AutopilotMonitor-NoSuchKey' -Name 'Deployed' | Should -BeNullOrEmpty
        Get-RegistryValue64 -SubKey 'SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name 'NoSuchValue' | Should -BeNullOrEmpty
    }

    It 'tells an existing key from a missing one' {
        Test-RegistryKey64 -SubKey 'SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Should -BeTrue
        Test-RegistryKey64 -SubKey 'SOFTWARE\AutopilotMonitor-NoSuchKey' | Should -BeFalse
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
        # logged-on user, 1h uptime. Tests override selectively.
        Mock Get-RegistryValue64 { $null }
        Mock Get-RealUserProfilePaths { @() }
        Mock Get-OobeState { 'Completed' }
        Mock Test-IsCloudPc { $false }
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-1) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
    }

    It 'installs on a clean ESP device' {
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeTrue
        $d.ReasonCode | Should -BeNullOrEmpty
        $d.RelaxActive | Should -BeFalse
    }

    It 'skips when the Deployed marker exists, read through the 64-bit view (guard 1)' {
        Mock Get-RegistryValue64 { '2026-08-01T10:00:00Z' } -ParameterFilter { $SubKey -eq 'SOFTWARE\AutopilotMonitor' -and $Name -eq 'Deployed' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'AlreadyDeployed'
    }

    It 'skips a productive device with an old profile (guard 2)' {
        $script:fakeProfile = New-FakeProfile 'OldUser' 5000
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'ProfilesFound'
    }

    It 'installs on a freshly provisioned Cloud PC with one fresh profile' {
        $script:fakeProfile = New-FakeProfile 'CloudPcUser' 0.6
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        Mock Test-IsCloudPc { $true }
        # First-connect user is already visible in LogonUI on W365 -- must not block either.
        Mock Get-RegistryValue64 { 'AzureAD\CloudPcUser' } -ParameterFilter { $SubKey -like '*LogonUI*' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeTrue
        $d.RelaxActive | Should -BeTrue
    }

    It 'skips when a real user has logged on and no relax applies, read through the 64-bit view (guard 3)' {
        Mock Get-RegistryValue64 { 'AzureAD\SomeUser' } -ParameterFilter { $SubKey -eq 'SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -and $Name -eq 'LastLoggedOnUser' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'LastLoggedOnUser'
    }

    It 'does not skip for the defaultuser0 OOBE account (guard 3)' {
        Mock Get-RegistryValue64 { 'defaultuser0' } -ParameterFilter { $SubKey -like '*LogonUI*' }
        (Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15).Install | Should -BeTrue
    }

    It 'treats an unreadable marker as absent for the decision (the agent check decides fail-safe)' {
        Mock Get-RegistryValue64 { throw 'access denied' }
        (Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15).Install | Should -BeTrue
    }

    It 'skips when uptime exceeds the bootstrap window (guard 4)' {
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-20) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
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
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeTrue
        $d.RelaxActive | Should -BeTrue
    }

    It 'still skips an over-uptime Cloud PC when the relax is NOT active (no fresh profile)' {
        Mock Test-IsCloudPc { $true }
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-90) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'UptimeExceeded'
    }

    It 'keeps guard 4 for the OOBE-restore relax (physical device, uptime is meaningful)' {
        $script:fakeProfile = New-FakeProfile 'RestoreUser' 3
        Mock Get-RealUserProfilePaths { @($script:fakeProfile) }
        Mock Get-OobeState { 'InProgress' }
        Mock Get-CimInstance { [pscustomobject]@{ LastBootUpTime = (Get-Date).AddHours(-20) } } -ParameterFilter { $ClassName -eq 'Win32_OperatingSystem' }
        $d = Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15
        $d.Install | Should -BeFalse
        $d.ReasonCode | Should -Be 'UptimeExceeded'
    }
}

Describe 'logging' {
    # Regression guard: Write-Log runs inside functions that RETURN decision
    # objects. If it ever emits to the output stream again (Write-Output), the
    # log lines leak into the return values and turn them into arrays. These
    # tests deliberately do NOT mock Write-Log.
    BeforeEach {
        # Plain local assignments: PowerShell's dynamic scoping makes Write-Log resolve these
        # instead of the ProgramData path and the state defined by the dot-sourced script.
        $LogFile = Join-Path $TestDrive "bootstrap-$([guid]::NewGuid().ToString('N')).log"
        $LogState = [pscustomobject]@{ Ready = $false; Pending = (New-Object System.Collections.Generic.List[string]) }
    }

    It 'Get-BootstrapDecision returns exactly one object with real logging' {
        Mock Get-RegistryValue64 { '2026-08-01T10:00:00Z' } -ParameterFilter { $SubKey -eq 'SOFTWARE\AutopilotMonitor' }
        $d = @(Get-BootstrapDecision -MaxBootstrapWindowHours 12 -OobeProfileMaxAgeMinutes 15)
        $d.Count | Should -Be 1
        $d[0].ReasonCode | Should -Be 'AlreadyDeployed'
    }

    It 'keeps lines in memory until the log file is opened, then writes them first' {
        Write-Log 'before the folder exists'
        Test-Path $LogFile | Should -BeFalse
        $LogState.Pending.Count | Should -Be 1

        Open-LogFile
        Write-Log 'after'

        $lines = @(Get-Content $LogFile)
        $lines.Count | Should -Be 2
        $lines[0] | Should -Match '^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\] before the folder exists$'
        $lines[1] | Should -Match 'after$'
        $LogState.Pending.Count | Should -Be 0
    }

    It 'writes the version line in the format the agent reads' {
        Write-Log "Bootstrap script version: v$ScriptVersion"
        Open-LogFile
        Get-Content $LogFile -Raw | Should -Match 'Bootstrap script version: v\d+(\.\d+){0,3}\b'
    }
}

Describe 'Get-AgentPresence' {
    BeforeAll {
        # Get-ScheduledTask is a CDXML function: a module-qualified call inside its mock still
        # reaches the mock, the command object does not.
        $script:realGetScheduledTask = Get-Command -Name Get-ScheduledTask -Module ScheduledTasks
    }

    BeforeEach {
        Mock Start-Sleep { }
        Mock Get-RegistryValue64 { $null }
        Mock Get-ScheduledTask { throw (New-CategoryError -Category ObjectNotFound) }
        $AgentBinPath = Join-Path $TestDrive "AgentBin-$([guid]::NewGuid().ToString('N'))"
    }

    It 'finds no agent when marker, executable and task are all absent' {
        Get-AgentPresence | Should -BeNullOrEmpty
    }

    It 'names the deployment marker' {
        Mock Get-RegistryValue64 { '2026-08-01T10:00:00Z' } -ParameterFilter { $SubKey -eq 'SOFTWARE\AutopilotMonitor' -and $Name -eq 'Deployed' }
        Get-AgentPresence | Should -Be 'deployment marker'
    }

    It 'names the agent executable' {
        New-Item -ItemType Directory -Path $AgentBinPath -Force | Out-Null
        Set-Content -Path (Join-Path $AgentBinPath 'AutopilotMonitor.Agent.exe') -Value 'stub'
        Get-AgentPresence | Should -Be 'agent executable'
    }

    It 'names the agent task' {
        Mock Get-ScheduledTask { [pscustomobject]@{ TaskName = 'AutopilotMonitor-Agent' } }
        Get-AgentPresence | Should -Be 'agent task'
    }

    It 'counts an unreadable marker as present' {
        Mock Get-RegistryValue64 { throw 'Requested registry access is not allowed.' }
        Get-AgentPresence | Should -BeLike 'deployment marker unreadable*'
    }

    It 'counts an agent executable it cannot check as present' {
        Mock Get-Item { throw (New-CategoryError -Category PermissionDenied) } -ParameterFilter { $LiteralPath -like '*AutopilotMonitor.Agent.exe' }
        Get-AgentPresence | Should -BeLike 'agent executable unreadable*'
    }

    It 'counts a task query that fails for another reason as present, after one retry' {
        Mock Get-ScheduledTask { throw (New-CategoryError -Category ResourceUnavailable) }
        Get-AgentPresence | Should -BeLike 'agent task unreadable*'
        Should -Invoke Get-ScheduledTask -Times 2 -Exactly
    }

    It 'decides "no task" by the error category, not the message' {
        # The real cmdlet on a name that cannot exist: its message is localized, its category is not.
        Mock Get-ScheduledTask { & $script:realGetScheduledTask -TaskName "AutopilotMonitor-NoSuchTask-$([guid]::NewGuid().ToString('N'))" -ErrorAction Stop }
        Get-AgentPresence | Should -BeNullOrEmpty
    }
}

Describe 'Test-IsOwnedByUs' {
    It 'accepts SYSTEM as owner' {
        Test-IsOwnedByUs $env:ProgramData | Should -BeTrue
    }

    It 'refuses another owner (TrustedInstaller owns the Windows folder)' {
        Test-IsOwnedByUs $env:SystemRoot | Should -BeFalse
    }

    It 'refuses what it cannot read' {
        Test-IsOwnedByUs (Join-Path $TestDrive 'does-not-exist') | Should -BeFalse
    }
}

Describe 'Test-IsReparsePoint' {
    It 'tells a junction from a folder' {
        $folder = New-TestFolder 'plain'
        $link = Join-Path $TestDrive "link-$([guid]::NewGuid().ToString('N'))"
        $null = New-SentinelJunction -Link $link
        Test-IsReparsePoint $folder | Should -BeFalse
        Test-IsReparsePoint $link | Should -BeTrue
    }
}

Describe 'Get-FolderState' {
    It 'reports a missing folder, also below a missing parent' {
        Get-FolderState (Join-Path $TestDrive 'nope') | Should -Be 'Missing'
        Get-FolderState (Join-Path $TestDrive 'nope\deeper') | Should -Be 'Missing'
    }

    It 'reports a file where the folder should be' {
        $file = Join-Path $TestDrive "file-$([guid]::NewGuid().ToString('N'))"
        Set-Content -Path $file -Value 'x'
        Get-FolderState $file | Should -Be 'File'
    }

    It 'reports a link without judging its target' {
        Mock Test-IsOwnedByUs { $true }
        $link = Join-Path $TestDrive "link-$([guid]::NewGuid().ToString('N'))"
        $null = New-SentinelJunction -Link $link
        Get-FolderState $link | Should -Be 'Link'
    }

    It 'reports a folder by its owner' {
        $folder = New-TestFolder 'owned'
        Mock Test-IsOwnedByUs { $true }
        Get-FolderState $folder | Should -Be 'Ours'
        Mock Test-IsOwnedByUs { $false }
        Get-FolderState $folder | Should -Be 'Foreign'
    }
}

Describe 'New-RunFolderName' {
    It 'is 8 lowercase hex characters and changes on every call' {
        $a = New-RunFolderName
        $b = New-RunFolderName
        $a | Should -Match '^[0-9a-f]{8}$'
        $b | Should -Match '^[0-9a-f]{8}$'
        $a | Should -Not -Be $b
    }
}

Describe 'New-RunFolder' {
    BeforeEach {
        Mock New-ProtectedDirectorySecurity { New-TestDirectorySecurity }
        Mock Test-IsOwnedByUs { $true }
        $script:parent = New-TestFolder 'runs'
    }

    It 'creates prefix plus 8 hex characters with the protected DACL' {
        $path = New-RunFolder -Parent $script:parent -Prefix 'Agent-Install-'
        Split-Path $path -Leaf | Should -Match '^Agent-Install-[0-9a-f]{8}$'
        (Get-Acl -LiteralPath $path).AreAccessRulesProtected | Should -BeTrue
        Should -Invoke New-ProtectedDirectorySecurity -Times 1 -Exactly
    }

    It 'never uses an existing name' {
        $script:names = [System.Collections.Generic.Queue[string]]::new([string[]]@('aaaaaaaa', 'bbbbbbbb'))
        Mock New-RunFolderName { $script:names.Dequeue() }
        $taken = Join-Path $script:parent 'Loader-aaaaaaaa'
        New-Item -ItemType Directory -Path $taken | Out-Null
        Set-Content -Path (Join-Path $taken 'planted.txt') -Value 'x'

        $path = New-RunFolder -Parent $script:parent -Prefix 'Loader-'

        Split-Path $path -Leaf | Should -Be 'Loader-bbbbbbbb'
        @(Get-ChildItem -LiteralPath $path -Force).Count | Should -Be 0
    }

    It 'never uses a junction planted at the name' {
        $script:names = [System.Collections.Generic.Queue[string]]::new([string[]]@('cccccccc', 'dddddddd'))
        Mock New-RunFolderName { $script:names.Dequeue() }
        $target = New-SentinelJunction -Link (Join-Path $script:parent 'Loader-cccccccc')

        $path = New-RunFolder -Parent $script:parent -Prefix 'Loader-'

        Split-Path $path -Leaf | Should -Be 'Loader-dddddddd'
        Test-Path (Join-Path $target 'sentinel.txt') | Should -BeTrue
    }

    It 'gives up after three names that are not ours' {
        Mock Test-IsOwnedByUs { $false }
        { New-RunFolder -Parent $script:parent -Prefix 'Loader-' } | Should -Throw '*Could not create a run folder*'
    }
}

Describe 'Remove-OwnTree' {
    BeforeEach {
        Mock Start-Sleep { }
        Mock Test-IsOwnedByUs { $true }
        $script:root = New-TestFolder 'tree'
    }

    It 'removes an own tree with whatever is in it, also files no version knows yet' {
        New-Item -ItemType Directory -Path (Join-Path $script:root 'Logs\deeper') -Force | Out-Null
        Set-Content -Path (Join-Path $script:root 'Logs\bootstrap-script.log') -Value 'x'
        Set-Content -Path (Join-Path $script:root 'Logs\deeper\future-file.dat') -Value 'x'
        Set-Content -Path (Join-Path $script:root 'Install-AutopilotMonitor.ps1') -Value 'x'

        $left = Remove-OwnTree -Path $script:root

        Test-Path $script:root | Should -BeFalse
        $left.Foreign.Count | Should -Be 0
        $left.Locked.Count | Should -Be 0
    }

    It 'removes a junction as a link and never follows it, at the top and deep in the tree' {
        New-Item -ItemType Directory -Path (Join-Path $script:root 'Logs') -Force | Out-Null
        $top = New-SentinelJunction -Link (Join-Path $script:root 'Agent')
        $deep = New-SentinelJunction -Link (Join-Path $script:root 'Logs\loop')

        $left = Remove-OwnTree -Path $script:root

        Test-Path $script:root | Should -BeFalse
        Test-Path (Join-Path $top 'sentinel.txt') | Should -BeTrue
        Test-Path (Join-Path $deep 'sentinel.txt') | Should -BeTrue
        $left.Locked.Count | Should -Be 0
    }

    It 'survives a junction that points back at the tree itself' {
        New-Item -ItemType Directory -Path (Join-Path $script:root 'Logs') -Force | Out-Null
        cmd.exe /c mklink /J "$(Join-Path $script:root 'Logs\cycle')" "$script:root" | Out-Null

        $left = Remove-OwnTree -Path $script:root

        Test-Path $script:root | Should -BeFalse
        $left.Locked.Count | Should -Be 0
    }

    It 'leaves another account''s entries alone, and with them their folder' {
        New-Item -ItemType Directory -Path (Join-Path $script:root 'Logs') -Force | Out-Null
        Set-Content -Path (Join-Path $script:root 'Logs\own.log') -Value 'x'
        Set-Content -Path (Join-Path $script:root 'Logs\foreign-planted.dll') -Value 'x'
        Mock Test-IsOwnedByUs { $Path -notlike '*foreign-*' }

        $left = Remove-OwnTree -Path $script:root

        Test-Path (Join-Path $script:root 'Logs\foreign-planted.dll') | Should -BeTrue
        Test-Path (Join-Path $script:root 'Logs\own.log') | Should -BeFalse
        $left.Foreign | Should -Be @((Join-Path $script:root 'Logs\foreign-planted.dll'))
    }

    It 'removes a read-only file' {
        $file = Join-Path $script:root 'readonly.txt'
        Set-Content -Path $file -Value 'x'
        Set-ItemProperty -Path $file -Name IsReadOnly -Value $true

        $null = Remove-OwnTree -Path $script:root

        Test-Path $script:root | Should -BeFalse
    }

    It 'reports a file in use, keeps going, and writes nothing to the error stream' {
        $locked = Join-Path $script:root 'in-use.log'
        Set-Content -Path $locked -Value 'x'
        Set-Content -Path (Join-Path $script:root 'other.txt') -Value 'x'
        $handle = [System.IO.File]::Open($locked, 'Open', 'Read', 'None')
        try {
            $errors = Get-ErrorRecords { $script:left = Remove-OwnTree -Path $script:root }
        } finally {
            $handle.Dispose()
        }

        $errors.Count | Should -Be 0
        $script:left.Locked | Should -Contain $locked
        Test-Path (Join-Path $script:root 'other.txt') | Should -BeFalse
        Should -Invoke Start-Sleep -Times 2 -Exactly
    }

    It 'keeps the root folder when asked to' {
        Set-Content -Path (Join-Path $script:root 'x.txt') -Value 'x'
        $null = Remove-OwnTree -Path $script:root -KeepRoot
        Test-Path $script:root | Should -BeTrue
        @(Get-ChildItem -LiteralPath $script:root -Force).Count | Should -Be 0
    }
}

Describe 'Initialize-AgentBase' {
    BeforeEach {
        Mock Start-Sleep { }
        Mock New-ProtectedDirectorySecurity { New-TestDirectorySecurity }
        Mock Test-IsOwnedByUs { $true }
        $script:base = Join-Path $TestDrive "AutopilotMonitor-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    }

    It 'creates a missing folder with the protected DACL' {
        Initialize-AgentBase -Path $script:base
        Test-Path $script:base | Should -BeTrue
        (Get-Acl -LiteralPath $script:base).AreAccessRulesProtected | Should -BeTrue
    }

    It 'protects and empties an existing folder of ours, keeping the folder' {
        New-Item -ItemType Directory -Path (Join-Path $script:base 'Agent') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:base 'Logs') -Force | Out-Null
        Set-Content -Path (Join-Path $script:base 'Logs\bootstrap-script.log') -Value 'old'
        Set-Content -Path (Join-Path $script:base 'Install-AutopilotMonitor.ps1') -Value 'old'

        Initialize-AgentBase -Path $script:base

        Test-Path $script:base | Should -BeTrue
        @(Get-ChildItem -LiteralPath $script:base -Force).Count | Should -Be 0
        (Get-Acl -LiteralPath $script:base).AreAccessRulesProtected | Should -BeTrue
    }

    It 'refuses when another account''s entries remain' {
        New-Item -ItemType Directory -Path $script:base -Force | Out-Null
        Set-Content -Path (Join-Path $script:base 'foreign-planted.dll') -Value 'x'
        Mock Test-IsOwnedByUs { $Path -notlike '*foreign-*' }
        { Initialize-AgentBase -Path $script:base } | Should -Throw '*holds entries another account created*'
        Test-Path (Join-Path $script:base 'foreign-planted.dll') | Should -BeTrue
    }

    It 'refuses a folder another account owns, without touching it' {
        New-Item -ItemType Directory -Path $script:base -Force | Out-Null
        Set-Content -Path (Join-Path $script:base 'theirs.txt') -Value 'x'
        Mock Test-IsOwnedByUs { $false }
        { Initialize-AgentBase -Path $script:base } | Should -Throw '*not a folder that SYSTEM or administrators created (Foreign)*'
        Test-Path (Join-Path $script:base 'theirs.txt') | Should -BeTrue
    }

    It 'refuses a link and leaves its target alone' {
        $target = New-SentinelJunction -Link $script:base
        { Initialize-AgentBase -Path $script:base } | Should -Throw '*(Link)*'
        Test-Path (Join-Path $target 'sentinel.txt') | Should -BeTrue
    }

    It 'refuses a file' {
        Set-Content -Path $script:base -Value 'x'
        { Initialize-AgentBase -Path $script:base } | Should -Throw '*(File)*'
    }
}

Describe 'Remove-AgentBaseOnSkip' {
    BeforeEach {
        Mock Start-Sleep { }
        Mock New-ProtectedDirectorySecurity { New-TestDirectorySecurity }
        Mock Test-IsOwnedByUs { $true }
        $script:base = Join-Path $TestDrive "AutopilotMonitor-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    }

    It 'has nothing to do when the folder does not exist' {
        Remove-AgentBaseOnSkip -Path $script:base | Should -BeLike '*does not exist*'
    }

    It 'removes a folder of ours with everything a bootstrap left in it' {
        New-Item -ItemType Directory -Path (Join-Path $script:base 'Agent') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:base 'Msi') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:base 'Logs') -Force | Out-Null
        Set-Content -Path (Join-Path $script:base 'Logs\bootstrap-loader.log') -Value 'x'
        Set-Content -Path (Join-Path $script:base 'Msi\Start-AutopilotMonitor.ps1') -Value 'x'
        Set-Content -Path (Join-Path $script:base 'Install-AutopilotMonitor.ps1') -Value 'x'

        Remove-AgentBaseOnSkip -Path $script:base | Should -BeLike '*removed.'

        Test-Path $script:base | Should -BeFalse
    }

    It 'keeps the folder when another account''s entry is in it, and says so' {
        New-Item -ItemType Directory -Path $script:base -Force | Out-Null
        Set-Content -Path (Join-Path $script:base 'foreign-planted.txt') -Value 'x'
        Set-Content -Path (Join-Path $script:base 'own.txt') -Value 'x'
        Mock Test-IsOwnedByUs { $Path -notlike '*foreign-*' }

        Remove-AgentBaseOnSkip -Path $script:base | Should -BeLike '*kept: 1 entries of another account*'

        Test-Path (Join-Path $script:base 'foreign-planted.txt') | Should -BeTrue
        Test-Path (Join-Path $script:base 'own.txt') | Should -BeFalse
    }

    It 'leaves a folder another account owns as it is' {
        New-Item -ItemType Directory -Path $script:base -Force | Out-Null
        Set-Content -Path (Join-Path $script:base 'theirs.txt') -Value 'x'
        Mock Test-IsOwnedByUs { $false }
        Remove-AgentBaseOnSkip -Path $script:base | Should -BeLike '*left as it is (Foreign)*'
        Test-Path (Join-Path $script:base 'theirs.txt') | Should -BeTrue
    }

    It 'leaves a link and its target as they are' {
        $target = New-SentinelJunction -Link $script:base
        Remove-AgentBaseOnSkip -Path $script:base | Should -BeLike '*left as it is (Link)*'
        Test-Path (Join-Path $target 'sentinel.txt') | Should -BeTrue
    }

    It 'never throws' {
        Mock Get-FolderState { throw 'unexpected' }
        { Remove-AgentBaseOnSkip -Path $script:base } | Should -Not -Throw
    }
}

Describe 'Get-AgentPayloadRejections' {
    # The payload catalog check, shared verbatim with the OOBE go-script. What matters is the
    # DECISION: which payload is accepted and which is refused. Test-FileCatalog is mocked for
    # the branches; one test runs the real cmdlet over a real (unsigned) catalog.
    BeforeEach {
        Mock Write-Log { }
        $script:ours = 'CN=glueckkanja AG, O=glueckkanja AG, C=DE'
        $script:dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe', 'AutopilotMonitor.Agent.cat')
    }

    It 'accepts a payload whose catalog is signed by us and matches every file' {
        Mock Test-FileCatalog { New-FakeCatalogResult -Signature (New-FakeSignature -Status 'Valid' -Subject $script:ours) }
        Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher | Should -BeNullOrEmpty
    }

    It 'asks for the detailed result and skips the catalog itself' {
        Mock Test-FileCatalog {
            $script:call = @{ Skip = $FilesToSkip; Detailed = [bool]$Detailed; Catalog = $CatalogFilePath; Path = $Path }
            New-FakeCatalogResult -Signature (New-FakeSignature -Status 'Valid' -Subject $script:ours)
        }
        Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher | Out-Null
        $script:call.Skip | Should -Be @('AutopilotMonitor.Agent.cat')
        $script:call.Detailed | Should -BeTrue
        $script:call.Catalog | Should -Be (Join-Path $script:dir 'AutopilotMonitor.Agent.cat')
        $script:call.Path | Should -Be $script:dir
    }

    It 'refuses a payload without catalog and does not run the check at all' {
        $dir = New-FakePayload -Files @('AutopilotMonitor.Agent.exe')
        Mock Test-FileCatalog { throw 'must not run' }
        $r = Get-AgentPayloadRejections -Path $dir -ExpectedPublisher $ExpectedPublisher
        $r.Count | Should -Be 1
        $r[0] | Should -BeLike '*AutopilotMonitor.Agent.cat missing*'
        Should -Invoke Test-FileCatalog -Times 0
    }

    It 'refuses matching files under an unsigned catalog (Status alone is not the verdict)' {
        Mock Test-FileCatalog { New-FakeCatalogResult -Status 'Valid' -Signature (New-FakeSignature -Status 'NotSigned' -Subject $null) }
        $r = Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher
        $r.Count | Should -Be 1
        $r[0] | Should -BeLike '*signature status NotSigned*'
    }

    It 'refuses a catalog whose signature does not validate' {
        Mock Test-FileCatalog { New-FakeCatalogResult -Signature (New-FakeSignature -Status 'HashMismatch' -Subject $script:ours) }
        (Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher).Count | Should -Be 1
    }

    It 'refuses a validly signed catalog from another publisher' {
        Mock Test-FileCatalog { New-FakeCatalogResult -Signature (New-FakeSignature -Status 'Valid' -Subject 'CN=Somebody Else, O=Somebody Else Ltd, C=US') }
        $r = Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher
        $r.Count | Should -Be 1
        $r[0] | Should -BeLike '*does not match*'
    }

    It 'names the missing, extra and changed files of a payload that differs' {
        Mock Test-FileCatalog {
            New-FakeCatalogResult -Status 'ValidationFailed' -Signature (New-FakeSignature -Status 'Valid' -Subject $script:ours) `
                -CatalogItems @{ 'a.dll' = 'AA'; 'b.dll' = 'BB'; 'c.config' = 'CC' } `
                -PathItems @{ 'a.dll' = 'AA'; 'b.dll' = 'XX'; 'evil.dll' = 'EE' }
        }
        $r = Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher
        $r.Count | Should -Be 1
        $r[0] | Should -BeLike '*missing: c.config;*'
        $r[0] | Should -BeLike '*extra: evil.dll;*'
        $r[0] | Should -BeLike '*changed: b.dll)*'
    }

    It 'reports a bad signature and a differing payload together' {
        Mock Test-FileCatalog {
            New-FakeCatalogResult -Status 'ValidationFailed' -Signature (New-FakeSignature -Status 'NotSigned' -Subject $null) `
                -PathItems @{ 'AutopilotMonitor.Agent.exe' = 'AA'; 'extra.dll' = 'EE' }
        }
        (Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher).Count | Should -Be 2
    }

    It 'refuses when the catalog check itself throws' {
        Mock Test-FileCatalog { throw 'access denied' }
        $r = Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher
        $r.Count | Should -Be 1
        $r[0] | Should -BeLike '*failed to run*access denied*'
    }

    It 'refuses when the catalog check returns nothing' {
        Mock Test-FileCatalog { }
        $r = Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher
        $r[0] | Should -BeLike '*returned nothing*'
    }

    It 'returns a single rejection as an array' {
        Mock Test-FileCatalog { throw 'boom' }
        $r = Get-AgentPayloadRejections -Path $script:dir -ExpectedPublisher $ExpectedPublisher
        ($r -is [array]) | Should -BeTrue
    }

    It 'catches an extra file with the real cmdlet over a real catalog' {
        # Real New-FileCatalog/Test-FileCatalog over text files: no PE, no process, no mock.
        $dir = New-FakePayload -Files @('a.txt', 'b.txt')
        $catalog = Join-Path $TestDrive "real-$([guid]::NewGuid()).cat"
        New-FileCatalog -Path $dir -CatalogFilePath $catalog -CatalogVersion 2.0 | Out-Null
        Copy-Item $catalog (Join-Path $dir 'AutopilotMonitor.Agent.cat')
        Set-Content -Path (Join-Path $dir 'extra.txt') -Value 'planted'

        $r = Get-AgentPayloadRejections -Path $dir -ExpectedPublisher $ExpectedPublisher

        ($r -join ' | ') | Should -BeLike '*extra: extra.txt;*'
        ($r -join ' | ') | Should -BeLike '*signature status NotSigned*'
    }
}

Describe 'Install-VerifiedAgentPayload' {
    BeforeEach {
        Mock Start-Sleep { }
        $src = New-FakePayload -Files @('AutopilotMonitor.Agent.exe', 'AutopilotMonitor.Agent.cat', 'Newtonsoft.Json.dll')
        $script:zip = Join-Path $TestDrive "payload-$([guid]::NewGuid()).zip"
        Compress-Archive -Path (Join-Path $src '*') -DestinationPath $script:zip
        $script:staging = New-TestFolder 'Agent-Install'
        $script:target = Join-Path $TestDrive "agent-$([guid]::NewGuid())"
    }

    It 'moves an accepted payload in as the agent folder' {
        Mock Get-AgentPayloadRejections { , @() }
        Install-VerifiedAgentPayload -ZipPath $script:zip -StagingPath $script:staging -TargetPath $script:target -ExpectedPublisher $ExpectedPublisher
        @(Get-ChildItem $script:target | ForEach-Object Name | Sort-Object) |
            Should -Be @('AutopilotMonitor.Agent.cat', 'AutopilotMonitor.Agent.exe', 'Newtonsoft.Json.dll')
        Test-Path (Join-Path $script:staging 'Agent') | Should -BeFalse
    }

    It 'checks the extracted payload in the run folder, never the agent folder' {
        Mock Get-AgentPayloadRejections { $script:checked = $Path; , @() }
        Install-VerifiedAgentPayload -ZipPath $script:zip -StagingPath $script:staging -TargetPath $script:target -ExpectedPublisher $ExpectedPublisher
        $script:checked | Should -Be (Join-Path $script:staging 'Agent')
    }

    It 'refuses a payload that fails the check and creates no agent folder' {
        Mock Get-AgentPayloadRejections { , @('payload differs from catalog (missing: none; extra: evil.dll; changed: none)') }
        { Install-VerifiedAgentPayload -ZipPath $script:zip -StagingPath $script:staging -TargetPath $script:target -ExpectedPublisher $ExpectedPublisher } |
            Should -Throw '*Payload verification FAILED*evil.dll*'
        Test-Path $script:target | Should -BeFalse
    }

    It 'never merges into an agent folder that is already there' {
        New-Item -ItemType Directory -Path $script:target | Out-Null
        Set-Content -Path (Join-Path $script:target 'stale.dll') -Value 'x'
        Mock Get-AgentPayloadRejections { , @() }
        { Install-VerifiedAgentPayload -ZipPath $script:zip -StagingPath $script:staging -TargetPath $script:target -ExpectedPublisher $ExpectedPublisher } | Should -Throw
        @(Get-ChildItem $script:target | ForEach-Object Name) | Should -Be @('stale.dll')
    }
}

Describe 'Invoke-AgentInstallMode' {
    It 'returns only the exit code; the program''s output goes to the host' {
        $r = @(Invoke-AgentInstallMode -ExePath "$env:SystemRoot\System32\cmd.exe" -Arguments @('/c', 'echo from-agent & exit 3'))
        $r.Count | Should -Be 1
        $r[0] | Should -Be 3
    }
}

Describe 'Read-LoaderLogs' {
    BeforeEach {
        $script:realProgramData = $env:ProgramData
        $env:ProgramData = New-TestFolder 'ProgramData'
        $AgentLogPath = Join-Path $env:ProgramData 'AutopilotMonitor\Logs'
        $LegacyInstallerPath = Join-Path $env:ProgramData 'AutopilotMonitor\Install-AutopilotMonitor.ps1'
    }
    AfterEach {
        $env:ProgramData = $script:realProgramData
    }

    It 'takes the whole log of loader 1.3 from the run folder it started this script from' {
        $run = Join-Path $env:ProgramData 'AutopilotMonitor-Loader-0a1b2c3d'
        New-Item -ItemType Directory -Path $run | Out-Null
        Set-Content -Path (Join-Path $run 'bootstrap-msi.log') -Value '[2026-10-22 10:00:00] === Autopilot Monitor bootstrap loader v1.3 started (user=SYSTEM, pid=1) ==='

        $logs = @(Read-LoaderLogs -ScriptPath (Join-Path $run 'Install-AutopilotMonitor.ps1') -ScriptRoot $run)

        $logs.Count | Should -Be 1
        $logs[0].Name | Should -Be 'bootstrap-msi.log'
        $logs[0].Text | Should -BeLike '*bootstrap loader v1.3 started*'
    }

    It 'takes only the last start line of an older loader, rebuilt from the pattern' {
        New-Item -ItemType Directory -Path $AgentLogPath -Force | Out-Null
        Set-Content -Path (Join-Path $AgentLogPath 'bootstrap-loader.log') -Value @(
            '[2026-10-01 09:00:00] === Autopilot Monitor bootstrap loader v1.1 started (user=SYSTEM, pid=1) ==='
            'anything else a user may have planted here'
            '[2026-10-22 10:00:00] === Autopilot Monitor bootstrap loader v1.2 started (user=SYSTEM, pid=2) ==='
            '[2026-10-22 10:00:01] Signature accepted'
        )

        $logs = @(Read-LoaderLogs -ScriptPath $LegacyInstallerPath -ScriptRoot (Split-Path $LegacyInstallerPath))

        $logs.Count | Should -Be 1
        $logs[0].Name | Should -Be 'bootstrap-loader.log'
        $logs[0].Text | Should -Be "[2026-10-22 10:00:00] === Autopilot Monitor bootstrap loader v1.2 started (carried over by the bootstrapper) ===`r`n"
    }

    It 'takes nothing when the script was assigned directly' {
        $ime = New-TestFolder 'IMEScripts'
        @(Read-LoaderLogs -ScriptPath (Join-Path $ime 'x.ps1') -ScriptRoot $ime).Count | Should -Be 0
    }
}

Describe 'Remove-LegacyInstallerCopy' {
    BeforeEach {
        $LegacyInstallerPath = Join-Path (New-TestFolder 'Base') 'Install-AutopilotMonitor.ps1'
        Set-Content -Path $LegacyInstallerPath -Value 'x'
    }

    It 'deletes this script when it runs from the path older loaders use, ignoring case' {
        Remove-LegacyInstallerCopy -ScriptPath $LegacyInstallerPath.ToUpperInvariant()
        Test-Path $LegacyInstallerPath | Should -BeFalse
    }

    It 'leaves every other path alone (IME copy, manual run)' {
        $other = Join-Path $TestDrive 'Install-AutopilotMonitor.ps1'
        Set-Content -Path $other -Value 'x'
        Remove-LegacyInstallerCopy -ScriptPath $other
        Test-Path $other | Should -BeTrue
        Test-Path $LegacyInstallerPath | Should -BeTrue
    }

    It 'never throws' {
        { Remove-LegacyInstallerCopy -ScriptPath $null } | Should -Not -Throw
        { Remove-LegacyInstallerCopy -ScriptPath 'C:\does\not\exist.ps1' } | Should -Not -Throw
    }
}

Describe 'Remove-StaleLoaderRunFolders' {
    BeforeEach {
        Mock Start-Sleep { }
        $script:realProgramData = $env:ProgramData
        $env:ProgramData = New-TestFolder 'ProgramData'
    }
    AfterEach {
        $env:ProgramData = $script:realProgramData
    }

    It 'removes the run folders of earlier loader runs and keeps the current one' {
        Mock Test-IsOwnedByUs { $true }
        $current = Join-Path $env:ProgramData 'AutopilotMonitor-Loader-11111111'
        $stale = Join-Path $env:ProgramData 'AutopilotMonitor-Loader-22222222'
        New-Item -ItemType Directory -Path $current, $stale | Out-Null
        Set-Content -Path (Join-Path $stale 'bootstrap-loader.log') -Value 'x'

        Remove-StaleLoaderRunFolders -ScriptRoot $current

        Test-Path $current | Should -BeTrue
        Test-Path $stale | Should -BeFalse
    }

    It 'leaves alone what is not ours or is a link' {
        Mock Test-IsOwnedByUs { $false }
        $foreign = Join-Path $env:ProgramData 'AutopilotMonitor-Loader-33333333'
        New-Item -ItemType Directory -Path $foreign | Out-Null
        $target = New-SentinelJunction -Link (Join-Path $env:ProgramData 'AutopilotMonitor-Loader-44444444')

        Remove-StaleLoaderRunFolders -ScriptRoot $null

        Test-Path $foreign | Should -BeTrue
        Test-Path (Join-Path $target 'sentinel.txt') | Should -BeTrue
    }
}

Describe 'script text' {
    BeforeAll {
        $script:text = Get-Content -LiteralPath $script:InstallerPath -Raw
        $tokens = $null
        $parseErrors = $null
        $script:ast = [System.Management.Automation.Language.Parser]::ParseFile($script:InstallerPath, [ref]$tokens, [ref]$parseErrors)
    }

    It 'declares the role and version the loader checks, one line each' {
        ([regex]::Matches($script:text, '(?m)^\$ScriptRole = "AutopilotMonitor-Installer"\s*$')).Count | Should -Be 1
        ([regex]::Matches($script:text, '(?m)^\$ScriptVersion = "2\.7"\s*$')).Count | Should -Be 1
    }

    It 'never stages anything in the shared temp folder' {
        $script:text | Should -Not -Match 'env:TEMP'
    }

    It 'never deletes recursively except through Remove-OwnTree' {
        $removeItems = $script:ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in @('Remove-Item', 'ri', 'rm', 'del', 'rd', 'rmdir', 'erase') }, $true)
        @($removeItems).Count | Should -Be 0
        $script:text | Should -Not -Match 'Delete\([^)]*\$true\)'
    }

    It 'carries each shared-block marker exactly once' {
        ([regex]::Matches($script:text, '### BEGIN shared install helpers')).Count | Should -Be 1
        ([regex]::Matches($script:text, '### END shared install helpers')).Count | Should -Be 1
    }
}

Describe 'publisher pin' {
    It 'pins the expected publisher by subject, not by thumbprint' {
        $ExpectedPublisher | Should -Be '*O=glueckkanja AG*'
        $ExpectedPublisher | Should -Not -Match '^[0-9A-Fa-f]{40}$'
    }

    It 'names the payload catalog the release pipeline signs' {
        $AgentCatalogName | Should -Be 'AutopilotMonitor.Agent.cat'
    }
}
