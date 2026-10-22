# Integration tests (Pester 5+) for the bootstrap chain: the real loader and installer, each
# started the way Intune and the bootstrap MSI start them -- powershell.exe -File, in a process
# of its own -- against a scratch ProgramData and TEMP. Loader 1.2 and the MSI 1.0.x runner run
# from TestData, frozen from git: devices keep running them until customers replace them.
#
# What these tests do not cover is swapped out by global aliases, which win over cmdlets and
# over functions a script defines: downloads (served from a scratch folder), signatures, the
# catalog check, the guard decision, the deployment marker, the agent task, the agent's
# --install and sleeps. Every swap writes a trace line, and the tests check the lines they rely
# on, so a swap that stops working turns a test red instead of reaching the real machine.
#
# Run elevated (CI also runs it as SYSTEM), the folder permissions are the real ones. Without
# elevation the test account may not make Administrators the owner, so the protected DACL is
# swapped for one without that owner change, and folders the account owns count as ours.
# Entries whose name starts with "foreign" count as another account's.
#
# This file MUST remain pure ASCII (PS 5.1 reads BOM-less files as ANSI).

BeforeAll {
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $script:loaderPath = Join-Path $PSScriptRoot 'Start-AutopilotMonitor.ps1'
    $script:installerPath = Join-Path $PSScriptRoot 'Install-AutopilotMonitor.ps1'
    $script:loader12Path = Join-Path $PSScriptRoot 'TestData\Start-AutopilotMonitor-1.2.ps1'
    $script:msiRunnerPath = Join-Path $PSScriptRoot 'TestData\Invoke-BootstrapDownload-1.0.ps1'
    # The backend's pin of the generated OOBE script, test values filled in.
    $script:oobeScriptPath = Join-Path $script:repoRoot 'src\Backend\AutopilotMonitor.Functions.Tests\TestData\oobe-bootstrap-golden.ps1'
    $script:powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $script:isElevated = (New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $script:payloadFiles = @('AutopilotMonitor.Agent.Core.dll', 'AutopilotMonitor.Agent.cat', 'AutopilotMonitor.Agent.exe', 'AutopilotMonitor.Agent.exe.config')

    # The agent package: text files under the agent's names. --install is swapped out, so
    # nothing in it ever runs.
    $payload = Join-Path $TestDrive 'payload'
    New-Item -ItemType Directory -Path $payload | Out-Null
    foreach ($name in $script:payloadFiles) { Set-Content -LiteralPath (Join-Path $payload $name) -Value "stub $name" }
    $script:agentZip = Join-Path $TestDrive 'AutopilotMonitor-Agent.zip'
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $script:agentZip
    $script:agentZipSha256 = (Get-FileHash -LiteralPath $script:agentZip -Algorithm SHA256).Hash.ToLowerInvariant()

    $script:wrapperText = @'
$global:HarnessDir = $PSScriptRoot
$global:HarnessOptions = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'options.json')) | ConvertFrom-Json

function global:Write-HarnessTrace {
    param([string]$Name, [string]$Detail)
    [System.IO.File]::AppendAllText((Join-Path $global:HarnessDir 'trace.txt'), "$Name|$Detail`r`n")
}

function global:Get-HarnessServerFile {
    param([string]$Uri)
    $source = Join-Path $global:HarnessOptions.Server ($Uri.Substring($Uri.LastIndexOf('/') + 1))
    if (-not [System.IO.File]::Exists($source)) { throw "The remote server returned an error: (404) Not Found. ($Uri)" }
    return $source
}

function global:HarnessInvokeWebRequest {
    [CmdletBinding()]
    param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing, [int]$TimeoutSec, [string]$Method)
    Write-HarnessTrace 'Invoke-WebRequest' $Uri
    [System.IO.File]::Copy((Get-HarnessServerFile $Uri), $OutFile, $true)
}

function global:HarnessInvokeRestMethod {
    [CmdletBinding()]
    param([string]$Uri, [switch]$UseBasicParsing, [int]$TimeoutSec)
    Write-HarnessTrace 'Invoke-RestMethod' $Uri
    return ([System.IO.File]::ReadAllText((Get-HarnessServerFile $Uri)) | ConvertFrom-Json)
}

function global:HarnessGetAuthenticodeSignature {
    [CmdletBinding()]
    param([string]$FilePath)
    Write-HarnessTrace 'Get-AuthenticodeSignature' $FilePath
    return [pscustomobject]@{
        Status            = $global:HarnessOptions.SignatureStatus
        SignerCertificate = [pscustomobject]@{ Subject = $global:HarnessOptions.SignerSubject; Thumbprint = 'HARNESS' }
    }
}

function global:HarnessTestFileCatalog {
    [CmdletBinding()]
    param([string]$Path, [string]$CatalogFilePath, [string[]]$FilesToSkip, [switch]$Detailed)
    Write-HarnessTrace 'Test-FileCatalog' $Path
    return [pscustomobject]@{
        Status       = $(if ($global:HarnessOptions.CatalogValid) { 'Valid' } else { 'ValidationFailed' })
        Signature    = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=glueckkanja AG, O=glueckkanja AG, C=DE' } }
        CatalogItems = @{}
        PathItems    = @{}
    }
}

