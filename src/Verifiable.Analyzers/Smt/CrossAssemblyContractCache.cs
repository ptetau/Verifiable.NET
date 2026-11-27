using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed class CrossAssemblyContractCache
{
    public const string ThisPlaceholder = "__verifiable_this";
    public const string ResultPlaceholder = "__verifiable_result";
    private const string ContractMetadataAttrName = "Verifiable.Contracts.VerifiableContractMetadataAttribute";

    private static readonly SymbolDisplayFormat StubTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly ConditionalWeakTable<Compilation, CrossAssemblyContractCache> CacheByCompilation = new();

    public sealed record StubEntry(
        IOperation Expression,
        IMethodSymbol StubMethod,
        string RawExpression,
        string? Description);

    private sealed class MemberContracts
    {
        public StubEntry? Body { get; set; }
        public List<StubEntry> Requires { get; } = new();
        public List<StubEntry> Ensures { get; } = new();
    }

    private sealed class AssemblyContracts
    {
        public static readonly AssemblyContracts Empty = new(
            new Dictionary<ISymbol, MemberContracts>(SymbolEqualityComparer.Default),
            new HashSet<ISymbol>(SymbolEqualityComparer.Default),
            new HashSet<ISymbol>(SymbolEqualityComparer.Default));

        public Dictionary<ISymbol, MemberContracts> Members { get; }
        public HashSet<ISymbol> MembersWithBodyAttr { get; }
        public HashSet<ISymbol> MembersWithRequiresAttr { get; }

        public AssemblyContracts(
            Dictionary<ISymbol, MemberContracts> members,
            HashSet<ISymbol> membersWithBodyAttr,
            HashSet<ISymbol> membersWithRequiresAttr)
        {
            Members = members;
            MembersWithBodyAttr = membersWithBodyAttr;
            MembersWithRequiresAttr = membersWithRequiresAttr;
        }
    }

    private readonly Compilation _compilation;
    private readonly ConcurrentDictionary<IAssemblySymbol, AssemblyContracts> _byAssembly =
        new(SymbolEqualityComparer.Default);

    private CrossAssemblyContractCache(Compilation compilation)
    {
        _compilation = compilation;
    }

    private static CrossAssemblyContractCache ForCompilation(Compilation compilation) =>
        CacheByCompilation.GetValue(compilation, static c => new CrossAssemblyContractCache(c));

    public static bool HasExportedBodyMetadata(IPropertySymbol prop)
    {
        if (prop.ContainingAssembly is null)
        {
            return false;
        }

        var memberId = prop.OriginalDefinition.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(memberId))
        {
            return false;
        }

        foreach (var attr in prop.ContainingAssembly.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == ContractMetadataAttrName &&
                attr.ConstructorArguments.Length >= 2 &&
                attr.ConstructorArguments[0].Value is string id &&
                attr.ConstructorArguments[1].Value is string kind &&
                kind == "Body" &&
                id == memberId)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasExportedBody(Compilation compilation, ISymbol member)
    {
        if (member.ContainingAssembly is null ||
            SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, compilation.Assembly))
        {
            return false;
        }

        var asmContracts = ForCompilation(compilation).GetOrBuild(member.ContainingAssembly);
        return asmContracts.Members.TryGetValue(member.OriginalDefinition, out var mc) && mc.Body is not null;
    }

    public static bool HasRequires(Compilation compilation, IMethodSymbol method)
    {
        if (method.ContainingAssembly is null ||
            SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, compilation.Assembly))
        {
            return false;
        }

        var asmContracts = ForCompilation(compilation).GetOrBuild(method.ContainingAssembly);
        return asmContracts.Members.TryGetValue(method.OriginalDefinition, out var mc) && mc.Requires.Count > 0;
    }

    public static bool TryGetBodyOperation(
        Compilation compilation,
        ISymbol member,
        out IParameterSymbol? singleParam,
        out IOperation? bodyOp,
        out IMethodSymbol? stubMethod)
    {
        singleParam = null;
        bodyOp = null;
        stubMethod = null;

        if (member.ContainingAssembly is null ||
            SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, compilation.Assembly))
        {
            return false;
        }

        var asmContracts = ForCompilation(compilation).GetOrBuild(member.ContainingAssembly);
        if (!asmContracts.Members.TryGetValue(member.OriginalDefinition, out var mc) || mc.Body is null)
        {
            return false;
        }

        bodyOp = mc.Body.Expression;
        stubMethod = mc.Body.StubMethod;
        if (member is IMethodSymbol { IsStatic: true, Parameters.Length: 1 } &&
            stubMethod.Parameters.Length == 1)
        {
            singleParam = stubMethod.Parameters[0];
        }

        return true;
    }

    public static bool TryGetMethodSpecs(
        Compilation compilation,
        IMethodSymbol method,
        out IReadOnlyList<StubEntry> requires,
        out IReadOnlyList<StubEntry> ensures)
    {
        requires = Array.Empty<StubEntry>();
        ensures = Array.Empty<StubEntry>();

        if (method.ContainingAssembly is null ||
            SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, compilation.Assembly))
        {
            return false;
        }

        var asmContracts = ForCompilation(compilation).GetOrBuild(method.ContainingAssembly);
        if (!asmContracts.Members.TryGetValue(method.OriginalDefinition, out var mc) ||
            (mc.Requires.Count == 0 && mc.Ensures.Count == 0))
        {
            return false;
        }

        requires = mc.Requires;
        ensures = mc.Ensures;
        return true;
    }

    public static void BindStubParameters(
        IDictionary<ISymbol, SmtTypedTerm> vars,
        IMethodSymbol originalMethod,
        IMethodSymbol stubMethod,
        IReadOnlyList<SmtTypedTerm> args)
    {
        var offset = !originalMethod.IsStatic && stubMethod.IsStatic ? 1 : 0;
        for (var i = 0; i < originalMethod.Parameters.Length && i < args.Count; i++)
        {
            var stubIndex = i + offset;
            if (stubIndex < stubMethod.Parameters.Length)
            {
                vars[stubMethod.Parameters[stubIndex]] = args[i];
            }
        }
    }

    private AssemblyContracts GetOrBuild(IAssemblySymbol assembly) =>
        _byAssembly.GetOrAdd(assembly, BuildAssemblyContracts);

    private AssemblyContracts BuildAssemblyContracts(IAssemblySymbol assembly)
    {
        var rawEntries = new List<(ISymbol Member, string Kind, string Expression, string? Description)>();
        var bodySet = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var reqSet = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var attr in assembly.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != ContractMetadataAttrName ||
                attr.ConstructorArguments.Length < 3 ||
                attr.ConstructorArguments[0].Value is not string memberId ||
                attr.ConstructorArguments[1].Value is not string kind ||
                attr.ConstructorArguments[2].Value is not string expr ||
                string.IsNullOrWhiteSpace(memberId) ||
                string.IsNullOrWhiteSpace(expr))
            {
                continue;
            }

            string? desc = attr.ConstructorArguments.Length >= 4
                ? attr.ConstructorArguments[3].Value as string
                : null;

            var resolved = DocumentationCommentId.GetFirstSymbolForDeclarationId(memberId, _compilation);
            if (resolved is null ||
                !SymbolEqualityComparer.Default.Equals(resolved.ContainingAssembly, assembly))
            {
                continue;
            }

            if (resolved is not IMethodSymbol and not IPropertySymbol)
            {
                continue;
            }

            rawEntries.Add((resolved.OriginalDefinition, kind, expr, desc));
            if (kind is "Body" or "IsValid" or "Invariant")
            {
                bodySet.Add(resolved.OriginalDefinition);
            }
            else if (kind == "Requires")
            {
                reqSet.Add(resolved.OriginalDefinition);
            }
        }

        if (rawEntries.Count == 0)
        {
            return AssemblyContracts.Empty;
        }

        var stubPlan = new List<(string StubName, ISymbol Member, string Kind, string Expression, string? Description)>();
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable");
        sb.Append("internal static class __VerifiableCrossAssemblyStubs_")
            .Append(SmtTypeSystem.SanitizeName(assembly.Name))
            .AppendLine();
        sb.AppendLine("{");

        var idx = 0;
        foreach (var (member, kind, expr, desc) in rawEntries)
        {
            var stubName = $"__stub_{idx++}";
            if (!TryAppendStubMethod(sb, stubName, member, kind, expr))
            {
                continue;
            }

            stubPlan.Add((stubName, member, kind, expr, desc));
        }

        sb.AppendLine("}");

        if (stubPlan.Count == 0)
        {
            return AssemblyContracts.Empty;
        }

        var parseOptions = (_compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions)
            ?? new CSharpParseOptions(LanguageVersion.Preview);
        var stubTree = CSharpSyntaxTree.ParseText(
            sb.ToString(),
            parseOptions,
            path: $"__VerifiableCrossAssemblyStubs_{SmtTypeSystem.SanitizeName(assembly.Name)}.g.cs");

        if (stubTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return AssemblyContracts.Empty;
        }

        var stubCompilation = _compilation.AddSyntaxTrees(stubTree);
        var stubModel = stubCompilation.GetSemanticModel(stubTree);
        var methodDecls = stubTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToDictionary(m => m.Identifier.ValueText, m => m, StringComparer.Ordinal);

        var members = new Dictionary<ISymbol, MemberContracts>(SymbolEqualityComparer.Default);
        foreach (var (stubName, member, kind, expr, desc) in stubPlan)
        {
            if (!methodDecls.TryGetValue(stubName, out var md) || md.ExpressionBody is null)
            {
                continue;
            }

            if (stubModel.GetDiagnostics(md.Span).Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                continue;
            }

            if (stubModel.GetDeclaredSymbol(md) is not IMethodSymbol stubMethod)
            {
                continue;
            }

            if (stubModel.GetOperation(md.ExpressionBody) is not IBlockOperation arrowBlock)
            {
                continue;
            }

            var retValue = arrowBlock.Operations.OfType<IReturnOperation>().SingleOrDefault()?.ReturnedValue;
            if (retValue is null)
            {
                continue;
            }

            if (!members.TryGetValue(member, out var mc))
            {
                mc = new MemberContracts();
                members[member] = mc;
            }

            var entry = new StubEntry(retValue, stubMethod, expr, desc);
            switch (kind)
            {
                case "Body":
                case "IsValid":
                case "Invariant":
                    mc.Body ??= entry;
                    break;
                case "Requires":
                    mc.Requires.Add(entry);
                    break;
                case "Ensures":
                    mc.Ensures.Add(entry);
                    break;
            }
        }

        return new AssemblyContracts(members, bodySet, reqSet);
    }

    private static bool TryAppendStubMethod(
        StringBuilder sb,
        string stubName,
        ISymbol member,
        string kind,
        string expression)
    {
        if (SyntaxFactory.ParseExpression(expression).ContainsDiagnostics)
        {
            return false;
        }

        if (member is IMethodSymbol method)
        {
            if (method.IsGenericMethod || method.ContainingType is null || method.ContainingType.IsGenericType)
            {
                return false;
            }

            var returnType = kind is "Requires" or "Ensures" or "Invariant" or "IsValid"
                ? "bool"
                : method.ReturnType.ToDisplayString(StubTypeFormat);

            if (kind == "Body" && method.ReturnsVoid)
            {
                return false;
            }

            if (kind == "Ensures" && method.ReturnsVoid)
            {
                return false;
            }

            var parameters = new List<string>();
            if (!method.IsStatic)
            {
                parameters.Add($"{method.ContainingType.ToDisplayString(StubTypeFormat)} {ThisPlaceholder}");
            }

            foreach (var p in method.Parameters)
            {
                if (p.RefKind != RefKind.None)
                {
                    return false;
                }

                parameters.Add($"{p.Type.ToDisplayString(StubTypeFormat)} {EscapeIdentifier(p.Name)}");
            }

            if (kind == "Ensures")
            {
                parameters.Add($"{method.ReturnType.ToDisplayString(StubTypeFormat)} {ResultPlaceholder}");
            }

            sb.Append("    internal static ")
                .Append(returnType)
                .Append(' ')
                .Append(stubName)
                .Append('(')
                .Append(string.Join(", ", parameters))
                .Append(") => (")
                .Append(expression)
                .AppendLine(");");
            return true;
        }

        if (member is IPropertySymbol prop)
        {
            if (prop.IsStatic || prop.IsIndexer || prop.ContainingType is null || prop.ContainingType.IsGenericType)
            {
                return false;
            }

            var returnType = kind == "Invariant"
                ? "bool"
                : prop.Type.ToDisplayString(StubTypeFormat);

            sb.Append("    internal static ")
                .Append(returnType)
                .Append(' ')
                .Append(stubName)
                .Append('(')
                .Append(prop.ContainingType.ToDisplayString(StubTypeFormat))
                .Append(' ')
                .Append(ThisPlaceholder)
                .Append(") => (")
                .Append(expression)
                .AppendLine(");");
            return true;
        }

        return false;
    }

    private static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}
