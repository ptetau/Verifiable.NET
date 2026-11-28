# Copies the live kernel (src/Verifiable.Core) into the frozen copy that proves it
# (src/Verifiable.Analyzers.Bootstrap/Kernel). See docs/trusted-core.md.
#
# Before copying, it builds Verifiable.Core, so the live kernel is proved by the current frozen kernel.
# Only a kernel that an earlier kernel has proved is promoted.
#
# -NoProof skips that build. Use it only when the bootstrap analyzer cannot compile, because the analyzer
# already calls kernel code the frozen copy lacks. The new kernel is then not checked by an earlier one:
# say so in the pull request.
[CmdletBinding()]
param(
    [switch]$NoProof
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$live = Join-Path $repoRoot "src/Verifiable.Core"
$frozen = Join-Path $repoRoot "src/Verifiable.Analyzers.Bootstrap/Kernel"

if (-not $NoProof) {
    Write-Host "==> Proving the live kernel with the current frozen kernel..."
    dotnet build (Join-Path $live "Verifiable.Core.csproj") -c Release
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine("The live kernel is not proved by the frozen kernel, so it was not promoted. Fix the errors above first.")
        exit 1
    }
}

Write-Host "==> Copying the live kernel into $frozen"
New-Item -ItemType Directory -Force -Path $frozen | Out-Null
Get-ChildItem -Path $frozen -Recurse -File -Filter *.cs | Remove-Item -Force

$liveFull = (Resolve-Path $live).Path
# The same files Verifiable.Analyzers compiles: everything but AssemblyInfo.cs, Proofs/, obj/ and bin/.
Get-ChildItem -Path $liveFull -Recurse -File -Filter *.cs | ForEach-Object {
    $relative = $_.FullName.Substring($liveFull.Length).TrimStart('\', '/').Replace('\', '/')
    if ($relative -eq "AssemblyInfo.cs" -or $relative -like "Proofs/*" -or $relative -like "obj/*" -or $relative -like "bin/*") {
        return
    }
    $target = Join-Path $frozen $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}

Get-ChildItem -Path $frozen -Recurse -Directory |
    Sort-Object { $_.FullName.Length } -Descending |
    Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
    Remove-Item -Force

if (Get-Command git -ErrorAction SilentlyContinue) {
    git -C $repoRoot status --short -- $frozen
}
Write-Host "==> Promoted. Commit the frozen kernel on its own, so reviewers see exactly what will prove the next change."