function global:HarnessGetBootstrapDecision {
    param([int]$MaxBootstrapWindowHours, [int]$OobeProfileMaxAgeMinutes)
    Write-HarnessTrace 'Get-BootstrapDecision' "MaxBootstrapWindowHours=$MaxBootstrapWindowHours"
    $install = [bool]$global:HarnessOptions.Install
    return [pscustomobject]@{ Install = $install; ReasonCode = $(if ($install) { $null } else { 'ProfilesFound' }); RelaxActive = $false }
}

function global:HarnessGetRegistryValue64 {
    param([string]$SubKey, [string]$Name)
    Write-HarnessTrace 'Get-RegistryValue64' "$SubKey\$Name"
    if ($SubKey -eq 'SOFTWARE\AutopilotMonitor' -and $Name -eq 'Deployed') { return $global:HarnessOptions.Marker }
    return $null
}

function global:HarnessGetScheduledTask {
    [CmdletBinding()]
    param([string]$TaskName)
    Write-HarnessTrace 'Get-ScheduledTask' $TaskName
    if ($global:HarnessOptions.TaskPresent) { return [pscustomobject]@{ TaskName = $TaskName } }
    throw (New-Object System.Management.Automation.ErrorRecord((New-Object System.Exception "No MSFT_ScheduledTask objects found with property 'TaskName' equal to '$TaskName'."), 'CmdletizationQuery_NotFound_TaskName,Get-ScheduledTask', [System.Management.Automation.ErrorCategory]::ObjectNotFound, $TaskName))
}

function global:HarnessInvokeAgentInstallMode {
    param([string]$ExePath, [string[]]$Arguments = @('--install'))
    Write-HarnessTrace 'Invoke-AgentInstallMode' ('{0} {1} exists={2}' -f $ExePath, ($Arguments -join ' '), [System.IO.File]::Exists($ExePath))
    return [int]$global:HarnessOptions.InstallExitCode
}

function global:HarnessGetProcess {
    [CmdletBinding()]
    param([string[]]$Name)
    if ($Name -contains 'AutopilotMonitor.Agent') {
        Write-HarnessTrace 'Get-Process' 'AutopilotMonitor.Agent'
        return [pscustomobject]@{ Id = 4242; StartTime = Get-Date }
    }
    return Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
}

function global:HarnessStartSleep {
    [CmdletBinding()]
    param([int]$Seconds, [int]$Milliseconds)
    Write-HarnessTrace 'Start-Sleep' "$Seconds s $Milliseconds ms"
}

function global:HarnessTestIsElevated {
    Write-HarnessTrace 'Test-IsElevated' "$($global:HarnessOptions.Elevated)"
    return [bool]$global:HarnessOptions.Elevated
}

# The protected DACL without the owner change a standard account may not make.
function global:HarnessNewProtectedDirectorySecurity {
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @([System.Security.Principal.WindowsIdentity]::GetCurrent().User, (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')))) {
        $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, $propagation, 'Allow')))
    }
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute', $inherit, $propagation, 'Allow')))
    return $security
}

function global:HarnessTestIsOwnedByUs {
    param([string]$Path)
    if ([System.IO.Path]::GetFileName($Path) -like 'foreign*') { return $false }
    try {
        $owner = (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).GetAccessControl([System.Security.AccessControl.AccessControlSections]::Owner).GetOwner([System.Security.Principal.SecurityIdentifier])
        return ($owner.Value -eq 'S-1-5-18' -or $owner.Value -eq 'S-1-5-32-544' -or $owner -eq [System.Security.Principal.WindowsIdentity]::GetCurrent().User)
    } catch {
        return $false
    }
}

$swaps = @{
    'Invoke-WebRequest'         = 'HarnessInvokeWebRequest'
    'Invoke-RestMethod'         = 'HarnessInvokeRestMethod'
    'Get-AuthenticodeSignature' = 'HarnessGetAuthenticodeSignature'
    'Test-FileCatalog'          = 'HarnessTestFileCatalog'
    'Get-BootstrapDecision'     = 'HarnessGetBootstrapDecision'
    'Get-RegistryValue64'       = 'HarnessGetRegistryValue64'
    'Get-ScheduledTask'         = 'HarnessGetScheduledTask'
    'Invoke-AgentInstallMode'   = 'HarnessInvokeAgentInstallMode'
    'Get-Process'               = 'HarnessGetProcess'
    'Start-Sleep'               = 'HarnessStartSleep'
    'Test-IsOwnedByUs'          = 'HarnessTestIsOwnedByUs'
}
if ($global:HarnessOptions.SwapElevation) { $swaps['Test-IsElevated'] = 'HarnessTestIsElevated' }
if ($global:HarnessOptions.SwapDacl) { $swaps['New-ProtectedDirectorySecurity'] = 'HarnessNewProtectedDirectorySecurity' }
foreach ($name in $swaps.Keys) { Set-Alias -Name $name -Value $swaps[$name] -Scope Global -Force }

