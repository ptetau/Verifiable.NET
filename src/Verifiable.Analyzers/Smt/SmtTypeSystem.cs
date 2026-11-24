using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Analyzers.Smt;

internal sealed record SmtFieldInfo(
    string Name,
    ITypeSymbol Type,
    string AccessorName);

internal sealed record SmtCompositeInfo(
    string SortName,
    string ConstructorName,
    IReadOnlyList<SmtFieldInfo> Fields,
    ITypeSymbol Symbol);

internal sealed class SmtTypeSystem
{
    public bool UseBitVec { get; }

    private readonly Dictionary<ITypeSymbol, string> _sortCache = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, SmtCompositeInfo> _compositesBySort = new(StringComparer.Ordinal);
    private readonly Dictionary<ITypeSymbol, (string SortName, ITypeSymbol KeyType, ITypeSymbol ValType)> _dictTypes = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, (string SortName, ITypeSymbol ElemType)> _seqTypes = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, (string SortName, ITypeSymbol ElemType)> _setTypes = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, (string SortName, ITypeSymbol InnerType)> _optTypes = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ITypeSymbol> _inProgress = new(SymbolEqualityComparer.Default);
    private readonly List<string> _declarations = new();
    private int _typeCounter;

    public SmtTypeSystem(bool useBitVec)
    {
        UseBitVec = useBitVec;
        _declarations.Add("(declare-datatype VerStr ((str-null) (str-val (str-text String))))");
    }

    public IReadOnlyList<string> Declarations => _declarations;

    public string GetSort(ITypeSymbol type)
    {
        if (_sortCache.TryGetValue(type, out var existing))
        {
            return existing;
        }

        if (type.SpecialType == SpecialType.System_Boolean)
            return Cache(type, "Bool");

        if (type.SpecialType == SpecialType.System_Decimal)
            return Cache(type, "Real");

        if (type.SpecialType == SpecialType.System_String)
            return Cache(type, "VerStr");

        if (type.SpecialType == SpecialType.System_Int32)
            return Cache(type, UseBitVec ? "(_ BitVec 32)" : "Int");

        if (type.SpecialType == SpecialType.System_Int64)
            return Cache(type, UseBitVec ? "(_ BitVec 64)" : "Int");

        if (type.SpecialType is SpecialType.System_UInt32 or SpecialType.System_UInt64 or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Char)
            return Cache(type, "Int");

        if (type.TypeKind == TypeKind.Enum)
            return Cache(type, "Int");

        if (type.ToDisplayString() == "System.Guid")
            return Cache(type, "Int");

        if (type is INamedTypeSymbol nullableNamed &&
            nullableNamed.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nullableNamed.TypeArguments.Length == 1)
        {
            var inner = nullableNamed.TypeArguments[0];
            var innerSort = GetSort(inner);
            var id = ++_typeCounter;
            var optSort = $"Opt_{id}";
            Cache(type, optSort);
            _optTypes[type] = (optSort, inner);
            _declarations.Add($"(declare-datatype {optSort} ((opt-none-{id}) (opt-some-{id} (opt-val-{id} {innerSort}))))");
            return optSort;
        }

        if (type is INamedTypeSymbol colNamed && colNamed.IsGenericType)
        {
            var orig = colNamed.OriginalDefinition.ToDisplayString();
            if (orig == "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>")
            {
                var k = colNamed.TypeArguments[0];
                var v = colNamed.TypeArguments[1];
                var kSort = GetSort(k);
                var vSort = GetSort(v);
                var id = ++_typeCounter;
                var mapSort = $"Map_{id}";
                Cache(type, mapSort);
                _dictTypes[type] = (mapSort, k, v);
                _declarations.Add($"(declare-datatype {mapSort} ((mk-{mapSort} (map_data_{id} (Array {kSort} {vSort})) (map_has_{id} (Array {kSort} Bool)) (map_count_{id} Int))))");
                return mapSort;
            }

            if (orig is "System.Collections.Immutable.ImmutableList<T>" or "System.Collections.Immutable.ImmutableArray<T>")
            {
                var elem = colNamed.TypeArguments[0];
                var eSort = GetSort(elem);
                var id = ++_typeCounter;
                var seqSort = $"Seq_{id}";
                Cache(type, seqSort);
                _seqTypes[type] = (seqSort, elem);
                _declarations.Add($"(declare-datatype {seqSort} ((mk-{seqSort} (seq_elems_{id} (Array Int {eSort})) (seq_count_{id} Int))))");
                return seqSort;
            }

            if (orig == "System.Collections.Immutable.ImmutableHashSet<T>")
            {
                var elem = colNamed.TypeArguments[0];
                var eSort = GetSort(elem);
                var id = ++_typeCounter;
                var setSort = $"Set_{id}";
                Cache(type, setSort);
                _setTypes[type] = (setSort, elem);
                _declarations.Add($"(declare-datatype {setSort} ((mk-{setSort} (set_has_{id} (Array {eSort} Bool)) (set_count_{id} Int))))");
                return setSort;
            }
        }

        if (type is INamedTypeSymbol composite)
        {
            if (_inProgress.Contains(composite))
            {
                return Cache(type, "Int");
            }

            _inProgress.Add(composite);
            try
            {
                var id = ++_typeCounter;
                var safeName = SanitizeName(composite.Name);
                var dtSort = $"Dt_{safeName}_{id}";
                var ctorName = $"mk-{dtSort}";

                var fields = ExtractStoredFields(composite, dtSort);
                var fieldDecls = new StringBuilder();
                foreach (var f in fields)
                {
                    var fSort = GetSort(f.Type);
                    fieldDecls.Append($" ({f.AccessorName} {fSort})");
                }

                if (fields.Count == 0)
                {
                    _declarations.Add($"(declare-datatype {dtSort} (({ctorName})))");
                }
                else
                {
                    _declarations.Add($"(declare-datatype {dtSort} (({ctorName}{fieldDecls})))");
                }

                var info = new SmtCompositeInfo(dtSort, ctorName, fields, composite);
                _compositesBySort[dtSort] = info;
                return Cache(type, dtSort);
            }
            finally
            {
                _inProgress.Remove(composite);
            }
        }

        return Cache(type, "Int");
    }

