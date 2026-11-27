using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Verifiable.Analyzers.Smt;

internal static class RegexModel
{
    private const int MaxQuantifierCount = 256;

    // System.Text.RegularExpressions.RegexOptions bit flags
    public const int OptNone = 0;
    public const int OptIgnoreCase = 1;
    public const int OptMultiline = 2;
    public const int OptExplicitCapture = 4;
    public const int OptCompiled = 8;
    public const int OptSingleline = 16;
    public const int OptIgnorePatternWhitespace = 32;
    public const int OptRightToLeft = 64;
    public const int OptECMAScript = 256;
    public const int OptCultureInvariant = 512;
    public const int OptNonBacktracking = 1024;

    private const int SupportedOptionsMask =
        OptExplicitCapture | OptCompiled | OptSingleline | OptECMAScript | OptCultureInvariant | OptNonBacktracking;

    public const string WhitespaceCharRegex =
        "(re.union " +
        "(re.range \"\\u{9}\" \"\\u{d}\") " +
        "(str.to_re \" \") " +
        "(str.to_re \"\\u{85}\") " +
        "(str.to_re \"\\u{a0}\") " +
        "(str.to_re \"\\u{1680}\") " +
        "(re.range \"\\u{2000}\" \"\\u{200a}\") " +
        "(str.to_re \"\\u{2028}\") " +
        "(str.to_re \"\\u{2029}\") " +
        "(str.to_re \"\\u{202f}\") " +
        "(str.to_re \"\\u{205f}\") " +
        "(str.to_re \"\\u{3000}\"))";

    public const string AsciiWhitespaceCharRegex =
        "(re.union (re.range \"\\u{9}\" \"\\u{d}\") (str.to_re \" \"))";

