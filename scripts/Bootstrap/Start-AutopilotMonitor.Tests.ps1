# Pester tests (Pester 5+) for the stage-1 loader Start-AutopilotMonitor.ps1.
#
# The script under test is dot-sourced; its entry guard prevents the loader from
# executing, so loading it here is side-effect free. Network and signature checks
# are mocked -- what is verified is the DECISION: what does the loader accept, what
# does it refuse, how often does it retry, and what does it leave behind.
#
# Run under Windows PowerShell 5.1 to match the IME runtime:
#   powershell.exe -NoProfile -Command "& { $c = New-PesterConfiguration; $c.Run.Path = 'scripts\Bootstrap'; $c.Output.Verbosity = 'Detailed'; Invoke-Pester -Configuration $c }"
#
# This file MUST remain pure ASCII (PS 5.1 reads BOM-less files as ANSI).

BeforeAll {
    . (Join-Path $PSScriptRoot 'Start-AutopilotMonitor.ps1')

    function New-FakeSignature {
        param([string]$Status, [string]$Subject)
        $cert = if ($Subject) {
            [pscustomobject]@{ Subject = $Subject; Thumbprint = '0123456789ABCDEF' }
        } else { $null }
        return [pscustomobject]@{ Status = $Status; SignerCertificate = $cert }
    }

    # Arguments as PowerShell hands them to a script, names marked as names; an array of
    # plain strings would bind by position instead.
    function ConvertTo-ScriptArguments { return , $args }

    function Get-ScriptAst {
        param([string]$Path)
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
        return [pscustomobject]@{ Ast = $ast; Tokens = $tokens; Errors = $errors }
    }

    $script:loaderPath = Join-Path $PSScriptRoot 'Start-AutopilotMonitor.ps1'
    $script:installerPath = Join-Path $PSScriptRoot 'Install-AutopilotMonitor.ps1'
}

Describe 'Test-BootstrapSignature' {
    BeforeEach {
        Mock Write-Log { }
    }

    It 'accepts a valid signature from the expected publisher' {
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'Valid' -Subject 'CN=glueckkanja AG, O=glueckkanja AG, C=DE' }
        Test-BootstrapSignature -Path 'TestDrive:\x.ps1' | Should -BeTrue
    }

    # An EV subject is a line of street address and registration numbers; the log gets the CN.
    It 'logs the signer CN, not the whole subject' {
        Mock Get-AuthenticodeSignature {
            New-FakeSignature -Status 'Valid' -Subject 'CN=glueckkanja AG, O=glueckkanja AG, L=Offenbach am Main, C=DE, SERIALNUMBER=HRB 12381'
        }
        Test-BootstrapSignature -Path 'TestDrive:\x.ps1' | Should -BeTrue
        Should -Invoke Write-Log -ParameterFilter { $Message -eq 'Signature status: Valid. Signer: glueckkanja AG' }
    }

    It 'refuses an unsigned script' {
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'NotSigned' -Subject $null }
        Test-BootstrapSignature -Path 'TestDrive:\x.ps1' | Should -BeFalse
    }

    It 'refuses a tampered script (hash mismatch)' {
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'HashMismatch' -Subject 'CN=glueckkanja AG, O=glueckkanja AG, C=DE' }
        Test-BootstrapSignature -Path 'TestDrive:\x.ps1' | Should -BeFalse
    }

    It 'refuses a signature whose chain does not validate' {
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'UnknownError' -Subject 'CN=glueckkanja AG, O=glueckkanja AG, C=DE' }
        Test-BootstrapSignature -Path 'TestDrive:\x.ps1' | Should -BeFalse
    }

    # The whole point of the pinned publisher: a validly signed script from someone else
    # is exactly what an attacker with any code-signing certificate can produce.
    It 'refuses a valid signature from a different publisher' {
        Mock Get-AuthenticodeSignature { New-FakeSignature -Status 'Valid' -Subject 'CN=Somebody Else, O=Somebody Else Ltd, C=US' }
        Test-BootstrapSignature -Path 'TestDrive:\x.ps1' | Should -BeFalse
    }

    It 'refuses when the signature check itself fails' {
        Mock Get-AuthenticodeSignature { throw 'file not found' }
        Test-BootstrapSignature -Path 'TestDrive:\x.ps1' | Should -BeFalse
    }
}