    public bool TryGetComposite(ITypeSymbol type, out SmtCompositeInfo? info)
    {
        var sort = GetSort(type);
        return _compositesBySort.TryGetValue(sort, out info);
    }

    public bool TryGetCompositeBySort(string sortName, out SmtCompositeInfo? info) =>
        _compositesBySort.TryGetValue(sortName, out info);

    public bool TryGetDictionaryInfo(ITypeSymbol type, out string sortName, out int id, out ITypeSymbol? keyType, out ITypeSymbol? valType)
    {
        var sort = GetSort(type);
        if (_dictTypes.TryGetValue(type, out var entry))
        {
            sortName = entry.SortName;
            id = int.Parse(sortName.Substring("Map_".Length));
            keyType = entry.KeyType;
            valType = entry.ValType;
            return true;
        }

        sortName = sort;
        id = 0;
        keyType = null;
        valType = null;
        return false;
    }

    public bool TryGetSequenceInfo(ITypeSymbol type, out string sortName, out int id, out ITypeSymbol? elemType)
    {
        var sort = GetSort(type);
        if (_seqTypes.TryGetValue(type, out var entry))
        {
            sortName = entry.SortName;
            id = int.Parse(sortName.Substring("Seq_".Length));
            elemType = entry.ElemType;
            return true;
        }

        sortName = sort;
        id = 0;
        elemType = null;
        return false;
    }

    public bool TryGetSetInfo(ITypeSymbol type, out string sortName, out int id, out ITypeSymbol? elemType)
    {
        var sort = GetSort(type);
        if (_setTypes.TryGetValue(type, out var entry))
        {
            sortName = entry.SortName;
            id = int.Parse(sortName.Substring("Set_".Length));
            elemType = entry.ElemType;
            return true;
        }

        sortName = sort;
        id = 0;
        elemType = null;
        return false;
    }

