using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Verifiable.Analyzers;
using Verifiable.Shared;

namespace Verifiable.Query;

/// <summary>A rule on a type: a constrained type's <c>IsValid</c>, or an <c>[Invariant]</c> member.</summary>
internal sealed record PackRule(string Kind, string Member, string? Reason, string? Clause);

/// <summary>A type the target uses, cut down to its data and rules.</summary>
internal sealed record PackType(string Name, string Kind, string Source, IReadOnlyList<PackRule> Rules, int Depth, string File, int Line);

/// <summary>A method the target may call, with its contracts. Lower rank is more relevant.</summary>
internal sealed record PackHelper(string Symbol, string Signature, IReadOnlyList<string> Contracts, int Rank, string File, int Line);

/// <summary>A method that calls the target: its contracts constrain what the target may do.</summary>
internal sealed record PackCaller(string Symbol, IReadOnlyList<string> Contracts, string CallSite, string File, int Line);

internal sealed record PackOmission(string Section, string Name);

/// <summary>
/// Everything an agent needs to work on one method, and nothing else (requirement R13): its signature
/// and contracts, the clause text, the types it uses with their rules, the helpers it may call with
/// their contracts, and the contracts of its callers.
/// </summary>
internal sealed record ContextPack(
    string Symbol,
    string Signature,
    IReadOnlyList<string> Contracts,
    IReadOnlyList<string> Clauses,
    IReadOnlyDictionary<string, string?> ClauseText,
    bool Hole,
    string File,
    int Line,
    IReadOnlyList<PackType> Types,
    IReadOnlyList<PackHelper> Helpers,
    IReadOnlyList<PackCaller> Callers,
    IReadOnlyList<PackOmission> Omitted);

internal static class ContextPacks
{
    /// <summary>Methods (and constructors) declared in source whose <c>Type.Member</c> name is in the scope.</summary>
    public static List<IMethodSymbol> Resolve(Compilation compilation, IReadOnlyList<string> scope) =>
        AllSourceMethods(compilation)
            .Where(m => ElmCheckEngine.SymbolInScope(Name(m), scope))
            .ToList();