Describe 'Get-InstallerIdentity' {
    BeforeAll {
        $script:roleLine = '$ScriptRole = "AutopilotMonitor-Installer"'
    }

    It 'reads the role and the version of LF text' {
        $id = Get-InstallerIdentity -Text ("param()`n`$ScriptVersion = `"2.7`"`n" + $script:roleLine + "`n")
        $id.Role | Should -BeExactly 'AutopilotMonitor-Installer'
        $id.Version | Should -Be ([version]'2.7')
    }

    It 'reads CRLF text that ends in an Authenticode signature block' {
        $text = (@('$ScriptVersion = "2.7"', $script:roleLine, '# SIG # Begin signature block', '# MIIFxwYJKoZIhvcNAQcCoIIFuDCCBbQCAQExCzAJBgUrDgMCGgUAMGkGCisGAQQB', '# SIG # End signature block') -join "`r`n") + "`r`n"
        (Get-InstallerIdentity -Text $text).Version | Should -Be ([version]'2.7')
    }

    It 'compares versions as numbers, so 2.10 is above 2.7' {
        $id = Get-InstallerIdentity -Text ("`$ScriptVersion = `"2.10`"`n" + $script:roleLine + "`n")
        $id.Version -gt [version]'2.7' | Should -BeTrue
    }

    It 'returns nothing for <Case>' -TestCases @(
        @{ Case = 'a missing role line'; Lines = @('$ScriptVersion = "2.7"') }
        @{ Case = 'a missing version line'; Lines = @('$ScriptRole = "AutopilotMonitor-Installer"') }
        @{ Case = 'a role line in a comment'; Lines = @('# $ScriptRole = "AutopilotMonitor-Installer"', '$ScriptVersion = "2.7"') }
        @{ Case = 'an indented version line'; Lines = @('$ScriptRole = "AutopilotMonitor-Installer"', '    $ScriptVersion = "2.7"') }
        @{ Case = 'a second version line'; Lines = @('$ScriptRole = "AutopilotMonitor-Installer"', '$ScriptVersion = "2.7"', '$ScriptVersion = "9.9"') }
        @{ Case = 'a second role line'; Lines = @('$ScriptRole = "AutopilotMonitor-Installer"', '$ScriptRole = "Other"', '$ScriptVersion = "2.7"') }
        @{ Case = 'a pre-release version'; Lines = @('$ScriptRole = "AutopilotMonitor-Installer"', '$ScriptVersion = "2.7-beta"') }
        @{ Case = 'a version line with more code'; Lines = @('$ScriptRole = "AutopilotMonitor-Installer"', '$ScriptVersion = "2.7"; $x = 1') }
    ) {
        Get-InstallerIdentity -Text (($Lines -join "`n") + "`n") | Should -BeNullOrEmpty
    }

    # The pair the loader and the installer in this repository must keep: the published
    # installer has to pass the loader that ships with it.
    It 'accepts the installer in this repository' {
        $id = Get-InstallerIdentity -Text ([System.IO.File]::ReadAllText($script:installerPath))
        $id.Role | Should -BeExactly $ExpectedInstallerRole
        $id.Version -ge $MinimumInstallerVersion | Should -BeTrue
    }
}

