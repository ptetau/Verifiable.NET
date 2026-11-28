using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Verifiable.Analyzers;

/// <summary>A small strict JSON reader; analyzers cannot take a JSON library dependency.</summary>
internal sealed class MiniJson(string text)
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
