#!/usr/bin/env bash
# Copies the live kernel (src/Verifiable.Core) into the frozen copy that proves it
# (src/Verifiable.Analyzers.Bootstrap/Kernel). See docs/trusted-core.md.
#
# Before copying, it builds Verifiable.Core, so the live kernel is proved by the current frozen kernel.
# Only a kernel that an earlier kernel has proved is promoted.
#
# Usage: scripts/promote-bootstrap-kernel.sh [--no-proof]
#   --no-proof  Skip that build. Use it only when the bootstrap analyzer cannot compile, because the
#               analyzer already calls kernel code the frozen copy lacks. The new kernel is then not
#               checked by an earlier one: say so in the pull request.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIVE="$REPO_ROOT/src/Verifiable.Core"
FROZEN="$REPO_ROOT/src/Verifiable.Analyzers.Bootstrap/Kernel"

prove=1
case "${1:-}" in
  "") ;;
  --no-proof) prove=0 ;;
  *) echo "Usage: $0 [--no-proof]" >&2; exit 2 ;;
esac

if [ "$prove" = 1 ]; then
  echo "==> Proving the live kernel with the current frozen kernel..."
  if ! dotnet build "$LIVE/Verifiable.Core.csproj" -c Release; then
    echo "The live kernel is not proved by the frozen kernel, so it was not promoted. Fix the errors above first." >&2
    exit 1
  fi
fi

echo "==> Copying the live kernel into $FROZEN"
mkdir -p "$FROZEN"
find "$FROZEN" -type f -name '*.cs' -delete
(
  cd "$LIVE"
  # The same files Verifiable.Analyzers compiles: everything but AssemblyInfo.cs, Proofs/, obj/ and bin/.
  find . -type f -name '*.cs' \
    ! -path './AssemblyInfo.cs' ! -path './Proofs/*' ! -path './obj/*' ! -path './bin/*' -print |
  while IFS= read -r file; do
    mkdir -p "$FROZEN/$(dirname "$file")"
    cp "$file" "$FROZEN/$file"
  done
)
find "$FROZEN" -mindepth 1 -type d -empty -delete

if git -C "$REPO_ROOT" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  git -C "$REPO_ROOT" status --short -- "$FROZEN"
fi
echo "==> Promoted. Commit the frozen kernel on its own, so reviewers see exactly what will prove the next change."
