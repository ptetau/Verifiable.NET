using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Verifiable.Analyzers;

/// <summary>One clause of the signed-off spec: its ID and its sentence.</summary>
internal sealed record SpecClause(string Id, string? Text);

/// <summary>
/// The drift gate (requirement R1). Given a clause manifest from the spec — an additional file whose
/// name ends in <c>.clauses.json</c>, shaped <c>{"clauses":[{"id":"C1","text":"..."}]}</c> — the build
/// fails on any clause no code carries (VER401) and on any code that names a clause the spec does not
/// have (VER403), and warns on any contract or rule with no clause (VER402). Without a manifest it
/// reports nothing.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClauseCoverageAnalyzer : DiagnosticAnalyzer
{
    /// <summary>File name ending that marks an additional file as the spec's clause manifest.</summary>
    public const string ManifestSuffix = ".clauses.json";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        VerifiableDescriptors.ClauseNotImplemented,
        VerifiableDescriptors.ConstructWithoutClause,
        VerifiableDescriptors.UnknownClause,
        VerifiableDescriptors.InvalidClauseManifest);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext ctx)
    {
        var manifestFile = ctx.Options.AdditionalFiles.FirstOrDefault(f => f.Path.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase));
        if (manifestFile is null) return;

        var text = manifestFile.GetText(ctx.CancellationToken);
        var manifestLocation = text is null
            ? Location.None
            : Location.Create(manifestFile.Path, new TextSpan(0, 0), new LinePositionSpan(LinePosition.Zero, LinePosition.Zero));
        if (!TryParseManifest(text?.ToString() ?? string.Empty, out var clauses, out var error))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.InvalidClauseManifest, manifestLocation,
                $"The clause manifest `{System.IO.Path.GetFileName(manifestFile.Path)}` could not be read: {error}. Expected {{\"clauses\":[{{\"id\":\"C1\",\"text\":\"...\"}}]}}."));
            return;
        }

        var known = new HashSet<string>(clauses.Select(c => c.Id), StringComparer.Ordinal);
        var carried = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tree in ctx.Compilation.SyntaxTrees)
        {
            if (tree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var site in ClauseIds.Sites(tree))
            {
                if (site.Clause is null)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.ConstructWithoutClause, site.Location,
                        $"{site.Kind} in `{site.Symbol}` names no spec clause, so nothing traces it to a sentence the spec signed off. Add `clause: \"C...\"` (or `Clause = \"C...\"` on an attribute)."));
                }
                else if (!known.Contains(site.Clause))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.UnknownClause, site.Location,
                        ImmutableDictionary<string, string?>.Empty.Add("Clause", site.Clause),
                        $"{site.Kind} in `{site.Symbol}` names clause `{site.Clause}`, which the spec does not have. Fix the ID, or add the clause to the spec."));
                }
                else
                {
                    carried.Add(site.Clause);
                }
            }
        }

        foreach (var clause in clauses)
        {
            if (carried.Contains(clause.Id)) continue;
            ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.ClauseNotImplemented, manifestLocation,
                ImmutableDictionary<string, string?>.Empty.Add("Clause", clause.Id),
                $"Spec clause `{clause.Id}` has no code: no contract, type alias or invariant carries it." +
                (string.IsNullOrWhiteSpace(clause.Text) ? string.Empty : $" Clause text: \"{clause.Text}\"")));
        }
    }

    /// <summary>Reads a clause manifest. IDs must be non-empty and unique.</summary>
    internal static bool TryParseManifest(string json, out List<SpecClause> clauses, out string? error)
    {
        clauses = new List<SpecClause>();
        error = null;
        object? root;
        try
        {
            var reader = new MiniJson(json);
            root = reader.ReadValue();
            reader.EndOfInput();
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        if (root is not Dictionary<string, object?> obj || !obj.TryGetValue("clauses", out var listObj) || listObj is not List<object?> list)
        {
            error = "there is no `clauses` array";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> c || !c.TryGetValue("id", out var idObj) || idObj is not string id || id.Trim().Length == 0)
            {
                error = "every clause needs a non-empty string `id`";
                return false;
            }
            if (!seen.Add(id))
            {
                error = $"clause `{id}` is listed twice";
                return false;
            }
            c.TryGetValue("text", out var textObj);
            clauses.Add(new SpecClause(id, textObj as string));
        }
        return true;
    }

    /// <summary>A small strict JSON reader; analyzers cannot take a JSON library dependency.</summary>
    private sealed class MiniJson(string text)
    {
        private int _pos;

        public void EndOfInput()
        {
            SkipWs();
            if (_pos != text.Length) throw Fail("unexpected text after the JSON value");
        }

        public object? ReadValue()
        {
            SkipWs();
            if (_pos >= text.Length) throw Fail("unexpected end of input");
            var c = text[_pos];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default:
                    if (c == '-' || char.IsDigit(c)) return ReadNumber();
                    throw Fail($"unexpected character `{c}`");
            }
        }

        private Dictionary<string, object?> ReadObject()
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            _pos++;
            SkipWs();
            if (Peek('}')) { _pos++; return obj; }
            while (true)
            {
                SkipWs();
                if (!Peek('"')) throw Fail("expected a property name");
                var key = ReadString();
                SkipWs();
                if (!Peek(':')) throw Fail("expected `:`");
                _pos++;
                obj[key] = ReadValue();
                SkipWs();
                if (Peek(',')) { _pos++; continue; }
                if (Peek('}')) { _pos++; return obj; }
                throw Fail("expected `,` or `}`");
            }
        }

        private List<object?> ReadArray()
        {
            var list = new List<object?>();
            _pos++;
            SkipWs();
            if (Peek(']')) { _pos++; return list; }
            while (true)
            {
                list.Add(ReadValue());
                SkipWs();
                if (Peek(',')) { _pos++; continue; }
                if (Peek(']')) { _pos++; return list; }
                throw Fail("expected `,` or `]`");
            }
        }

        private string ReadString()
        {
            _pos++;
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_pos >= text.Length) break;
                var e = text[_pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_pos + 4 > text.Length ||
                            !int.TryParse(text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Fail("bad \\u escape");
                        }
                        sb.Append((char)code);
                        _pos += 4;
                        break;
                    default: throw Fail($"bad escape `\\{e}`");
                }
            }
            throw Fail("unterminated string");
        }

        private double ReadNumber()
        {
            var start = _pos;
            while (_pos < text.Length && "+-0123456789.eE".IndexOf(text[_pos]) >= 0) _pos++;
            if (!double.TryParse(text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                throw Fail("bad number");
            }
            return d;
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(text, _pos, word, 0, word.Length) != 0) throw Fail($"expected `{word}`");
            _pos += word.Length;
        }

        private bool Peek(char c) => _pos < text.Length && text[_pos] == c;

        private void SkipWs()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private FormatException Fail(string message) => new($"{message} at character {_pos}");
    }
}