    public bool TryGetNullableInfo(ITypeSymbol type, out string sortName, out int id, out ITypeSymbol? innerType)
    {
        var sort = GetSort(type);
        if (_optTypes.TryGetValue(type, out var entry))
        {
            sortName = entry.SortName;
            id = int.Parse(sortName.Substring("Opt_".Length));
            innerType = entry.InnerType;
            return true;
        }

        sortName = sort;
        id = 0;
        innerType = null;
        return false;
    }

    public string GetDefaultTerm(ITypeSymbol type)
    {
        var sort = GetSort(type);
        switch (sort)
        {
            case "Bool": return "false";
            case "Int": return "0";
            case "Real": return "0.0";
            case "(_ BitVec 32)": return "(_ bv0 32)";
            case "(_ BitVec 64)": return "(_ bv0 64)";
            case "VerStr": return "str-null";
        }

        if (TryGetNullableInfo(type, out _, out var optId, out _))
        {
            return $"opt-none-{optId}";
        }

        if (TryGetDictionaryInfo(type, out var mapSort, out _, out var kType, out var vType))
        {
            var kSort = GetSort(kType!);
            var vSort = GetSort(vType!);
            var defV = GetDefaultTerm(vType!);
            return $"(mk-{mapSort} ((as const (Array {kSort} {vSort})) {defV}) ((as const (Array {kSort} Bool)) false) 0)";
        }

        if (TryGetSequenceInfo(type, out var seqSort, out _, out var elemType))
        {
            var eSort = GetSort(elemType!);
            var defE = GetDefaultTerm(elemType!);
            return $"(mk-{seqSort} ((as const (Array Int {eSort})) {defE}) 0)";
        }

        if (TryGetSetInfo(type, out var setSort, out _, out var setElem))
        {
            var eSort = GetSort(setElem!);
            return $"(mk-{setSort} ((as const (Array {eSort} Bool)) false) 0)";
        }

        if (_compositesBySort.TryGetValue(sort, out var comp))
        {
            if (comp.Fields.Count == 0)
            {
                return comp.ConstructorName;
            }

            var args = string.Join(" ", comp.Fields.Select(f => GetDefaultTerm(f.Type)));
            return $"({comp.ConstructorName} {args})";
        }

        return "0";
    }

    public static bool TryGetConstrainedUnderlying(ITypeSymbol type, out ITypeSymbol? underlying) =>
        TryGetConstrainedUnderlying(type, out underlying, out _);

    public static bool TryGetConstrainedUnderlying(ITypeSymbol type, out ITypeSymbol? underlying, out string? description)
    {
        if (type is INamedTypeSymbol named)
        {
            foreach (var attr in named.GetAttributes())
            {
                if (attr.AttributeClass is { IsGenericType: true, TypeArguments.Length: 1 } ac)
                {
                    var defName = ac.ConstructedFrom.ToDisplayString();
                    if (defName is "Verifiable.Contracts.TypeAliasAttribute<TUnderlying>" or "Verifiable.Contracts.RefinedAttribute<TUnderlying>")
                    {
                        underlying = ac.TypeArguments[0];
                        description = attr.ConstructorArguments.Length >= 1
                            ? attr.ConstructorArguments[0].Value as string
                            : null;
                        return true;
                    }
                }
            }
        }

        underlying = null;
        description = null;
        return false;
    }

    public static bool TryGetIsValidMethod(ITypeSymbol type, out IMethodSymbol? method)
    {
        if (TryGetConstrainedUnderlying(type, out var underlying) && type is INamedTypeSymbol named)
        {
            foreach (var m in named.GetMembers("IsValid").OfType<IMethodSymbol>())
            {
                if (m.IsStatic &&
                    m.ReturnType.SpecialType == SpecialType.System_Boolean &&
                    m.Parameters.Length == 1 &&
                    SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, underlying))
                {
                    method = m;
                    return true;
                }
            }
        }

