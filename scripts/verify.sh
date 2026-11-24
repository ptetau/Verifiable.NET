#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [ "${1:-}" = "--install-z3" ]; then
  bash scripts/install-z3.sh
fi

export VERIFIABLE_AGENT=1
export GALAXYCHECK_AGENT=1

echo "==> Building Verifiable.NET.sln (Release) with Z3 SMT verification..."
dotnet build Verifiable.NET.sln -c Release

echo "==> Running all GalaxyCheck & Roslyn verification tests..."
dotnet test Verifiable.NET.sln -c Release --no-build --verbosity normal

echo "==> Verifiable.NET verification succeeded."
