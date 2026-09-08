# Pester tests (Pester 5+) for the stage-1 loader Start-AutopilotMonitor.ps1.
#
# The script under test is dot-sourced; its entry guard prevents the loader from
# executing, so loading it here is side-effect free. Network and signature checks
# are mocked -- what is verified is the DECISION: what does the loader accept, what
# does it refuse, and how often does it retry.
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

Describe 'Get-VerifiedBootstrapScript' {
    BeforeEach {
        Mock Write-Log { }
        Mock Start-Sleep { }
        Mock Invoke-WebRequest { }
    }

    It 'returns after the first attempt when the download verifies' {
        Mock Test-BootstrapSignature { $true }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination 'TestDrive:\s.ps1' | Should -BeTrue
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly
    }

    It 'gives up after three attempts when verification keeps failing' {
        Mock Test-BootstrapSignature { $false }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination 'TestDrive:\s.ps1' | Should -BeFalse
        Should -Invoke Invoke-WebRequest -Times 3 -Exactly
    }

    # A truncated or proxy-mangled body fails verification, so the retry has to cover the
    # download AND the check, not just the transport.
    It 'retries a failed verification and accepts a later attempt' {
        $script:calls = 0
        Mock Test-BootstrapSignature { $script:calls++; return ($script:calls -ge 2) }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination 'TestDrive:\s.ps1' | Should -BeTrue
        Should -Invoke Invoke-WebRequest -Times 2 -Exactly
    }

    It 'retries a transport failure' {
        $script:downloads = 0
        Mock Invoke-WebRequest { $script:downloads++; if ($script:downloads -eq 1) { throw 'connection reset' } }
        Mock Test-BootstrapSignature { $true }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination 'TestDrive:\s.ps1' | Should -BeTrue
        Should -Invoke Invoke-WebRequest -Times 2 -Exactly
    }

    It 'never executes anything when no attempt verifies' {
        Mock Test-BootstrapSignature { $false }
        Get-VerifiedBootstrapScript -Url 'https://example.invalid/s.ps1' -Destination 'TestDrive:\s.ps1' | Should -BeFalse
    }
}

Describe 'publish contract' {
    BeforeAll {
        $script:loaderText = Get-Content (Join-Path $PSScriptRoot 'Start-AutopilotMonitor.ps1') -Raw
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
        $script:loaderText -match '\$ScriptVersion\s*=\s*"([\d\.\-a-zA-Z]+)"' | Should -BeTrue
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
}
