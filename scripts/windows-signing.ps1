function Resolve-WindowsSignTool {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $candidates = [System.Collections.Generic.List[string]]::new()
    $appCertificationTool = Join-Path $kitsRoot 'App Certification Kit\signtool.exe'
    if (Test-Path -LiteralPath $appCertificationTool -PathType Leaf) {
        $candidates.Add($appCertificationTool)
    }
    $binRoot = Join-Path $kitsRoot 'bin'
    if (Test-Path -LiteralPath $binRoot -PathType Container) {
        Get-ChildItem -LiteralPath $binRoot -Directory |
            Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
            ForEach-Object {
                $candidate = Join-Path $_.FullName 'x64\signtool.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $candidates.Add($candidate)
                }
            }
    }
    if ($candidates.Count -eq 0) {
        throw 'signtool.exe was not found in PATH or the Windows 10 SDK.'
    }
    return $candidates[0]
}

function Invoke-WindowsArtifactSigning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string[]]$Path,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9A-Fa-f]{40}$')]
        [string]$CertificateThumbprint,

        [ValidateSet('CurrentUser', 'LocalMachine')]
        [string]$CertificateStoreLocation = 'CurrentUser',

        [string]$TimestampUrl,

        [switch]$AllowUntimestamped,

        [switch]$RequireTrustedSignature
    )

    if ([string]::IsNullOrWhiteSpace($TimestampUrl) -and !$AllowUntimestamped) {
        throw 'A RFC3161 TimestampUrl is required unless AllowUntimestamped is explicit.'
    }
    if (![string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $timestampUri = $null
        if (![Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
            $timestampUri.Scheme -notin @('http', 'https')) {
            throw 'TimestampUrl must be an absolute HTTP or HTTPS URI.'
        }
    }

    $thumbprint = $CertificateThumbprint.ToUpperInvariant()
    $storePath = "Cert:\$CertificateStoreLocation\My\$thumbprint"
    $certificate = Get-Item -LiteralPath $storePath -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or !$certificate.HasPrivateKey) {
        throw "The code-signing certificate is unavailable or lacks a private key: $storePath"
    }
    if (($certificate.EnhancedKeyUsageList | ForEach-Object ObjectId) -notcontains '1.3.6.1.5.5.7.3.3') {
        throw "Certificate $thumbprint is not valid for code signing."
    }

    $signTool = Resolve-WindowsSignTool
    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $Path) {
        $resolved = [System.IO.Path]::GetFullPath($item)
        if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Signing input does not exist: $resolved"
        }
        $arguments = @('sign', '/fd', 'SHA256', '/sha1', $thumbprint, '/s', 'My')
        if ($CertificateStoreLocation -eq 'LocalMachine') {
            $arguments += '/sm'
        }
        if (![string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
        }
        $arguments += $resolved
        & $signTool @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed to sign $resolved with exit code $LASTEXITCODE."
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $resolved
        if ($null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $thumbprint -or
            $signature.Status -in @('NotSigned', 'HashMismatch')) {
            throw "Authenticode signature presence verification failed for $resolved ($($signature.Status))."
        }
        if (![string]::IsNullOrWhiteSpace($TimestampUrl) -and $null -eq $signature.TimeStamperCertificate) {
            throw "The signed artifact does not contain the required timestamp: $resolved"
        }
        if ($RequireTrustedSignature) {
            $verifyArguments = @('verify', '/pa', '/all')
            if (![string]::IsNullOrWhiteSpace($TimestampUrl)) {
                $verifyArguments += '/tw'
            }
            $verifyArguments += $resolved
            & $signTool @verifyArguments | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "Windows trust verification failed for $resolved with exit code $LASTEXITCODE."
            }
        }
        $results.Add([ordered]@{
            path = $resolved
            thumbprint = $thumbprint
            status = $signature.Status.ToString()
            timestamped = $null -ne $signature.TimeStamperCertificate
            trustedVerificationRequired = [bool]$RequireTrustedSignature
        })
    }
    return $results
}
