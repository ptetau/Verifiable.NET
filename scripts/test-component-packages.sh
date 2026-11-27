#!/usr/bin/env bash
# Exercise the analyzer imports and MSBuild diagnostic as a real NuGet consumer.
set -euo pipefail
feed="$(cd "${1:?path to packed components}" && pwd)"
z3="${2:?absolute path to Z3}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cd "$tmp"
export NUGET_PACKAGES="$tmp/packages"
cat > Smoke.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Verifiable.Contracts" Version="0.1.0" />
    <PackageReference Include="Verifiable.Analyzers" Version="0.1.0" />
    <PackageReference Include="Verifiable.Generators" Version="0.1.0" />
    <PackageReference Include="Verifiable.CodeFixes" Version="0.1.0" />
  </ItemGroup>
</Project>
EOF
cat > Sample.cs <<'EOF'
using Verifiable.Contracts;
[Verifiable]
public static class Sample {
    public static decimal Same(decimal x) {
        Spec.Requires(x >= 0m);
        Spec.Ensures<decimal>(r => r > x, "deliberately false");
        return x;
    }
}
EOF
dotnet restore --source "$feed"
test ! -d "$NUGET_PACKAGES/verifiable.z3"
if VERIFIABLE_AGENT=0 dotnet build --no-restore -p:UseSharedCompilation=false \
    -p:VerifiableZ3Path="$z3" -p:VerifiableAgent=1 -p:ErrorLog=proof.sarif%2Cversion=2.1 > build.log 2>&1; then
    echo "The deliberately false contract compiled successfully" >&2
    exit 1
fi
grep -F 'error VER102: Postcondition of Sample.Same fails: deliberately false; x = ' build.log
grep -Fq '<verifiable-counterexample>' proof.sarif

# A library compiled with Verifiable.Generators exports canonical contract metadata for public pure predicates,
# while helpers relying on private non-constant implementations fail closed with VER101.
cat > Sample.cs <<'EOF'
using Verifiable.Contracts;
[Verifiable]
public static class Bounds {
    public const decimal Min = -100m, Max = 100m;
    public static bool IsValid(decimal x) => x >= Min && x <= Max;
    private static bool Secret(decimal x) => x >= 0m;
    public static bool UsesSecret(decimal x) => Secret(x);
}
EOF
dotnet build --no-restore -p:VerifiableZ3Path="$z3" -o library
cat > Sample.cs <<'EOF'
using Verifiable.Contracts;
[Verifiable]
public static class Consumer {
    public static decimal Keep(decimal x) {
        Spec.Requires(Bounds.IsValid(x));
        Spec.Ensures<decimal>(r => r == x && r >= -100m && r <= 100m);
        return x;
    }
}
EOF
sed -i '/<TargetFramework>/a\  <PropertyGroup><AssemblyName>Consumer</AssemblyName></PropertyGroup>\n  <ItemGroup><Reference Include="Smoke"><HintPath>library/Smoke.dll</HintPath></Reference></ItemGroup>' Smoke.csproj
dotnet build --no-restore -p:VerifiableZ3Path="$z3"
sed -i 's/r == x && r >= -100m && r <= 100m/r >= 0m/' Sample.cs
if dotnet build --no-restore -p:VerifiableZ3Path="$z3" > external-ver102.log 2>&1; then
    echo "The false postcondition under Bounds.IsValid(x) compiled successfully" >&2
    exit 1
fi
grep -F 'error VER102' external-ver102.log
sed -i 's/Bounds\.IsValid(x)/Bounds.UsesSecret(x)/' Sample.cs
if dotnet build --no-restore -p:VerifiableZ3Path="$z3" > external.log 2>&1; then
    echo "The unexported private-helper predicate was silently accepted" >&2
    exit 1
fi
grep -F 'error VER101' external.log