    public static bool TryTranslate(string pattern, int options, out string smtRegex, out string? error)
    {
        smtRegex = string.Empty;
        error = null;

        if ((options & ~SupportedOptionsMask) != 0)
        {
            error = $"Unsupported RegexOptions flags (0x{options:X}); supported flags are None, Compiled, CultureInvariant, ExplicitCapture, Singleline, ECMAScript, and NonBacktracking.";
            return false;
        }

        try
        {
            var parser = new Parser(pattern,
                singleline: (options & OptSingleline) != 0,
                ecmaScript: (options & OptECMAScript) != 0);
            smtRegex = parser.ParseTopLevel();
            return true;
        }
        catch (RegexTranslationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed class RegexTranslationException(string message) : Exception(message);

    private sealed class Parser
    {
        private readonly string _p;
        private readonly bool _singleline;
        private readonly bool _ecmaScript;
        private int _pos;

        public Parser(string pattern, bool singleline, bool ecmaScript)
        {
            _p = pattern;
            _singleline = singleline;
            _ecmaScript = ecmaScript;
        }

        public string ParseTopLevel()
        {
            var branches = new List<string>();
            while (true)
            {
                branches.Add(ParseTopLevelBranch());
                if (_pos < _p.Length && _p[_pos] == '|')
                {
                    _pos++;
                    continue;
                }
                break;
            }

            if (_pos != _p.Length)
            {
                throw new RegexTranslationException($"Unexpected character '{_p[_pos]}' at position {_pos} in regex '{_p}'.");
            }

            return CombineUnion(branches);
        }

        private string ParseTopLevelBranch()
        {
            var startAnchored = false;
            var endAnchoredExact = false;
            var endAnchoredDollar = false;

            // Consume leading start anchors (^ or \A)
            while (_pos < _p.Length)
            {
                if (_p[_pos] == '^')
                {
                    startAnchored = true;
                    _pos++;
                }
                else if (_pos + 1 < _p.Length && _p[_pos] == '\\' && _p[_pos + 1] == 'A')
                {
                    startAnchored = true;
                    _pos += 2;
                }
                else
                {
                    break;
                }
            }

            var atoms = new List<string>();
            while (_pos < _p.Length && _p[_pos] != '|' && _p[_pos] != ')')
            {
                if (_p[_pos] == '$')
                {
                    endAnchoredDollar = true;
                    _pos++;
                    CheckOnlyTrailingAnchors(ref endAnchoredExact, ref endAnchoredDollar);
                    break;
                }

                if (_pos + 1 < _p.Length && _p[_pos] == '\\' && _p[_pos + 1] == 'z')
                {
                    endAnchoredExact = true;
                    _pos += 2;
                    CheckOnlyTrailingAnchors(ref endAnchoredExact, ref endAnchoredDollar);
                    break;
                }

                if (_pos + 1 < _p.Length && _p[_pos] == '\\' && _p[_pos + 1] == 'Z')
                {
                    endAnchoredDollar = true;
                    _pos += 2;
                    CheckOnlyTrailingAnchors(ref endAnchoredExact, ref endAnchoredDollar);
                    break;
                }

                atoms.Add(ParseQuantifiedAtom());
            }

            var parts = new List<string>();
            if (!startAnchored)
            {
                parts.Add("re.all");
            }

            parts.AddRange(atoms);

            if (endAnchoredExact)
            {
                // Exact end of string: no trailing re.all and no optional \n.
            }
            else if (endAnchoredDollar)
            {
                // In .NET (without Multiline), $ and \Z match end-of-string or before a single trailing '\n'.
                parts.Add("(re.opt (str.to_re \"\\u{a}\"))");
            }
            else
            {
                parts.Add("re.all");
            }

            return CombineConcat(parts);
        }

        private void CheckOnlyTrailingAnchors(ref bool endExact, ref bool endDollar)
        {
            while (_pos < _p.Length && _p[_pos] != '|' && _p[_pos] != ')')
            {
                if (_p[_pos] == '$')
                {
                    endDollar = true;
                    _pos++;
                }
                else if (_pos + 1 < _p.Length && _p[_pos] == '\\' && _p[_pos + 1] == 'z')
                {
                    endExact = true;
                    _pos += 2;
                }
                else if (_pos + 1 < _p.Length && _p[_pos] == '\\' && _p[_pos + 1] == 'Z')
                {
                    endDollar = true;
                    _pos += 2;
                }
                else
                {
                    throw new RegexTranslationException($"Mid-pattern end anchor at position {_pos} is not supported in regex '{_p}'.");
                }
            }
        }

        private string ParseAlternationInsideGroup()
        {
            var branches = new List<string>();
            while (true)
            {
                var parts = new List<string>();
                while (_pos < _p.Length && _p[_pos] != '|' && _p[_pos] != ')')
                {
                    parts.Add(ParseQuantifiedAtom());
                }
                branches.Add(CombineConcat(parts));

                if (_pos < _p.Length && _p[_pos] == '|')
                {
                    _pos++;
                    continue;
                }
                break;
            }
            return CombineUnion(branches);
        }

        private string ParseQuantifiedAtom()
        {
            var atom = ParseAtom();
            if (_pos >= _p.Length)
            {
                return atom;
            }

            string result;
            switch (_p[_pos])
            {
                case '*':
                    _pos++;
                    ConsumeOptionalLazyOrCheckPossessive();
                    result = $"(re.* {atom})";
                    break;
                case '+':
                    _pos++;
                    ConsumeOptionalLazyOrCheckPossessive();
                    result = $"(re.+ {atom})";
                    break;
                case '?':
                    _pos++;
                    ConsumeOptionalLazyOrCheckPossessive();
                    result = $"(re.opt {atom})";
                    break;
                case '{':
                    if (TryParseBraceQuantifier(out var min, out var max))
                    {
                        ConsumeOptionalLazyOrCheckPossessive();
                        result = ApplyCountQuantifier(atom, min, max);
                    }
                    else
                    {
                        result = atom;
                    }
                    break;
                default:
                    return atom;
            }

            // Reject stacked quantifiers like a** or a+?+
            if (_pos < _p.Length && _p[_pos] is '*' or '+' or '?')
            {
                throw new RegexTranslationException($"Nested quantifier at position {_pos} is not supported in regex '{_p}'.");
            }

            return result;
        }

        private void ConsumeOptionalLazyOrCheckPossessive()
        {
            if (_pos < _p.Length && _p[_pos] == '?')
            {
                // Lazy quantifier accepts the exact same language for IsMatch.
                _pos++;
            }
            else if (_pos < _p.Length && _p[_pos] == '+')
            {
                throw new RegexTranslationException($"Possessive quantifier at position {_pos} is not supported in regex '{_p}'.");
            }
        }

        private bool TryParseBraceQuantifier(out int min, out int? max)
        {
            min = 0;
            max = null;
            var save = _pos;
            if (_p[_pos] != '{') return false;
            _pos++;

            var minStart = _pos;
            while (_pos < _p.Length && char.IsDigit(_p[_pos])) _pos++;
            if (_pos == minStart)
            {
                _pos = save;
                return false;
            }

            if (!int.TryParse(_p.Substring(minStart, _pos - minStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out min))
            {
                throw new RegexTranslationException($"Quantifier bound overflow in regex '{_p}'.");
            }

            if (_pos < _p.Length && _p[_pos] == '}')
            {
                _pos++;
                max = min;
                ValidateQuantifierBounds(min, max);
                return true;
            }

            if (_pos < _p.Length && _p[_pos] == ',')
            {
                _pos++;
                if (_pos < _p.Length && _p[_pos] == '}')
                {
                    _pos++;
                    max = null; // {min,}
                    ValidateQuantifierBounds(min, max);
                    return true;
                }

                var maxStart = _pos;
                while (_pos < _p.Length && char.IsDigit(_p[_pos])) _pos++;
                if (_pos > maxStart && _pos < _p.Length && _p[_pos] == '}')
                {
                    if (!int.TryParse(_p.Substring(maxStart, _pos - maxStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxVal))
                    {
                        throw new RegexTranslationException($"Quantifier bound overflow in regex '{_p}'.");
                    }
                    _pos++;
                    max = maxVal;
                    ValidateQuantifierBounds(min, max);
                    return true;
                }
            }

            _pos = save;
            return false;
        }

        private void ValidateQuantifierBounds(int min, int? max)
        {
            if (min < 0 || min > MaxQuantifierCount)
            {
                throw new RegexTranslationException($"Quantifier lower bound {min} exceeds limit {MaxQuantifierCount} in regex '{_p}'.");
            }

            if (max.HasValue && (max.Value < min || max.Value > MaxQuantifierCount))
            {
                throw new RegexTranslationException($"Quantifier upper bound {max.Value} is invalid or exceeds limit {MaxQuantifierCount} in regex '{_p}'.");
            }
        }

        private static string ApplyCountQuantifier(string atom, int min, int? max)
        {
            if (max.HasValue)
            {
                if (min == max.Value)
                {
                    return min == 0 ? "(str.to_re \"\")" : min == 1 ? atom : $"((_ re.^ {min}) {atom})";
                }
                return $"((_ re.loop {min} {max.Value}) {atom})";
            }

            if (min == 0) return $"(re.* {atom})";
            if (min == 1) return $"(re.+ {atom})";
            return $"(re.++ ((_ re.^ {min}) {atom}) (re.* {atom}))";
        }

        private string ParseAtom()
        {
            var ch = _p[_pos];
            switch (ch)
            {
                case '^':
                case '$':
                    throw new RegexTranslationException($"Anchor '{ch}' at position {_pos} is only supported at top-level branch boundaries.");
                case '*':
                case '+':
                case '?':
                    throw new RegexTranslationException($"Unexpected quantifier '{ch}' at position {_pos} in regex '{_p}'.");
                case '.':
                    _pos++;
                    return _singleline ? "re.allchar" : "(re.diff re.allchar (str.to_re \"\\u{a}\"))";
                case '(':
                    return ParseGroup();
                case '[':
                    return ParseCharClass();
                case '\\':
                    return ParseEscapeAtom();
                default:
                    _pos++;
                    return SingleCharRe(ch);
            }
        }

        private string ParseGroup()
        {
            _pos++; // consume '('
            if (_pos < _p.Length && _p[_pos] == '?')
            {
                if (_pos + 1 < _p.Length && _p[_pos + 1] == ':')
                {
                    _pos += 2; // non-capturing (?:...)
                }
                else
                {
                    throw new RegexTranslationException($"Unsupported group construct '(?...' at position {_pos - 1} in regex '{_p}'.");
                }
            }

            var inner = ParseAlternationInsideGroup();
            if (_pos >= _p.Length || _p[_pos] != ')')
            {
                throw new RegexTranslationException($"Unclosed '(' in regex '{_p}'.");
            }
            _pos++; // consume ')'
            return inner;
        }

        private string ParseCharClass()
        {
            _pos++; // consume '['
            var negated = false;
            if (_pos < _p.Length && _p[_pos] == '^')
            {
                negated = true;
                _pos++;
            }

            var items = new List<string>();
            var first = true;
            while (_pos < _p.Length)
            {
                if (_p[_pos] == ']' && !first)
                {
                    _pos++;
                    var union = CombineUnion(items);
                    return negated ? $"(re.diff re.allchar {union})" : union;
                }
                first = false;

                if (_p[_pos] == '-' && _pos + 1 < _p.Length && _p[_pos + 1] == '[')
                {
                    throw new RegexTranslationException($"Character class subtraction at position {_pos} is not supported.");
                }

                if (TryParseClassElement(out var singleChar, out var classRe))
                {
                    if (singleChar.HasValue && _pos + 1 < _p.Length && _p[_pos] == '-' && _p[_pos + 1] != ']')
                    {
                        _pos++; // consume '-'
                        if (!TryParseClassElement(out var endChar, out _) || !endChar.HasValue)
                        {
                            throw new RegexTranslationException($"Invalid character range at position {_pos} in regex '{_p}'.");
                        }
                        if (endChar.Value < singleChar.Value)
                        {
                            throw new RegexTranslationException($"Reversed character range '{singleChar.Value}-{endChar.Value}' in regex '{_p}'.");
                        }
                        items.Add($"(re.range {ContractExpressionTranslator.EscapeSmtString(singleChar.Value.ToString())} {ContractExpressionTranslator.EscapeSmtString(endChar.Value.ToString())})");
                    }
                    else
                    {
                        items.Add(singleChar.HasValue ? SingleCharRe(singleChar.Value) : classRe!);
                    }
                }
            }

            throw new RegexTranslationException($"Unclosed '[' in regex '{_p}'.");
        }

        private bool TryParseClassElement(out char? singleChar, out string? classRe)
        {
            singleChar = null;
            classRe = null;
            if (_p[_pos] != '\\')
            {
                singleChar = _p[_pos++];
                return true;
            }

            _pos++; // consume '\'
            if (_pos >= _p.Length)
            {
                throw new RegexTranslationException($"Trailing backslash in regex '{_p}'.");
            }

            var esc = _p[_pos++];
            switch (esc)
            {
                case 'd': classRe = DigitClass(); return true;
                case 'D': classRe = $"(re.diff re.allchar {DigitClass()})"; return true;
                case 'w': classRe = WordClass(); return true;
                case 'W': classRe = $"(re.diff re.allchar {WordClass()})"; return true;
                case 's': classRe = SpaceClass(); return true;
                case 'S': classRe = $"(re.diff re.allchar {SpaceClass()})"; return true;
                default:
                    singleChar = ParseCharEscapeAfterBackslash(esc);
                    return true;
            }
        }

        private string ParseEscapeAtom()
        {
            _pos++; // consume '\'
            if (_pos >= _p.Length)
            {
                throw new RegexTranslationException($"Trailing backslash in regex '{_p}'.");
            }

            var esc = _p[_pos++];
            switch (esc)
            {
                case 'd': return DigitClass();
                case 'D': return $"(re.diff re.allchar {DigitClass()})";
                case 'w': return WordClass();
                case 'W': return $"(re.diff re.allchar {WordClass()})";
                case 's': return SpaceClass();
                case 'S': return $"(re.diff re.allchar {SpaceClass()})";
                case 'A':
                case 'Z':
                case 'z':
                case 'b':
                case 'B':
                case 'G':
                case 'p':
                case 'P':
                case 'k':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    throw new RegexTranslationException($"Regex escape '\\{esc}' at position {_pos - 2} is not supported.");
                default:
                    return SingleCharRe(ParseCharEscapeAfterBackslash(esc));
            }
        }

        private char ParseCharEscapeAfterBackslash(char esc)
        {
            switch (esc)
            {
                case 't': return '\t';
                case 'r': return '\r';
                case 'n': return '\n';
                case 'f': return '\f';
                case 'v': return '\v';
                case 'a': return '\a';
                case 'e': return '\u001B';
                case '0': return '\0';
                case 'x':
                    return ParseHexChar(2);
                case 'u':
                    return ParseHexChar(4);
                default:
                    if (char.IsLetterOrDigit(esc))
                    {
                        throw new RegexTranslationException($"Unsupported escape '\\{esc}' in regex '{_p}'.");
                    }
                    return esc;
            }
        }

        private char ParseHexChar(int digits)
        {
            if (_pos + digits > _p.Length)
            {
                throw new RegexTranslationException($"Incomplete hex escape in regex '{_p}'.");
            }

            var hex = _p.Substring(_pos, digits);
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                throw new RegexTranslationException($"Invalid hex escape '{hex}' in regex '{_p}'.");
            }
            _pos += digits;
            return (char)code;
        }

        private string DigitClass() =>
            "(re.range \"0\" \"9\")";

        private string WordClass() =>
            "(re.union (re.range \"0\" \"9\") (re.range \"A\" \"Z\") (re.range \"a\" \"z\") (str.to_re \"_\"))";

        private string SpaceClass() =>
            _ecmaScript ? AsciiWhitespaceCharRegex : WhitespaceCharRegex;

        private static string SingleCharRe(char ch) =>
            $"(str.to_re {ContractExpressionTranslator.EscapeSmtString(ch.ToString())})";

        private static string CombineConcat(List<string> parts) => parts.Count switch
        {
            0 => "(str.to_re \"\")",
            1 => parts[0],
            _ => $"(re.++ {string.Join(" ", parts)})"
        };

        private static string CombineUnion(List<string> branches) => branches.Count switch
        {
            0 => "re.none",
            1 => branches[0],
            _ => $"(re.union {string.Join(" ", branches)})"
        };
    }
}
