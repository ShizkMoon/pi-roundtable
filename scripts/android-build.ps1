[CmdletBinding()]
param(
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$androidRoot = Join-Path $workspace 'apps\android'
$gradleWrapper = Join-Path $androidRoot 'gradlew.bat'

$proxyValue = if ($env:HTTPS_PROXY) { $env:HTTPS_PROXY } else { $env:HTTP_PROXY }
if ($proxyValue) {
    $proxy = [Uri]$proxyValue
    if ($proxy.Scheme -notin @('http', 'https')) {
        throw "Gradle proxy must use an HTTP-compatible endpoint."
    }
    $proxyPort = if ($proxy.IsDefaultPort) {
        if ($proxy.Scheme -eq 'https') { 443 } else { 80 }
    } else {
        $proxy.Port
    }
    $proxyOptions = @(
        "-Dhttp.proxyHost=$($proxy.Host)",
        "-Dhttp.proxyPort=$proxyPort",
        "-Dhttps.proxyHost=$($proxy.Host)",
        "-Dhttps.proxyPort=$proxyPort"
    ) -join ' '
    $gradleOptions = @($env:GRADLE_OPTS, $proxyOptions) | Where-Object { $_ }
    $env:GRADLE_OPTS = $gradleOptions -join ' '
}

$arguments = @('--no-daemon', '--console=plain')
if ($Offline) {
    $arguments += '--offline'
}
$arguments += ':app:testDebugUnitTest'
$arguments += ':app:assembleDebug'

Push-Location $androidRoot
try {
    & $gradleWrapper @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Android build failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}
