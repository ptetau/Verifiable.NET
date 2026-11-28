using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Verifiable.Shared;

namespace Verifiable.Analyzers;

/// <summary>A contract call, type alias, refinement or invariant, with the clause ID it names (if any).</summary>
internal sealed class ClauseConstruct
{
    public ClauseConstruct(
        string kind,
        ISymbol symbol,
        string? clause,
        string? problem,
        string? because,
        Location location,
        bool needsClause)
    {
        Kind = kind;
        Symbol = symbol;
        Clause = clause;
        Problem = problem;
        Because = because;
        Location = location;
        NeedsClause = needsClause;
    }

    /// <summary><c>requires</c>, <c>ensures</c>, <c>loopInvariant</c>, <c>decreases</c>, <c>typeAlias</c>, <c>refined</c> or <c>invariant</c>.</summary>
    public string Kind { get; }

    /// <summary>The method, type or property that carries the construct.</summary>
    public ISymbol Symbol { get; }

    /// <summary>The clause ID, or <see langword="null"/> when none was given or it is unusable (see <see cref="Problem"/>).</summary>
    public string? Clause { get; }

    /// <summary>Why the clause given is not usable (VER204), or <see langword="null"/>.</summary>
    public string? Problem { get; }

    /// <summary>The <c>because</c> text or description, when it is a constant.</summary>
    public string? Because { get; }

    /// <summary>Where the construct is written.</summary>
    public Location Location { get; }

    /// <summary>True if, with a manifest present, a missing clause is worth a warning (VER203).</summary>
    public bool NeedsClause { get; }

    /// <summary>Symbol display such as <c>Bookings.Schedule.Book(Schedule, Booking)</c>.</summary>
    public string SymbolDisplay => ClauseCatalog.DisplaySymbol(Symbol);
}

/// <summary>What the manifest and the constructs say about each other.</summary>
internal sealed class ClauseCoverage
{
    public ClauseCoverage(
        IReadOnlyList<ClauseConstruct> unknown,
        IReadOnlyList<ManifestClause> uncovered,
        IReadOnlyList<ClauseConstruct> withoutClause)
    {
        Unknown = unknown;
        Uncovered = uncovered;
        WithoutClause = withoutClause;
    }

    /// <summary>Constructs naming a clause that is not in the manifest (VER201).</summary>
    public IReadOnlyList<ClauseConstruct> Unknown { get; }

    /// <summary>Manifest clauses no construct names (VER202).</summary>
    public IReadOnlyList<ManifestClause> Uncovered { get; }

    /// <summary>Constructs that should carry a clause and do not (VER203).</summary>
    public IReadOnlyList<ClauseConstruct> WithoutClause { get; }
}

/// <summary>
/// Finds the clause-carrying constructs of a compilation and compares them with the manifest. The clause
/// coverage analyzer (VER201-VER204) and <c>verifiable-query clauses</c> both use it, so they cannot disagree.
/// </summary>
internal static class ClauseCatalog
{
    private const string TypeAliasDef = "Verifiable.Contracts.TypeAliasAttribute<TUnderlying>";
    private const string RefinedDef = "Verifiable.Contracts.RefinedAttribute<TUnderlying>";

    /// <summary>Reads the construct a <c>Spec.*</c> call is, or <see langword="null"/> if it is not a contract call.</summary>
    public static ClauseConstruct? FromInvocation(IInvocationOperation inv, ISymbol? owner)
    {
        var method = inv.TargetMethod;
        if (method.ContainingType?.ToDisplayString() != VerifiableSymbolHelpers.SpecTypeName)
        {
            return null;
        }

        var kind = method.Name switch
        {
            "Requires" or "RequiresWhen" => "requires",
            "Ensures" or "EnsuresWhen" => "ensures",
            "LoopInvariant" => "loopInvariant",
            "Decreases" => "decreases",
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        owner = Normalize(owner);
        if (owner is null)
        {
            return null;
        }

        var becauseOrdinal = SpecCallArguments.BecauseOrdinal(method.Name);
        var because = ConstantString(FindArg(inv, becauseOrdinal));
        string? clause = null;
        string? problem = null;
        var clauseArg = FindArg(inv, becauseOrdinal + 1);
        if (clauseArg is not null && !clauseArg.IsImplicit)
        {
            var value = clauseArg.Value;
            while (value is IConversionOperation { Operand: { } inner })
            {
                value = inner;
            }

            var cv = value.ConstantValue;
            if (!cv.HasValue)
            {
                problem = "The `clause` argument must be a constant string, such as \"C1\" or a `const` field, so the build can read it.";
            }
            else if (cv.Value is null)
            {
                // clause: null means no clause.
            }
            else if (cv.Value is string s)
            {
                if (ClauseManifest.IsValidId(s))
                {
                    clause = s;
                }
                else
                {
                    problem = "The `clause` argument must not be empty or contain whitespace.";
                }
            }
            else
            {
                problem = "The `clause` argument must be a constant string.";
            }
        }

        return new ClauseConstruct(kind, owner, clause, problem, because, inv.Syntax.GetLocation(), NeedsClause(owner, inv.Syntax.GetLocation()));
    }

    /// <summary>Reads the construct a <c>[TypeAlias]</c>, <c>[Refined]</c> or <c>[Invariant]</c> attribute is, or <see langword="null"/>.</summary>
    public static ClauseConstruct? FromAttribute(AttributeData attr, ISymbol target)
    {
        string? kind = null;
        var cls = attr.AttributeClass;
        if (cls is { IsGenericType: true })
        {
            var def = cls.ConstructedFrom.ToDisplayString();
            kind = def == TypeAliasDef ? "typeAlias" : def == RefinedDef ? "refined" : null;
        }
        else if (cls?.ToDisplayString() == VerifiableSymbolHelpers.InvariantAttr)
        {
            kind = "invariant";
        }

        if (kind is null)
        {
            return null;
        }

        string? clause = null;
        string? problem = null;
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key != "Clause")
            {
                continue;
            }

            if (named.Value.Value is string s)
            {
                if (ClauseManifest.IsValidId(s))
                {
                    clause = s;
                }
                else
                {
                    problem = "The `Clause` property must not be empty or contain whitespace.";
                }
            }
        }