# The OOBE script arrives as irm | iex, which ignores #Requires.
if ($global:HarnessOptions.Iex) {
    Invoke-Expression ([System.IO.File]::ReadAllText($global:HarnessOptions.Script))
} else {
    & $global:HarnessOptions.Script @args
}
exit $LASTEXITCODE
'@

    function New-Scenario {
        $root = Join-Path $TestDrive ('s' + [guid]::NewGuid().ToString('N').Substring(0, 7))
        $scenario = [pscustomobject]@{
            Root        = $root
            ProgramData = Join-Path $root 'ProgramData'
            Base        = Join-Path $root 'ProgramData\AutopilotMonitor'
            Temp        = Join-Path $root 'Temp'
            Server      = Join-Path $root 'Server'
            Outside     = Join-Path $root 'Outside'
            Ime         = Join-Path $root 'Ime'
        }
        foreach ($dir in @($scenario.ProgramData, $scenario.Temp, $scenario.Server, $scenario.Outside, $scenario.Ime)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Copy-Item -LiteralPath $script:installerPath -Destination (Join-Path $scenario.Server 'Install-AutopilotMonitor.ps1')
        Copy-Item -LiteralPath $script:agentZip -Destination (Join-Path $scenario.Server 'AutopilotMonitor-Agent.zip')
        Set-Content -LiteralPath (Join-Path $scenario.Server 'version.json') -Value ('{"version":"2.0.9999","sha256":"' + $script:agentZipSha256 + '"}')
        return $scenario
    }

    function Invoke-Scenario {
        param(
            $Scenario,
            [string]$Script,
            [string[]]$Arguments = @(),
            [string]$WorkingDirectory,
            [hashtable]$Options = @{}
        )
        $settings = @{
            Script          = $Script
            Server          = $Scenario.Server
            Install         = $true
            Marker          = $null
            TaskPresent     = $false
            InstallExitCode = 0
            SignatureStatus = 'Valid'
            SignerSubject   = 'CN=glueckkanja AG, O=glueckkanja AG, C=DE'
            CatalogValid    = $true
            Elevated        = $true
            Iex             = $false
        }
        foreach ($key in $Options.Keys) { $settings[$key] = $Options[$key] }
        $settings.SwapElevation = ([bool]$settings.Elevated -ne $script:isElevated)
        $settings.SwapDacl = -not $script:isElevated

        $harness = Join-Path $Scenario.Root 'Harness'
        New-Item -ItemType Directory -Path $harness -Force | Out-Null
        $tracePath = Join-Path $harness 'trace.txt'
        if (Test-Path -LiteralPath $tracePath) { Remove-Item -LiteralPath $tracePath }
        [System.IO.File]::WriteAllText((Join-Path $harness 'options.json'), ($settings | ConvertTo-Json))
        [System.IO.File]::WriteAllText((Join-Path $harness 'run.ps1'), $script:wrapperText)

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $script:powershell
        $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
        $psi.Arguments = ('-NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f (Join-Path $harness 'run.ps1'), ($quoted -join ' ')).Trim()
        $psi.WorkingDirectory = $(if ($WorkingDirectory) { $WorkingDirectory } else { $Scenario.Root })
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.EnvironmentVariables['ProgramData'] = $Scenario.ProgramData
        $psi.EnvironmentVariables['ALLUSERSPROFILE'] = $Scenario.ProgramData
        $psi.EnvironmentVariables['TEMP'] = $Scenario.Temp
        $psi.EnvironmentVariables['TMP'] = $Scenario.Temp
        $process = [System.Diagnostics.Process]::Start($psi)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) { $process.Kill(); throw 'The harness run did not end within five minutes.' }
        $process.WaitForExit()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = $stdout.Result
            StdErr   = $stderr.Result
            Trace    = @(if (Test-Path -LiteralPath $tracePath) { Get-Content -LiteralPath $tracePath })
        }
    }

    # Every entry below $Root, links as links and never followed; run-folder ids as <id>.
    # -Content adds a hash per file, for "exactly as it was".
    function Get-Tree {
        param([string]$Root, [switch]$Content)
        $entries = New-Object System.Collections.Generic.List[string]
        $walk = {
            param([string]$Folder)
            foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($Folder)) {
                $relative = $entry.Substring($Root.Length + 1) -replace '-[0-9a-f]{8}(?=\\|$)', '-<id>'
                $attributes = [System.IO.File]::GetAttributes($entry)
                if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { $entries.Add("$relative -> link"); continue }
                if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) { $entries.Add("$relative\"); & $walk $entry; continue }
                if ($Content) { $relative += ' ' + (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash.Substring(0, 16) }
                $entries.Add($relative)
            }
        }
        if ([System.IO.Directory]::Exists($Root)) { & $walk $Root }
        return (Join-Tree $entries)
    }

    function Join-Tree {
        param([string[]]$Entries)
        $sorted = [string[]]@($Entries | Where-Object { $_ })
        [System.Array]::Sort($sorted, [System.StringComparer]::Ordinal)
        return ($sorted -join "`n")
    }

    function Get-StdErrLines {
        param($Run)
        return @($Run.StdErr -split "`r?`n" | Where-Object { $_ })
    }

    function New-Junction {
        param([string]$Link, [string]$Target)
        cmd.exe /c mklink /J "$Link" "$Target" | Out-Null
    }

    function Add-File {
        param([string]$Path, [string]$Value = 'x')
        New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
        Set-Content -LiteralPath $Path -Value $Value
    }

    # A base with what earlier bootstrap versions and agents could leave behind, plus a file
    # no version knows yet and a junction out of it.
    function Add-Leftovers {
        param($Scenario)
        Add-File (Join-Path $Scenario.Base 'Install-AutopilotMonitor.ps1')
        Add-File (Join-Path $Scenario.Base 'Logs\bootstrap-loader.log')
        Add-File (Join-Path $Scenario.Base 'Logs\bootstrap-script.log')
        Add-File (Join-Path $Scenario.Base 'Agent-Install\Agent\AutopilotMonitor.Agent.Core.dll')
        Add-File (Join-Path $Scenario.Base 'future-version-file.bin')
        Add-File (Join-Path $Scenario.Outside 'target\sentinel.txt') 'must survive'
        New-Junction -Link (Join-Path $Scenario.Base 'linked') -Target (Join-Path $Scenario.Outside 'target')
    }

    $script:installedTree = @(
        'AutopilotMonitor\'
        'AutopilotMonitor\Agent\'
        'AutopilotMonitor\Agent\AutopilotMonitor.Agent.Core.dll'
        'AutopilotMonitor\Agent\AutopilotMonitor.Agent.cat'
        'AutopilotMonitor\Agent\AutopilotMonitor.Agent.exe'
        'AutopilotMonitor\Agent\AutopilotMonitor.Agent.exe.config'
        'AutopilotMonitor\Logs\'
    )
}

