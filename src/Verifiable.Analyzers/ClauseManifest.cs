using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Verifiable.Analyzers;

/// <summary>One clause of the spec: the ID a person signed off, and optionally the sentence.</summary>
internal sealed class ManifestClause
{
    public ManifestClause(string id, string? text, int start, int length)
    {
        Id = id;
        Text = text;
        Start = start;
        Length = length;
    }

    /// <summary>The clause ID, for example <c>C1</c>.</summary>
    public string Id { get; }

    /// <summary>The spec sentence, if the manifest gives it.</summary>
    public string? Text { get; }

    /// <summary>Offset of the clause's entry in the manifest text.</summary>
    public int Start { get; }

    /// <summary>Length of the clause's entry in the manifest text.</summary>
    public int Length { get; }
}

/// <summary>A problem found while reading the manifest, with where it is in the text.</summary>
internal readonly struct ManifestError
{
    public ManifestError(string message, int start, int length)
    {
        Message = message;
        Start = start;
        Length = length;
    }

    public string Message { get; }

    public int Start { get; }

    public int Length { get; }
}

/// <summary>
/// The clause manifest, <c>verifiable.clauses.json</c>: <c>{ "clauses": [ { "id": "C1", "text": "..." } ] }</c>.
/// IDs must be non-empty, free of whitespace, and unique (compared ordinally). The analyzer and the
/// <c>verifiable-query clauses</c> command both read it here, so they agree on what a valid manifest is.
/// </summary>
internal sealed class ClauseManifest
{
    /// <summary>The file name that marks an <c>AdditionalFiles</c> item as the manifest (matched case-insensitively).</summary>
    public const string FileName = "verifiable.clauses.json";

    private ClauseManifest(List<ManifestClause> clauses, List<ManifestError> errors)
    {
        Clauses = clauses;
        Errors = errors;
    }

    /// <summary>The valid clauses, in file order.</summary>
    public IReadOnlyList<ManifestClause> Clauses { get; }

    /// <summary>Everything wrong with the file. When this is not empty the manifest must not be used.</summary>
    public IReadOnlyList<ManifestError> Errors { get; }

    /// <summary>True if <paramref name="path"/> names a clause manifest.</summary>
    public static bool IsManifestPath(string? path) =>
        path is not null &&
        string.Equals(System.IO.Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="id"/> is usable as a clause ID: not empty and without whitespace.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }
        foreach (var c in id!)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Reads the manifest from its text.</summary>
    public static ClauseManifest Parse(string text)
    {
        var clauses = new List<ManifestClause>();
        var errors = new List<ManifestError>();
        var parser = new JsonReader(text);
        var root = parser.ReadDocument(out var parseError);
        if (parseError is { } pe)
        {
            errors.Add(pe);
            return new ClauseManifest(clauses, errors);
        }

        if (root is not { Kind: JKind.Object } ||
            root.Find("clauses") is not { Kind: JKind.Array } list)
        {
            errors.Add(new ManifestError("The clause manifest must be a JSON object with a \"clauses\" array.", 0, Math.Min(text.Length, 1)));
            return new ClauseManifest(clauses, errors);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.Items)
        {
            var length = Math.Max(item.End - item.Start, 1);
            if (item.Kind != JKind.Object || item.Find("id") is not { Kind: JKind.String } idNode)
            {
                errors.Add(new ManifestError("Every clause needs an \"id\" string, for example { \"id\": \"C1\", \"text\": \"...\" }.", item.Start, length));
                continue;
            }

            var id = idNode.Text!;
            if (!IsValidId(id))
            {
                errors.Add(new ManifestError($"Clause ID \"{id}\" is not valid: an ID must not be empty or contain whitespace.", idNode.Start, Math.Max(idNode.End - idNode.Start, 1)));
                continue;
            }

            if (!seen.Add(id))
            {
                errors.Add(new ManifestError($"Clause ID \"{id}\" appears more than once in the manifest.", idNode.Start, Math.Max(idNode.End - idNode.Start, 1)));
                continue;
            }

            var clauseText = item.Find("text") is { Kind: JKind.String } t ? t.Text : null;
            clauses.Add(new ManifestClause(id, clauseText, item.Start, length));
        }

        return new ClauseManifest(clauses, errors);
    }