    /// <summary>Methods whose own leading contracts or holes carry one of <paramref name="clauses"/>.</summary>
    public static List<IMethodSymbol> ForClauses(Compilation compilation, IReadOnlyCollection<string> clauses) =>
        AllSourceMethods(compilation)
            .Where(m => m.DeclaringSyntaxReferences.Any(r =>
            {
                var decl = r.GetSyntax();
                var own = LeadingContractBlock.GetLeadingSpecCalls(decl, c => ClauseIds.SpecMethodName(c) is not null).Select(ClauseIds.FromSpecCall)
                    .Concat(decl.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Holes.IsHoleCall(i, null))
                        .Select(h => Holes.Describe(h, new Dictionary<string, string?>()).Clause));
                return own.Any(c => c is not null && clauses.Contains(c));
            }))
            .ToList();

    public static List<IMethodSymbol> HoleMethods(Compilation compilation) =>
        AllSourceMethods(compilation)
            .Where(m => m.DeclaringSyntaxReferences.Any(r => Holes.ContainsHole(r.GetSyntax())))
            .ToList();

    private static IEnumerable<IMethodSymbol> AllSourceMethods(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees.Where(t => !t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is IMethodSymbol m) yield return m;
            }
        }
    }

    public static string Name(IMethodSymbol m) =>
        $"{m.ContainingType?.Name}.{(m.MethodKind == MethodKind.Constructor ? m.ContainingType?.Name : m.Name)}";

    private static string TypeName(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>Declared in hand-written source (a partial type with a generated half still counts).</summary>
    private static bool IsSource(ISymbol s) => !s.IsImplicitlyDeclared &&
        s.Locations.Any(l => l.IsInSource && !(l.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ?? true));

    private static (string File, int Line) Where(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return (AgentOutput.NormalizePath(span.Path), span.StartLinePosition.Line + 1);
    }

    private static string OneLine(string text) =>
        string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    private static List<string> LeadingContracts(SyntaxNode decl) =>
        LeadingContractBlock.GetLeadingSpecCalls(decl, c => ClauseIds.SpecMethodName(c) is not null)
            .Select(c => OneLine(c.ToString()))
            .ToList();

    private static string Signature(IMethodSymbol m, SyntaxNode decl) => decl switch
    {
        MethodDeclarationSyntax md => OneLine($"{string.Join(" ", md.Modifiers.Select(x => x.ValueText))} {md.ReturnType} {md.Identifier.ValueText}{md.TypeParameterList}{md.ParameterList}{string.Concat(md.ConstraintClauses.Select(c => " " + c))}"),
        ConstructorDeclarationSyntax cd => OneLine($"{string.Join(" ", cd.Modifiers.Select(x => x.ValueText))} {cd.Identifier.ValueText}{cd.ParameterList}"),
        _ => m.ToDisplayString(),
    };

    /// <summary>Builds the pack for one method.</summary>
    public static ContextPack Build(Compilation compilation, IMethodSymbol target, IReadOnlyDictionary<string, string?> clauseText)
    {
        var decl = target.DeclaringSyntaxReferences.First().GetSyntax();
        var model = compilation.GetSemanticModel(decl.SyntaxTree);
        var contractCalls = LeadingContractBlock.GetLeadingSpecCalls(decl, c => ClauseIds.SpecMethodName(c) is not null);
        var holeCalls = decl.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Holes.IsHoleCall(i, null)).ToList();

        var clauses = contractCalls.Select(ClauseIds.FromSpecCall)
            .Concat(holeCalls.Select(h => Holes.Describe(h, new Dictionary<string, string?>()).Clause))
            .Where(c => c is not null).Select(c => c!).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        // Types: the signature's, the containing type, and those named in the contracts (depth 0); then the
        // types of their data members (depth 1).
        var depthOf = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
        void AddType(ITypeSymbol? t, int depth)
        {
            switch (t)
            {
                case null: return;
                case IArrayTypeSymbol arr: AddType(arr.ElementType, depth); return;
                case INamedTypeSymbol named:
                    foreach (var arg in named.TypeArguments) AddType(arg, depth);
                    var def = named.OriginalDefinition;
                    if (!IsSource(def)) return;
                    if (!depthOf.TryGetValue(def, out var existing) || existing > depth) depthOf[def] = depth;
                    return;
            }
        }

        foreach (var p in target.Parameters) AddType(p.Type, 0);
        AddType(target.ReturnType, 0);
        AddType(target.ContainingType, 0);
        foreach (var call in contractCalls)
        {
            foreach (var node in call.DescendantNodes().OfType<ExpressionSyntax>())
            {
                AddType(model.GetTypeInfo(node).Type, 0);
            }
        }

        // Follow data members (properties, fields, record parameters) to every type they reach; each hop
        // is one level deeper, and deeper types are trimmed first.
        void Close()
        {
            var queue = new Queue<INamedTypeSymbol>(depthOf.Keys);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                var next = depthOf[t] + 1;
                foreach (var member in t.GetMembers())
                {
                    var memberType = member switch
                    {
                        IPropertySymbol prop when !prop.IsImplicitlyDeclared || t.IsRecord => prop.Type,
                        IFieldSymbol field when !field.IsImplicitlyDeclared => field.Type,
                        _ => null,
                    };
                    var snapshot = new Dictionary<INamedTypeSymbol, int>(depthOf, SymbolEqualityComparer.Default);
                    AddType(memberType, next);
                    foreach (var kv in depthOf)
                    {
                        if (!snapshot.TryGetValue(kv.Key, out var old) || old != kv.Value) queue.Enqueue(kv.Key);
                    }
                }
            }
        }

        Close();

        // Helpers: named in the contracts (0), the containing type's other methods (1), the pack types'
        // methods (2), and any other contracted method in scope (3).
        var inContracts = new HashSet<IMethodSymbol>(
            contractCalls.SelectMany(c => c.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Select(i => model.GetSymbolInfo(i).Symbol).OfType<IMethodSymbol>().Select(m => m.OriginalDefinition),
            SymbolEqualityComparer.Default);
        var packTypes = new HashSet<INamedTypeSymbol>(depthOf.Keys, SymbolEqualityComparer.Default);

        var helpers = new List<PackHelper>();
        var helperSymbols = new List<(IMethodSymbol Method, int Rank)>();
        foreach (var m in AllSourceMethods(compilation))
        {
            if (SymbolEqualityComparer.Default.Equals(m, target)) continue;
            if (m.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor)) continue;
            if (m.DeclaredAccessibility == Accessibility.Private && !SymbolEqualityComparer.Default.Equals(m.ContainingType, target.ContainingType)) continue;
            var mDecl = m.DeclaringSyntaxReferences.First().GetSyntax();
            var contracts = LeadingContracts(mDecl);
            int rank;
            if (inContracts.Contains(m)) rank = 0;
            else if (SymbolEqualityComparer.Default.Equals(m.ContainingType, target.ContainingType)) rank = 1;
            else if (m.ContainingType is not null && packTypes.Contains(m.ContainingType.OriginalDefinition)) rank = 2;
            else if (contracts.Count > 0 && m.IsStatic && VerifiableSymbolHelpers.GetScope(m) == VerificationScope.Verifiable) rank = 3;
            else continue;
            if (IsRuleMember(m)) continue; // already shown with its type
            var (file, line) = Where(mDecl);
            helpers.Add(new PackHelper(Name(m), Signature(m, mDecl), contracts, rank, file, line));
            helperSymbols.Add((m, rank));
        }
        helpers = helpers.OrderBy(h => h.Rank).ThenBy(h => h.Symbol, StringComparer.Ordinal).ThenBy(h => h.Signature, StringComparer.Ordinal).ToList();

        // The types in the signatures of the closer helpers come too: a body that calls a helper handles its result.
        foreach (var m in helperSymbols.Where(x => x.Rank <= 2).Select(x => x.Method))
        {
            foreach (var p in m.Parameters) AddType(p.Type, 2);
            AddType(m.ReturnType, 2);
        }
        Close();

        var types = depthOf
            .Select(kv => DescribeType(kv.Key, kv.Value))
            .Where(t => t is not null).Select(t => t!)
            .OrderBy(t => t.Depth).ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        // Callers: methods whose bodies call the target.
        var callers = new List<PackCaller>();
        foreach (var tree in compilation.SyntaxTrees.Where(t => !t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var treeModel = compilation.GetSemanticModel(tree);
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (treeModel.GetSymbolInfo(inv).Symbol is not IMethodSymbol called ||
                    !SymbolEqualityComparer.Default.Equals(called.OriginalDefinition, target)) continue;
                var callerDecl = inv.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                if (callerDecl is null || treeModel.GetDeclaredSymbol(callerDecl) is not IMethodSymbol caller) continue;
                if (SymbolEqualityComparer.Default.Equals(caller, target)) continue;
                var stmt = inv.Ancestors().OfType<StatementSyntax>().FirstOrDefault() ?? (SyntaxNode)inv;
                var (file, line) = Where(inv);
                callers.Add(new PackCaller(Name(caller), LeadingContracts(callerDecl), OneLine(stmt.ToString()), file, line));
            }
        }
        callers = callers.OrderBy(c => c.Symbol, StringComparer.Ordinal).ThenBy(c => c.Line).ToList();

        var (tFile, tLine) = Where(decl);
        return new ContextPack(
            Name(target),
            Signature(target, decl),
            contractCalls.Select(c => OneLine(c.ToString())).ToList(),
            clauses,
            clauses.ToDictionary(c => c, c => clauseText.TryGetValue(c, out var text) ? text : null, StringComparer.Ordinal),
            holeCalls.Count > 0,
            tFile,
            tLine,
            types,
            helpers,
            callers,
            new List<PackOmission>());
    }

    private static bool IsRuleMember(IMethodSymbol m) =>
        (m.Name == "IsValid" && m.IsStatic) ||
        m.GetAttributes().Any(a => a.AttributeClass?.Name == "InvariantAttribute");

    /// <summary>The type's declaration with its data members and rules, and without other method bodies.</summary>
    private static PackType? DescribeType(INamedTypeSymbol type, int depth)
    {
        var syntax = type.DeclaringSyntaxReferences
            .Where(r => !r.SyntaxTree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GetSyntax()).OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (syntax is null) return null;

        var rules = new List<PackRule>();
        foreach (var attr in syntax.AttributeLists.SelectMany(l => l.Attributes).Where(ClauseIds.IsClauseAttribute))
        {
            var reason = attr.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null;
            rules.Add(new PackRule(attr.Name.ToString(), "IsValid", reason, ClauseIds.FromAttribute(attr)));
        }

        SyntaxNode trimmed = syntax;
        if (syntax is TypeDeclarationSyntax tds)
        {
            var drop = new List<SyntaxNode>();
            foreach (var member in tds.Members)
            {
                var invariant = member.AttributeLists.SelectMany(l => l.Attributes)
                    .FirstOrDefault(a => ClauseIds.IsClauseAttribute(a) && a.Name.ToString().StartsWith("Invariant", StringComparison.Ordinal));
                if (invariant is not null)
                {
                    var reason = invariant.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null;
                    var name = member switch
                    {
                        MethodDeclarationSyntax m => m.Identifier.ValueText,
                        PropertyDeclarationSyntax p => p.Identifier.ValueText,
                        _ => "?",
                    };
                    rules.Add(new PackRule("[Invariant]", name, reason, ClauseIds.FromAttribute(invariant)));
                    continue;
                }

                var keep = member switch
                {
                    FieldDeclarationSyntax => true,
                    PropertyDeclarationSyntax => true,
                    MethodDeclarationSyntax { Identifier.ValueText: "IsValid" } => true,
                    EnumMemberDeclarationSyntax => true,
                    _ => false,
                };
                if (!keep) drop.Add(member);
            }
            trimmed = tds.RemoveNodes(drop, SyntaxRemoveOptions.KeepNoTrivia)!;
        }

        var kind = syntax switch
        {
            EnumDeclarationSyntax => "enum",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            _ => "class",
        };
        var (file, line) = Where(syntax);
        var source = trimmed.WithoutTrivia().NormalizeWhitespace(eol: "\n").ToFullString();
        return new PackType(type.Name, kind, source, rules, depth, file, line);
    }

    /// <summary>A rough token count (four characters a token), enough to keep a pack under a budget.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>
    /// Drops the least relevant parts until the pack fits <paramref name="maxTokens"/>: other contracted
    /// methods first, then callers, then second-level types, then the pack types' helpers, then the
    /// containing type's helpers, then first-level types. The target's own signature, contracts and clause
    /// text are never dropped. Each dropped item is listed in <c>omitted</c>.
    /// </summary>
    public static ContextPack Trim(ContextPack pack, int maxTokens, Func<ContextPack, string> render)
    {
        var types = pack.Types.ToList();
        var helpers = pack.Helpers.ToList();
        var callers = pack.Callers.ToList();
        var omitted = new List<PackOmission>();
        ContextPack Current() => pack with { Types = types, Helpers = helpers, Callers = callers, Omitted = omitted };

        bool Fits() => EstimateTokens(render(Current())) <= maxTokens;

        bool DropLast<T>(List<T> list, Func<T, bool> which, string section, Func<T, string> name)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (!which(list[i])) continue;
                omitted.Add(new PackOmission(section, name(list[i])));
                list.RemoveAt(i);
                return true;
            }
            return false;
        }

        var steps = new List<Func<bool>>
        {
            () => DropLast(helpers, h => h.Rank == 3, "helpers", h => h.Symbol),
            () => DropLast(callers, _ => true, "callers", c => c.Symbol),
            () => DropLast(types, t => t.Depth >= 3, "types", t => t.Name),
            () => DropLast(types, t => t.Depth == 2, "types", t => t.Name),
            () => DropLast(types, t => t.Depth == 1, "types", t => t.Name),
            () => DropLast(helpers, h => h.Rank == 2, "helpers", h => h.Symbol),
            () => DropLast(helpers, h => h.Rank == 1, "helpers", h => h.Symbol),
            () => DropLast(types, t => t.Depth == 0 && t.Name != pack.Symbol.Split('.')[0], "types", t => t.Name),
            () => DropLast(helpers, h => h.Rank == 0, "helpers", h => h.Symbol),
        };

        foreach (var step in steps)
        {
            while (!Fits() && step()) { }
            if (Fits()) break;
        }

        omitted.Sort((a, b) => string.CompareOrdinal(a.Section + "/" + a.Name, b.Section + "/" + b.Name));
        return Current();
    }

    public static void Write(System.Text.Json.Utf8JsonWriter w, ContextPack p)
    {
        w.WriteStartObject();
        w.WriteString("symbol", p.Symbol);
        w.WriteString("signature", p.Signature);
        w.WriteBoolean("hole", p.Hole);
        w.WriteString("file", p.File);
        w.WriteNumber("line", p.Line);
        w.WriteStartArray("contracts");
        foreach (var c in p.Contracts) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteStartArray("clauses");
        foreach (var c in p.Clauses)
        {
            w.WriteStartObject();
            w.WriteString("id", c);
            AgentOutput.StringOrNull(w, "text", p.ClauseText.TryGetValue(c, out var t) ? t : null);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("types");
        foreach (var t in p.Types)
        {
            w.WriteStartObject();
            w.WriteString("name", t.Name);
            w.WriteString("kind", t.Kind);
            w.WriteNumber("depth", t.Depth);
            w.WriteString("file", t.File);
            w.WriteNumber("line", t.Line);
            w.WriteString("source", t.Source);
            w.WriteStartArray("rules");
            foreach (var r in t.Rules)
            {
                w.WriteStartObject();
                w.WriteString("kind", r.Kind);
                w.WriteString("member", r.Member);
                AgentOutput.StringOrNull(w, "reason", r.Reason);
                AgentOutput.StringOrNull(w, "clause", r.Clause);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("helpers");
        foreach (var h in p.Helpers)
        {
            w.WriteStartObject();
            w.WriteString("symbol", h.Symbol);
            w.WriteString("signature", h.Signature);
            w.WriteNumber("rank", h.Rank);
            w.WriteString("file", h.File);
            w.WriteNumber("line", h.Line);
            w.WriteStartArray("contracts");
            foreach (var c in h.Contracts) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("callers");
        foreach (var c in p.Callers)
        {
            w.WriteStartObject();
            w.WriteString("symbol", c.Symbol);
            w.WriteString("callSite", c.CallSite);
            w.WriteString("file", c.File);
            w.WriteNumber("line", c.Line);
            w.WriteStartArray("contracts");
            foreach (var k in c.Contracts) w.WriteStringValue(k);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("omitted");
        foreach (var o in p.Omitted)
        {
            w.WriteStartObject();
            w.WriteString("section", o.Section);
            w.WriteString("name", o.Name);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static string RenderOne(ContextPack p) => AgentOutput.Write(indented: false, w => Write(w, p));
}
