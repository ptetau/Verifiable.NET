using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Verifiable.Shared;

namespace Verifiable.Analyzers;

/// <summary>One locked method: its key, the hash of its signature and leading contracts, and their clauses.</summary>
internal sealed record ContractLockEntry(string Symbol, string Hash, IReadOnlyList<string> Clauses);

/// <summary>
/// The contract lock (requirement R3, hash check). The generator records, in an additional file whose
/// name ends in <c>.contracts.lock.json</c>, a SHA-256 of each generated method's signature and leading
/// contract block. Whoever fills in the body may change the body only: any change to a locked method's
/// signature or contracts — weaker, stronger, reordered, removed, or moved below other code — fails the
/// build with VER109 naming the method and its clauses. A lock file that cannot be read is VER110.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ContractLockAnalyzer : DiagnosticAnalyzer
{
    /// <summary>File name ending that marks an additional file as a contract lock.</summary>
    public const string LockSuffix = ".contracts.lock.json";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(VerifiableDescriptors.LockedContractChanged, VerifiableDescriptors.InvalidContractLock);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext ctx)
    {
        var lockFile = ctx.Options.AdditionalFiles.FirstOrDefault(f => f.Path.EndsWith(LockSuffix, StringComparison.OrdinalIgnoreCase));
        if (lockFile is null) return;

        var lockLocation = Location.Create(lockFile.Path, new TextSpan(0, 0), new LinePositionSpan(LinePosition.Zero, LinePosition.Zero));
        if (!ContractLock.TryParse(lockFile.GetText(ctx.CancellationToken)?.ToString() ?? string.Empty, out var entries, out var error))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.InvalidContractLock, lockLocation,
                $"The contract lock `{System.IO.Path.GetFileName(lockFile.Path)}` could not be read: {error}. Regenerate it with `verifiable-query lock`."));
            return;
        }

        var current = ContractLock.Compute(ctx.Compilation.SyntaxTrees).ToDictionary(e => e.Symbol, StringComparer.Ordinal);
        foreach (var locked in entries)
        {
            var clauses = locked.Clauses.Count == 0 ? "no clause" : "clause " + string.Join(", ", locked.Clauses);
            var props = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
            var paren = locked.Symbol.IndexOf('(');
            props["Symbol"] = paren < 0 ? locked.Symbol : locked.Symbol.Substring(0, paren);
            if (locked.Clauses.Count > 0) props["Clause"] = string.Join(",", locked.Clauses);
            props["Fix"] = "Restore the generated signature and contracts exactly, and change only the body. If the spec changed, regenerate the method and its lock.";

            if (!current.TryGetValue(locked.Symbol, out var now))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.LockedContractChanged, lockLocation, props.ToImmutable(),
                    $"Locked method `{locked.Symbol}` ({clauses}) is gone, or its signature changed. Its generated signature and contracts must stay as they were."));
                continue;
            }

            if (!string.Equals(now.Hash, locked.Hash, StringComparison.OrdinalIgnoreCase))
            {
                props["Contract"] = now.ContractText;
                ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.LockedContractChanged, now.Location, props.ToImmutable(),
                    $"The contracts of locked method `{locked.Symbol}` ({clauses}) were changed. Only the body may be edited: a changed contract could be weakened until the proof passes."));
            }
        }
    }
}

/// <summary>Computes and reads contract locks; shared by the analyzer and <c>verifiable-query lock</c>.</summary>
internal static class ContractLock
{
    internal sealed record Current(string Symbol, string Hash, IReadOnlyList<string> Clauses, string ContractText, Location Location);

    /// <summary>Every method or constructor with a leading contract block, keyed by <c>Type.Member(param types)</c>.</summary>
    public static IEnumerable<Current> Compute(IEnumerable<SyntaxTree> trees)
    {
        foreach (var tree in trees)
        {
            if (tree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var member in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var calls = LeadingContractBlock.GetLeadingSpecCalls(member, c => ClauseIds.SpecMethodName(c) is not null);
                if (calls.Count == 0) continue;
                var key = Key(member);
                if (key is null) continue;
                var contracts = calls.Select(c => c.NormalizeWhitespace().ToFullString()).ToList();
                var material = Signature(member) + "\n" + string.Join("\n", contracts);
                var clauses = calls.Select(ClauseIds.FromSpecCall).Where(c => c is not null).Select(c => c!).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
                var location = member switch
                {
                    MethodDeclarationSyntax m => m.Identifier.GetLocation(),
                    ConstructorDeclarationSyntax c => c.Identifier.GetLocation(),
                    _ => member.GetLocation(),
                };
                yield return new Current(key, Sha256(material), clauses, string.Join("\n", contracts), location);
            }
        }
    }

    /// <summary><c>Type.Method(int, string)</c>, with nested types joined by dots; null outside a type.</summary>
    public static string? Key(BaseMethodDeclarationSyntax member)
    {
        var name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText + (m.TypeParameterList is null ? string.Empty : "`" + m.TypeParameterList.Parameters.Count),
            ConstructorDeclarationSyntax c => c.Identifier.ValueText,
            _ => null,
        };
        if (name is null) return null;
        var types = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText).ToList();
        if (types.Count == 0) return null;
        var parameters = string.Join(", ", member.ParameterList.Parameters.Select(p => p.Type?.NormalizeWhitespace().ToFullString() ?? "?"));
        return $"{string.Join(".", types)}.{name}({parameters})";
    }

    private static string Signature(BaseMethodDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => $"{m.ReturnType.NormalizeWhitespace()} {m.Identifier.ValueText}{m.TypeParameterList?.NormalizeWhitespace()}{m.ParameterList.NormalizeWhitespace()}{string.Concat(m.ConstraintClauses.Select(c => " " + c.NormalizeWhitespace()))}",
        _ => member.ParameterList.NormalizeWhitespace().ToFullString(),
    };

    public static string Sha256(string text)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Reads <c>{"version":1,"locks":[{"symbol":"...","hash":"...","clauses":["C1"]}]}</c>.</summary>
    public static bool TryParse(string json, out List<ContractLockEntry> entries, out string? error)
    {
        entries = new List<ContractLockEntry>();
        error = null;
        object? root;
        try
        {
            var reader = new MiniJson(json);
            root = reader.ReadValue();
            reader.EndOfInput();
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        if (root is not Dictionary<string, object?> obj || !obj.TryGetValue("locks", out var locksObj) || locksObj is not List<object?> locks)
        {
            error = "there is no `locks` array";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in locks)
        {
            if (item is not Dictionary<string, object?> e ||
                !e.TryGetValue("symbol", out var symObj) || symObj is not string symbol || symbol.Length == 0 ||
                !e.TryGetValue("hash", out var hashObj) || hashObj is not string hash || hash.Length != 64)
            {
                error = "every lock needs a `symbol` and a 64-character `hash`";
                return false;
            }
            if (!seen.Add(symbol))
            {
                error = $"`{symbol}` is locked twice";
                return false;
            }
            var clauses = e.TryGetValue("clauses", out var cl) && cl is List<object?> list
                ? list.OfType<string>().ToList()
                : new List<string>();
            entries.Add(new ContractLockEntry(symbol, hash, clauses));
        }
        return true;
    }
}