        method = null;
        return false;
    }

    public static string? GetInvariantDescription(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == VerifiableSymbolHelpers.InvariantAttr)
            {
                return attr.ConstructorArguments.Length >= 1
                    ? attr.ConstructorArguments[0].Value as string
                    : null;
            }
        }

        return null;
    }

    public static IReadOnlyList<ISymbol> GetInvariantMembers(ITypeSymbol type)
    {
        var list = new List<ISymbol>();
        if (type is not INamedTypeSymbol named)
        {
            return list;
        }

        var stack = new Stack<INamedTypeSymbol>();
        for (var cur = named; cur is not null && cur.SpecialType != SpecialType.System_Object && cur.SpecialType != SpecialType.System_ValueType; cur = cur.BaseType)
        {
            stack.Push(cur);
        }

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var m in cur.GetMembers().OfType<IMethodSymbol>())
            {
                if (!m.IsStatic &&
                    m.MethodKind == MethodKind.Ordinary &&
                    m.Parameters.Length == 0 &&
                    m.ReturnType.SpecialType == SpecialType.System_Boolean &&
                    VerifiableSymbolHelpers.HasAttribute(m, VerifiableSymbolHelpers.InvariantAttr))
                {
                    list.Add(m);
                }
            }

            foreach (var p in cur.GetMembers().OfType<IPropertySymbol>())
            {
                if (!p.IsStatic &&
                    !p.IsIndexer &&
                    p.Type.SpecialType == SpecialType.System_Boolean &&
                    VerifiableSymbolHelpers.HasAttribute(p, VerifiableSymbolHelpers.InvariantAttr))
                {
                    list.Add(p);
                }
            }
        }

        return list;
    }

    public static bool HasAnyInvariants(ITypeSymbol type, HashSet<ITypeSymbol>? visited = null)
    {
        visited ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        if (!visited.Add(type))
        {
            return false;
        }

        if (TryGetConstrainedUnderlying(type, out _))
        {
            return true;
        }

        if (GetInvariantMembers(type).Count > 0)
        {
            return true;
        }

        if (type is INamedTypeSymbol named && !named.IsGenericType)
        {
            foreach (var prop in named.GetMembers().OfType<IPropertySymbol>())
            {
                if (!prop.IsStatic && !prop.IsIndexer && prop.Name != "EqualityContract" &&
                    HasAnyInvariants(prop.Type, visited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryExtractSingleReturnOperation(
        Compilation compilation,
        ISymbol member,
        out IParameterSymbol? parameterSymbol,
        out IOperation? returnOp)
    {
        parameterSymbol = member is IMethodSymbol ms && ms.Parameters.Length == 1 ? ms.Parameters[0] : null;

        foreach (var synRef in member.DeclaringSyntaxReferences)
        {
            var syn = synRef.GetSyntax();
            if (!compilation.SyntaxTrees.Contains(syn.SyntaxTree))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(syn.SyntaxTree);
            if (syn is MethodDeclarationSyntax md)
            {
                if (md.ExpressionBody?.Expression is { } arrowExpr)
                {
                    returnOp = model.GetOperation(arrowExpr);
                    if (returnOp is not null) return true;
                }

                if (md.Body is { Statements.Count: 1 } body &&
                    body.Statements[0] is ReturnStatementSyntax ret &&
                    ret.Expression is { } retExpr)
                {
                    returnOp = model.GetOperation(retExpr);
                    if (returnOp is not null) return true;
                }
            }
            else if (syn is PropertyDeclarationSyntax pd)
            {
                if (pd.ExpressionBody?.Expression is { } propArrow)
                {
                    returnOp = model.GetOperation(propArrow);
                    if (returnOp is not null) return true;
                }

                if (pd.AccessorList is not null)
                {
                    var getter = pd.AccessorList.Accessors.FirstOrDefault(a => a.Keyword.Text == "get");
                    if (getter?.ExpressionBody?.Expression is { } gArrow)
                    {
                        returnOp = model.GetOperation(gArrow);
                        if (returnOp is not null) return true;
                    }

                    if (getter?.Body is { Statements.Count: 1 } gBody &&
                        gBody.Statements[0] is ReturnStatementSyntax gRet &&
                        gRet.Expression is { } gExpr)
                    {
                        returnOp = model.GetOperation(gExpr);
                        if (returnOp is not null) return true;
                    }
                }
            }
        }

        returnOp = null;
        return false;
    }

    private List<SmtFieldInfo> ExtractStoredFields(INamedTypeSymbol symbol, string dtSort)
    {
        var fields = new List<SmtFieldInfo>();

        if (symbol.IsTupleType)
        {
            foreach (var elem in symbol.TupleElements)
            {
                var name = elem.Name;
                fields.Add(new SmtFieldInfo(name, elem.Type, $"f_{dtSort}_{SanitizeName(name)}"));
            }
            return fields;
        }

        if (TryGetConstrainedUnderlying(symbol, out var underlying))
        {
            fields.Add(new SmtFieldInfo("Value", underlying!, $"f_{dtSort}_Value"));
            return fields;
        }

        var fullOrig = symbol.OriginalDefinition.ToDisplayString();
        if (fullOrig == "Verifiable.Contracts.Result<T, TError>")
        {
            var tArg = symbol.TypeArguments[0];
            var eArg = symbol.TypeArguments[1];
            fields.Add(new SmtFieldInfo("IsSuccess", symbol.GetMembers("IsSuccess").OfType<IPropertySymbol>().First().Type, $"f_{dtSort}_IsSuccess"));
            fields.Add(new SmtFieldInfo("Value", tArg, $"f_{dtSort}_Value"));
            fields.Add(new SmtFieldInfo("Error", eArg, $"f_{dtSort}_Error"));
            return fields;
        }

        var ctorParamNames = new HashSet<string>(
            symbol.InstanceConstructors
                .Where(c => !IsCopyConstructor(symbol, c))
                .SelectMany(c => c.Parameters)
                .Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var props = symbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic &&
                        !p.IsIndexer &&
                        p.Name != "EqualityContract" &&
                        !VerifiableSymbolHelpers.HasAttribute(p, VerifiableSymbolHelpers.GhostAttr) &&
                        !VerifiableSymbolHelpers.HasAttribute(p, VerifiableSymbolHelpers.InvariantAttr))
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in props)
        {
            if (IsStoredProperty(p, ctorParamNames))
            {
                seen.Add(p.Name);
                fields.Add(new SmtFieldInfo(p.Name, p.Type, $"f_{dtSort}_{SanitizeName(p.Name)}"));
            }
        }

        foreach (var f in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (!f.IsStatic && !f.IsImplicitlyDeclared && f.AssociatedSymbol is null &&
                !VerifiableSymbolHelpers.HasAttribute(f, VerifiableSymbolHelpers.GhostAttr) &&
                seen.Add(f.Name))
            {
                fields.Add(new SmtFieldInfo(f.Name, f.Type, $"f_{dtSort}_{SanitizeName(f.Name)}"));
            }
        }

        return fields;
    }

    private static bool IsCopyConstructor(INamedTypeSymbol type, IMethodSymbol ctor) =>
        ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type);

    private static bool IsStoredProperty(IPropertySymbol prop, ISet<string> ctorParamNames)
    {
        if (ctorParamNames.Contains(prop.Name))
        {
            return true;
        }

        if (prop.DeclaringSyntaxReferences.Length == 0)
        {
            return true;
        }

        foreach (var synRef in prop.DeclaringSyntaxReferences)
        {
            var syn = synRef.GetSyntax();
            if (syn is ParameterSyntax)
            {
                return true;
            }

            if (syn is PropertyDeclarationSyntax pd)
            {
                if (pd.ExpressionBody is not null)
                {
                    return false;
                }

                if (pd.AccessorList is not null)
                {
                    var getter = pd.AccessorList.Accessors.FirstOrDefault(a => a.Keyword.Text == "get");
                    if (getter is not null && getter.Body is null && getter.ExpressionBody is null)
                    {
                        return true;
                    }
                    return false;
                }
            }
        }

        return false;
    }

    private string Cache(ITypeSymbol type, string sort)
    {
        _sortCache[type] = sort;
        return sort;
    }

    public static string SanitizeName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }
        return sb.Length == 0 ? "T" : sb.ToString();
    }
}
