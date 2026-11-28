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
/// Generates verified 1:1 field/property projection methods for <c>[MapTo(typeof(TargetType))]</c>
/// on source types and <c>[MapTo]</c> on <c>static partial</c> projection methods.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MapToGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat NullableGlobalFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private sealed record TypeMapWorkItem(
        INamedTypeSymbol SourceType,
        INamedTypeSymbol? TargetType,
        string? ExplicitMethodName,
        Location DiagnosticLocation,
        Compilation Compilation);

    private sealed record MethodMapWorkItem(
        IMethodSymbol Method,
        INamedTypeSymbol? ExplicitTargetType,
        Location DiagnosticLocation,
        bool IsPartialMethod,
        bool AllContainingTypesPartial,
        Compilation Compilation);

    private sealed record SourceMember(
        string Name,
        ITypeSymbol Type);

    private sealed record TargetMember(
        string Name,
        ITypeSymbol Type,
        bool IsRequired);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var typeTargets = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.MapToAttributeMetadataName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => TransformType(ctx))
            .SelectMany(static (items, _) => items);

        var methodTargets = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.MapToAttributeMetadataName,
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (ctx, _) => TransformMethod(ctx))
            .Where(static item => item is not null);

        context.RegisterSourceOutput(typeTargets, static (spc, item) => EmitTypeMapping(spc, item));
        context.RegisterSourceOutput(methodTargets, static (spc, item) =>
        {
            if (item is not null) EmitMethodMapping(spc, item);
        });
    }

    private static ImmutableArray<TypeMapWorkItem> TransformType(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol sourceType)
        {
            return ImmutableArray<TypeMapWorkItem>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<TypeMapWorkItem>();
        foreach (var attr in context.Attributes)
        {
            INamedTypeSymbol? targetType = null;
            if (attr.ConstructorArguments.Length >= 1 &&
                attr.ConstructorArguments[0].Value is INamedTypeSymbol namedTarget)
            {
                targetType = namedTarget;
            }

            string? methodName = null;
            foreach (var namedArg in attr.NamedArguments)
            {
                if (namedArg.Key == "MethodName" && namedArg.Value.Value is string m && !string.IsNullOrWhiteSpace(m))
                {
                    methodName = m;
                }
            }

            var loc = attr.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                ?? context.TargetNode.GetLocation();

            builder.Add(new TypeMapWorkItem(
                sourceType,
                targetType,
                methodName,
                loc,
                context.SemanticModel.Compilation));
        }

        return builder.ToImmutable();
    }

    private static MethodMapWorkItem? TransformMethod(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method ||
            context.TargetNode is not MethodDeclarationSyntax md)
        {
            return null;
        }

        var attr = context.Attributes.FirstOrDefault();
        INamedTypeSymbol? explicitTarget = null;
        if (attr is not null &&
            attr.ConstructorArguments.Length >= 1 &&
            attr.ConstructorArguments[0].Value is INamedTypeSymbol namedTarget)
        {
            explicitTarget = namedTarget;
        }

        var isPartialMethod = md.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) &&
            md.Body is null &&
            md.ExpressionBody is null;

        var allContainingPartial = true;
        for (var cur = md.Parent; cur is TypeDeclarationSyntax td; cur = cur.Parent)
        {
            if (!td.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                allContainingPartial = false;
                break;
            }
        }

        var loc = attr?.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? md.GetLocation();
        return new MethodMapWorkItem(
            method,
            explicitTarget,
            loc,
            isPartialMethod,
            allContainingPartial,
            context.SemanticModel.Compilation);
    }

    private static void EmitTypeMapping(SourceProductionContext spc, TypeMapWorkItem item)
    {
        if (item.TargetType is null)
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"`[MapTo]` on type `{item.SourceType.Name}` requires an explicit target type argument.",
                $"Write `[MapTo(typeof(TargetType))]` on `{item.SourceType.Name}`.");
            return;
        }

        if (item.SourceType.IsGenericType)
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"`[MapTo]` on generic source type `{item.SourceType.Name}` is not supported.",
                "Apply `[MapTo]` to a concrete non-generic type or a `static partial` projection method.");
            return;
        }

        var sourceVis = GetEffectiveTopLevelAccessibility(item.SourceType);
        var targetVis = GetEffectiveTopLevelAccessibility(item.TargetType);
        if (sourceVis == Accessibility.Private || targetVis == Accessibility.Private)
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"Cannot generate top-level extension mapping `{item.SourceType.Name} -> {item.TargetType.Name}` because one of the types is private or nested inside a private type.",
                "Make both types `public` or `internal`, or declare a `[MapTo] public static partial` method inside the enclosing type.");
            return;
        }

        var methodName = item.ExplicitMethodName ?? $"To{item.TargetType.Name}";
        if (!SyntaxFacts.IsValidIdentifier(methodName))
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"Invalid `[MapTo]` method name `{methodName}` on `{item.SourceType.Name}`.",
                "Specify a valid C# identifier for `MethodName`.");
            return;
        }

        if (!TryBuildProjectionExpression(
                item.Compilation,
                item.SourceType,
                item.TargetType,
                "source",
                out var projectionExpr,
                out var sourceRequires,
                out var errorReason,
                out var fixHint))
        {
            ReportMapToError(spc, item.DiagnosticLocation, errorReason!, fixHint!);
            return;
        }

        var classVisibility = sourceVis == Accessibility.Public ? "public" : "internal";
        var methodVisibility = sourceVis == Accessibility.Public && targetVis == Accessibility.Public
            ? "public"
            : "internal";
        var targetNs = TypeRefHelper.GetNamespace(item.SourceType);
        var mappingsClassName = BuildMappingsClassName(item.SourceType);
        var sourceTypeName = item.SourceType.ToDisplayString(NullableGlobalFormat);
        var targetTypeName = item.TargetType.ToDisplayString(NullableGlobalFormat);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");

        if (CanExportMetadata(item.Compilation, methodVisibility))
        {
            var nsPrefix = string.IsNullOrEmpty(targetNs) ? string.Empty : targetNs + ".";
            var paramDocId = FormatDocIdType(item.SourceType);
            var memberId = $"M:{nsPrefix}{mappingsClassName}.{methodName}({paramDocId})";
            sb.Append("[assembly: global::Verifiable.Contracts.VerifiableContractMetadata(")
                .Append(TypeRefHelper.EscapeStringLiteral(memberId))
                .Append(", \"Body\", ")
                .Append(TypeRefHelper.EscapeStringLiteral(projectionExpr!))
                .AppendLine(")]");
            AppendRequiresMetadata(sb, memberId, sourceRequires);
        }

        sb.AppendLine();

        var indent = string.Empty;
        if (!string.IsNullOrEmpty(targetNs))
        {
            sb.Append("namespace ").Append(targetNs).AppendLine();
            sb.AppendLine("{");
            indent = "    ";
        }

        sb.Append(indent).Append(classVisibility).Append(" static partial class ").AppendLine(mappingsClassName);
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("    [global::Verifiable.Contracts.Verifiable]");
        sb.Append(indent).Append("    ").Append(methodVisibility).Append(" static ").Append(targetTypeName)
            .Append(' ').Append(methodName).Append("(this ").Append(sourceTypeName).Append(" source)");
        AppendProjectionBody(sb, indent + "    ", projectionExpr!, sourceRequires);
        sb.Append(indent).AppendLine("}");

        if (!string.IsNullOrEmpty(targetNs))
        {
            sb.AppendLine("}");
        }

        var hintName = TypeRefHelper.SanitizeHintName(
            $"{(string.IsNullOrEmpty(targetNs) ? "Global" : targetNs)}.{mappingsClassName}.{methodName}.MapTo.g.cs");
        spc.AddSource(hintName, sb.ToString());
    }

    private static void EmitMethodMapping(SourceProductionContext spc, MethodMapWorkItem item)
    {
        var method = item.Method;
        if (!item.IsPartialMethod || !method.IsStatic || method.IsGenericMethod ||
            method.ContainingType is null || method.ContainingType.IsGenericType)
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"`[MapTo]` method `{method.Name}` must be a non-generic `static partial` method without a body inside a non-generic type.",
                $"Declare `public static partial TargetType {method.Name}(SourceType source);` inside a `partial` class or struct.");
            return;
        }

        if (!item.AllContainingTypesPartial)
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"Enclosing type `{method.ContainingType.Name}` of `[MapTo]` method `{method.Name}` must be declared `partial`.",
                $"Add the `partial` modifier to `{method.ContainingType.Name}` (and any enclosing types).");
            return;
        }

        if (method.Parameters.Length != 1 || method.Parameters[0].RefKind != RefKind.None ||
            method.Parameters[0].Type is not INamedTypeSymbol sourceType)
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"`[MapTo]` method `{method.Name}` must take exactly one non-ref class/struct parameter as the mapping source.",
                $"Change the signature to `static partial TargetType {method.Name}(SourceType source);`.");
            return;
        }

        if (method.ReturnsVoid || method.ReturnType is not INamedTypeSymbol targetType)
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"`[MapTo]` method `{method.Name}` must return a concrete class, record, or struct target type.",
                $"Return the target DTO or record type from `{method.Name}`.");
            return;
        }

        if (item.ExplicitTargetType is not null &&
            !SymbolEqualityComparer.Default.Equals(item.ExplicitTargetType, targetType))
        {
            ReportMapToError(spc, item.DiagnosticLocation,
                $"`[MapTo(typeof({item.ExplicitTargetType.Name}))]` on `{method.Name}` does not match its return type `{targetType.Name}`.",
                $"Either change the return type to `{item.ExplicitTargetType.Name}` or remove the redundant `typeof(...)` argument.");
            return;
        }

        var param = method.Parameters[0];
        var paramName = EscapeIdentifier(param.Name);
        if (!TryBuildProjectionExpression(
                item.Compilation,
                sourceType,
                targetType,
                paramName,
                out var projectionExpr,
                out var sourceRequires,
                out var errorReason,
                out var fixHint))
        {
            ReportMapToError(spc, item.DiagnosticLocation, errorReason!, fixHint!);
            return;
        }

        var ns = TypeRefHelper.GetNamespace(method.ContainingType);
        var enclosing = GetEnclosingPartialDeclarations(method.ContainingType);
        var acc = FormatAccessibility(method.DeclaredAccessibility);
        var returnTypeName = method.ReturnType.ToDisplayString(NullableGlobalFormat);
        var paramTypeName = param.Type.ToDisplayString(NullableGlobalFormat);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");

        var methodVis = GetEffectiveTopLevelAccessibility(method);
        if (CanExportMetadata(item.Compilation, methodVis == Accessibility.Public ? "public" : "internal") &&
            methodVis != Accessibility.Private)
        {
            var memberId = method.GetDocumentationCommentId();
            if (!string.IsNullOrEmpty(memberId))
            {
                sb.Append("[assembly: global::Verifiable.Contracts.VerifiableContractMetadata(")
                    .Append(TypeRefHelper.EscapeStringLiteral(memberId!))
                    .Append(", \"Body\", ")
                    .Append(TypeRefHelper.EscapeStringLiteral(projectionExpr!))
                    .AppendLine(")]");
                AppendRequiresMetadata(sb, memberId!, sourceRequires);
            }
        }

        sb.AppendLine();

        var indent = string.Empty;
        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").Append(ns).AppendLine();
            sb.AppendLine("{");
            indent = "    ";
        }

        foreach (var enc in enclosing)
        {
            sb.Append(indent).Append("partial ").AppendLine(enc);
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        sb.Append(indent).Append(acc).Append("static partial ").Append(returnTypeName)
            .Append(' ').Append(method.Name).Append('(').Append(paramTypeName).Append(' ').Append(paramName).Append(')');
        AppendProjectionBody(sb, indent, projectionExpr!, sourceRequires);

        for (var i = 0; i < enclosing.Count; i++)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent).AppendLine("}");
        }

        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine("}");
        }

        var hintName = TypeRefHelper.SanitizeHintName(
            $"{(string.IsNullOrEmpty(ns) ? "Global" : ns)}.{method.ContainingType.Name}.{method.Name}.MapTo.g.cs");
        spc.AddSource(hintName, sb.ToString());
    }

    private static bool TryBuildProjectionExpression(
        Compilation compilation,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol targetType,
        string sourceParamExpr,
        out string? projectionExpr,
        out List<string> sourceRequires,
        out string? errorReason,
        out string? fixHint)
    {
        projectionExpr = null;
        sourceRequires = new List<string>();
        errorReason = null;
        fixHint = null;

        if (targetType.IsAbstract ||
            targetType.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            targetType.SpecialType != SpecialType.None ||
            targetType.IsUnboundGenericType ||
            TypeRefHelper.TryGetConstrainedUnderlyingType(targetType, out _))
        {
            errorReason = $"Target type `{targetType.Name}` cannot be constructed by `[MapTo]`.";
            fixHint = "Target a concrete record, struct, or DTO class with a positional constructor or settable/init-only properties.";
            return false;
        }

        var targetInvariants = TypeRefHelper.GetInvariantMembers(targetType);
        if (targetInvariants.Count > 0)
        {
            var invNames = string.Join(", ", targetInvariants.Select(i => i.Name));
            errorReason = $"Target type `{targetType.Name}` declares `[Invariant]` member(s) (`{invNames}`) that a precondition-free `[MapTo]` projection cannot prove.";
            fixHint = $"Use `[InboundPort]` to validate raw inputs against `{targetType.Name}`'s invariants, or write an explicit `[Verifiable]` mapping method with `Spec.Requires(...)`.";
            return false;
        }

        var sourceMembers = CollectReadableSourceMembers(sourceType, compilation.Assembly);
        var targetWritableMembers = CollectWritableTargetMembers(targetType, compilation.Assembly);

        var accessibleCtors = targetType.InstanceConstructors
            .Where(c => IsAccessibleFromCompilation(c, compilation.Assembly) && !IsCopyConstructor(targetType, c))
            .ToList();

        var hasParameterlessCtor = accessibleCtors.Any(c => c.Parameters.Length == 0);
        var positionalCtor = SelectBestPositionalConstructor(targetType, accessibleCtors, sourceMembers);

        // Prefer positional constructor for positional records or whenever no parameterless constructor exists.
        var usePositional = positionalCtor is not null &&
            (!hasParameterlessCtor || IsRecordPositionalConstructor(targetType, positionalCtor));

        var ctorArgs = new List<string>();
        var coveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (usePositional)
        {
            foreach (var p in positionalCtor!.Parameters)
            {
                if (!TryMatchSourceMember(compilation, sourceType, sourceMembers, p.Name, p.Type, targetType, out var matched, out errorReason, out fixHint))
                {
                    return false;
                }

                ctorArgs.Add($"{sourceParamExpr}.{EscapeIdentifier(matched!.Name)}");
                NoteNonDefaultSource(sourceType, targetType, p.Name, matched.Name, sourceParamExpr, sourceRequires);
                coveredNames.Add(p.Name);
            }
        }
        else if (!hasParameterlessCtor)
        {
            // No parameterless constructor and no fully-matching positional constructor; diagnose the first accessible non-copy ctor.
            var candidate = accessibleCtors.OrderBy(c => c.Parameters.Length).FirstOrDefault();
            if (candidate is null)
            {
                errorReason = $"Target type `{targetType.Name}` has no accessible constructor for `[MapTo]`.";
                fixHint = $"Expose a public/internal constructor on `{targetType.Name}`.";
                return false;
            }

            foreach (var p in candidate.Parameters)
            {
                if (!TryMatchSourceMember(compilation, sourceType, sourceMembers, p.Name, p.Type, targetType, out _, out errorReason, out fixHint))
                {
                    return false;
                }
            }

            errorReason = $"Could not select a constructor on `{targetType.Name}` for `[MapTo]`.";
            fixHint = $"Ensure `{sourceType.Name}` exposes readable properties matching `{targetType.Name}`.";
            return false;
        }

        var initAssignments = new List<string>();
        foreach (var tm in targetWritableMembers)
        {
            if (coveredNames.Contains(tm.Name))
            {
                continue;
            }

            if (!TryMatchSourceMember(compilation, sourceType, sourceMembers, tm.Name, tm.Type, targetType, out var matched, out errorReason, out fixHint))
            {
                return false;
            }

            initAssignments.Add($"{EscapeIdentifier(tm.Name)} = {sourceParamExpr}.{EscapeIdentifier(matched!.Name)}");
            NoteNonDefaultSource(sourceType, targetType, tm.Name, matched.Name, sourceParamExpr, sourceRequires);
            coveredNames.Add(tm.Name);
        }

        if (ctorArgs.Count == 0 && initAssignments.Count == 0)
        {
            errorReason = $"Target type `{targetType.Name}` has no mappable constructor parameters or writable properties/fields for `[MapTo]`.";
            fixHint = $"Add positional parameters or `init`/`set` properties to `{targetType.Name}`.";
            return false;
        }

        var targetGlobal = targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (ctorArgs.Count > 0 && initAssignments.Count > 0)
        {
            projectionExpr = $"new {targetGlobal}({string.Join(", ", ctorArgs)}) {{ {string.Join(", ", initAssignments)} }}";
        }
        else if (ctorArgs.Count > 0)
        {
            projectionExpr = $"new {targetGlobal}({string.Join(", ", ctorArgs)})";
        }
        else
        {
            projectionExpr = $"new {targetGlobal} {{ {string.Join(", ", initAssignments)} }}";
        }

        return true;
    }

    private static bool TryMatchSourceMember(
        Compilation compilation,
        INamedTypeSymbol sourceType,
        Dictionary<string, SourceMember> sourceMembers,
        string targetMemberName,
        ITypeSymbol targetMemberType,
        INamedTypeSymbol targetType,
        out SourceMember? matched,
        out string? errorReason,
        out string? fixHint)
    {
        matched = null;
        errorReason = null;
        fixHint = null;

        if (!sourceMembers.TryGetValue(targetMemberName, out var exact))
        {
            exact = sourceMembers.Values.FirstOrDefault(m =>
                string.Equals(m.Name, targetMemberName, StringComparison.OrdinalIgnoreCase));
        }

        if (exact is null)
        {
            errorReason = $"Target member `{targetType.Name}.{targetMemberName}` has no matching readable property or field on `{sourceType.Name}`.";
            fixHint = $"Add a readable `{targetMemberName}` property to `{sourceType.Name}` or write an explicit `[Verifiable]` mapping helper.";
            return false;
        }

        var conv = compilation.ClassifyConversion(exact.Type, targetMemberType);
        if (!conv.IsImplicit)
        {
            var srcDisp = exact.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var dstDisp = targetMemberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            errorReason = $"Cannot map `{sourceType.Name}.{exact.Name}` (`{srcDisp}`) to `{targetType.Name}.{targetMemberName}` (`{dstDisp}`) without an implicit conversion.";
            fixHint = TypeRefHelper.TryGetConstrainedUnderlyingType(targetMemberType, out _)
                ? $"Use `[InboundPort]` to validate and marshal raw `{srcDisp}` inputs into constrained type `{dstDisp}`."
                : $"Align the property types or write an explicit `[Verifiable]` projection method.";
            return false;
        }

        if (exact.Type.IsReferenceType &&
            exact.Type.NullableAnnotation == NullableAnnotation.Annotated &&
            targetMemberType.IsReferenceType &&
            targetMemberType.NullableAnnotation == NullableAnnotation.NotAnnotated)
        {
            var srcDisp = exact.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var dstDisp = targetMemberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            errorReason = $"Cannot map nullable `{sourceType.Name}.{exact.Name}` (`{srcDisp}`) to non-nullable `{targetType.Name}.{targetMemberName}` (`{dstDisp}`).";
            fixHint = $"Make `{targetType.Name}.{targetMemberName}` nullable or validate/coalesce nulls in an explicit `[Verifiable]` method or `[InboundPort]`.";
            return false;
        }

        matched = exact;
        return true;
    }

    private const string NonDefaultRequiresBecause =
        "A [NonDefault] target member cannot receive default(ImmutableArray).";

    private static void AppendRequiresMetadata(StringBuilder sb, string memberId, List<string> sourceRequires)
    {
        foreach (var req in sourceRequires)
        {
            sb.Append("[assembly: global::Verifiable.Contracts.VerifiableContractMetadata(")
                .Append(TypeRefHelper.EscapeStringLiteral(memberId))
                .Append(", \"Requires\", ")
                .Append(TypeRefHelper.EscapeStringLiteral(req))
                .Append(", ")
                .Append(TypeRefHelper.EscapeStringLiteral(NonDefaultRequiresBecause))
                .AppendLine(")]");
        }
    }

    /// <summary>Writes the rest of the signature line and the body: an expression, or a block that starts with the requirements.</summary>
    private static void AppendProjectionBody(StringBuilder sb, string indent, string projectionExpr, List<string> sourceRequires)
    {
        if (sourceRequires.Count == 0)
        {
            sb.AppendLine(" =>");
            sb.Append(indent).Append("    ").Append(projectionExpr).AppendLine(";");
            return;
        }

        sb.AppendLine();
        sb.Append(indent).AppendLine("{");
        foreach (var req in sourceRequires)
        {
            sb.Append(indent).Append("    global::Verifiable.Contracts.Spec.Requires(")
                .Append(req).Append(", ")
                .Append(TypeRefHelper.EscapeStringLiteral(NonDefaultRequiresBecause))
                .AppendLine(");");
        }
        sb.Append(indent).Append("    return ").Append(projectionExpr).AppendLine(";");
        sb.Append(indent).AppendLine("}");
    }

    /// <summary>
    /// A <c>[NonDefault]</c> target array is assumed initialized by every analysed caller, and generated code is not
    /// analysed. Unless the source member is itself <c>[NonDefault]</c>, the projection therefore requires
    /// <c>!source.Member.IsDefault</c>, which the callers of the generated method must then prove.
    /// </summary>
    private static void NoteNonDefaultSource(
        INamedTypeSymbol sourceType,
        INamedTypeSymbol targetType,
        string targetMemberName,
        string sourceMemberName,
        string sourceParamExpr,
        List<string> sourceRequires)
    {
        if (IsNonDefaultImmutableArrayMember(targetType, targetMemberName) &&
            !IsNonDefaultImmutableArrayMember(sourceType, sourceMemberName))
        {
            sourceRequires.Add($"!{sourceParamExpr}.{EscapeIdentifier(sourceMemberName)}.IsDefault");
        }
    }

    private static bool IsNonDefaultImmutableArrayMember(INamedTypeSymbol type, string memberName)
    {
        foreach (var cur in BuildHierarchy(type))
        {
            foreach (var member in cur.GetMembers().Where(m =>
                         string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase)))
            {
                var memberType = member switch
                {
                    IPropertySymbol p => p.Type,
                    IFieldSymbol f => f.Type,
                    _ => null
                };
                if (memberType is INamedTypeSymbol { IsGenericType: true } named &&
                    named.OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableArray<T>" &&
                    HasNonDefaultAttribute(member))
                {
                    return true;
                }
            }

            // Positional record parameters carry the attribute on the primary constructor parameter.
            foreach (var ctor in cur.InstanceConstructors)
            {
                foreach (var p in ctor.Parameters)
                {
                    if (string.Equals(p.Name, memberName, StringComparison.OrdinalIgnoreCase) &&
                        HasNonDefaultAttribute(p) &&
                        p.Type is INamedTypeSymbol { IsGenericType: true } pt &&
                        pt.OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableArray<T>")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasNonDefaultAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == TypeRefHelper.NonDefaultAttributeMetadataName);

    private static Dictionary<string, SourceMember> CollectReadableSourceMembers(
        INamedTypeSymbol sourceType,
        IAssemblySymbol currentAssembly)
    {
        var result = new Dictionary<string, SourceMember>(StringComparer.Ordinal);
        var hierarchy = BuildHierarchy(sourceType);
        foreach (var cur in hierarchy)
        {
            foreach (var prop in cur.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsIndexer || prop.Name == "EqualityContract" ||
                    prop.GetMethod is null ||
                    !IsAccessibleFromCompilation(prop.GetMethod, currentAssembly) ||
                    TypeRefHelper.HasGhostAttribute(prop) ||
                    TypeRefHelper.HasInvariantAttribute(prop))
                {
                    continue;
                }

                result[prop.Name] = new SourceMember(prop.Name, prop.Type);
            }

            foreach (var field in cur.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsImplicitlyDeclared || field.AssociatedSymbol is not null ||
                    !IsAccessibleFromCompilation(field, currentAssembly) ||
                    TypeRefHelper.HasGhostAttribute(field))
                {
                    continue;
                }

                result[field.Name] = new SourceMember(field.Name, field.Type);
            }
        }

        return result;
    }

    private static List<TargetMember> CollectWritableTargetMembers(
        INamedTypeSymbol targetType,
        IAssemblySymbol currentAssembly)
    {
        var result = new List<TargetMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hierarchy = BuildHierarchy(targetType);
        foreach (var cur in hierarchy)
        {
            foreach (var prop in cur.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsIndexer || prop.Name == "EqualityContract" ||
                    prop.SetMethod is null ||
                    !IsAccessibleFromCompilation(prop.SetMethod, currentAssembly) ||
                    TypeRefHelper.HasGhostAttribute(prop) ||
                    TypeRefHelper.HasInvariantAttribute(prop))
                {
                    continue;
                }

                if (seen.Add(prop.Name))
                {
                    result.Add(new TargetMember(prop.Name, prop.Type, prop.IsRequired));
                }
            }

            foreach (var field in cur.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsReadOnly || field.IsConst ||
                    field.IsImplicitlyDeclared || field.AssociatedSymbol is not null ||
                    !IsAccessibleFromCompilation(field, currentAssembly) ||
                    TypeRefHelper.HasGhostAttribute(field))
                {
                    continue;
                }

                if (seen.Add(field.Name))
                {
                    result.Add(new TargetMember(field.Name, field.Type, field.IsRequired));
                }
            }
        }

        return result;
    }

    private static IMethodSymbol? SelectBestPositionalConstructor(
        INamedTypeSymbol targetType,
        List<IMethodSymbol> accessibleCtors,
        Dictionary<string, SourceMember> sourceMembers)
    {
        var nonEmpty = accessibleCtors.Where(c => c.Parameters.Length > 0).ToList();
        if (nonEmpty.Count == 0)
        {
            return null;
        }

        var recordPrimary = nonEmpty.FirstOrDefault(c => IsRecordPositionalConstructor(targetType, c));
        if (recordPrimary is not null)
        {
            return recordPrimary;
        }

        return nonEmpty
            .OrderByDescending(c => c.Parameters.Count(p =>
                sourceMembers.ContainsKey(p.Name) ||
                sourceMembers.Keys.Any(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase))))
            .ThenByDescending(c => c.Parameters.Length)
            .FirstOrDefault();
    }

    private static bool IsRecordPositionalConstructor(INamedTypeSymbol targetType, IMethodSymbol ctor)
    {
        if (ctor.Parameters.Length == 0 || IsCopyConstructor(targetType, ctor))
        {
            return false;
        }

        var isRecord = targetType.IsRecord ||
            targetType.GetMembers("PrintMembers").OfType<IMethodSymbol>().Any() ||
            (targetType.IsValueType &&
             targetType.GetMembers("Deconstruct").OfType<IMethodSymbol>().Any(m => m.Parameters.Length == ctor.Parameters.Length));

        if (!isRecord)
        {
            return false;
        }

        return ctor.Parameters.All(p =>
            targetType.GetMembers(p.Name).OfType<IPropertySymbol>().Any(prop =>
                !prop.IsStatic && SymbolEqualityComparer.Default.Equals(prop.Type, p.Type)));
    }

    private static bool IsCopyConstructor(INamedTypeSymbol type, IMethodSymbol ctor) =>
        ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type);

    private static List<INamedTypeSymbol> BuildHierarchy(INamedTypeSymbol type)
    {
        var list = new List<INamedTypeSymbol>();
        for (var cur = type;
             cur is not null && cur.SpecialType != SpecialType.System_Object && cur.SpecialType != SpecialType.System_ValueType;
             cur = cur.BaseType)
        {
            list.Add(cur);
        }

        list.Reverse();
        return list;
    }

    private static bool IsAccessibleFromCompilation(ISymbol symbol, IAssemblySymbol currentAssembly)
    {
        if (symbol.DeclaredAccessibility == Accessibility.Public)
        {
            return true;
        }

        if (symbol.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
            SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, currentAssembly))
        {
            return true;
        }

        return false;
    }

    private static Accessibility GetEffectiveTopLevelAccessibility(ISymbol symbol)
    {
        var effective = Accessibility.Public;
        for (var cur = symbol; cur is not null && cur is not INamespaceSymbol and not IModuleSymbol; cur = cur.ContainingSymbol)
        {
            if (cur.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            {
                return Accessibility.Private;
            }

            if (cur.DeclaredAccessibility == Accessibility.Internal)
            {
                effective = Accessibility.Internal;
            }
        }

        return effective;
    }

    private static string BuildMappingsClassName(INamedTypeSymbol sourceType)
    {
        var parts = new List<string>();
        for (var cur = sourceType; cur is not null; cur = cur.ContainingType)
        {
            parts.Add(cur.Name);
        }

        parts.Reverse();
        return string.Join("_", parts) + "Mappings";
    }

    private static List<string> GetEnclosingPartialDeclarations(INamedTypeSymbol symbol)
    {
        var list = new List<string>();
        for (var cur = symbol; cur is not null; cur = cur.ContainingType)
        {
            var kind = cur.IsRecord
                ? (cur.IsValueType ? "record struct" : "record class")
                : (cur.IsValueType ? "struct" : "class");
            list.Add($"{kind} {cur.Name}");
        }

        list.Reverse();
        return list;
    }

    private static string FormatAccessibility(Accessibility acc) => acc switch
    {
        Accessibility.Public => "public ",
        Accessibility.Internal => "internal ",
        Accessibility.Private => "private ",
        Accessibility.Protected => "protected ",
        Accessibility.ProtectedOrInternal => "protected internal ",
        Accessibility.ProtectedAndInternal => "private protected ",
        _ => string.Empty
    };

    private static bool CanExportMetadata(Compilation compilation, string visibility)
    {
        if (compilation.GetTypeByMetadataName(TypeRefHelper.ContractMetadataAttributeMetadataName) is null)
        {
            return false;
        }

        if (visibility == "public")
        {
            return true;
        }

        return compilation.Assembly.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InternalsVisibleToAttribute");
    }

    private static string FormatDocIdType(ITypeSymbol type)
    {
        var id = type.GetDocumentationCommentId();
        if (!string.IsNullOrEmpty(id) && id!.Length > 2 && id[1] == ':')
        {
            return id.Substring(2);
        }

        return type.ToDisplayString();
    }

    private static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static void ReportMapToError(
        SourceProductionContext spc,
        Location location,
        string intro,
        string fixHint)
    {
        var msg = ElmGeneratorFormatter.Format(
            "INVALID MAPTO PROJECTION",
            location,
            intro,
            "Why this rule exists: `[MapTo]` generates a complete, type-safe 1:1 field projection that Z3 verifies directly without hidden defaults or lossy casts.",
            fixHint);
        spc.ReportDiagnostic(Diagnostic.Create(
            GeneratorDescriptors.InvalidMapToDeclaration,
            location,
            msg));
    }
}