        var because = attr.ConstructorArguments.Length >= 1 ? attr.ConstructorArguments[0].Value as string : null;
        var location = attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? target.Locations.FirstOrDefault() ?? Location.None;
        return new ClauseConstruct(kind, target, clause, problem, because, location, NeedsClause(target, location));
    }

    /// <summary>The constructs on a declared symbol's own attributes.</summary>
    public static IEnumerable<ClauseConstruct> FromSymbol(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (FromAttribute(attr, symbol) is { } c)
            {
                yield return c;
            }
        }
    }

    /// <summary>
    /// Every clause-carrying construct written in the source of <paramref name="compilation"/>, skipping generated
    /// files the way the analyzers do.
    /// </summary>
    public static List<ClauseConstruct> CollectAll(Compilation compilation)
    {
        var result = new List<ClauseConstruct>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsGeneratedFile(tree))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax call:
                        if (model.GetOperation(call) is IInvocationOperation inv)
                        {
                            var c = FromInvocation(inv, model.GetEnclosingSymbol(call.SpanStart));
                            if (c is not null)
                            {
                                result.Add(c);
                            }
                        }
                        break;

                    case TypeDeclarationSyntax or EnumDeclarationSyntax or BaseMethodDeclarationSyntax or PropertyDeclarationSyntax:
                        if (model.GetDeclaredSymbol(node) is { } sym && sym.Locations.Length > 0 && sym.Locations[0].SourceTree == tree)
                        {
                            result.AddRange(FromSymbol(sym));
                        }
                        break;
                }
            }
        }

        return result;
    }

    /// <summary>Compares the constructs with a manifest. Results are sorted so output is stable.</summary>
    public static ClauseCoverage Compare(ClauseManifest manifest, IEnumerable<ClauseConstruct> constructs)
    {
        var ids = new HashSet<string>(manifest.Clauses.Select(c => c.Id), StringComparer.Ordinal);
        var all = constructs.ToList();
        var used = new HashSet<string>(all.Where(c => c.Clause is not null).Select(c => c.Clause!), StringComparer.Ordinal);
        var unknown = all.Where(c => c.Clause is not null && !ids.Contains(c.Clause)).OrderBy(SortKey, StringComparer.Ordinal).ToList();
        var uncovered = manifest.Clauses.Where(c => !used.Contains(c.Id)).ToList();
        var without = all.Where(c => c.Clause is null && c.Problem is null && c.NeedsClause).OrderBy(SortKey, StringComparer.Ordinal).ToList();
        return new ClauseCoverage(unknown, uncovered, without);
    }

    private static string SortKey(ClauseConstruct c)
    {
        var span = c.Location.GetLineSpan();
        return $"{c.Clause}\u0001{c.SymbolDisplay}\u0001{span.Path}\u0001{span.StartLinePosition.Line:D8}";
    }

    /// <summary>Display such as <c>Bookings.Schedule.Book(Schedule, Booking)</c>.</summary>
    public static string DisplaySymbol(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol m:
                var sb = new StringBuilder();
                if (m.ContainingType is { } t)
                {
                    sb.Append(t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None))).Append('.');
                }
                sb.Append(m.MethodKind == MethodKind.Constructor ? m.ContainingType?.Name ?? ".ctor" : m.Name);
                sb.Append('(')
                  .Append(string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))))
                  .Append(')');
                return sb.ToString();
            case INamedTypeSymbol nt:
                return nt.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None));
            default:
                return (symbol.ContainingType is { } ct
                    ? ct.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None)) + "."
                    : "") + symbol.Name;
        }
    }

    private static ISymbol? Normalize(ISymbol? owner)
    {
        while (owner is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            owner = owner.ContainingSymbol;
        }
        return owner;
    }

    // Judged by where the construct is written, not by where the symbol is declared: a partial type that the
    // generators extend still has its own attributes in the author's file.
    private static bool NeedsClause(ISymbol symbol, Location where)
    {
        if (VerifiableSymbolHelpers.GetScope(symbol) != VerificationScope.Verifiable ||
            VerifiableSymbolHelpers.IsSynthesizedRecordMember(symbol))
        {
            return false;
        }

        var path = where.SourceTree?.FilePath;
        return path is null || !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static IArgumentOperation? FindArg(IInvocationOperation inv, int ordinal)
    {
        foreach (var arg in inv.Arguments)
        {
            if (arg.Parameter?.Ordinal == ordinal)
            {
                return arg;
            }
        }
        return null;
    }

    private static string? ConstantString(IArgumentOperation? arg) =>
        arg is not null && arg.Value.ConstantValue is { HasValue: true, Value: string s } ? s : null;

    private static bool IsGeneratedFile(SyntaxTree tree)
    {
        var path = tree.FilePath ?? string.Empty;
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstTrivia = tree.GetRoot().GetLeadingTrivia().ToString();
        return firstTrivia.IndexOf("<auto-generated", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