Describe 'Test-InstallerIdentity' {
    BeforeEach {
        Mock Write-Log { }
        $script:candidate = Join-Path $TestDrive ("installer-" + [guid]::NewGuid().ToString('N') + '.ps1')
    }

    It 'accepts version <Version>' -TestCases @(@{ Version = '2.8' }, @{ Version = '2.10' }, @{ Version = '3.0.1' }) {
        [System.IO.File]::WriteAllText($script:candidate, "`$ScriptVersion = `"$Version`"`n`$ScriptRole = `"AutopilotMonitor-Installer`"`n")
        Test-InstallerIdentity -Path $script:candidate | Should -BeTrue
    }

    # Every installer the publisher ever signed keeps a valid signature; the minimum keeps the
    # older ones from running.
    It 'refuses an installer below the minimum version' {
        [System.IO.File]::WriteAllText($script:candidate, "`$ScriptVersion = `"2.7`"`n`$ScriptRole = `"AutopilotMonitor-Installer`"`n")
        Test-InstallerIdentity -Path $script:candidate | Should -BeFalse
        Should -Invoke Write-Log -ParameterFilter { $Message -like '*below the minimum 2.8*' }
    }

    # The publisher signs other scripts too.
    It 'refuses another role' {
        [System.IO.File]::WriteAllText($script:candidate, "`$ScriptVersion = `"2.8`"`n`$ScriptRole = `"AutopilotMonitor-Uninstaller`"`n")
        Test-InstallerIdentity -Path $script:candidate | Should -BeFalse
    }

    It 'refuses a script without the identity lines' {
        [System.IO.File]::WriteAllText($script:candidate, "Write-Host 'signed, but not the installer'`n")
        Test-InstallerIdentity -Path $script:candidate | Should -BeFalse
    }

    It 'refuses a file it cannot read' {
        Test-InstallerIdentity -Path (Join-Path $TestDrive 'missing.ps1') | Should -BeFalse
    }
}

Describe 'Get-VerifiedBootstrapScript' {
    BeforeEach {
        Mock Write-Log { }
        Mock Start-Sleep { }
        Mock Invoke-WebRequest { [System.IO.File]::WriteAllText($OutFile, 'downloaded') }
        Mock Test-InstallerIdentity { $true }
        # The test account owns the folder; the loader asks for SYSTEM or Administrators.
        Mock Test-IsOwnedByUs { $true }
        $script:run = Join-Path $TestDrive ('run-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $script:run | Out-Null
        $script:dest = Join-Path $script:run 'Install-AutopilotMonitor.ps1'
        $RunState = [pscustomobject]@{ LogFile = $null; Installer = $null }
    }
    AfterEach {
        if ($RunState.Installer) { $RunState.Installer.Dispose() }
    }

    It 'returns after the first attempt when the download verifies' {
        Mock Test-BootstrapSignature { $true }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeTrue
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly
    }

    It 'gives up after three attempts when verification keeps failing' {
        Mock Test-BootstrapSignature { $false }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeFalse
        Should -Invoke Invoke-WebRequest -Times 3 -Exactly
    }

    # A truncated or proxy-mangled body fails verification, so the retry has to cover the
    # download AND the check, not just the transport.
    It 'retries a failed verification and accepts a later attempt' {
        $script:calls = 0
        Mock Test-BootstrapSignature { $script:calls++; return ($script:calls -ge 2) }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeTrue
        Should -Invoke Invoke-WebRequest -Times 2 -Exactly
    }

    It 'retries a transport failure' {
        $script:downloads = 0
        Mock Invoke-WebRequest { $script:downloads++; if ($script:downloads -eq 1) { throw 'connection reset' }; [System.IO.File]::WriteAllText($OutFile, 'downloaded') }
        Mock Test-BootstrapSignature { $true }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeTrue
        Should -Invoke Invoke-WebRequest -Times 2 -Exactly
    }

    It 'refuses a validly signed script that is not the current installer' {
        Mock Test-BootstrapSignature { $true }
        Mock Test-InstallerIdentity { $false }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeFalse
        Should -Invoke Invoke-WebRequest -Times 3 -Exactly
    }

    # The identity lines count only once the signature proves they are the publisher's.
    It 'reads the identity only after the signature passed' {
        Mock Test-BootstrapSignature { $false }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeFalse
        Should -Invoke Test-InstallerIdentity -Times 0 -Exactly
    }

    It 'stops without downloading when the run folder is no longer a folder of ours' {
        Mock Test-IsOwnedByUs { $false }
        Mock Test-BootstrapSignature { $true }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeFalse
        Should -Invoke Invoke-WebRequest -Times 0 -Exactly
    }

    # The file that was checked is the file that runs: nobody can replace it in between.
    It 'keeps the verified file open, so it cannot be replaced or deleted' {
        Mock Test-BootstrapSignature { $true }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeTrue
        $RunState.Installer | Should -Not -BeNullOrEmpty
        { [System.IO.File]::WriteAllText($script:dest, 'replaced') } | Should -Throw
        { [System.IO.File]::Delete($script:dest) } | Should -Throw
        [System.IO.File]::ReadAllText($script:dest) | Should -Be 'downloaded'
    }

    It 'holds nothing when no attempt verifies' {
        Mock Test-BootstrapSignature { $false }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination $script:dest -RunFolder $script:run | Should -BeFalse
        $RunState.Installer | Should -BeNullOrEmpty
        { [System.IO.File]::Delete($script:dest) } | Should -Not -Throw
    }
}

Describe 'Get-DownloadSecurityProtocol' {
    It 'keeps the system default, which includes TLS 1.3' {
        Get-DownloadSecurityProtocol -Current ([Net.SecurityProtocolType]::SystemDefault) | Should -Be ([Net.SecurityProtocolType]::SystemDefault)
    }

    It 'adds TLS 1.2 to an explicit list' {
        Get-DownloadSecurityProtocol -Current ([Net.SecurityProtocolType]'Ssl3, Tls') | Should -Be ([Net.SecurityProtocolType]'Ssl3, Tls, Tls12')
    }
}

Describe 'Remove-LoaderRunFiles' {
    BeforeEach {
        Mock Write-Host { }
        $run = Join-Path $TestDrive ("run-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $run | Out-Null
        $installer = Join-Path $run 'Install-AutopilotMonitor.ps1'
        $log = Join-Path $run 'bootstrap-loader.log'
        Set-Content -Path $installer -Value 'downloaded'
        Set-Content -Path $log -Value 'log'
        $RunState = [pscustomobject]@{ LogFile = $log; Installer = $null }
    }

    It 'removes the download, the log and the folder' {
        Remove-LoaderRunFiles -RunFolder $run -InstallerPath $installer
        Test-Path -LiteralPath $run | Should -BeFalse
        $RunState.LogFile | Should -BeNullOrEmpty
    }

    It 'releases the held download before deleting it' {
        $RunState.Installer = New-Object System.IO.FileStream($installer, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        Remove-LoaderRunFiles -RunFolder $run -InstallerPath $installer
        Test-Path -LiteralPath $run | Should -BeFalse
        $RunState.Installer | Should -BeNullOrEmpty
    }

    It 'keeps the log and the folder with -KeepLog, but never the download' {
        Remove-LoaderRunFiles -RunFolder $run -InstallerPath $installer -KeepLog
        @(Get-ChildItem -LiteralPath $run -Force | ForEach-Object Name) | Should -Be @('bootstrap-loader.log')
    }

    It 'leaves a folder that holds anything else, without throwing' {
        Set-Content -Path (Join-Path $run 'other.txt') -Value 'other'
        { Remove-LoaderRunFiles -RunFolder $run -InstallerPath $installer } | Should -Not -Throw
        @(Get-ChildItem -LiteralPath $run -Force | ForEach-Object Name) | Should -Be @('other.txt')
        Should -Invoke Write-Host -ParameterFilter { "$Object" -like 'INFO: run folder*' }
    }
}

Describe 'Start-BootstrapLoader' {
    BeforeEach {
        $script:savedProgramData = $env:ProgramData
        $env:ProgramData = Join-Path $TestDrive ("pd-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $env:ProgramData | Out-Null
        $script:runFolder = Join-Path $env:ProgramData 'AutopilotMonitor-Loader-0a1b2c3d'
        $script:marker = Join-Path $TestDrive ("ran-" + [guid]::NewGuid().ToString('N') + '.txt')

        $RunState = [pscustomobject]@{ LogFile = $null; Installer = $null }
        $InstallerArguments = @()

        Mock Write-Host { }
        Mock Start-Sleep { }
        Mock Test-IsElevated { $true }
        # A standard user cannot make Administrators the owner, so the run folder is a plain
        # one here; New-RunFolder itself is tested with the installer, which shares it.
        Mock New-RunFolder { [System.IO.Directory]::CreateDirectory($script:runFolder) | Out-Null; return $script:runFolder }
    }

    AfterEach {
        $env:ProgramData = $script:savedProgramData
    }

    It 'refuses to run without SYSTEM or administrator rights and creates nothing' {
        Mock Test-IsElevated { $false }
        $stderr = New-Object System.IO.StringWriter
        $previous = [Console]::Error
        [Console]::SetError($stderr)
        try { Start-BootstrapLoader } finally { [Console]::SetError($previous) }
        $stderr.ToString() | Should -Match 'not running as SYSTEM or administrator'
        Should -Invoke New-RunFolder -Times 0 -Exactly
        @(Get-ChildItem -LiteralPath $env:ProgramData -Force).Count | Should -Be 0
    }

    It 'runs nothing and keeps only its log when no download verifies' {
        Mock Get-VerifiedBootstrapScript { [System.IO.File]::WriteAllText($Destination, "Set-Content -LiteralPath '$($script:marker)' -Value ran"); $false }
        Start-BootstrapLoader
        Test-Path -LiteralPath $script:marker | Should -BeFalse
        @(Get-ChildItem -LiteralPath $script:runFolder -Force | ForEach-Object Name) | Should -Be @('bootstrap-loader.log')
        Get-Content -LiteralPath (Join-Path $script:runFolder 'bootstrap-loader.log') -Raw | Should -Match 'Nothing was executed'
    }

    It 'runs the verified installer with the passed arguments by name and removes its folder after exit 0' {
        $agentLog = Join-Path $env:ProgramData 'AutopilotMonitor\Logs\agent.log'
        New-Item -ItemType Directory -Path (Split-Path $agentLog) -Force | Out-Null
        Set-Content -Path $agentLog -Value 'agent'
        Mock Get-VerifiedBootstrapScript {
            [System.IO.File]::WriteAllText($Destination, "param([string]`$AgentDownloadUrl = 'default', [int]`$MaxBootstrapWindowHours = 12)`nSet-Content -LiteralPath '$($script:marker)' -Value `"`$AgentDownloadUrl `$MaxBootstrapWindowHours`"`nexit 0`n")
            $true
        }
        $InstallerArguments = ConvertTo-ScriptArguments -MaxBootstrapWindowHours 24

        Start-BootstrapLoader

        Get-Content -LiteralPath $script:marker | Should -Be 'default 24'
        Test-Path -LiteralPath $script:runFolder | Should -BeFalse
        Get-Content -LiteralPath $agentLog | Should -Be 'agent'
    }

    It 'keeps its log but not the installer when the installer fails' {
        Mock Get-VerifiedBootstrapScript { [System.IO.File]::WriteAllText($Destination, "exit 1`n"); $true }
        Start-BootstrapLoader
        @(Get-ChildItem -LiteralPath $script:runFolder -Force | ForEach-Object Name) | Should -Be @('bootstrap-loader.log')
        Get-Content -LiteralPath (Join-Path $script:runFolder 'bootstrap-loader.log') -Raw | Should -Match 'Installer finished \(exit code 1\)'
    }

    It 'keeps its log when the installer throws' {
        Mock Get-VerifiedBootstrapScript { [System.IO.File]::WriteAllText($Destination, "throw 'installer broke'`n"); $true }
        Start-BootstrapLoader
        Get-Content -LiteralPath (Join-Path $script:runFolder 'bootstrap-loader.log') -Raw | Should -Match 'Installer threw: installer broke'
    }

    # An installer that ends without an exit code must not inherit a 0 from earlier work.
    It 'does not take an earlier exit code for the installer''s' {
        Mock Get-VerifiedBootstrapScript { [System.IO.File]::WriteAllText($Destination, "Write-Output 'no exit code'`n"); $true }
        $global:LASTEXITCODE = 0
        Start-BootstrapLoader
        Test-Path -LiteralPath (Join-Path $script:runFolder 'bootstrap-loader.log') | Should -BeTrue
    }

    It 'names its version first in its log, in the form the installer carries over' {
        Mock Get-VerifiedBootstrapScript { $false }
        Start-BootstrapLoader
        $first = (Get-Content -LiteralPath (Join-Path $script:runFolder 'bootstrap-loader.log'))[0]
        $first | Should -Match '^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\] === Autopilot Monitor bootstrap loader v\d+(\.\d+){0,3} started'
    }
}

Describe 'loader contract' {
    BeforeAll {
        $script:loader = Get-ScriptAst $script:loaderPath
    }

    It 'parses without errors' {
        $script:loader.Errors.Count | Should -Be 0
    }

    # Unbound arguments reach the installer only while the script is not an advanced one.
    It 'stays a plain script, so unbound arguments are kept for the installer' {
        $script:loader.Ast.ParamBlock.Attributes.Count | Should -Be 0
        @($script:loader.Ast.ParamBlock.Parameters.Attributes | Where-Object { $_.TypeName.Name -eq 'Parameter' }) | Should -BeNullOrEmpty
    }

    # The six functions also run in the installer and, embedded, in the OOBE script.
    It 'carries the shared functions word for word as the installer has them' {
        $installer = Get-ScriptAst $script:installerPath
        foreach ($name in @('Test-IsElevated', 'New-ProtectedDirectorySecurity', 'Test-IsReparsePoint', 'Test-IsOwnedByUs', 'New-RunFolderName', 'New-RunFolder')) {
            $mine = $script:loader.Ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name }, $true)
            $theirs = $installer.Ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name }, $true)
            $mine | Should -Not -BeNullOrEmpty -Because $name
            $mine.Extent.Text | Should -BeExactly $theirs.Extent.Text -Because $name
        }
    }

    It 'calls no command beyond the few it needs' {
        $own = @($script:loader.Ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
        $allowed = $own + @('Add-Content', 'ForEach-Object', 'Get-AuthenticodeSignature', 'Get-Date', 'Get-Item', 'Invoke-WebRequest', 'Join-Path', 'New-Object', 'Out-Null', 'Start-Sleep', 'Test-Path', 'Write-Host')
        $used = @($script:loader.Ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Sort-Object -Unique)
        @($used | Where-Object { $allowed -notcontains $_ }) | Should -BeNullOrEmpty
    }

    It 'deletes only the download, its log and its own empty folder' {
        $deletes = @($script:loader.Ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $a.Member.Extent.Text -eq 'Delete' }, $true))
        $deletes.Count | Should -Be 3
        foreach ($delete in $deletes) {
            if ($delete.Expression.Extent.Text -eq '[System.IO.Directory]') {
                $delete.Arguments[1].Extent.Text | Should -Be '$false'
            } else {
                $delete.Expression.Extent.Text | Should -Be '[System.IO.File]'
            }
        }
    }

    It 'never names the agent folder' {
        $strings = @($script:loader.Tokens | Where-Object { $_ -is [System.Management.Automation.Language.StringToken] } | ForEach-Object { $_.Value })
        @($strings | Where-Object { $_ -match '(^|\\)AutopilotMonitor($|\\)' }) | Should -BeNullOrEmpty
    }
}

Describe 'publish contract' {
    BeforeAll {
        $script:loaderText = Get-Content $script:loaderPath -Raw
    }

    # Publish-BootstrapScripts.ps1 renders the -Dev loader by substituting this exact
    # literal and fails the publish when it is gone. Catching that here means the PR fails
    # instead of the publish.
    It 'carries the dev-render anchor the publisher substitutes' {
        $anchor = '$BootstrapUrl = "https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor.ps1"'
        $script:loaderText.Contains($anchor) | Should -BeTrue
    }

    # The publisher parses this to write version.json.loaderVersion and to run the
    # version-bump guard.
    It 'declares a parsable ScriptVersion' {
        $script:loaderText -match '(?m)^\$ScriptVersion = "\d+(\.\d+){1,3}"\r?$' | Should -BeTrue
    }

    It 'pins the expected publisher by subject, not by thumbprint' {
        $script:loaderText -match '\$ExpectedPublisher\s*=\s*"\*O=' | Should -BeTrue
    }

    # The bootstrap MSI installs this same file and runs it with -LogFileName so the delivery
    # channel stays visible in a diagnostics package. Renaming the parameter on either side
    # would leave the MSI channel silently logging under the wrong name.
    It 'keeps the -LogFileName contract the bootstrap MSI relies on' {
        $script:loaderText -match '\[string\]\$LogFileName' | Should -BeTrue

        $wxs = Get-Content (Join-Path $PSScriptRoot '..\..\src\Agent\AutopilotMonitor.BootstrapMsi\Package.wxs') -Raw
        $wxs.Contains('[INSTALLFOLDER]Start-AutopilotMonitor.ps1') | Should -BeTrue
        $wxs.Contains('-LogFileName bootstrap-msi.log') | Should -BeTrue
    }

    # Everything of ours lives under ProgramData, so the copy the MSI installs does too, where
    # the installer removes it. A standard user could have created that folder first, so the
    # action never runs the file as such: it checks the signature on the bytes it read and runs
    # those bytes, from System32.
    It 'is installed by the bootstrap MSI under ProgramData and run only after the check' {
        $wxs = Get-Content (Join-Path $PSScriptRoot '..\..\src\Agent\AutopilotMonitor.BootstrapMsi\Package.wxs') -Raw
        $wxs | Should -Match '<StandardDirectory Id="CommonAppDataFolder">'
        $wxs | Should -Not -Match 'ProgramFiles'
        $wxs.Contains('Get-AuthenticodeSignature -Content $b -SourcePathOrExtension ''.ps1''') | Should -BeTrue
        $wxs.Contains('Directory="System64Folder"') | Should -BeTrue
    }
}