Describe 'loader 1.3' {
    It 'installs and leaves only the agent folder, with its log carried over' {
        $s = New-Scenario
        $run = Invoke-Scenario $s -Script $script:loaderPath -Arguments @('-MaxBootstrapWindowHours', '24')

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree ($script:installedTree + 'AutopilotMonitor\Logs\bootstrap-loader.log' + 'AutopilotMonitor\Logs\bootstrap-script.log'))
        Get-Tree $s.Temp | Should -BeNullOrEmpty
        $run.Trace | Should -Contain 'Get-BootstrapDecision|MaxBootstrapWindowHours=24'
        $run.Trace | Should -Contain 'Invoke-WebRequest|https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor.ps1'
        $run.Trace | Should -Contain 'Invoke-WebRequest|https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip'
        $run.Trace | Should -Contain 'Invoke-RestMethod|https://download.autopilotmonitor.com/agent/version.json'
        @($run.Trace | Where-Object { $_ -like 'Test-FileCatalog|*' }).Count | Should -Be 1
        @($run.Trace | Where-Object { $_ -like 'Invoke-AgentInstallMode|*\AutopilotMonitor\Agent\AutopilotMonitor.Agent.exe --install exists=True' }).Count | Should -Be 1
        $run.StdOut | Should -Match 'Bootstrap Completed Successfully'
        Get-Content -LiteralPath (Join-Path $s.Base 'Logs\bootstrap-loader.log') -Raw | Should -Match '=== Autopilot Monitor bootstrap loader v1\.3 started'
        if ($script:isElevated) {
            (Get-Item -LiteralPath $s.Base).GetAccessControl('Owner').GetOwner([System.Security.Principal.SecurityIdentifier]).Value | Should -Be 'S-1-5-32-544'
        }
    }

    It 'skips and leaves nothing behind, also what older versions left and files no version knows' {
        $s = New-Scenario
        Add-Leftovers $s
        $run = Invoke-Scenario $s -Script $script:loaderPath -Options @{ Install = $false }

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -BeNullOrEmpty
        Get-Content -LiteralPath (Join-Path $s.Outside 'target\sentinel.txt') | Should -Be 'must survive'
        $run.StdOut | Should -Match 'No agent on this device: .* removed\.'
        @($run.Trace | Where-Object { $_ -like 'Invoke-WebRequest|*Agent.zip' }).Count | Should -Be 0
    }

    It 'leaves the folder of an agent exactly as it is, self-update leftovers included' {
        $s = New-Scenario
        Add-File (Join-Path $s.Base 'Agent\AutopilotMonitor.Agent.exe') 'running agent'
        Add-File (Join-Path $s.Base 'Agent\AutopilotMonitor.Agent.Core.dll.old') 'previous version'
        Add-File (Join-Path $s.Base 'Updates\AutopilotMonitor-Agent-Update.zip') 'update'
        Add-File (Join-Path $s.Base 'Agent-Update\AutopilotMonitor.Agent.Core.dll') 'staged'
        Add-File (Join-Path $s.Base 'Logs\agent.log') 'agent log'
        Add-File (Join-Path $s.Base 'State\session.id') 'session'
        $before = Get-Tree $s.ProgramData -Content
        $run = Invoke-Scenario $s -Script $script:loaderPath

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData -Content | Should -Be $before
        $run.StdOut | Should -Match 'Agent present \(agent executable\)'
        @($run.Trace | Where-Object { $_ -like 'Invoke-AgentInstallMode|*' }).Count | Should -Be 0
    }

    It 'leaves the folder alone when only the deployment marker <Case>' -TestCases @(
        @{ Case = 'is there (logs kept after enrollment)'; Options = @{ Marker = '2026-10-01T08:00:00Z' } }
        @{ Case = 'is missing but the agent task exists'; Options = @{ TaskPresent = $true } }
    ) {
        $s = New-Scenario
        Add-File (Join-Path $s.Base 'Logs\agent.log') 'kept log'
        $before = Get-Tree $s.ProgramData -Content
        $run = Invoke-Scenario $s -Script $script:loaderPath -Options $Options

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData -Content | Should -Be $before
    }

    It 'keeps its log when the installer fails, and the next run removes it' {
        $s = New-Scenario
        Remove-Item -LiteralPath (Join-Path $s.Server 'version.json')
        $failed = Invoke-Scenario $s -Script $script:loaderPath

        $failed.ExitCode | Should -Be 0
        @(Get-StdErrLines $failed).Count | Should -Be 1
        $failed.StdErr | Should -Match '^AutopilotMonitor bootstrap failed: No SHA-256 hash available'
        Get-Tree $s.ProgramData | Should -Be (Join-Tree @(
            'AutopilotMonitor-Loader-<id>\'
            'AutopilotMonitor-Loader-<id>\bootstrap-loader.log'
            'AutopilotMonitor\'
            'AutopilotMonitor\Logs\'
            'AutopilotMonitor\Logs\bootstrap-loader.log'
            'AutopilotMonitor\Logs\bootstrap-script.log'
        ))

        Set-Content -LiteralPath (Join-Path $s.Server 'version.json') -Value ('{"version":"2.0.9999","sha256":"' + $script:agentZipSha256 + '"}')
        $next = Invoke-Scenario $s -Script $script:loaderPath

        $next.ExitCode | Should -Be 0
        $next.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree ($script:installedTree + 'AutopilotMonitor\Logs\bootstrap-loader.log' + 'AutopilotMonitor\Logs\bootstrap-script.log'))
    }

    It 'runs nothing it cannot verify: <Case>' -TestCases @(
        @{ Case = 'unsigned'; Options = @{ SignatureStatus = 'NotSigned' }; Edit = $null }
        @{ Case = 'another publisher'; Options = @{ SignerSubject = 'CN=Somebody Else, O=Somebody Else Ltd, C=US' }; Edit = $null }
        @{ Case = 'an installer below 2.7'; Options = @{}; Edit = @('$ScriptVersion = "2.7"', '$ScriptVersion = "2.6"') }
        @{ Case = 'not the installer'; Options = @{}; Edit = @('$ScriptRole = "AutopilotMonitor-Installer"', '$ScriptRole = "AutopilotMonitor-Other"') }
    ) {
        $s = New-Scenario
        if ($Edit) {
            $served = Join-Path $s.Server 'Install-AutopilotMonitor.ps1'
            $text = [System.IO.File]::ReadAllText($served)
            $text.Contains($Edit[0]) | Should -BeTrue
            [System.IO.File]::WriteAllText($served, $text.Replace($Edit[0], $Edit[1]))
        }
        $run = Invoke-Scenario $s -Script $script:loaderPath -Options $Options

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        @($run.Trace | Where-Object { $_ -like 'Invoke-WebRequest|*Install-AutopilotMonitor.ps1' }).Count | Should -Be 3
        @($run.Trace | Where-Object { $_ -like 'Get-BootstrapDecision|*' }).Count | Should -Be 0
        Get-Tree $s.ProgramData | Should -Be (Join-Tree @('AutopilotMonitor-Loader-<id>\', 'AutopilotMonitor-Loader-<id>\bootstrap-loader.log'))
    }

    It 'refuses to run without SYSTEM or administrator rights and creates nothing' {
        $s = New-Scenario
        $run = Invoke-Scenario $s -Script $script:loaderPath -Options @{ Elevated = $false }

        $run.ExitCode | Should -Be 0
        @(Get-StdErrLines $run) | Should -Be 'AutopilotMonitor loader: not running as SYSTEM or administrator; nothing was downloaded or changed.'
        Get-Tree $s.ProgramData | Should -BeNullOrEmpty
        @($run.Trace | Where-Object { $_ -like 'Invoke-WebRequest|*' }).Count | Should -Be 0
    }
}

Describe 'installer assigned directly' {
    BeforeEach {
        $s = New-Scenario
        $imeCopy = Join-Path $s.Ime '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0.ps1'
        Copy-Item -LiteralPath $script:installerPath -Destination $imeCopy
    }

    It 'installs, and the copy IME runs stays where IME put it' {
        $run = Invoke-Scenario $s -Script $imeCopy

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree ($script:installedTree + 'AutopilotMonitor\Logs\bootstrap-script.log'))
        Test-Path -LiteralPath $imeCopy | Should -BeTrue
        Get-Tree $s.Temp | Should -BeNullOrEmpty
    }

    It 'skips and leaves nothing behind' {
        Add-Leftovers $s
        $run = Invoke-Scenario $s -Script $imeCopy -Options @{ Install = $false }

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -BeNullOrEmpty
        Get-Content -LiteralPath (Join-Path $s.Outside 'target\sentinel.txt') | Should -Be 'must survive'
    }

    It 'fails with exit code 1 and exactly one line on stderr: <Case>' -TestCases @(
        @{ Case = 'the download fails'; Missing = 'AutopilotMonitor-Agent.zip'; Options = @{}; Message = 'AutopilotMonitor bootstrap failed: The remote server returned an error: (404) Not Found.*' }
        @{ Case = 'the payload fails its catalog'; Missing = $null; Options = @{ CatalogValid = $false }; Message = 'AutopilotMonitor bootstrap failed: Payload verification FAILED*' }
        @{ Case = 'the agent install fails'; Missing = $null; Options = @{ InstallExitCode = 7 }; Message = 'AutopilotMonitor bootstrap failed: Agent install failed with exit code 7' }
    ) {
        if ($Missing) { Remove-Item -LiteralPath (Join-Path $s.Server $Missing) }
        $run = Invoke-Scenario $s -Script $imeCopy -Options $Options

        $run.ExitCode | Should -Be 1
        @(Get-StdErrLines $run).Count | Should -Be 1
        @(Get-StdErrLines $run)[0] | Should -BeLike $Message
        Get-Tree $s.ProgramData | Should -Not -Match 'Agent-Install'
        Test-Path -LiteralPath (Join-Path $s.Base 'Logs\bootstrap-script.log') | Should -BeTrue
    }

    It 'refuses to run without SYSTEM or administrator rights and changes nothing' {
        Add-File (Join-Path $s.Base 'Logs\bootstrap-script.log') 'earlier run'
        $before = Get-Tree $s.ProgramData -Content
        $run = Invoke-Scenario $s -Script $imeCopy -Options @{ Elevated = $false }

        $run.ExitCode | Should -Be 1
        @(Get-StdErrLines $run) | Should -Be 'AutopilotMonitor bootstrap: not running as SYSTEM or administrator; nothing was changed.'
        Get-Tree $s.ProgramData -Content | Should -Be $before
    }

    It 'does not install into a folder that holds another account''s entries, and leaves them' {
        Add-File (Join-Path $s.Base 'foreign-planted.dll') 'planted'
        Add-File (Join-Path $s.Base 'Logs\bootstrap-script.log') 'earlier run'
        $run = Invoke-Scenario $s -Script $imeCopy

        $run.ExitCode | Should -Be 1
        @(Get-StdErrLines $run).Count | Should -Be 1
        $run.StdErr | Should -Match 'holds entries another account created'
        Get-Tree $s.ProgramData | Should -Be (Join-Tree @('AutopilotMonitor\', 'AutopilotMonitor\foreign-planted.dll'))
        @($run.Trace | Where-Object { $_ -like 'Invoke-WebRequest|*' }).Count | Should -Be 0
    }

    It 'leaves another account''s entries and their folder when it skips' {
        Add-File (Join-Path $s.Base 'foreign-planted.dll') 'planted'
        Add-File (Join-Path $s.Base 'Logs\bootstrap-script.log') 'earlier run'
        $run = Invoke-Scenario $s -Script $imeCopy -Options @{ Install = $false }

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree @('AutopilotMonitor\', 'AutopilotMonitor\foreign-planted.dll'))
    }

    It 'never follows an agent folder that is a link: <Case>' -TestCases @(
        @{ Case = 'install'; Install = $true; ExitCode = 1 }
        @{ Case = 'skip'; Install = $false; ExitCode = 0 }
    ) {
        Add-File (Join-Path $s.Outside 'target\sentinel.txt') 'must survive'
        New-Junction -Link $s.Base -Target (Join-Path $s.Outside 'target')
        $run = Invoke-Scenario $s -Script $imeCopy -Options @{ Install = $Install }

        $run.ExitCode | Should -Be $ExitCode
        Get-Tree $s.ProgramData | Should -Be 'AutopilotMonitor -> link'
        Get-Tree (Join-Path $s.Outside 'target') | Should -Be 'sentinel.txt'
    }
}

Describe 'loader 1.2, frozen' {
    It 'installs with the current installer, which removes its copy and keeps the loader start line' {
        $s = New-Scenario
        $run = Invoke-Scenario $s -Script $script:loader12Path

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree ($script:installedTree + 'AutopilotMonitor\Logs\bootstrap-loader.log' + 'AutopilotMonitor\Logs\bootstrap-script.log'))
        $log = @(Get-Content -LiteralPath (Join-Path $s.Base 'Logs\bootstrap-loader.log'))
        $log[0] | Should -Match '^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\] === Autopilot Monitor bootstrap loader v1\.2 started \(carried over by the bootstrapper\) ===$'
    }

    It 'leaves the folder of an agent as it is, but not the copy the old loader saved there' {
        $s = New-Scenario
        Add-File (Join-Path $s.Base 'Agent\AutopilotMonitor.Agent.exe') 'running agent'
        Add-File (Join-Path $s.Base 'Logs\agent.log') 'agent log'
        $run = Invoke-Scenario $s -Script $script:loader12Path

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree @(
            'AutopilotMonitor\', 'AutopilotMonitor\Agent\', 'AutopilotMonitor\Agent\AutopilotMonitor.Agent.exe'
            'AutopilotMonitor\Logs\', 'AutopilotMonitor\Logs\agent.log', 'AutopilotMonitor\Logs\bootstrap-loader.log'
        ))
        Get-Content -LiteralPath (Join-Path $s.Base 'Agent\AutopilotMonitor.Agent.exe') | Should -Be 'running agent'
    }

    It 'skips with the current installer and leaves nothing behind' {
        $s = New-Scenario
        Add-Leftovers $s
        $run = Invoke-Scenario $s -Script $script:loader12Path -Options @{ Install = $false }

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -BeNullOrEmpty
        Get-Content -LiteralPath (Join-Path $s.Outside 'target\sentinel.txt') | Should -Be 'must survive'
    }
}

Describe 'bootstrap MSI layouts, started in the folder the MSI installed' {
    It '<Name> skips and leaves <Left>' -TestCases @(
        @{ Name = 'MSI 1.2 (loader 1.3)'; File = 'Start-AutopilotMonitor.ps1'; Source = 'loader'; Arguments = @('-LogFileName', 'bootstrap-msi.log'); Left = 'nothing'; Tree = @() }
        @{ Name = 'MSI 1.1 (loader 1.2)'; File = 'Start-AutopilotMonitor.ps1'; Source = 'loader12'; Arguments = @('-LogFileName', 'bootstrap-msi.log'); Left = 'nothing'; Tree = @() }
        @{ Name = 'MSI 1.0 (own runner)'; File = 'Invoke-BootstrapDownload.ps1'; Source = 'msiRunner'; Arguments = @(); Left = 'only the log the runner holds open'; Tree = @('AutopilotMonitor\', 'AutopilotMonitor\Logs\', 'AutopilotMonitor\Logs\bootstrap-msi.log') }
    ) {
        $s = New-Scenario
        $msi = Join-Path $s.Base 'Msi'
        New-Item -ItemType Directory -Path $msi -Force | Out-Null
        $sourcePath = @{ loader = $script:loaderPath; loader12 = $script:loader12Path; msiRunner = $script:msiRunnerPath }[$Source]
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $msi $File)
        $run = Invoke-Scenario $s -Script (Join-Path $msi $File) -Arguments $Arguments -WorkingDirectory $msi -Options @{ Install = $false }

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree $Tree)
    }

    It '<Name> installs' -TestCases @(
        @{ Name = 'MSI 1.2 (loader 1.3)'; File = 'Start-AutopilotMonitor.ps1'; Source = 'loader'; Arguments = @('-LogFileName', 'bootstrap-msi.log') }
        @{ Name = 'MSI 1.1 (loader 1.2)'; File = 'Start-AutopilotMonitor.ps1'; Source = 'loader12'; Arguments = @('-LogFileName', 'bootstrap-msi.log') }
        @{ Name = 'MSI 1.0 (own runner)'; File = 'Invoke-BootstrapDownload.ps1'; Source = 'msiRunner'; Arguments = @() }
    ) {
        $s = New-Scenario
        $msi = Join-Path $s.Base 'Msi'
        New-Item -ItemType Directory -Path $msi -Force | Out-Null
        $sourcePath = @{ loader = $script:loaderPath; loader12 = $script:loader12Path; msiRunner = $script:msiRunnerPath }[$Source]
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $msi $File)
        $run = Invoke-Scenario $s -Script (Join-Path $msi $File) -Arguments $Arguments -WorkingDirectory $msi

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree ($script:installedTree + 'AutopilotMonitor\Logs\bootstrap-msi.log' + 'AutopilotMonitor\Logs\bootstrap-script.log'))
    }
}

