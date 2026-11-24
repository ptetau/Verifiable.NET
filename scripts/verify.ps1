[CmdletBinding()]
param(
    [switch]$InstallZ3,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    if ($InstallZ3) {
        & (Join-Path $PSScriptRoot "install-z3.ps1")
    }

    $env:VERIFIABLE_AGENT = "1"
    $env:GALAXYCHECK_AGENT = "1"

    Write-Host "==> Building Verifiable.NET.sln ($Configuration) with Z3 SMT verification..."
    dotnet build Verifiable.NET.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "==> Running all GalaxyCheck & Roslyn verification tests..."
    dotnet test Verifiable.NET.sln -c $Configuration --no-build --verbosity normal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "==> Verifiable.NET verification succeeded."
} finally {
    Pop-Location
}