    private enum JKind { Object, Array, String, Other }

    private sealed class JNode
    {
        public JKind Kind;
        public string? Text;
        public int Start;
        public int End;
        public List<JNode> Items = new();
        public List<KeyValuePair<string, JNode>> Props = new();

        public JNode? Find(string name)
        {
            foreach (var p in Props)
            {
                if (string.Equals(p.Key, name, StringComparison.Ordinal))
                {
                    return p.Value;
                }
            }
            return null;
        }
    }

    private sealed class JsonReader
    {
        private readonly string _s;
        private int _i;

        public JsonReader(string s) => _s = s;

        public JNode? ReadDocument(out ManifestError? error)
        {
            try
            {
                SkipWs();
                var node = ReadValue();
                SkipWs();
                if (_i < _s.Length)
                {
                    throw new FormatException("Unexpected text after the JSON value.");
                }
                error = null;
                return node;
            }
            catch (FormatException ex)
            {
                error = new ManifestError("The clause manifest is not valid JSON: " + ex.Message, Math.Min(_i, Math.Max(_s.Length - 1, 0)), 1);
                return null;
            }
        }

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }

        private JNode ReadValue()
        {
            if (_i >= _s.Length)
            {
                throw new FormatException("The text ends early.");
            }

            var start = _i;
            var c = _s[_i];
            if (c == '{')
            {
                _i++;
                var obj = new JNode { Kind = JKind.Object, Start = start };
                SkipWs();
                if (Peek('}'))
                {
                    _i++;
                }
                else
                {
                    while (true)
                    {
                        SkipWs();
                        if (!Peek('"'))
                        {
                            throw new FormatException("Expected a property name.");
                        }
                        var key = ReadString();
                        SkipWs();
                        Expect(':');
                        SkipWs();
                        obj.Props.Add(new KeyValuePair<string, JNode>(key, ReadValue()));
                        SkipWs();
                        if (Peek(',')) { _i++; continue; }
                        Expect('}');
                        break;
                    }
                }
                obj.End = _i;
                return obj;
            }

            if (c == '[')
            {
                _i++;
                var arr = new JNode { Kind = JKind.Array, Start = start };
                SkipWs();
                if (Peek(']'))
                {
                    _i++;
                }
                else
                {
                    while (true)
                    {
                        SkipWs();
                        arr.Items.Add(ReadValue());
                        SkipWs();
                        if (Peek(',')) { _i++; continue; }
                        Expect(']');
                        break;
                    }
                }
                arr.End = _i;
                return arr;
            }

            if (c == '"')
            {
                var str = ReadString();
                return new JNode { Kind = JKind.String, Text = str, Start = start, End = _i };
            }

            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '-' or '+' or '.'))
            {
                _i++;
            }
            if (_i == start)
            {
                throw new FormatException($"Unexpected character '{c}'.");
            }
            return new JNode { Kind = JKind.Other, Start = start, End = _i };
        }

        private bool Peek(char c) => _i < _s.Length && _s[_i] == c;

        private void Expect(char c)
        {
            if (!Peek(c))
            {
                throw new FormatException($"Expected '{c}'.");
            }
            _i++;
        }

        private string ReadString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length)
                {
                    throw new FormatException("A string is not closed.");
                }
                var c = _s[_i++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (_i >= _s.Length)
                {
                    throw new FormatException("A string is not closed.");
                }
                var e = _s[_i++];
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
                        if (_i + 4 > _s.Length ||
                            !int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw new FormatException("A \\u escape needs four hex digits.");
                        }
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default:
                        throw new FormatException($"Unknown escape '\\{e}'.");
                }
            }
        }
    }
}
