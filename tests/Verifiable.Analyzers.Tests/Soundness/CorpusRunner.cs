using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using GalaxyCheck;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Verifiable.Analyzers.Smt;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>What happened when one input was run.</summary>
public abstract record RunOutcome
{
    /// <summary>The input does not meet the method's own preconditions, so it says nothing.</summary>
    public sealed record Discarded : RunOutcome;

    /// <summary>Every checked contract held.</summary>
    public sealed record Held : RunOutcome;

    /// <summary>A contract the analyzer accepted was broken while the code ran.</summary>
    public sealed record Broken(string Reason) : RunOutcome;
}

/// <summary>The result of checking a corpus program.</summary>
public sealed record CorpusReport(
    ImmutableArray<Diagnostic> Diagnostics,
    IReadOnlyList<string> Fatal,
    bool Rejected,
    IReadOnlyList<(string Witness, RunOutcome Outcome)> Witnesses,
    IReadOnlyList<string> CrossCheckFailures,
    IReadOnlyList<string> CrossCheckNotes);

/// <summary>
/// Runs a corpus program through the analyzers and through the CLR, and reports both.
/// </summary>
internal static class CorpusRunner
{
    private const string RuntimeSpecType = "global::Verifiable.Analyzers.Tests.Soundness.RuntimeSpec";
    private const int Samples = 300;
    private static readonly TimeSpan WitnessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CrossCheckBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CrossCheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] ContractMethods =
        ["Requires", "RequiresWhen", "Ensures", "EnsuresWhen", "LoopInvariant", "Decreases"];

    public static DiagnosticAnalyzer[] Analyzers() =>
    [
        new VerifiableGatekeeperAnalyzer(),
        new VerifiableSmtAnalyzer(),
        new LinearObligationAnalyzer(),
        new ProofSuppressionAuditor(),
    ];

    /// <summary>Diagnostics that count as the analyzer refusing a program: any VER error, or VER106 (proof may be vacuous).</summary>
    public static bool IsRejection(Diagnostic d) =>
        d.Id.StartsWith("VER", StringComparison.Ordinal) && d.Id != "VER104" &&
        (d.Severity == DiagnosticSeverity.Error || d.Id == "VER106");

    public static async Task<CorpusReport> RunAsync(CorpusProgram program)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(program.Source, Analyzers());

        var fatal = new List<string>();
        foreach (var d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("CS", StringComparison.Ordinal))
            {
                fatal.Add($"The program does not compile: {d}");
            }
            else if (d.Id == "AD0001")
            {
                fatal.Add($"An analyzer crashed: {d}");
            }
            else if (d.Id == "VER104")
            {
                fatal.Add("Z3 was not found, so nothing was proved. Run scripts/install-z3.sh first.");
            }
        }

        var rejections = diagnostics.Where(IsRejection).ToList();
        if (fatal.Count > 0)
        {
            return new CorpusReport(diagnostics, fatal, rejections.Count > 0, [], [], []);
        }

        var runtime = RuntimeBuild.Create(program.Source, program.Witnesses, out var buildErrors);
        if (runtime is null)
        {
            fatal.AddRange(buildErrors);
            return new CorpusReport(diagnostics, fatal, rejections.Count > 0, [], [], []);
        }

        try
        {
            var witnesses = new List<(string, RunOutcome)>();
            for (var i = 0; i < program.Witnesses.Count; i++)
            {
                witnesses.Add((program.Witnesses[i], runtime.RunWitness(i)));
            }

            var failures = new List<string>();
            var notes = new List<string>();
            if (program.RuntimeSkipReason is null && program.KnownBug is null)
            {
                var flagged = FlaggedMethods(program.Source, rejections);
                runtime.CrossCheck(flagged, failures, notes);
            }

            return new CorpusReport(diagnostics, fatal, rejections.Count > 0, witnesses, failures, notes);
        }
        finally
        {
            runtime.Dispose();
        }
    }

    /// <summary>
    /// Runs only the runtime half: every method in a <c>[Verifiable]</c> scope is treated as accepted and run on
    /// random inputs. Used to test the harness itself.
    /// </summary>
    internal static (List<string> Failures, List<string> Notes, List<RunOutcome> Witnesses) RunWithoutAnalyzer(string source, params string[] witnesses) =>
        RunWithoutAnalyzer(source, new HashSet<string>(), witnesses);

    /// <inheritdoc cref="RunWithoutAnalyzer(string, string[])"/>
    /// <param name="rejectedMethods">Method keys to treat as rejected by the analyzer.</param>
    internal static (List<string> Failures, List<string> Notes, List<RunOutcome> Witnesses) RunWithoutAnalyzer(string source, HashSet<string> rejectedMethods, params string[] witnesses)
    {
        var runtime = RuntimeBuild.Create(source, witnesses, out var errors)
            ?? throw new System.InvalidOperationException(string.Join("\n", errors));
        try
        {
            var failures = new List<string>();
            var notes = new List<string>();
            var outcomes = Enumerable.Range(0, witnesses.Length).Select(runtime.RunWitness).ToList();
            runtime.CrossCheck(rejectedMethods, failures, notes);
            return (failures, notes, outcomes);
        }
        finally
        {
            runtime.Dispose();
        }
    }

    /// <summary>The keys of the methods that contain a rejecting diagnostic.</summary>
    private static HashSet<string> FlaggedMethods(string source, IEnumerable<Diagnostic> rejections)
    {
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in rejections)
        {
            if (!d.Location.IsInSource || d.Location.SourceTree?.FilePath != "TestInput.cs") continue;
            var node = root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
            var method = node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (method is not null) keys.Add(MethodKey(method));
        }

        return keys;
    }

    /// <summary>A name for a method that survives the rewrite: its containing types, name and parameter list.</summary>
    internal static string MethodKey(BaseMethodDeclarationSyntax method)
    {
        var types = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).Reverse();
        var name = method switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => ".ctor:" + c.Identifier.Text,
            _ => method.Kind().ToString(),
        };
        var parameters = string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"));
        return $"{string.Join(".", types)}.{name}({parameters})";
    }

    internal static bool IsContractCall(InvocationExpressionSyntax invocation, out string name)
    {
        name = "";
        if (invocation.Expression is not MemberAccessExpressionSyntax access) return false;
        var receiver = access.Expression.ToString().Replace(" ", "");
        if (receiver is not ("Spec" or "Verifiable.Contracts.Spec" or "global::Verifiable.Contracts.Spec")) return false;
        name = access.Name.Identifier.Text;
        return Array.IndexOf(ContractMethods, name) >= 0;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on its own thread with a large stack, so deep recursion and hung loops fail the
    /// test instead of taking down or stalling the test run.
    /// </summary>
    internal static T OnFreshThread<T>(Func<T> work, TimeSpan timeout, Func<T> onTimeout)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }, maxStackSize: 64 * 1024 * 1024) { IsBackground = true };
        thread.Start();
        if (!thread.Join(timeout)) return onTimeout();
        if (error is not null) throw new InvalidOperationException("The corpus runner failed.", error);
        return result;
    }

    /// <summary>The program rewritten to check its contracts at run time, compiled and loaded.</summary>
    private sealed class RuntimeBuild : IDisposable
    {
        private readonly AssemblyLoadContext _context;
        private readonly Assembly _assembly;
        private readonly Compilation _compilation;
        private readonly SyntaxTree _programTree;
        private readonly IReadOnlyList<IMethodSymbol> _witnessTargets;

        private RuntimeBuild(AssemblyLoadContext context, Assembly assembly, Compilation compilation, SyntaxTree programTree, IReadOnlyList<IMethodSymbol> witnessTargets)
        {
            _context = context;
            _assembly = assembly;
            _compilation = compilation;
            _programTree = programTree;
            _witnessTargets = witnessTargets;
        }

        public static RuntimeBuild? Create(string source, IReadOnlyList<string> witnesses, out List<string> errors)
        {
            errors = new List<string>();
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var original = CSharpSyntaxTree.ParseText(source, parseOptions, path: "TestInput.cs");
            var rewritten = new ContractRewriter().Visit(original.GetRoot());
            var programTree = CSharpSyntaxTree.Create((CSharpSyntaxNode)rewritten, parseOptions, path: "TestInput.cs");

            var compilation = (Compilation)CSharpCompilation.Create(
                "SoundnessRun" + Guid.NewGuid().ToString("N"),
                [programTree],
                AnalyzerTestHarness.CollectDefaultReferences(null),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators: AnalyzerTestHarness.CreateDefaultGenerators(null),
                parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out _);

            // Bind each witness call to find the method and its parameter types.
            var targets = new List<IMethodSymbol>();
            if (witnesses.Count > 0)
            {
                var bindSource = "internal static class __SoundnessBind\n{\n" +
                    string.Join("\n", witnesses.Select((w, i) => $"    internal static void B{i}() {{ {w}; }}")) + "\n}\n";
                var bindTree = CSharpSyntaxTree.ParseText(bindSource, parseOptions, path: "Witness.cs");
                var bound = compilation.AddSyntaxTrees(bindTree);
                var model = bound.GetSemanticModel(bindTree);
                var calls = bindTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Select(m => m.Body!.Statements[0]).ToList();
                var argsSource = new System.Text.StringBuilder("internal static class __SoundnessArgs\n{\n");
                for (var i = 0; i < calls.Count; i++)
                {
                    if (calls[i] is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } ||
                        model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target)
                    {
                        errors.Add($"@witness `{witnesses[i]}` does not name a method that can be called: " +
                            string.Join("; ", bound.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == bindTree)));
                        continue;
                    }

                    if (!target.IsStatic || target.Parameters.Any(p => p.RefKind != RefKind.None) ||
                        invocation.ArgumentList.Arguments.Count != target.Parameters.Length ||
                        invocation.ArgumentList.Arguments.Any(a => a.NameColon is not null))
                    {
                        errors.Add($"@witness `{witnesses[i]}` must call a static method with every argument given in order, and no ref or out.");
                        continue;
                    }

                    targets.Add(target);
                    var items = invocation.ArgumentList.Arguments.Select((a, j) =>
                        $"(object?)({target.Parameters[j].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})({a.Expression})");
                    argsSource.Append($"    internal static object?[] A{targets.Count - 1}() => new object?[] {{ {string.Join(", ", items)} }};\n");
                }

                argsSource.Append("}\n");
                if (errors.Count > 0) return null;
                compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(argsSource.ToString(), parseOptions, path: "WitnessArgs.cs"));
            }

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                errors.Add("The program, rewritten to check contracts at run time, does not compile: " +
                    string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
                return null;
            }

            var context = new AssemblyLoadContext("soundness", isCollectible: true);
            ms.Position = 0;
            var assembly = context.LoadFromStream(ms);

            return new RuntimeBuild(context, assembly, compilation, programTree, targets);
        }

        public RunOutcome RunWitness(int index)
        {
            var args = (object?[])_assembly.GetType("__SoundnessArgs")!
                .GetMethod("A" + index, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            var method = Resolve(_witnessTargets[index]);
            if (method is null) return new RunOutcome.Broken("(harness) could not find the witness method by reflection");
            return OnFreshThread(
                () => Invoke(method, args),
                WitnessTimeout,
                () => new RunOutcome.Broken($"did not finish within {WitnessTimeout.TotalSeconds:0} s"));
        }

        public void CrossCheck(HashSet<string> flagged, List<string> failures, List<string> notes)
        {
            var model = _compilation.GetSemanticModel(_programTree);
            foreach (var decl in _programTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!decl.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsRuntimeSpecCall)) continue;
                if (model.GetDeclaredSymbol(decl) is not IMethodSymbol symbol) continue;

                var key = MethodKey(decl);
                if (flagged.Contains(key) || !InVerifiableScope(symbol) || HasAttribute(symbol, "PureAxiomAttribute")) continue;

                if (!symbol.IsStatic || symbol.IsGenericMethod || symbol.ContainingType.IsGenericType ||
                    symbol.Parameters.Any(p => p.RefKind != RefKind.None))
                {
                    notes.Add($"{key}: not cross-checked (only static, non-generic methods without ref or out are run).");
                    continue;
                }

                var gens = symbol.Parameters.Select(p => InputGens.For(p.Type)).ToList();
                if (gens.Any(g => g is null))
                {
                    notes.Add($"{key}: not cross-checked (no input generator for a parameter type).");
                    continue;
                }

                var method = Resolve(symbol);
                if (method is null)
                {
                    notes.Add($"{key}: not cross-checked (could not find it by reflection).");
                    continue;
                }

                var inputs = InputGens.Combine(gens!).Sample(iterations: Samples, seed: StableSeed(key));
                var result = OnFreshThread<(string? Failure, int Held, int Discarded)>(
                    () =>
                    {
                        var held = 0;
                        var discarded = 0;
                        var clock = Stopwatch.StartNew();
                        foreach (var input in inputs)
                        {
                            if (clock.Elapsed > CrossCheckBudget) break;
                            var outcome = Invoke(method, input);

                            // A run through a method the analyzer already rejected may fail because of that method,
                            // as the analyzer checks each method against its callees' contracts, not their code.
                            if (RuntimeSpec.Entered.Any(flagged.Contains))
                            {
                                discarded++;
                                continue;
                            }

                            switch (outcome)
                            {
                                case RunOutcome.Broken b:
                                    return ($"{key} was accepted, but {Describe(symbol, input)} {b.Reason}", held, discarded);
                                case RunOutcome.Held:
                                    held++;
                                    break;
                                default:
                                    discarded++;
                                    break;
                            }
                        }

                        return (null, held, discarded);
                    },
                    CrossCheckTimeout,
                    () => ($"{key}: one run did not finish within {CrossCheckTimeout.TotalSeconds:0} s. If the loop is just slow, add `// @runtime: skip <reason>`.", 0, 0));

                if (result.Failure is not null)
                {
                    failures.Add(result.Failure);
                }
                else
                {
                    notes.Add($"{key}: {result.Held} runs held, {result.Discarded} set aside (preconditions not met, or the run went through a rejected method).");
                }
            }
        }

        private static RunOutcome Invoke(MethodInfo method, object?[] args)
        {
            RuntimeSpec.BeginRun();
            object? result;
            try
            {
                result = method.Invoke(null, (object?[])args.Clone());
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InputRejectedException)
            {
                return new RunOutcome.Discarded();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ContractBrokenException broken)
            {
                return new RunOutcome.Broken(broken.Message);
            }
            catch (TargetInvocationException ex)
            {
                return new RunOutcome.Broken($"threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            }

            // A postcondition may call a contracted method. Evaluate it inside a frame so that callee counts as a
            // callee (its broken precondition is a contract failure, its own postconditions are not collected), and
            // iterate over a copy because that call must not change the list being checked.
            foreach (var (because, predicate) in RuntimeSpec.Postconditions.ToList())
            {
                bool holds;
                try
                {
                    using (RuntimeSpec.Enter("(postcondition)", "postcondition"))
                    {
                        holds = (bool)predicate.DynamicInvoke(result)!;
                    }
                }
                catch (TargetInvocationException ex) when (ex.InnerException is ContractBrokenException broken)
                {
                    return new RunOutcome.Broken($"returned {Show(result)}, and checking the postcondition \"{because}\" broke a contract: {broken.Message}");
                }
                catch (TargetInvocationException ex)
                {
                    return new RunOutcome.Broken($"returned {Show(result)}, and the postcondition \"{because}\" threw {ex.InnerException?.GetType().Name}");
                }

                if (!holds) return new RunOutcome.Broken($"returned {Show(result)}, which breaks the postcondition \"{because}\"");
            }

            return new RunOutcome.Held();
        }

        private MethodInfo? Resolve(IMethodSymbol symbol)
        {
            var type = _assembly.GetType(MetadataName(symbol.ContainingType));
            if (type is null) return null;
            var candidates = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == symbol.MetadataName && m.GetParameters().Length == symbol.Parameters.Length)
                .ToList();
            if (candidates.Count <= 1) return candidates.FirstOrDefault();
            return candidates.FirstOrDefault(m => m.GetParameters().Select(p => p.ParameterType.Name)
                .SequenceEqual(symbol.Parameters.Select(p => p.Type.MetadataName)));
        }

        private static string MetadataName(INamedTypeSymbol type)
        {
            var name = type.MetadataName;
            for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            {
                name = outer.MetadataName + "+" + name;
            }

            var ns = type.ContainingNamespace;
            return ns is null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
        }

        private static bool InVerifiableScope(IMethodSymbol method)
        {
            for (ISymbol? s = method; s is not null; s = s.ContainingType)
            {
                if (HasAttribute(s, "UnverifiableAttribute")) return false;
                if (HasAttribute(s, "VerifiableAttribute")) return true;
            }

            return method.ContainingAssembly.GetAttributes().Any(a => a.AttributeClass?.Name == "VerifiableAttribute");
        }

        private static bool HasAttribute(ISymbol symbol, string name) =>
            symbol.GetAttributes().Any(a => a.AttributeClass?.Name == name);

        private static bool IsRuntimeSpecCall(InvocationExpressionSyntax invocation) =>
            invocation.Expression is MemberAccessExpressionSyntax access && access.Expression.ToString() == RuntimeSpecType;

        private static string Describe(IMethodSymbol symbol, object?[] input) =>
            $"{symbol.Name}({string.Join(", ", input.Select(Show))})";

        private static string Show(object? value) => value switch
        {
            null => "null",
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\0", "\\0") + "\"",
            char c => c < 32 || c > 126 ? $"'\\u{(int)c:X4}'" : $"'{c}'",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "?",
        };

        private static int StableSeed(string key)
        {
            unchecked
            {
                var h = 17;
                foreach (var ch in key) h = h * 31 + ch;
                return h;
            }
        }

        public void Dispose() => _context.Unload();
    }

    /// <summary>
    /// Turns <c>Spec.*</c> contract calls into <see cref="RuntimeSpec"/> calls, and opens a <see cref="RuntimeSpec"/>
    /// frame at the start of every method body.
    /// </summary>
    private sealed class ContractRewriter : CSharpSyntaxRewriter
    {
        private const string EntryPrefix = "__entry_";

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!IsContractCall(node, out var name)) return visited;
            var access = (MemberAccessExpressionSyntax)visited.Expression;
            visited = visited.WithExpression(access.WithExpression(ParseExpression(RuntimeSpecType)));

            if (name is "LoopInvariant" or "Decreases")
            {
                // Tell the runtime which loop this is, so loops nested inside it can start afresh.
                var loop = node.Ancestors().FirstOrDefault(a => a is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax);
                var (start, end) = loop is null ? (-1, -1) : (loop.SpanStart, loop.Span.End);
                visited = visited.AddArgumentListArguments(
                    Argument(NameColon("loopStart"), default, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(start))),
                    Argument(NameColon("loopEnd"), default, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(end))));
            }
            else if (name is "Ensures" or "EnsuresWhen")
            {
                // A postcondition talks about the parameters' values on entry, so it reads the copies made then.
                var parameters = EnclosingParameters(node);
                if (parameters.Count > 0)
                {
                    var args = visited.ArgumentList.Arguments;
                    var renamed = args.Select(a => a.Expression is LambdaExpressionSyntax lambda
                        ? a.WithExpression((ExpressionSyntax)new EntryRenamer(parameters, LambdaParameters(lambda)).Visit(lambda))
                        : a);
                    visited = visited.WithArgumentList(visited.ArgumentList.WithArguments(SeparatedList(renamed)));
                }
            }

            return visited;
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            // Postconditions over Spec.Result<T>() cannot run before the method returns. Their arguments could also
            // throw (Spec.Result<string>() is null at run time), so the whole call is dropped.
            if (node.Expression is InvocationExpressionSyntax inv && IsContractCall(inv, out var name) &&
                ((name == "Ensures" && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LambdaExpressionSyntax) ||
                 (name == "EnsuresWhen" && inv.ArgumentList.Arguments.ElementAtOrDefault(1)?.Expression is not LambdaExpressionSyntax)))
            {
                return EmptyStatement().WithTriviaFrom(node);
            }

            return base.VisitExpressionStatement(node);
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var key = MethodKey(node);
            var parameters = SnapshotParameters(node.ParameterList);
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

            // Generators read some members in their written shape (a TypeAlias's `IsValid(...) => ...`), so an
            // expression-bodied member without contracts is left as it is, without a frame.
            if (visited.ExpressionBody is not null && !HasOwnContracts(node)) return visited;

            if (visited.ExpressionBody is { } arrow)
            {
                var isVoid = visited.ReturnType is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword);
                StatementSyntax statement = isVoid ? ExpressionStatement(arrow.Expression) : ReturnStatement(arrow.Expression);
                visited = visited.WithExpressionBody(null).WithSemicolonToken(default).WithBody(Block(statement));
            }

            return visited.Body is null ? visited : visited.WithBody(WithFrame(visited.Body, key, parameters));
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var key = MethodKey(node);
            var parameters = SnapshotParameters(node.ParameterList);
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
            return visited.Body is not null ? visited.WithBody(WithFrame(visited.Body, key, parameters)) : visited;
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var owner = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            var key = (owner is null ? "" : MethodKey(owner)) + "/local:" + node.Identifier.Text;
            var parameters = SnapshotParameters(node.ParameterList);
            var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            if (visited.ExpressionBody is { } arrow)
            {
                var isVoid = visited.ReturnType is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword);
                StatementSyntax statement = isVoid ? ExpressionStatement(arrow.Expression) : ReturnStatement(arrow.Expression);
                visited = visited.WithExpressionBody(null).WithSemicolonToken(default).WithBody(Block(statement));
            }

            return visited.Body is not null ? visited.WithBody(WithFrame(visited.Body, key, parameters)) : visited;
        }

        /// <summary>Whether the member has contract calls of its own, not counting those inside nested local functions.</summary>
        private static bool HasOwnContracts(SyntaxNode member) =>
            member.DescendantNodes(n => n == member || n is not LocalFunctionStatementSyntax)
                .OfType<InvocationExpressionSyntax>()
                .Any(i => IsContractCall(i, out _));

        private static BlockSyntax WithFrame(BlockSyntax body, string key, IReadOnlyList<string> parameters)
        {
            var literal = LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(key)).ToFullString();
            var prologue = new List<StatementSyntax>
            {
                ParseStatement($"using var __soundnessFrame = {RuntimeSpecType}.Enter({literal});\n"),
            };
            prologue.AddRange(parameters.Select(p => ParseStatement($"var {EntryPrefix}{p} = {p};\n")));
            return body.WithStatements(body.Statements.InsertRange(0, prologue));
        }

        /// <summary>Parameters that can be copied on entry: not <c>ref</c>, <c>out</c> or <c>in</c>, and not by-ref-like.</summary>
        private static IReadOnlyList<string> SnapshotParameters(ParameterListSyntax list) =>
            list.Parameters
                .Where(p => p.Modifiers.Count == 0 && p.Type is not null && !p.Type.ToString().Contains("Span<", StringComparison.Ordinal))
                .Select(p => p.Identifier.Text)
                .ToList();

        private static HashSet<string> EnclosingParameters(SyntaxNode node)
        {
            var member = node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
            var list = member switch
            {
                BaseMethodDeclarationSyntax m => m.ParameterList,
                LocalFunctionStatementSyntax l => l.ParameterList,
                _ => null,
            };
            return list is null ? new HashSet<string>() : new HashSet<string>(SnapshotParameters(list), StringComparer.Ordinal);
        }

        private static HashSet<string> LambdaParameters(LambdaExpressionSyntax lambda) => lambda switch
        {
            SimpleLambdaExpressionSyntax s => new HashSet<string>(StringComparer.Ordinal) { s.Parameter.Identifier.Text },
            ParenthesizedLambdaExpressionSyntax p => new HashSet<string>(p.ParameterList.Parameters.Select(x => x.Identifier.Text), StringComparer.Ordinal),
            _ => new HashSet<string>(),
        };

        /// <summary>Points a postcondition's references to method parameters at their entry copies.</summary>
        private sealed class EntryRenamer(HashSet<string> parameters, HashSet<string> shadowed) : CSharpSyntaxRewriter
        {
            public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            {
                var name = node.Identifier.Text;
                if (!parameters.Contains(name) || shadowed.Contains(name)) return node;
                if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node) return node;
                if (node.Parent is NameColonSyntax or NameEqualsSyntax) return node;
                return node.WithIdentifier(Identifier(EntryPrefix + name).WithTriviaFrom(node.Identifier));
            }

            // A nested lambda's own parameters hide the method's.
            public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) =>
                new EntryRenamer(parameters, new HashSet<string>(shadowed.Append(node.Parameter.Identifier.Text))).VisitLambdaBody(node);

            public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) =>
                new EntryRenamer(parameters, new HashSet<string>(shadowed.Concat(node.ParameterList.Parameters.Select(p => p.Identifier.Text)))).VisitLambdaBody(node);

            private SyntaxNode? VisitLambdaBody(LambdaExpressionSyntax lambda) => lambda switch
            {
                SimpleLambdaExpressionSyntax s when s.ExpressionBody is { } e => s.WithExpressionBody((ExpressionSyntax)Visit(e)),
                SimpleLambdaExpressionSyntax s when s.Block is { } b => s.WithBlock((BlockSyntax)Visit(b)),
                ParenthesizedLambdaExpressionSyntax p when p.ExpressionBody is { } e => p.WithExpressionBody((ExpressionSyntax)Visit(e)),
                ParenthesizedLambdaExpressionSyntax p when p.Block is { } b => p.WithBlock((BlockSyntax)Visit(b)),
                _ => lambda,
            };
        }
    }
}
