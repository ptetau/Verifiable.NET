using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

/// <summary>
/// Generates raw I/O DTOs, invariant marshaling gates, and checked port wrappers for <c>[InboundPort]</c> methods.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class InboundPortGenerator : IIncrementalGenerator
{
    private sealed record PortMethodTarget(
        IMethodSymbol Method,
        IReadOnlyList<string> UsingDirectives);

    private sealed record DomainMember(
        string Name,
        ITypeSymbol Type,
        bool IsConstructorParam,
        bool IsSettableProperty);

    private sealed record DomainShape(
        INamedTypeSymbol Symbol,
        IReadOnlyList<DomainMember> ConstructorMembers,
        IReadOnlyList<DomainMember> InitMembers,
        ISet<string> AllInstanceMemberNames,
        IReadOnlyList<string> Invariants,
        IReadOnlyList<string> UsingDirectives);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var portMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.InboundPortAttributeMetadataName,
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (ctx, _) => TransformMethod(ctx))
            .Where(static m => m is not null)
            .Collect();

        context.RegisterSourceOutput(portMethods, static (spc, methods) =>
        {
            if (methods.IsDefaultOrEmpty)
            {
                return;
            }

            EmitAll(spc, methods!);
        });
    }

    private static PortMethodTarget? TransformMethod(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method)
        {
            return null;
        }

        var usings = CollectUsings(context.TargetNode.SyntaxTree);
        return new PortMethodTarget(method, usings);
    }

    private static IReadOnlyList<string> CollectUsings(SyntaxTree tree)
    {
        var list = new List<string>();
        if (tree.GetRoot() is CompilationUnitSyntax cu)
        {
            foreach (var u in cu.Usings)
            {
                if (u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
                {
                    var ns = u.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(ns))
                    {
                        list.Add(ns);
                    }
                }
            }
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void EmitAll(SourceProductionContext spc, ImmutableArray<PortMethodTarget> targets)
    {
        var discoveredTypes = new Dictionary<INamedTypeSymbol, IReadOnlyList<string>>(SymbolEqualityComparer.Default);
        var queue = new Queue<(ITypeSymbol Type, IReadOnlyList<string> Usings)>();

        foreach (var t in targets)
        {
            foreach (var p in t.Method.Parameters)
            {
                queue.Enqueue((p.Type, t.UsingDirectives));
            }
        }

        while (queue.Count > 0)
        {
            var (type, usings) = queue.Dequeue();
            DiscoverCompositeTypes(type, usings, discoveredTypes, queue);
        }

        foreach (var kv in discoveredTypes)
        {
            var shape = BuildDomainShape(kv.Key, kv.Value);
            EmitGateFile(spc, shape);
        }

        foreach (var group in targets.GroupBy(t => t.Method.ContainingType, SymbolEqualityComparer.Default))
        {
            if (group.Key is INamedTypeSymbol declaringType)
            {
                EmitPortClassFile(spc, declaringType, group.ToList());
            }
        }
    }

    private static void DiscoverCompositeTypes(
        ITypeSymbol type,
        IReadOnlyList<string> usings,
        Dictionary<INamedTypeSymbol, IReadOnlyList<string>> discovered,
        Queue<(ITypeSymbol Type, IReadOnlyList<string> Usings)> queue)
    {
        if (type is INamedTypeSymbol nullableNamed &&
            nullableNamed.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nullableNamed.TypeArguments.Length == 1)
        {
            queue.Enqueue((nullableNamed.TypeArguments[0], usings));
            return;
        }

        if (TypeRefHelper.TryGetRefinedUnderlyingType(type, out _, out _))
        {
            return;
        }

        if (TypeRefHelper.IsScalarOrKnownBclType(type))
        {
            return;
        }

        if (TypeRefHelper.IsImmutableCollection(type, out _, out var elemOrKey, out var valType))
        {
            if (elemOrKey is not null) queue.Enqueue((elemOrKey, usings));
            if (valType is not null) queue.Enqueue((valType, usings));
            return;
        }

        if (type is INamedTypeSymbol named && !discovered.ContainsKey(named))
        {
            var mergedUsings = usings.ToList();
            foreach (var loc in named.Locations)
            {
                if (loc.IsInSource && loc.SourceTree is { } st)
                {
                    mergedUsings.AddRange(CollectUsings(st));
                }
            }
            discovered[named] = mergedUsings.Distinct(StringComparer.Ordinal).ToList();

            var shape = BuildDomainShape(named, discovered[named]);
            foreach (var m in shape.ConstructorMembers.Concat(shape.InitMembers))
            {
                queue.Enqueue((m.Type, discovered[named]));
            }
        }
    }

    private static DomainShape BuildDomainShape(INamedTypeSymbol symbol, IReadOnlyList<string> usings)
    {
        var props = symbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && p.Name != "EqualityContract")
            .ToList();

        var fields = symbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsImplicitlyDeclared && f.DeclaredAccessibility == Accessibility.Public)
            .ToList();

        var allMemberNames = new HashSet<string>(
            props.Select(p => p.Name).Concat(fields.Select(f => f.Name)),
            StringComparer.Ordinal);

        var nonGhostProps = props.Where(p => !TypeRefHelper.HasGhostAttribute(p)).ToList();
        var propByName = new Dictionary<string, IPropertySymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in nonGhostProps)
        {
            propByName[p.Name] = p;
        }

        var bestCtor = symbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && !IsCopyConstructor(symbol, c))
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        var ctorMembers = new List<DomainMember>();
        var matchedPropNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (bestCtor is not null)
        {
            foreach (var p in bestCtor.Parameters)
            {
                var propName = propByName.TryGetValue(p.Name, out var matchedProp)
                    ? matchedProp.Name
                    : ToPascalCase(p.Name);
                matchedPropNames.Add(propName);
                matchedPropNames.Add(p.Name);
                ctorMembers.Add(new DomainMember(propName, p.Type, IsConstructorParam: true, IsSettableProperty: false));
            }
        }

        var initMembers = new List<DomainMember>();
        foreach (var p in nonGhostProps)
        {
            if (matchedPropNames.Contains(p.Name))
            {
                continue;
            }

            if (p.SetMethod is { DeclaredAccessibility: Accessibility.Public })
            {
                initMembers.Add(new DomainMember(p.Name, p.Type, IsConstructorParam: false, IsSettableProperty: true));
            }
        }

        var invariants = TypeRefHelper.GetInvariants(symbol);
        return new DomainShape(symbol, ctorMembers, initMembers, allMemberNames, invariants, usings);
    }

    private static bool IsCopyConstructor(INamedTypeSymbol type, IMethodSymbol ctor) =>
        ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type);

    private static void EmitGateFile(SourceProductionContext spc, DomainShape shape)
    {
        var ns = TypeRefHelper.GetNamespace(shape.Symbol);
        var rawName = GetRawTypeShortName(shape.Symbol);
        var gateName = GetGateShortName(shape.Symbol);
        var targetGlobal = TypeRefHelper.ToGlobalTypeName(shape.Symbol);

        var usings = new HashSet<string>(shape.UsingDirectives, StringComparer.Ordinal)
        {
            "System",
            "System.Collections.Generic",
            "System.Collections.Immutable",
            "System.Linq",
            "Verifiable.Contracts"
        };

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        foreach (var u in usings.OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append("using ").Append(u).AppendLine(";");
        }
        sb.AppendLine();

        var indent = string.Empty;
        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").Append(ns).AppendLine();
            sb.AppendLine("{");
            indent = "    ";
        }

        // Raw DTO class
        var allInputMembers = shape.ConstructorMembers.Concat(shape.InitMembers).ToList();
        sb.Append(indent).Append("public sealed class ").Append(rawName).AppendLine();
        sb.Append(indent).AppendLine("{");
        foreach (var m in allInputMembers)
        {
            var rawType = GetRawPropertyTypeName(m.Type, forCollectionItem: false);
            var defaultInit = GetRawDefaultInitializer(m.Type);
            sb.Append(indent).Append("    public ").Append(rawType).Append(' ').Append(m.Name)
                .Append(" { get; set; }").Append(defaultInit).AppendLine();
        }
        sb.Append(indent).AppendLine("}");
        sb.AppendLine();

        // Gate static class
        sb.Append(indent).Append("public static class ").Append(gateName).AppendLine();
        sb.Append(indent).AppendLine("{");
        var b = indent + "    ";
        sb.Append(b).Append("public static global::Verifiable.Contracts.MarshalResult<").Append(targetGlobal)
            .Append("> TryMarshal(").Append(rawName).AppendLine("? raw, string path = \"\")");
        sb.Append(b).AppendLine("{");
        sb.Append(b).AppendLine("    var __ver_violations = new global::System.Collections.Generic.List<global::Verifiable.Contracts.ContractViolation>();");
        sb.Append(b).AppendLine("    var __ver_prefix = string.IsNullOrEmpty(path) ? string.Empty : path + \".\";");
        sb.Append(b).AppendLine("    if (raw is null)");
        sb.Append(b).AppendLine("    {");
        sb.Append(b).Append("        __ver_violations.Add(new global::Verifiable.Contracts.ContractViolation(")
            .Append("string.IsNullOrEmpty(path) ? \"").Append(shape.Symbol.Name).Append("\" : path, \"!= null\", \"Payload cannot be null.\"));")
            .AppendLine();
        sb.Append(b).Append("        return global::Verifiable.Contracts.MarshalResult<").Append(targetGlobal)
            .AppendLine(">.Fail(global::System.Collections.Immutable.ImmutableArray.CreateRange(__ver_violations));");
        sb.Append(b).AppendLine("    }");
        sb.AppendLine();

        var counter = 0;
        var ctorVarNames = new List<string>();
        foreach (var m in shape.ConstructorMembers)
        {
            var varName = $"__ver_m_{counter++}";
            ctorVarNames.Add(varName);
            EmitMarshalExpression(
                sb,
                b + "    ",
                $"raw.{m.Name}",
                m.Type,
                $"__ver_prefix + {TypeRefHelper.EscapeStringLiteral(m.Name)}",
                varName,
                ref counter);
        }

        var initAssignments = new List<(string PropName, string VarName)>();
        foreach (var m in shape.InitMembers)
        {
            var varName = $"__ver_m_{counter++}";
            initAssignments.Add((m.Name, varName));
            EmitMarshalExpression(
                sb,
                b + "    ",
                $"raw.{m.Name}",
                m.Type,
                $"__ver_prefix + {TypeRefHelper.EscapeStringLiteral(m.Name)}",
                varName,
                ref counter);
        }

        sb.AppendLine();
        sb.Append(b).AppendLine("    if (__ver_violations.Count > 0)");
        sb.Append(b).AppendLine("    {");
        sb.Append(b).Append("        return global::Verifiable.Contracts.MarshalResult<").Append(targetGlobal)
            .AppendLine(">.Fail(global::System.Collections.Immutable.ImmutableArray.CreateRange(__ver_violations));");
        sb.Append(b).AppendLine("    }");
        sb.AppendLine();

        sb.Append(b).Append("    var candidate = new ").Append(targetGlobal).Append('(')
            .Append(string.Join(", ", ctorVarNames)).Append(')');
        if (initAssignments.Count > 0)
        {
            sb.Append(" { ")
                .Append(string.Join(", ", initAssignments.Select(a => $"{a.PropName} = {a.VarName}")))
                .Append(" }");
        }
        sb.AppendLine(";");

        foreach (var inv in shape.Invariants)
        {
            if (PredicateHelper.TryValidateExpression(inv, out var parsedInv, out _))
            {
                var qualified = PredicateHelper.QualifyMembers(parsedInv!, "candidate", shape.AllInstanceMemberNames);
                var invPath = $"string.IsNullOrEmpty(path) ? {TypeRefHelper.EscapeStringLiteral(shape.Symbol.Name)} : path";
                var invLit = TypeRefHelper.EscapeStringLiteral(inv);
                var invMsg = TypeRefHelper.EscapeStringLiteral($"Invariant '{inv}' failed on {shape.Symbol.Name}.");
                sb.Append(b).Append("    if (!(").Append(qualified).AppendLine("))");
                sb.Append(b).AppendLine("    {");
                sb.Append(b).Append("        __ver_violations.Add(new global::Verifiable.Contracts.ContractViolation(")
                    .Append(invPath).Append(", ").Append(invLit).Append(", ").Append(invMsg).AppendLine("));");
                sb.Append(b).AppendLine("    }");
            }
        }

        sb.AppendLine();
        sb.Append(b).AppendLine("    if (__ver_violations.Count > 0)");
        sb.Append(b).AppendLine("    {");
        sb.Append(b).Append("        return global::Verifiable.Contracts.MarshalResult<").Append(targetGlobal)
            .AppendLine(">.Fail(global::System.Collections.Immutable.ImmutableArray.CreateRange(__ver_violations));");
        sb.Append(b).AppendLine("    }");
        sb.AppendLine();
        sb.Append(b).Append("    return global::Verifiable.Contracts.MarshalResult<").Append(targetGlobal)
            .AppendLine(">.Ok(candidate);");
        sb.Append(b).AppendLine("}");
        sb.Append(indent).AppendLine("}");

        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine("}");
        }

        var hintName = TypeRefHelper.SanitizeHintName(
            string.IsNullOrEmpty(ns) ? shape.Symbol.Name : $"{ns}.{shape.Symbol.Name}") + ".Gate.g.cs";
        spc.AddSource(hintName, sb.ToString());
    }

    private static void EmitPortClassFile(
        SourceProductionContext spc,
        INamedTypeSymbol declaringType,
        IReadOnlyList<PortMethodTarget> methods)
    {
        var ns = TypeRefHelper.GetNamespace(declaringType);
        var portClassName = declaringType.Name + "Port";
        var declaringGlobal = TypeRefHelper.ToGlobalTypeName(declaringType);

        var usings = new HashSet<string>(
            methods.SelectMany(m => m.UsingDirectives),
            StringComparer.Ordinal)
        {
            "System",
            "System.Collections.Generic",
            "System.Collections.Immutable",
            "System.Linq",
            "Verifiable.Contracts"
        };

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        foreach (var u in usings.OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append("using ").Append(u).AppendLine(";");
        }
        sb.AppendLine();

        var indent = string.Empty;
        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").Append(ns).AppendLine();
            sb.AppendLine("{");
            indent = "    ";
        }

        var accessibility = declaringType.DeclaredAccessibility == Accessibility.Internal ? "internal" : "public";
        sb.Append(indent).Append(accessibility).Append(" static partial class ").AppendLine(portClassName);
        sb.Append(indent).AppendLine("{");
        var mIndent = indent + "    ";

        for (var idx = 0; idx < methods.Count; idx++)
        {
            if (idx > 0) sb.AppendLine();
            EmitPortMethod(sb, mIndent, declaringGlobal, methods[idx].Method);
        }

        sb.Append(indent).AppendLine("}");

        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine("}");
        }

        var hintName = TypeRefHelper.SanitizeHintName(
            string.IsNullOrEmpty(ns) ? portClassName : $"{ns}.{portClassName}") + ".InboundPort.g.cs";
        spc.AddSource(hintName, sb.ToString());
    }

    private static void EmitPortMethod(
        StringBuilder sb,
        string indent,
        string declaringGlobal,
        IMethodSymbol method)
    {
        var returnGlobal = TypeRefHelper.ToGlobalTypeName(method.ReturnType);
        var sigParams = new List<string>();

        if (!method.IsStatic)
        {
            sigParams.Add($"{declaringGlobal} __ver_instance");
        }

        foreach (var p in method.Parameters)
        {
            var rawType = GetRawPropertyTypeName(p.Type, forCollectionItem: false);
            sigParams.Add($"{rawType} __ver_raw_{p.Name}");
        }

        sb.Append(indent).Append("public static global::Verifiable.Contracts.MarshalResult<").Append(returnGlobal)
            .Append("> ").Append(method.Name).Append('(').Append(string.Join(", ", sigParams)).AppendLine(")");
        sb.Append(indent).AppendLine("{");

        var b = indent + "    ";
        sb.Append(b).AppendLine("var __ver_violations = new global::System.Collections.Generic.List<global::Verifiable.Contracts.ContractViolation>();");
        var counter = 0;

        foreach (var p in method.Parameters)
        {
            EmitMarshalExpression(
                sb,
                b,
                $"__ver_raw_{p.Name}",
                p.Type,
                TypeRefHelper.EscapeStringLiteral(p.Name),
                p.Name,
                ref counter);
        }

        sb.AppendLine();
        sb.Append(b).AppendLine("if (__ver_violations.Count > 0)");
        sb.Append(b).AppendLine("{");
        sb.Append(b).Append("    return global::Verifiable.Contracts.MarshalResult<").Append(returnGlobal)
            .AppendLine(">.Fail(global::System.Collections.Immutable.ImmutableArray.CreateRange(__ver_violations));");
        sb.Append(b).AppendLine("}");

        var requires = TypeRefHelper.GetRequires(method);
        if (requires.Count > 0)
        {
            var instanceMemberNames = new HashSet<string>(
                method.ContainingType.GetMembers()
                    .Where(m => !m.IsStatic)
                    .Select(m => m.Name),
                StringComparer.Ordinal);

            foreach (var req in requires)
            {
                if (PredicateHelper.TryValidateExpression(req, out var parsedReq, out _))
                {
                    var evalExpr = method.IsStatic
                        ? req
                        : PredicateHelper.QualifyMembers(parsedReq!, "__ver_instance", instanceMemberNames);
                    var reqLit = TypeRefHelper.EscapeStringLiteral(req);
                    var msgLit = TypeRefHelper.EscapeStringLiteral($"Precondition '{req}' failed.");
                    sb.Append(b).Append("if (!(").Append(evalExpr).AppendLine("))");
                    sb.Append(b).AppendLine("{");
                    sb.Append(b).Append("    __ver_violations.Add(new global::Verifiable.Contracts.ContractViolation(\"requires\", ")
                        .Append(reqLit).Append(", ").Append(msgLit).AppendLine("));");
                    sb.Append(b).AppendLine("}");
                }
            }

            sb.Append(b).AppendLine("if (__ver_violations.Count > 0)");
            sb.Append(b).AppendLine("{");
            sb.Append(b).Append("    return global::Verifiable.Contracts.MarshalResult<").Append(returnGlobal)
                .AppendLine(">.Fail(global::System.Collections.Immutable.ImmutableArray.CreateRange(__ver_violations));");
            sb.Append(b).AppendLine("}");
        }

        sb.AppendLine();
        var callTarget = method.IsStatic ? declaringGlobal : "__ver_instance";
        var callArgs = string.Join(", ", method.Parameters.Select(p => p.Name));
        sb.Append(b).Append("return global::Verifiable.Contracts.MarshalResult<").Append(returnGlobal)
            .Append(">.Ok(").Append(callTarget).Append('.').Append(method.Name)
            .Append('(').Append(callArgs).AppendLine("));");
        sb.Append(indent).AppendLine("}");
    }

    private static void EmitMarshalExpression(
        StringBuilder sb,
        string indent,
        string rawExpr,
        ITypeSymbol targetType,
        string fieldPathExpr,
        string targetVarName,
        ref int counter)
    {
        var id = counter++;
        var targetGlobal = TypeRefHelper.ToGlobalTypeName(targetType);

        if (targetType is INamedTypeSymbol nullableNamed &&
            nullableNamed.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nullableNamed.TypeArguments.Length == 1)
        {
            var innerType = nullableNamed.TypeArguments[0];
            var innerVar = $"__ver_opt_{id}";
            sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName).AppendLine(" = null;");
            sb.Append(indent).Append("if (").Append(rawExpr).AppendLine(" is not null)");
            sb.Append(indent).AppendLine("{");
            var innerRawExpr = IsNullableValueTypeInRaw(innerType) ? $"{rawExpr}.Value" : rawExpr;
            EmitMarshalExpression(sb, indent + "    ", innerRawExpr, innerType, fieldPathExpr, innerVar, ref counter);
            sb.Append(indent).Append("    ").Append(targetVarName).Append(" = ").Append(innerVar).AppendLine(";");
            sb.Append(indent).AppendLine("}");
            return;
        }

        if (TypeRefHelper.TryGetRefinedUnderlyingType(targetType, out var underlying, out _))
        {
            var resVar = $"__ver_res_{id}";
            sb.Append(indent).Append("var ").Append(resVar).Append(" = ").Append(targetGlobal)
                .Append(".TryMarshal(").Append(rawExpr).Append("!, ").Append(fieldPathExpr).AppendLine(");");
            sb.Append(indent).Append("if (!").Append(resVar).Append(".IsValid) __ver_violations.AddRange(")
                .Append(resVar).AppendLine(".Violations);");
            sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName)
                .Append(" = ").Append(resVar).AppendLine(".Value;");
            return;
        }

        if (TypeRefHelper.IsImmutableCollection(targetType, out var kind, out var elemOrKey, out var valType))
        {
            if (kind == "ImmutableDictionary")
            {
                var kGlobal = TypeRefHelper.ToGlobalTypeName(elemOrKey!);
                var vGlobal = TypeRefHelper.ToGlobalTypeName(valType!);
                var builderVar = $"__ver_dict_{id}";
                var kvVar = $"__ver_kv_{id}";
                var keyVar = $"__ver_k_{id}";
                var valVar = $"__ver_v_{id}";
                var beforeCount = $"__ver_errBefore_{id}";

                sb.Append(indent).Append("var ").Append(builderVar)
                    .Append(" = global::System.Collections.Immutable.ImmutableDictionary.CreateBuilder<")
                    .Append(kGlobal).Append(", ").Append(vGlobal).AppendLine(">();");
                sb.Append(indent).Append("if (").Append(rawExpr).AppendLine(" is not null)");
                sb.Append(indent).AppendLine("{");
                sb.Append(indent).Append("    foreach (var ").Append(kvVar).Append(" in ").Append(rawExpr).AppendLine(")");
                sb.Append(indent).AppendLine("    {");
                sb.Append(indent).Append("        var ").Append(beforeCount).AppendLine(" = __ver_violations.Count;");
                var dictPathVar = $"__ver_dp_{id}";
                sb.Append(indent).Append("        var ").Append(dictPathVar).Append(" = ").Append(fieldPathExpr)
                    .Append(" + $\"[{").Append(kvVar).AppendLine(".Key}]\";");
                EmitMarshalExpression(sb, indent + "        ", $"{kvVar}.Key", elemOrKey!, dictPathVar, keyVar, ref counter);
                EmitMarshalExpression(sb, indent + "        ", $"{kvVar}.Value", valType!, dictPathVar, valVar, ref counter);
                sb.Append(indent).Append("        if (__ver_violations.Count == ").Append(beforeCount).Append(") ")
                    .Append(builderVar).Append('[').Append(keyVar).Append("] = ").Append(valVar).AppendLine(";");
                sb.Append(indent).AppendLine("    }");
                sb.Append(indent).AppendLine("}");
                sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName)
                    .Append(" = ").Append(builderVar).AppendLine(".ToImmutable();");
                return;
            }
            else
            {
                var eGlobal = TypeRefHelper.ToGlobalTypeName(elemOrKey!);
                var builderVar = $"__ver_col_{id}";
                var idxVar = $"__ver_i_{id}";
                var elemVar = $"__ver_e_{id}";
                var listVar = $"__ver_rawList_{id}";

                sb.Append(indent).Append("var ").Append(builderVar)
                    .Append(" = global::System.Collections.Immutable.").Append(kind).Append(".CreateBuilder<")
                    .Append(eGlobal).AppendLine(">();");
                sb.Append(indent).Append("var ").Append(listVar).Append(" = ").Append(rawExpr).AppendLine(";");
                sb.Append(indent).Append("if (").Append(listVar).AppendLine(" is not null)");
                sb.Append(indent).AppendLine("{");
                sb.Append(indent).Append("    for (var ").Append(idxVar).Append(" = 0; ").Append(idxVar)
                    .Append(" < ").Append(listVar).Append(".Count; ").Append(idxVar).AppendLine("++)");
                sb.Append(indent).AppendLine("    {");
                var listPathVar = $"__ver_lp_{id}";
                sb.Append(indent).Append("        var ").Append(listPathVar).Append(" = ").Append(fieldPathExpr)
                    .Append(" + $\"[{").Append(idxVar).AppendLine("}]\";");
                EmitMarshalExpression(sb, indent + "        ", $"{listVar}[{idxVar}]", elemOrKey!, listPathVar, elemVar, ref counter);
                sb.Append(indent).Append("        ").Append(builderVar).Append(".Add(").Append(elemVar).AppendLine(");");
                sb.Append(indent).AppendLine("    }");
                sb.Append(indent).AppendLine("}");
                sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName)
                    .Append(" = ").Append(builderVar).AppendLine(".ToImmutable();");
                return;
            }
        }

        if (targetType.TypeKind == TypeKind.Enum)
        {
            sb.Append(indent).Append("if (!global::System.Enum.IsDefined(typeof(").Append(targetGlobal).Append("), ").Append(rawExpr).AppendLine("))");
            sb.Append(indent).AppendLine("{");
            sb.Append(indent).Append("    __ver_violations.Add(new global::Verifiable.Contracts.ContractViolation(")
                .Append(fieldPathExpr).Append(", \"Enum.IsDefined\", $\"Invalid ").Append(targetType.Name)
                .Append(" value '{").Append(rawExpr).AppendLine("}'.\"));");
            sb.Append(indent).AppendLine("}");
            sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName).Append(" = ").Append(rawExpr).AppendLine(";");
            return;
        }

        if (TypeRefHelper.IsScalarOrKnownBclType(targetType))
        {
            if (targetType.SpecialType == SpecialType.System_String && targetType.NullableAnnotation != NullableAnnotation.Annotated)
            {
                sb.Append(indent).Append("if (").Append(rawExpr).AppendLine(" is null)");
                sb.Append(indent).AppendLine("{");
                sb.Append(indent).Append("    __ver_violations.Add(new global::Verifiable.Contracts.ContractViolation(")
                    .Append(fieldPathExpr).AppendLine(", \"!= null\", \"String field cannot be null.\"));");
                sb.Append(indent).AppendLine("}");
                sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName)
                    .Append(" = ").Append(rawExpr).AppendLine(" ?? string.Empty;");
            }
            else
            {
                sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName)
                    .Append(" = ").Append(rawExpr).AppendLine(";");
            }
            return;
        }

        if (targetType is INamedTypeSymbol composite)
        {
            var gateGlobal = GetGateGlobalName(composite);
            var resVar = $"__ver_res_{id}";
            sb.Append(indent).Append("var ").Append(resVar).Append(" = ").Append(gateGlobal)
                .Append(".TryMarshal(").Append(rawExpr).Append(", ").Append(fieldPathExpr).AppendLine(");");
            sb.Append(indent).Append("if (!").Append(resVar).Append(".IsValid) __ver_violations.AddRange(")
                .Append(resVar).AppendLine(".Violations);");
            sb.Append(indent).Append(targetGlobal).Append(' ').Append(targetVarName)
                .Append(" = ").Append(resVar).AppendLine(".Value;");
        }
    }

    private static bool IsNullableValueTypeInRaw(ITypeSymbol type)
    {
        if (TypeRefHelper.TryGetRefinedUnderlyingType(type, out var underlying, out _))
        {
            return underlying!.IsValueType;
        }
        return type.IsValueType;
    }

    private static string GetRawPropertyTypeName(ITypeSymbol type, bool forCollectionItem)
    {
        if (type is INamedTypeSymbol nullableNamed &&
            nullableNamed.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nullableNamed.TypeArguments.Length == 1)
        {
            var inner = GetRawPropertyTypeName(nullableNamed.TypeArguments[0], forCollectionItem: false);
            return inner.EndsWith("?", StringComparison.Ordinal) ? inner : inner + "?";
        }

        if (TypeRefHelper.TryGetRefinedUnderlyingType(type, out var underlying, out _))
        {
            if (underlying!.SpecialType == SpecialType.System_String)
            {
                return forCollectionItem ? "string" : "string?";
            }
            return TypeRefHelper.ToGlobalTypeName(underlying);
        }

        return GetRawCollectionOrCompositeTypeName(type, forCollectionItem);
    }

    private static string GetRawCollectionOrCompositeTypeName(ITypeSymbol type, bool forCollectionItem)
    {
        if (TypeRefHelper.IsImmutableCollection(type, out var kind, out var elemOrKey, out var valType))
        {
            if (kind == "ImmutableDictionary")
            {
                var k = GetRawPropertyTypeName(elemOrKey!, forCollectionItem: true);
                var v = GetRawPropertyTypeName(valType!, forCollectionItem: true);
                return $"global::System.Collections.Generic.Dictionary<{k}, {v}>?";
            }
            var e = GetRawPropertyTypeName(elemOrKey!, forCollectionItem: true);
            return $"global::System.Collections.Generic.List<{e}>?";
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return forCollectionItem ? "string" : "string?";
        }

        if (TypeRefHelper.IsScalarOrKnownBclType(type))
        {
            return TypeRefHelper.ToGlobalTypeName(type);
        }

        if (type is INamedTypeSymbol composite)
        {
            return GetRawGlobalName(composite) + (forCollectionItem ? string.Empty : "?");
        }

        return TypeRefHelper.ToGlobalTypeName(type);
    }

    private static string GetRawDefaultInitializer(ITypeSymbol type)
    {
        if (TypeRefHelper.IsImmutableCollection(type, out _, out _, out _))
        {
            return " = new();";
        }

        if (type is INamedTypeSymbol nullableNamed &&
            nullableNamed.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return string.Empty;
        }

        if (TypeRefHelper.TryGetRefinedUnderlyingType(type, out var underlying, out _))
        {
            return underlying!.IsReferenceType ? string.Empty : string.Empty;
        }

        return string.Empty;
    }

    private static string GetRawTypeShortName(INamedTypeSymbol symbol) =>
        symbol.ContainingType is null ? $"Raw{symbol.Name}" : $"Raw{symbol.ContainingType.Name}_{symbol.Name}";

    private static string GetGateShortName(INamedTypeSymbol symbol) =>
        symbol.ContainingType is null ? $"{symbol.Name}Gate" : $"{symbol.ContainingType.Name}_{symbol.Name}Gate";

    private static string GetRawGlobalName(INamedTypeSymbol symbol)
    {
        var ns = TypeRefHelper.GetNamespace(symbol);
        var name = GetRawTypeShortName(symbol);
        return string.IsNullOrEmpty(ns) ? $"global::{name}" : $"global::{ns}.{name}";
    }

    private static string GetGateGlobalName(INamedTypeSymbol symbol)
    {
        var ns = TypeRefHelper.GetNamespace(symbol);
        var name = GetGateShortName(symbol);
        return string.IsNullOrEmpty(ns) ? $"global::{name}" : $"global::{ns}.{name}";
    }

    private static string ToPascalCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsUpper(name[0])
            ? name
            : char.ToUpperInvariant(name[0]) + name.Substring(1);
}
