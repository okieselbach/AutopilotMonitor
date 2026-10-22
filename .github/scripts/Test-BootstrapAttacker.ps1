<#
.SYNOPSIS
    Checks the bootstrap's folders against a real standard user.

.DESCRIPTION
    Creates a temporary local standard user and, with that user's token, tries what any local user
    on an enrolling device could try against the folders the loader and the installer create:
    write, change, rename and delete files, add entries, move or delete the folder. Every attempt
    must be denied. Folders the user creates first, or entries the user plants in an unprotected
    agent folder of an older installer, must make the installer stop instead of installing into
    them. A control attempt proves the user can write where users may.

    The folders come from the installer's own functions, in a scratch copy of ProgramData that
    carries the real ProgramData permissions.

    CI only (bootstrap-script-gates.yml): it needs an elevated runner, and it creates a local
    account and removes it again.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
)

$ErrorActionPreference = 'Stop'

# The user's token, for impersonation in this process (a CI helper; customer scripts never
# compile code).
Add-Type -Namespace ApmCi -Name Native -MemberDefinition @'
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool LogonUser(string user, string domain, string password, int logonType, int logonProvider, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
'@

. (Join-Path $RepoRoot 'scripts\Bootstrap\Install-AutopilotMonitor.ps1')

$results = New-Object System.Collections.Generic.List[object]

function Invoke-AsUser {
    param([scriptblock]$Action)
    [System.Security.Principal.WindowsIdentity]::RunImpersonated($script:token, [Action]$Action)
}

# An attempt the user must not get through: it has to fail with "access denied", not with
# any other error that would only hide a broken test.
function Assert-Denied {
    param([string]$Name, [scriptblock]$Attempt)
    $outcome = 'allowed'
    try {
        Invoke-AsUser $Attempt
    } catch {
        $base = $_.Exception.GetBaseException()
        $outcome = if ($base -is [System.UnauthorizedAccessException] -or $base.HResult -eq -2147024891) { 'denied' } else { "error: $($base.Message)" }
    }
    $results.Add([pscustomobject]@{ Check = $Name; Expected = 'denied'; Actual = $outcome })
}

function Assert-That {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    $results.Add([pscustomobject]@{ Check = $Name; Expected = 'true'; Actual = $(if ($Condition) { 'true' } else { "false $Detail".Trim() }) })
}

# What a bootstrap running as SYSTEM leaves as owner; the default for other admin-created
# entries differs between Windows editions.
function Set-AdministratorsOwner {
    param([string]$Path)
    $security = (Get-Item -LiteralPath $Path -Force).GetAccessControl('Owner')
    $security.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    (Get-Item -LiteralPath $Path -Force).SetAccessControl($security)
}

$name = 'apm-ci-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$password = [guid]::NewGuid().ToString('N') + 'Aa1!'
$root = Join-Path $env:SystemDrive ('apm-ci-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$pd = Join-Path $root 'ProgramData'

New-LocalUser -Name $name -Password (ConvertTo-SecureString $password -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires | Out-Null
try {
    try { Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $name } catch { if ($_.FullyQualifiedErrorId -notlike 'MemberExists*') { throw } }
    $token = $null
    # Interactive logon first (what a local user has); network logon where a server allows no
    # local logon for Users.
    if (-not [ApmCi.Native]::LogonUser($name, '.', $password, 2, 0, [ref]$token) -and
        -not [ApmCi.Native]::LogonUser($name, '.', $password, 3, 0, [ref]$token)) {
        throw "LogonUser failed with Win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())."
    }
    $script:token = $token
    $userSid = (New-Object System.Security.Principal.NTAccount($name)).Translate([System.Security.Principal.SecurityIdentifier])
    $script:probe = $null
    Invoke-AsUser { $script:probe = [System.Security.Principal.WindowsIdentity]::GetCurrent() }
    Assert-That 'impersonation runs as the test user' ($script:probe.User -eq $userSid) "$($script:probe.User)"
    Assert-That 'the test user is no administrator' (-not (New-Object System.Security.Principal.WindowsPrincipal($script:probe)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator))

    # A scratch ProgramData with the real one's permissions.
    New-Item -ItemType Directory -Path $pd -Force | Out-Null
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetSecurityDescriptorSddlForm((Get-Acl -LiteralPath $env:ProgramData).GetSecurityDescriptorSddlForm('Access'), 'Access')
    # Nothing from the drive root, whose default lets authenticated users change subfolders.
    $security.SetAccessRuleProtection($true, $false)
    (Get-Item -LiteralPath $pd).SetAccessControl($security)

    # Control: the user may create in ProgramData, so every denial below is the folders' doing.
    $control = Join-Path $pd 'user-may-create-here'
    Invoke-AsUser { [System.IO.Directory]::CreateDirectory($control) | Out-Null; [System.IO.File]::WriteAllText((Join-Path $control 'x.txt'), 'x') }
    Assert-That 'control: the user can create in ProgramData' (Test-Path -LiteralPath (Join-Path $control 'x.txt'))

    # The loader's run folder with a downloaded installer in it.
    $run = New-RunFolder -Parent $pd -Prefix 'AutopilotMonitor-Loader-'
    $download = Join-Path $run 'Install-AutopilotMonitor.ps1'
    [System.IO.File]::WriteAllText($download, 'verified installer')
    Assert-Denied 'run folder: add a file' { [System.IO.File]::WriteAllText((Join-Path $run 'planted.ps1'), 'x') }
    Assert-Denied 'run folder: add a folder or link' { [System.IO.Directory]::CreateDirectory((Join-Path $run 'planted')) | Out-Null }
    Assert-Denied 'run folder: change the download' { [System.IO.File]::AppendAllText($download, 'tampered') }
    Assert-Denied 'run folder: replace the download' { [System.IO.File]::WriteAllText($download, 'replaced') }
    Assert-Denied 'run folder: rename the download' { [System.IO.File]::Move($download, $download + '.old') }
    Assert-Denied 'run folder: delete the download' { [System.IO.File]::Delete($download) }
    Assert-Denied 'run folder: rename the folder' { [System.IO.Directory]::Move($run, $run + '-moved') }
    Assert-Denied 'run folder: delete the folder' { [System.IO.Directory]::Delete($run, $true) }
    Assert-That 'run folder: the download is unchanged' ([System.IO.File]::ReadAllText($download) -eq 'verified installer')

    # The agent folder an install prepares.
    $base = Join-Path $pd 'AutopilotMonitor'
    Initialize-AgentBase -Path $base
    $logs = [System.IO.Directory]::CreateDirectory((Join-Path $base 'Logs')).FullName
    [System.IO.File]::WriteAllText((Join-Path $logs 'bootstrap-script.log'), 'log')
    Assert-Denied 'agent folder: add a file' { [System.IO.File]::WriteAllText((Join-Path $base 'planted.dll'), 'x') }
    Assert-Denied 'agent folder: add a folder or link' { [System.IO.Directory]::CreateDirectory((Join-Path $base 'Agent')) | Out-Null }
    Assert-Denied 'agent folder: change a log' { [System.IO.File]::AppendAllText((Join-Path $logs 'bootstrap-script.log'), 'x') }
    Assert-Denied 'agent folder: delete a log' { [System.IO.File]::Delete((Join-Path $logs 'bootstrap-script.log')) }
    Assert-Denied 'agent folder: rename the folder' { [System.IO.Directory]::Move($base, $base + '-moved') }
    $owner = (Get-Item -LiteralPath $base).GetAccessControl('Owner').GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    Assert-That 'agent folder: owned by Administrators' ($owner -eq 'S-1-5-32-544') $owner
    [System.IO.Directory]::Delete($base, $true)

    # The user creates the agent folder first: the installer must neither install into it nor
    # remove it.
    Invoke-AsUser { [System.IO.Directory]::CreateDirectory($base) | Out-Null; [System.IO.File]::WriteAllText((Join-Path $base 'planted.dll'), 'x') }
    Assert-That 'squatted agent folder: seen as another account''s' ((Get-FolderState $base) -eq 'Foreign')
    $refused = $false
    try { Initialize-AgentBase -Path $base } catch { $refused = $_.Exception.Message -like '*(Foreign); not installing into it.' }
    Assert-That 'squatted agent folder: the install refuses it' $refused
    Assert-That 'squatted agent folder: a skip leaves it' ((Remove-AgentBaseOnSkip -Path $base) -like '*left as it is (Foreign).')
    Assert-That 'squatted agent folder: the planted file stays' (Test-Path -LiteralPath (Join-Path $base 'planted.dll'))
    [System.IO.Directory]::Delete($base, $true)

    # An agent folder an older installer created without protection: the user plants a file,
    # the install protects the folder and stops at the planted file.
    [System.IO.Directory]::CreateDirectory($base) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $base 'old-installer.log'), 'old')
    Set-AdministratorsOwner $base
    Set-AdministratorsOwner (Join-Path $base 'old-installer.log')
    Invoke-AsUser { [System.IO.File]::WriteAllText((Join-Path $base 'planted.dll'), 'x') }
    $refused = $false
    try { Initialize-AgentBase -Path $base } catch { $refused = $_.Exception.Message -like '*holds entries another account created*' }
    Assert-That 'planted entry: the install stops' $refused
    Assert-That 'planted entry: own leftovers are gone, the planted file stays' ((-not (Test-Path -LiteralPath (Join-Path $base 'old-installer.log'))) -and (Test-Path -LiteralPath (Join-Path $base 'planted.dll')))
    Assert-Denied 'planted entry: the protected folder takes no more' { [System.IO.File]::WriteAllText((Join-Path $base 'second.dll'), 'x') }
    [System.IO.Directory]::Delete($base, $true)

    # The user creates a run folder name first: it is never used.
    $squatted = Join-Path $pd 'AutopilotMonitor-Loader-00000000'
    Invoke-AsUser { [System.IO.Directory]::CreateDirectory($squatted) | Out-Null }
    $script:names = [System.Collections.Generic.Queue[string]]::new([string[]]@('00000000', '11111111'))
    function New-RunFolderName { return $script:names.Dequeue() }
    $fresh = New-RunFolder -Parent $pd -Prefix 'AutopilotMonitor-Loader-'
    Assert-That 'squatted run folder: a new name is drawn' ($fresh -eq (Join-Path $pd 'AutopilotMonitor-Loader-11111111')) $fresh
    $squattedOwner = (Get-Item -LiteralPath $squatted).GetAccessControl('Owner').GetOwner([System.Security.Principal.SecurityIdentifier])
    Assert-That 'squatted run folder: still the user''s, untouched' ($squattedOwner -eq $userSid)
} finally {
    if ($script:token) { $script:token.Dispose() }
    Remove-LocalUser -Name $name -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object { $_.Actual -ne $_.Expected })
if ($failed.Count -gt 0) { throw "Attacker test: $($failed.Count) of $($results.Count) checks failed." }
Write-Host "Attacker test: all $($results.Count) checks passed."