Describe 'OOBE script, as the backend generates it' {
    It 'installs the agent with the bootstrap token and leaves only the agent folder' {
        $s = New-Scenario
        Add-Leftovers $s
        $run = Invoke-Scenario $s -Script $script:oobeScriptPath -Options @{ Iex = $true }

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        Get-Tree $s.ProgramData | Should -Be (Join-Tree ($script:installedTree + 'AutopilotMonitor\Logs\oobe_bootstrap.log'))
        Get-Tree $s.Temp | Should -BeNullOrEmpty
        Get-Content -LiteralPath (Join-Path $s.Outside 'target\sentinel.txt') | Should -Be 'must survive'
        $run.Trace | Should -Contain ('Invoke-AgentInstallMode|' + $s.Base + '\Agent\AutopilotMonitor.Agent.exe --install --no-auth --bootstrap-token 22222222-2222-2222-2222-222222222222 --tenant-id 11111111-1111-1111-1111-111111111111 exists=True')
        @($run.Trace | Where-Object { $_ -like 'Test-FileCatalog|*' }).Count | Should -Be 1
    }

    It 'only runs --install again when the agent is there' {
        $s = New-Scenario
        Add-File (Join-Path $s.Base 'Agent\AutopilotMonitor.Agent.exe') 'running agent'
        Add-File (Join-Path $s.Base 'State\session.id') 'session'
        $run = Invoke-Scenario $s -Script $script:oobeScriptPath -Options @{ Iex = $true }

        $run.ExitCode | Should -Be 0
        Get-Tree $s.ProgramData | Should -Be (Join-Tree @(
            'AutopilotMonitor\', 'AutopilotMonitor\Agent\', 'AutopilotMonitor\Agent\AutopilotMonitor.Agent.exe'
            'AutopilotMonitor\Logs\', 'AutopilotMonitor\Logs\oobe_bootstrap.log', 'AutopilotMonitor\State\', 'AutopilotMonitor\State\session.id'
        ))
        @($run.Trace | Where-Object { $_ -like 'Invoke-WebRequest|*' }).Count | Should -Be 0
        @($run.Trace | Where-Object { $_ -like 'Invoke-AgentInstallMode|*' }).Count | Should -Be 1
    }

    It 'refuses to run without SYSTEM or administrator rights and creates nothing' {
        $s = New-Scenario
        $run = Invoke-Scenario $s -Script $script:oobeScriptPath -Options @{ Iex = $true; Elevated = $false }

        $run.ExitCode | Should -Be 1
        $run.StdOut | Should -Match 'Run this script from PowerShell as SYSTEM or administrator\. Nothing was changed\.'
        Get-Tree $s.ProgramData | Should -BeNullOrEmpty
    }
}

