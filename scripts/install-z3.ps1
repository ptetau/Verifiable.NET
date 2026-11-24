[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:USERPROFILE ".verifiable\z3"),
    [string]$Version = "z3-5.3.3"
)

$ErrorActionPreference = "Stop"

$z3Exe = Join-Path $Destination "z3.exe"
if (Test-Path $z3Exe) {
    $ver = & $z3Exe --version 2>&1
    Write-Host "Z3 already installed at $z3Exe ($ver)"
    Write-Host "VERIFIABLE_Z3_PATH=$z3Exe"
    exit 0
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$assetUrl = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/Z3Prover/z3/releases/latest" -Headers @{ "User-Agent" = "Verifiable.NET-Installer" } -TimeoutSec 15
    $asset = $release.assets | Where-Object { $_.name -match "x64-win\.zip$" } | Select-Object -First 1
    if ($asset) {
        $assetUrl = $asset.browser_download_url
    }
} catch {
    Write-Warning "GitHub API lookup failed ($($_.Exception.Message)); falling back to $Version."
}

if (-not $assetUrl) {
    $assetUrl = "https://github.com/Z3Prover/z3/releases/download/$Version/$Version-x64-win.zip"
}

$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("verifiable-z3-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$zipPath = Join-Path $tmpRoot "z3.zip"
$extractPath = Join-Path $tmpRoot "extract"

try {
    Write-Host "Downloading Z3 from $assetUrl ..."
    Invoke-WebRequest -Uri $assetUrl -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

    $foundExe = Get-ChildItem -Path $extractPath -Recurse -Filter "z3.exe" | Select-Object -First 1
    if (-not $foundExe) {
        throw "Could not find z3.exe inside downloaded archive."
    }

    $binDir = $foundExe.Directory.FullName
    Copy-Item -Path (Join-Path $binDir "*") -Destination $Destination -Recurse -Force

    $ver = & $z3Exe --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "z3.exe verification failed with exit code $LASTEXITCODE`: $ver"
    }

    Write-Host "Successfully installed $ver to $z3Exe"
    Write-Host "VERIFIABLE_Z3_PATH=$z3Exe"
} finally {
    if (Test-Path $tmpRoot) {
        Remove-Item -Path $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
