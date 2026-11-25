[CmdletBinding()]
param(
    [string]$Version = "z3-5.3.3",
    [string]$Destination = "",
    [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path (Split-Path -Parent $PSScriptRoot) "src\Verifiable.Z3\tools"
}


$patterns = @{
    "win-x64"     = @{ Pattern = "x64-win\.zip$";       Exe = "z3.exe"; Fallback = "$Version-x64-win.zip" }
    "win-arm64"   = @{ Pattern = "arm64-win\.zip$";     Exe = "z3.exe"; Fallback = "$Version-arm64-win.zip" }
    "linux-x64"   = @{ Pattern = "x64-glibc.*\.zip$";   Exe = "z3";     Fallback = "$Version-x64-glibc-2.39.zip" }
    "linux-arm64" = @{ Pattern = "arm64-glibc.*\.zip$"; Exe = "z3";     Fallback = "$Version-arm64-glibc-2.38.zip" }
    "osx-x64"     = @{ Pattern = "x64-osx.*\.zip$";     Exe = "z3";     Fallback = "$Version-x64-osx-13.3.zip" }
    "osx-arm64"   = @{ Pattern = "arm64-osx.*\.zip$";   Exe = "z3";     Fallback = "$Version-arm64-osx-13.3.zip" }
}

$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/Z3Prover/z3/releases/tags/$Version" -Headers @{ "User-Agent" = "Verifiable.NET-Packager" } -TimeoutSec 20
} catch {
    Write-Warning "GitHub API lookup failed ($($_.Exception.Message)); falling back to known asset names for $Version."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("verifiable-z3-pack-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null

try {
    foreach ($rid in $Rids) {
        if (-not $patterns.ContainsKey($rid)) {
            throw "Unsupported RID '$rid'."
        }

        $spec = $patterns[$rid]
        $targetDir = Join-Path $Destination $rid
        $targetExe = Join-Path $targetDir $spec.Exe
        if (Test-Path $targetExe) {
            Write-Host "[$rid] Already staged at $targetExe"
            continue
        }

        $assetUrl = $null
        if ($release -and $release.assets) {
            $asset = $release.assets | Where-Object { $_.name -match $spec.Pattern } | Select-Object -First 1
            if ($asset) { $assetUrl = $asset.browser_download_url }
        }
        if (-not $assetUrl) {
            $assetUrl = "https://github.com/Z3Prover/z3/releases/download/$Version/$($spec.Fallback)"
        }

        $zipPath = Join-Path $tmpRoot "$rid.zip"
        Write-Host "[$rid] Downloading $assetUrl ..."
        Invoke-WebRequest -Uri $assetUrl -OutFile $zipPath -UseBasicParsing

        $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -match "/bin/$([regex]::Escape($spec.Exe))$" } | Select-Object -First 1
            if (-not $entry) {
                throw "Could not find bin/$($spec.Exe) in $assetUrl"
            }

            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $targetExe, $true)
            Write-Host "[$rid] Staged $targetExe ($([Math]::Round((Get-Item $targetExe).Length / 1MB, 2)) MB)"
        } finally {
            $zip.Dispose()
        }
    }
} finally {
    if (Test-Path $tmpRoot) {
        Remove-Item -Path $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
