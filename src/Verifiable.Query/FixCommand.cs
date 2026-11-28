using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Verifiable.Analyzers;

namespace Verifiable.Query;

/// <summary>One text edit in a file, positioned in the original text (1-based lines and columns).</summary>
internal sealed record TextEditInfo(string File, int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText);

/// <summary>A code fix for one diagnostic, as data an agent can apply.</summary>
internal sealed record FixInfo(string Rule, string? Symbol, string File, int Line, string Title, string Provider, IReadOnlyList<TextEditInfo> Edits);

/// <summary>
/// <c>verifiable-query fix</c> (requirement R15): runs the code fixes that otherwise live only in the IDE,
/// and returns them as JSON text edits (schema <c>verifiable-query/fix@1</c>); <c>--apply</c> writes them.
/// </summary>
internal static class FixCommand
{
    private static DiagnosticAnalyzer[] Analyzers() => [new VerifiableGatekeeperAnalyzer(), new SpecQualityAnalyzer()];

    private static (AdhocWorkspace Workspace, Project Project, Dictionary<string, DocumentId> Docs) Build(CSharpCompilation compilation)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), compilation.AssemblyName ?? "Fix", compilation.AssemblyName ?? "Fix",
            LanguageNames.CSharp,
            compilationOptions: compilation.Options,
            parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.First().Options,
            metadataReferences: compilation.References));
        var docs = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var doc = workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id), Path.GetFileName(tree.FilePath),
                loader: TextLoader.From(TextAndVersion.Create(tree.GetText(), VersionStamp.Create(), tree.FilePath)),
                filePath: tree.FilePath));
            docs[tree.FilePath] = doc.Id;
        }
        return (workspace, workspace.CurrentSolution.GetProject(project.Id)!, docs);
    }

    private static IReadOnlyList<Diagnostic> Diagnose(Project project, string? rule, IReadOnlyList<string>? scope)
    {
        var comp = project.GetCompilationAsync().GetAwaiter().GetResult()!;
        return comp.WithAnalyzers(Analyzers().ToImmutableArray()).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .Where(d => d.Location.IsInSource && !d.Location.SourceTree!.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(d => rule is null || d.Id == rule)
            .Where(d => scope is null || ElmCheckEngine.SymbolInScope(DiagnosticFacts.EnclosingSymbol(comp, d.Location), scope))
            .OrderBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The first offered fix for a diagnostic, as the solution it produces.</summary>
    private static (string Title, string Provider, Solution Changed)? FirstFix(Project project, Diagnostic diagnostic)
    {
        var doc = project.Documents.FirstOrDefault(d => d.FilePath == diagnostic.Location.SourceTree?.FilePath);
        if (doc is null) return null;
        foreach (var provider in RuleCatalog.CodeFixProviders().Where(p => p.FixableDiagnosticIds.Contains(diagnostic.Id)))
        {
            var actions = new List<CodeAction>();
            var ctx = new CodeFixContext(doc, diagnostic, (a, _) => actions.Add(a), CancellationToken.None);
            try
            {
                provider.RegisterCodeFixesAsync(ctx).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var action in actions.SelectMany(Flatten))
            {
                var ops = action.GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (ops.OfType<ApplyChangesOperation>().FirstOrDefault() is { } apply)
                {
                    return (action.Title, provider.GetType().Name, apply.ChangedSolution);
                }
            }
        }
        return null;
    }

    private static IEnumerable<CodeAction> Flatten(CodeAction action) =>
        action.NestedActions.Length == 0 ? new[] { action } : action.NestedActions.SelectMany(Flatten);

    private static List<TextEditInfo> Edits(Solution before, Solution after)
    {
        var edits = new List<TextEditInfo>();
        foreach (var change in after.GetChanges(before).GetProjectChanges())
        {
            foreach (var id in change.GetChangedDocuments())
            {
                var oldText = before.GetDocument(id)!.GetTextAsync().GetAwaiter().GetResult();
                var newText = after.GetDocument(id)!.GetTextAsync().GetAwaiter().GetResult();
                var path = before.GetDocument(id)!.FilePath ?? string.Empty;
                // Code fixes often rewrite the whole document; report only the part that changed.
                var a = oldText.ToString();
                var b = newText.ToString();
                var prefix = 0;
                while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
                var suffix = 0;
                while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
                if (prefix == a.Length && prefix == b.Length) continue;
                var changed = TextSpan.FromBounds(prefix, a.Length - suffix);
                var lines = oldText.Lines.GetLinePositionSpan(changed);
                edits.Add(new TextEditInfo(AgentOutput.NormalizePath(path),
                    lines.Start.Line + 1, lines.Start.Character + 1, lines.End.Line + 1, lines.End.Character + 1,
                    b.Substring(prefix, b.Length - suffix - prefix).Replace("\r\n", "\n")));
            }
        }
        return edits;
    }

    /// <summary>The fix offered for each diagnostic in the current text.</summary>
    public static List<FixInfo> Propose(CSharpCompilation compilation, string? rule, IReadOnlyList<string>? scope)
    {
        var (workspace, project, _) = Build(compilation);
        using (workspace)
        {
            var comp = project.GetCompilationAsync().GetAwaiter().GetResult()!;
            var fixes = new List<FixInfo>();
            foreach (var d in Diagnose(project, rule, scope))
            {
                var fix = FirstFix(project, d);
                if (fix is null) continue;
                var span = d.Location.GetLineSpan();
                fixes.Add(new FixInfo(d.Id, DiagnosticFacts.EnclosingSymbol(comp, d.Location), AgentOutput.NormalizePath(span.Path),
                    span.StartLinePosition.Line + 1, fix.Value.Title, fix.Value.Provider, Edits(project.Solution, fix.Value.Changed)));
            }
            return fixes;
        }
    }

    /// <summary>
    /// Applies fixes one at a time — re-diagnosing after each, since one fix moves the text another
    /// points at — and writes the changed files. Returns what was applied.
    /// </summary>
    public static List<FixInfo> Apply(CSharpCompilation compilation, string? rule, IReadOnlyList<string>? scope, int maxFixes = 100)
    {
        var (workspace, project, _) = Build(compilation);
        using (workspace)
        {
            var original = project.Solution;
            var applied = new List<FixInfo>();
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < maxFixes; i++)
            {
                var comp = project.GetCompilationAsync().GetAwaiter().GetResult()!;
                var next = Diagnose(project, rule, scope)
                    .Select(d => (d, key: $"{d.Id}|{d.Location.SourceTree!.FilePath}|{d.Location.SourceSpan}"))
                    .FirstOrDefault(x => !skipped.Contains(x.key) && FirstFix(project, x.d) is not null);
                if (next.d is null) break;
                var fix = FirstFix(project, next.d)!.Value;
                var span = next.d.Location.GetLineSpan();
                applied.Add(new FixInfo(next.d.Id, DiagnosticFacts.EnclosingSymbol(comp, next.d.Location), AgentOutput.NormalizePath(span.Path),
                    span.StartLinePosition.Line + 1, fix.Title, fix.Provider, Edits(project.Solution, fix.Changed)));
                var newProject = fix.Changed.GetProject(project.Id)!;
                if (newProject.Solution.GetChanges(project.Solution).GetProjectChanges().All(c => !c.GetChangedDocuments().Any()))
                {
                    skipped.Add(next.key);
                    continue;
                }
                project = newProject;
            }

            foreach (var change in project.Solution.GetChanges(original).GetProjectChanges())
            {
                foreach (var id in change.GetChangedDocuments())
                {
                    var doc = project.Solution.GetDocument(id)!;
                    if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
                    File.WriteAllText(doc.FilePath, doc.GetTextAsync().GetAwaiter().GetResult().ToString());
                }
            }
            return applied;
        }
    }

    public static string ToJson(IReadOnlyList<FixInfo> fixes, bool applied, bool indented, int limit, int offset)
    {
        var page = AgentOutput.Slice(fixes.Count, offset, limit);
        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("fix"));
            w.WriteString("command", "fix");
            w.WriteString("status", AgentOutput.StatusWord(AgentOutcome.Proved));
            w.WriteNumber("exitCode", 0);
            w.WriteBoolean("applied", applied);
            w.WriteStartArray("fixes");
            for (var i = page.Offset; i < page.Offset + page.Count; i++)
            {
                var f = fixes[i];
                w.WriteStartObject();
                w.WriteString("rule", f.Rule);
                AgentOutput.StringOrNull(w, "symbol", f.Symbol);
                w.WriteString("file", f.File);
                w.WriteNumber("line", f.Line);
                w.WriteString("title", f.Title);
                w.WriteString("provider", f.Provider);
                w.WriteStartArray("edits");
                foreach (var e in f.Edits)
                {
                    w.WriteStartObject();
                    w.WriteString("file", e.File);
                    w.WriteStartObject("start");
                    w.WriteNumber("line", e.StartLine);
                    w.WriteNumber("column", e.StartColumn);
                    w.WriteEndObject();
                    w.WriteStartObject("end");
                    w.WriteNumber("line", e.EndLine);
                    w.WriteNumber("column", e.EndColumn);
                    w.WriteEndObject();
                    w.WriteString("newText", e.NewText);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            AgentOutput.WritePage(w, page, limit);
            w.WriteEndObject();
        });
    }
}