Describe 'publish pipeline and dev channel' {
    BeforeAll {
        $script:rendered = Join-Path $TestDrive 'rendered'
        $script:pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $script:publish = Join-Path $script:repoRoot '.github\scripts\Publish-BootstrapScripts.ps1'

        # Output read as text from a process of its own: a stderr line must not become an error
        # record, which ends the test under the 'Stop' preference CI runs with.
        function Invoke-Publish {
            param([string[]]$Arguments)
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $script:pwsh
            $psi.Arguments = ('-NoProfile -File "{0}" {1}' -f $script:publish, ($Arguments -join ' ')).Trim()
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $process = [System.Diagnostics.Process]::Start($psi)
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = @(($stdout.Result + "`n" + $stderr.Result) -split "`r?`n") }
        }

        $dryRun = Invoke-Publish @('-DryRun', '-SignedOutputDir', ('"' + $script:rendered + '"'))
        if ($dryRun.ExitCode -ne 0) { throw "Publish dry run failed: $($dryRun.Output -join [Environment]::NewLine)" }
        $script:publishOutput = $dryRun.Output
    }

    # A loader that went live first would refuse the installer still being served.
    It 'puts both installers before both loaders' {
        $order = @($script:publishOutput | Where-Object { $_ -match '\[dry-run\] (\S+\.ps1) ' } | ForEach-Object { $Matches[1] })
        $order[0..3] | Should -Be @('Install-AutopilotMonitor.ps1', 'Install-AutopilotMonitor-Dev.ps1', 'Start-AutopilotMonitor.ps1', 'Start-AutopilotMonitor-Dev.ps1')
        @($script:publishOutput | Where-Object { $_ -like '*Installer accepted: AutopilotMonitor-Installer v*' }).Count | Should -Be 2
    }

    It 'refuses to publish an installer the loader would refuse: <Case>' -TestCases @(
        @{ Case = 'below the minimum version'; Old = '$ScriptVersion = "2.7"'; New = '$ScriptVersion = "2.6"' }
        @{ Case = 'without its role line'; Old = '$ScriptRole = "AutopilotMonitor-Installer"'; New = '# no role' }
        @{ Case = 'with a second version line'; Old = '$ScriptVersion = "2.7"'; New = "`$ScriptVersion = `"2.7`"`n`$ScriptVersion = `"2.8`"" }
    ) {
        $root = Join-Path $TestDrive ('repo' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        New-Item -ItemType Directory -Path (Join-Path $root 'scripts\Bootstrap') -Force | Out-Null
        Copy-Item -LiteralPath $script:loaderPath -Destination (Join-Path $root 'scripts\Bootstrap')
        $text = [System.IO.File]::ReadAllText($script:installerPath)
        $text.Contains($Old) | Should -BeTrue
        [System.IO.File]::WriteAllText((Join-Path $root 'scripts\Bootstrap\Install-AutopilotMonitor.ps1'), $text.Replace($Old, $New))

        $refused = Invoke-Publish @('-DryRun', '-Channel', 'dev', '-RepoRoot', ('"' + $root + '"'))

        $refused.ExitCode | Should -Not -Be 0
        ($refused.Output -join "`n") | Should -Match 'The loader would refuse Install-AutopilotMonitor-Dev\.ps1'
        ($refused.Output -join "`n") | Should -Not -Match '\[dry-run\]'
    }

    It 'the dev loader fetches the dev installer, which installs the dev agent' {
        $s = New-Scenario
        Copy-Item -LiteralPath (Join-Path $script:rendered 'Install-AutopilotMonitor-Dev.ps1') -Destination $s.Server
        Copy-Item -LiteralPath $script:agentZip -Destination (Join-Path $s.Server 'AutopilotMonitor-Agent-dev.zip')
        Set-Content -LiteralPath (Join-Path $s.Server 'version-dev.json') -Value ('{"version":"2.0.9999","sha256":"' + $script:agentZipSha256 + '"}')
        Remove-Item -LiteralPath (Join-Path $s.Server 'Install-AutopilotMonitor.ps1'), (Join-Path $s.Server 'AutopilotMonitor-Agent.zip'), (Join-Path $s.Server 'version.json')
        $devLoader = Join-Path $s.Ime 'Start-AutopilotMonitor-Dev.ps1'
        Copy-Item -LiteralPath (Join-Path $script:rendered 'Start-AutopilotMonitor-Dev.ps1') -Destination $devLoader
        $run = Invoke-Scenario $s -Script $devLoader

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        @($run.Trace | Where-Object { $_ -like 'Invoke-*' }) | Should -Be @(
            'Invoke-WebRequest|https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor-Dev.ps1'
            'Invoke-RestMethod|https://download.autopilotmonitor.com/agent/version-dev.json'
            'Invoke-WebRequest|https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent-dev.zip'
            ('Invoke-AgentInstallMode|' + $s.Base + '\Agent\AutopilotMonitor.Agent.exe --install exists=True')
        )
        Get-Tree $s.ProgramData | Should -Be (Join-Tree ($script:installedTree + 'AutopilotMonitor\Logs\bootstrap-loader.log' + 'AutopilotMonitor\Logs\bootstrap-script.log'))
    }

    It 'the stable loader in its published form runs the stable installer in its published form' {
        $s = New-Scenario
        Copy-Item -LiteralPath (Join-Path $script:rendered 'Install-AutopilotMonitor.ps1') -Destination $s.Server -Force
        $loader = Join-Path $s.Ime 'Start-AutopilotMonitor.ps1'
        Copy-Item -LiteralPath (Join-Path $script:rendered 'Start-AutopilotMonitor.ps1') -Destination $loader
        [System.IO.File]::ReadAllText($loader).Contains("`r`n") | Should -BeTrue
        $run = Invoke-Scenario $s -Script $loader

        $run.ExitCode | Should -Be 0
        $run.StdErr | Should -BeNullOrEmpty
        $run.StdOut | Should -Match 'Installer accepted: AutopilotMonitor-Installer v2\.7'
        $run.StdOut | Should -Match 'Bootstrap Completed Successfully'
    }
}
