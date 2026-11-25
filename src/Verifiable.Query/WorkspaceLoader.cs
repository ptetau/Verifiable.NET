using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Contracts;
using Verifiable.Generators;

namespace Verifiable.Query;

internal sealed record WorkspaceLoadResult(
    CSharpCompilation? Compilation,
    IReadOnlyList<Diagnostic> CompilationErrors,
    IReadOnlyList<string> SourceFiles,
    string? ErrorMessage);

internal static class WorkspaceLoader
{
    private static readonly CSharpParseOptions DefaultParseOptions = new(LanguageVersion.Preview);

    public static WorkspaceLoadResult Load(
        string? projectOrDirPath,
        IReadOnlyList<string> explicitFiles,
        string? stdinSource,
        string stdinName = "StdinInput.cs")
    {
        var collectedFiles = new List<(string Path, string Text)>();
        var implicitUsingsEnabled = true;

        if (!string.IsNullOrWhiteSpace(stdinSource))
        {
            var full = Path.GetFullPath(stdinName);
            collectedFiles.Add((full, stdinSource));
        }

        foreach (var file in explicitFiles)
        {
            var full = Path.GetFullPath(file);
            if (!File.Exists(full))
            {
                return new WorkspaceLoadResult(null, Array.Empty<Diagnostic>(), Array.Empty<string>(), $"Source file not found: '{full}'.");
            }
            collectedFiles.Add((full, File.ReadAllText(full)));
        }

        if (!string.IsNullOrWhiteSpace(projectOrDirPath))
        {
            var resolved = ResolveProjectOrDirectory(projectOrDirPath!, out var resolveError);
            if (resolveError is not null)
            {
                return new WorkspaceLoadResult(null, Array.Empty<Diagnostic>(), Array.Empty<string>(), resolveError);
            }

            if (resolved!.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var projFiles = new List<string>();
                if (!CollectProjectSources(resolved, visited, projFiles, out var projImplicitUsings, out var projErr))
                {
                    return new WorkspaceLoadResult(null, Array.Empty<Diagnostic>(), Array.Empty<string>(), projErr);
                }

                implicitUsingsEnabled = projImplicitUsings;
                foreach (var f in projFiles)
                {
                    collectedFiles.Add((f, File.ReadAllText(f)));
                }
            }
            else
            {
                foreach (var f in EnumerateDirectoryCsFiles(resolved, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                {
                    collectedFiles.Add((f, File.ReadAllText(f)));
                }
            }
        }

        if (collectedFiles.Count == 0)
        {
            return new WorkspaceLoadResult(
                null,
                Array.Empty<Diagnostic>(),
                Array.Empty<string>(),
                "No C# source files were provided. Pass `--project <path.csproj>`, `--file <file.cs>`, or `--stdin`.");
        }

        // Deduplicate preserving order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var syntaxTrees = new List<SyntaxTree>();
        var fileList = new List<string>();

        foreach (var (path, text) in collectedFiles)
        {
            if (seen.Add(path))
            {
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(text, DefaultParseOptions, path: path));
                fileList.Add(path);
            }
        }

        if (implicitUsingsEnabled)
        {
            const string globalUsings = """
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Threading;
                global using System.Threading.Tasks;
                """;
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(globalUsings, DefaultParseOptions, path: "VerifiableQuery.GlobalUsings.g.cs"));
        }

        var refs = BuildMetadataReferences();
        var assemblyName = DeriveAssemblyName(projectOrDirPath, fileList);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            refs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new TypeAliasGenerator().AsSourceGenerator(), new InboundPortGenerator().AsSourceGenerator()],
            parseOptions: DefaultParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var updatedCSharp = (CSharpCompilation)updated;
        var errors = updatedCSharp.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        return new WorkspaceLoadResult(updatedCSharp, errors, fileList, null);
    }

    private static string? ResolveProjectOrDirectory(string inputPath, out string? error)
    {
        error = null;
        var full = Path.GetFullPath(inputPath);
        if (File.Exists(full))
        {
            if (!full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Expected a .csproj file or directory, but got file '{full}'. Use `--file` for standalone .cs files.";
                return null;
            }
            return full;
        }

        if (Directory.Exists(full))
        {
            var projs = Directory.GetFiles(full, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projs.Length == 1)
            {
                return projs[0];
            }
            if (projs.Length > 1)
            {
                var names = string.Join(", ", projs.Select(Path.GetFileName));
                error = $"Multiple .csproj files found in '{full}' ({names}). Pass the exact .csproj path with `--project`.";
                return null;
            }
            return full;
        }

        error = $"Project or directory not found: '{full}'.";
        return null;
    }

    private static bool CollectProjectSources(
        string csprojPath,
        HashSet<string> visitedProjects,
        List<string> collectedFiles,
        out bool implicitUsingsEnabled,
        out string? error)
    {
        implicitUsingsEnabled = true;
        error = null;

        var fullProj = Path.GetFullPath(csprojPath);
        if (!visitedProjects.Add(fullProj))
        {
            return true;
        }

        if (!File.Exists(fullProj))
        {
            error = $"Referenced project file not found: '{fullProj}'.";
            return false;
        }

        var projDir = Path.GetDirectoryName(fullProj)!;
        var doc = XDocument.Load(fullProj);
        var root = doc.Root;
        if (root is null)
        {
            error = $"Invalid empty project XML: '{fullProj}'.";
            return false;
        }

        var implicitEl = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ImplicitUsings");
        if (implicitEl is not null &&
            implicitEl.Value.Trim().Equals("disable", StringComparison.OrdinalIgnoreCase))
        {
            implicitUsingsEnabled = false;
        }

        var enableDefaultCompile = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "EnableDefaultCompileItems");
        var useDefaultCompile = enableDefaultCompile is null ||
            !enableDefaultCompile.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

        var removedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compileEl in root.Descendants().Where(e => e.Name.LocalName == "Compile"))
        {
            var rem = compileEl.Attribute("Remove")?.Value;
            if (!string.IsNullOrWhiteSpace(rem))
            {
                foreach (var part in rem.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    removedPatterns.Add(Path.GetFullPath(Path.Combine(projDir, part.Trim())));
                }
            }

            var inc = compileEl.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(inc))
            {
                foreach (var part in inc.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = part.Trim();
                    if (!trimmed.Contains('*') && !trimmed.Contains('?'))
                    {
                        var candidate = Path.GetFullPath(Path.Combine(projDir, trimmed));
                        if (File.Exists(candidate))
                        {
                            collectedFiles.Add(candidate);
                        }
                    }
                }
            }
        }

        if (useDefaultCompile)
        {
            foreach (var cs in EnumerateDirectoryCsFiles(projDir, removedPatterns))
            {
                collectedFiles.Add(cs);
            }
        }

        foreach (var projRef in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var refOut = projRef.Attribute("ReferenceOutputAssembly")?.Value
                ?? projRef.Elements().FirstOrDefault(e => e.Name.LocalName == "ReferenceOutputAssembly")?.Value;
            if (refOut is not null && refOut.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var outItem = projRef.Attribute("OutputItemType")?.Value
                ?? projRef.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputItemType")?.Value;
            if (outItem is not null && outItem.Trim().Equals("Analyzer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inc = projRef.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(inc))
            {
                continue;
            }

            if (inc.EndsWith("Verifiable.Contracts.csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var refProjPath = Path.GetFullPath(Path.Combine(projDir, inc.Replace('\\', Path.DirectorySeparatorChar)));
            if (!CollectProjectSources(refProjPath, visitedProjects, collectedFiles, out _, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> EnumerateDirectoryCsFiles(string rootDir, HashSet<string> excludedFiles)
    {
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(file);
                if (!excludedFiles.Contains(full))
                {
                    yield return full;
                }
            }

            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".tools", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("artifacts", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                stack.Push(sub);
            }
        }
    }

    private static List<MetadataReference> BuildMetadataReferences()
    {
        var refs = new List<MetadataReference>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            if (addedPaths.Add(full))
            {
                refs.Add(MetadataReference.CreateFromFile(full));
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var p in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var fileName = Path.GetFileName(p);
                // Avoid referencing Verifiable.Analyzers or Roslyn itself inside user target compilation unless harmless; include BCL + System.* + Verifiable.Contracts
                if (fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase))
                {
                    TryAdd(p);
                }
            }
        }

        TryAdd(typeof(VerifiableAttribute).Assembly.Location);
        TryAdd(typeof(ImmutableDictionary).Assembly.Location);
        return refs;
    }

    private static string DeriveAssemblyName(string? projectPath, IReadOnlyList<string> fileList)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var baseName = Path.GetFileNameWithoutExtension(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                return baseName;
            }
        }

        if (fileList.Count > 0)
        {
            var baseName = Path.GetFileNameWithoutExtension(fileList[0]);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                return baseName;
            }
        }

        return "VerifiableQueryTarget";
    }
}
