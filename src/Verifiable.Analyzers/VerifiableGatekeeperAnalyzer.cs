using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers;

/// <summary>
/// Enforces deterministic, aliasing-free verifiable cage rules (VER001-VER006) on [Verifiable] (errors)
/// and [VerifiableCandidate] (warnings) symbols.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VerifiableGatekeeperAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [
            VerifiableDescriptors.HeapAliasingOrMutableState,
            VerifiableDescriptors.MutableCollection,
            VerifiableDescriptors.UncheckedInitialization,
            VerifiableDescriptors.NondeterminismOrAmbientAuthority,
            VerifiableDescriptors.DisallowedControlFlow,
            VerifiableDescriptors.UnverifiedCallOrDynamic
        ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
        context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterOperationBlockAction(AnalyzeOperationBlock);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type) return;
        if (type.IsImplicitlyDeclared || VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(type)) return;

        var scope = VerifiableSymbolHelpers.GetScope(type);
        if (scope == VerificationScope.None) return;

        var isCandidate = scope == VerificationScope.Candidate;
        var loc = type.Locations.FirstOrDefault() ?? Location.None;

        if (type.TypeKind == TypeKind.Class && !type.IsStatic && !type.IsRecord)
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.HeapAliasingOrMutableState,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER001_MutableType"),
                $"mutable class '{type.Name}'"));
        }
        else if (type.TypeKind == TypeKind.Struct && !type.IsReadOnly)
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.HeapAliasingOrMutableState,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER001_MutableStruct"),
                $"non-readonly struct '{type.Name}'"));
        }
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        if (context.Symbol is not IPropertySymbol prop) return;
        if (prop.IsImplicitlyDeclared ||
            VerifiableSymbolHelpers.IsSynthesizedRecordMember(prop) ||
            VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(prop) ||
            VerifiableSymbolHelpers.HasAttribute(prop, VerifiableSymbolHelpers.GhostAttr))
        {
            return;
        }

        var scope = VerifiableSymbolHelpers.GetScope(prop);
        if (scope == VerificationScope.None) return;

        var isCandidate = scope == VerificationScope.Candidate;
        var loc = prop.Locations.FirstOrDefault() ?? Location.None;

        if (prop.SetMethod is { IsInitOnly: false })
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.HeapAliasingOrMutableState,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER001_SettableProperty"),
                $"settable property '{prop.ContainingType?.Name}.{prop.Name}'"));
        }

        if (VerifiableSymbolHelpers.IsMutableCollectionType(prop.Type, out var colDisplay))
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.MutableCollection,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER002_MutableCollection"),
                colDisplay));
        }
    }

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        if (context.Symbol is not IFieldSymbol field) return;
        if (field.IsImplicitlyDeclared ||
            field.AssociatedSymbol is not null ||
            VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(field) ||
            VerifiableSymbolHelpers.HasAttribute(field, VerifiableSymbolHelpers.GhostAttr))
        {
            return;
        }

        var scope = VerifiableSymbolHelpers.GetScope(field);
        if (scope == VerificationScope.None) return;

        var isCandidate = scope == VerificationScope.Candidate;
        var loc = field.Locations.FirstOrDefault() ?? Location.None;

        if (field.IsStatic && !field.IsReadOnly && !field.IsConst)
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.NondeterminismOrAmbientAuthority,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER004_StaticField"),
                $"mutable static field '{field.ContainingType?.Name}.{field.Name}'"));
        }
        else if (!field.IsReadOnly && !field.IsConst)
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.HeapAliasingOrMutableState,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER001_MutableField"),
                $"mutable field '{field.ContainingType?.Name}.{field.Name}'"));
        }

        if (VerifiableSymbolHelpers.IsMutableCollectionType(field.Type, out var colDisplay))
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.MutableCollection,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER002_MutableCollection"),
                colDisplay));
        }
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        if (context.Symbol is not IMethodSymbol method) return;
        if (VerifiableSymbolHelpers.IsSynthesizedRecordMember(method) ||
            VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(method) ||
            VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.PureAxiomAttr))
        {
            return;
        }

        var scope = VerifiableSymbolHelpers.GetScope(method);
        if (scope == VerificationScope.None) return;

        var isCandidate = scope == VerificationScope.Candidate;
        var loc = method.Locations.FirstOrDefault() ?? Location.None;

        if (method.IsAsync || IsTaskLike(method.ReturnType))
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.DisallowedControlFlow,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER005_AsyncTask"),
                method.IsAsync ? "async method" : $"Task-returning method '{method.Name}'"));
        }

        if (VerifiableSymbolHelpers.IsMutableCollectionType(method.ReturnType, out var retCol))
        {
            context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                VerifiableDescriptors.MutableCollection,
                loc,
                isCandidate,
                ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER002_MutableCollection"),
                retCol));
        }

        foreach (var param in method.Parameters)
        {
            var paramLoc = param.Locations.FirstOrDefault() ?? loc;
            if (param.RefKind is RefKind.Ref or RefKind.Out)
            {
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.HeapAliasingOrMutableState,
                    paramLoc,
                    isCandidate,
                    ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER001_RefOutParam"),
                    $"{param.RefKind.ToString().ToLowerInvariant()} parameter '{param.Name}'"));
            }

            if (VerifiableSymbolHelpers.IsMutableCollectionType(param.Type, out var paramCol))
            {
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.MutableCollection,
                    paramLoc,
                    isCandidate,
                    ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER002_MutableCollection"),
                    paramCol));
            }
        }
    }

    private static void AnalyzeOperationBlock(OperationBlockAnalysisContext context)
    {
        var owning = context.OwningSymbol;
        if (VerifiableSymbolHelpers.IsSynthesizedRecordMember(owning) ||
            VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(owning) ||
            VerifiableSymbolHelpers.HasAttribute(owning, VerifiableSymbolHelpers.PureAxiomAttr) ||
            (owning is not IMethodSymbol && VerifiableSymbolHelpers.HasAttribute(owning, VerifiableSymbolHelpers.GhostAttr)))
        {
            return;
        }

        var scope = VerifiableSymbolHelpers.GetScope(owning);
        if (scope == VerificationScope.None) return;

        var isCandidate = scope == VerificationScope.Candidate;

        foreach (var block in context.OperationBlocks)
        {
            foreach (var op in block.DescendantsAndSelf())
            {
                AnalyzeOperation(context, op, isCandidate);
            }
        }
    }

    private static void AnalyzeOperation(
        OperationBlockAnalysisContext context,
        IOperation op,
        bool isCandidate)
    {
        switch (op)
        {
            case IVariableDeclaratorOperation varDecl:
                if (varDecl.Symbol.RefKind == RefKind.Ref)
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.HeapAliasingOrMutableState,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        $"ref local '{varDecl.Symbol.Name}'"));
                }

                if (!op.IsImplicit && VerifiableSymbolHelpers.IsMutableCollectionType(varDecl.Symbol.Type, out var localCol))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.MutableCollection,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER002_MutableCollection"),
                        localCol));
                }
                break;

            case IAssignmentOperation assign when !assign.IsImplicit:
                if (assign.Target is IMemberReferenceOperation memberTarget && !IsInsideObjectOrWithInitializer(assign))
                {
                    if (IsConstructorImmutableMemberInit(context.OwningSymbol, memberTarget))
                    {
                        break;
                    }

                    if (memberTarget.Member is IFieldSymbol fs && fs.IsStatic && !fs.IsReadOnly && !fs.IsConst)
                    {
                        context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                            VerifiableDescriptors.NondeterminismOrAmbientAuthority,
                            op.Syntax.GetLocation(),
                            isCandidate,
                            null,
                            $"assignment to static field '{fs.Name}'"));
                    }
                    else
                    {
                        context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                            VerifiableDescriptors.HeapAliasingOrMutableState,
                            op.Syntax.GetLocation(),
                            isCandidate,
                            ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER001_MemberAssignment"),
                            $"mutation of '{memberTarget.Member.Name}'"));
                    }
                }
                break;


            case IArrayCreationOperation arrCreate:
                if (!(arrCreate.IsImplicit && arrCreate.Parent is IArgumentOperation { Parameter.IsParams: true }))
                {
                    var arrDisplay = arrCreate.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "T[]";
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.MutableCollection,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER002_MutableCollection"),
                        arrDisplay));
                }
                break;

            case IObjectCreationOperation objCreate when objCreate.Type is not null:
                if (IsNondeterministicType(objCreate.Type))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.NondeterminismOrAmbientAuthority,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        $"new {objCreate.Type.Name}()"));
                }
                else if (VerifiableSymbolHelpers.IsMutableCollectionType(objCreate.Type, out var createdCol))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.MutableCollection,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER002_MutableCollection"),
                        createdCol));
                }
                else if (objCreate.Constructor is { Parameters.Length: 0 } &&
                         VerifiableSymbolHelpers.HasInvariantOrRefinedContract(objCreate.Type))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.UncheckedInitialization,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        objCreate.Type.Name));
                }
                break;

            case IDefaultValueOperation defOp when !defOp.IsImplicit && defOp.Type is not null:
                if (VerifiableSymbolHelpers.HasInvariantOrRefinedContract(defOp.Type))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.UncheckedInitialization,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        defOp.Type.Name));
                }
                break;

            case IMemberReferenceOperation memRef:
                if (VerifiableSymbolHelpers.IsNondeterministicMember(memRef.Member, out var memDisplay))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.NondeterminismOrAmbientAuthority,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER004_AmbientAuthority"),
                        memDisplay));
                }
                else if (IsReflectionSymbol(memRef.Member))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.UnverifiedCallOrDynamic,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        $"System.Reflection member '{memRef.Member.Name}'"));
                }
                break;

            case IInvocationOperation inv:
                if (VerifiableSymbolHelpers.IsNondeterministicMember(inv.TargetMethod, out var invDisplay))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.NondeterminismOrAmbientAuthority,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER004_AmbientAuthority"),
                        invDisplay));
                }
                else if (inv.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
                {
                    // Allow pure LINQ predicate lambdas inside Enumerable calls; disallow standalone unanalyzed delegate calls
                    if (!IsDirectLinqCallbackInvocation(inv))
                    {
                        context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                            VerifiableDescriptors.UnverifiedCallOrDynamic,
                            op.Syntax.GetLocation(),
                            isCandidate,
                            null,
                            "delegate invocation"));
                    }
                }
                else if (inv.TargetMethod.MethodKind == MethodKind.LocalFunction)
                {
                    // Local functions inside the same block are analyzed by this OperationBlockAction.
                }
                else if (IsReflectionSymbol(inv.TargetMethod))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.UnverifiedCallOrDynamic,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        $"System.Reflection call '{inv.TargetMethod.Name}'"));
                }
                else if (VerifiableSymbolHelpers.HasAttribute(inv.TargetMethod, VerifiableSymbolHelpers.GhostAttr)
                    ? !IsSpecificationUse(inv, context.OwningSymbol)
                    : !IsTrustedInvocation(inv))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.UnverifiedCallOrDynamic,
                        op.Syntax.GetLocation(),
                        isCandidate,
                        ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER006_UnverifiedCall"),
                        $"{inv.TargetMethod.ContainingType?.Name}.{inv.TargetMethod.Name}(...)"));
                }
                break;

            case IDynamicInvocationOperation:
            case IDynamicMemberReferenceOperation:
            case IDynamicIndexerAccessOperation:
            case IDynamicObjectCreationOperation:
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.UnverifiedCallOrDynamic,
                    op.Syntax.GetLocation(),
                    isCandidate,
                    null,
                    "dynamic dispatch"));
                break;

            case IAwaitOperation:
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.DisallowedControlFlow,
                    op.Syntax.GetLocation(),
                    isCandidate,
                    ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER005_Await"),
                    "await expression"));
                break;

            case IThrowOperation when !op.IsImplicit:
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.DisallowedControlFlow,
                    op.Syntax.GetLocation(),
                    isCandidate,
                    ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER005_Throw"),
                    "throw statement/expression"));
                break;

            case ITryOperation:
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.DisallowedControlFlow,
                    op.Syntax.GetLocation(),
                    isCandidate,
                    null,
                    "try/catch block"));
                break;

            case ILockOperation:
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.DisallowedControlFlow,
                    op.Syntax.GetLocation(),
                    isCandidate,
                    null,
                    "lock statement"));
                break;

            case IBranchOperation branch when branch.BranchKind == BranchKind.GoTo && branch.Syntax is GotoStatementSyntax:
                context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                    VerifiableDescriptors.DisallowedControlFlow,
                    op.Syntax.GetLocation(),
                    isCandidate,
                    null,
                    "goto statement"));
                break;

            case IWhileLoopOperation whileOp:
                if (!LoopHasSpecAnnotation(whileOp.Body) && whileOp.Syntax is WhileStatementSyntax or DoStatementSyntax)
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.DisallowedControlFlow,
                        whileOp.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        whileOp.Syntax is DoStatementSyntax ? "unannotated do-while loop" : "unannotated while loop"));
                }
                break;

            case IForEachLoopOperation foreachOp:
                if (!LoopHasSpecAnnotation(foreachOp.Body))
                {
                    context.ReportDiagnostic(VerifiableDescriptors.CreateScoped(
                        VerifiableDescriptors.DisallowedControlFlow,
                        foreachOp.Syntax.GetLocation(),
                        isCandidate,
                        null,
                        "unannotated foreach loop"));
                }
                break;
        }
    }
    private static bool IsConstructorImmutableMemberInit(ISymbol owningSymbol, IMemberReferenceOperation memberTarget)
    {
        if (owningSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor })
        {
            return false;
        }

        if (memberTarget.Instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance })
        {
            return false;
        }

        return memberTarget.Member switch
        {
            IFieldSymbol fs => fs.IsReadOnly,
            IPropertySymbol ps => ps.SetMethod is null || ps.SetMethod.IsInitOnly,
            _ => false
        };
    }

    private static bool IsSpecificationUse(IOperation op, ISymbol owningSymbol)
    {
        if (VerifiableSymbolHelpers.HasAttribute(owningSymbol, VerifiableSymbolHelpers.GhostAttr) ||
            VerifiableSymbolHelpers.HasAttribute(owningSymbol, VerifiableSymbolHelpers.InvariantAttr))
            return true;
        for (var parent = op.Parent; parent is not null; parent = parent.Parent)
            if (parent is IInvocationOperation inv && inv.TargetMethod.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
                return true;
        return false;
    }

    private static bool LoopHasSpecAnnotation(IOperation body)
    {
        var stack = new Stack<IOperation>();
        stack.Push(body);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is IInvocationOperation inv &&
                (VerifiableSymbolHelpers.IsSpecCall(inv.TargetMethod, "LoopInvariant") ||
                 VerifiableSymbolHelpers.IsSpecCall(inv.TargetMethod, "Decreases")))
            {
                return true;
            }

            foreach (var child in cur.ChildOperations)
            {
                if (child is not ILoopOperation)
                {
                    stack.Push(child);
                }
            }
        }
        return false;
    }

    private static bool IsInsideObjectOrWithInitializer(IOperation assign)
    {
        for (var cur = assign.Parent; cur is not null; cur = cur.Parent)
        {
            if (cur is IObjectOrCollectionInitializerOperation or IWithOperation)
            {
                return true;
            }
            if (cur is IBlockOperation)
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsTaskLike(ITypeSymbol type)
    {
        var full = type.OriginalDefinition.ToDisplayString();
        return full is
            "System.Threading.Tasks.Task" or
            "System.Threading.Tasks.Task<TResult>" or
            "System.Threading.Tasks.ValueTask" or
            "System.Threading.Tasks.ValueTask<TResult>";
    }

    private static bool IsNondeterministicType(ITypeSymbol type)
    {
        var full = type.OriginalDefinition.ToDisplayString();
        return full is
            "System.Random" or
            "System.Diagnostics.Stopwatch" or
            "System.Net.Http.HttpClient" ||
            full.StartsWith("System.IO.", StringComparison.Ordinal);
    }

    private static bool IsReflectionSymbol(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns == "System.Reflection" || ns.StartsWith("System.Reflection.", StringComparison.Ordinal);
    }

    private static bool IsDirectLinqCallbackInvocation(IInvocationOperation inv) => false;

    private static bool IsTrustedInvocation(IInvocationOperation inv)
    {
        var method = inv.TargetMethod;
        if (method.ContainingType?.SpecialType == SpecialType.System_String)
        {
            return StringIntrinsics.IsTrustedStringInvocation(inv);
        }

        if (method.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Text.RegularExpressions.Regex")
        {
            return IsSupportedRegexIsMatch(inv);
        }

        return VerifiableSymbolHelpers.IsTrustedPureMethod(method);
    }

    private static bool IsSupportedRegexIsMatch(IInvocationOperation inv)
    {
        var method = inv.TargetMethod;
        if (method.Name != "IsMatch")
        {
            return false;
        }

        if (method.IsStatic)
        {
            if (method.Parameters.Length is < 2 or > 3)
            {
                return false;
            }
            if (method.Parameters[0].Type.SpecialType != SpecialType.System_String ||
                method.Parameters[1].Type.SpecialType != SpecialType.System_String)
            {
                return false;
            }
            if (method.Parameters.Length == 3 &&
                method.Parameters[2].Type.ToDisplayString() != "System.Text.RegularExpressions.RegexOptions")
            {
                return false;
            }
            return true;
        }

        if (method.Parameters.Length == 1 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
            inv.Instance is not null)
        {
            if (inv.Instance is IInvocationOperation factoryInv &&
                VerifiableSymbolHelpers.TryGetGeneratedRegexAttribute(factoryInv.TargetMethod, out _, out _))
            {
                return true;
            }

            if (inv.Instance is IPropertyReferenceOperation propRef &&
                VerifiableSymbolHelpers.TryGetGeneratedRegexAttribute(propRef.Property, out _, out _))
            {
                return true;
            }

            if (inv.Instance is IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } })
            {
                return true;
            }
        }

        return false;
    }
}
