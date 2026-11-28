using System;
using System.Text;

namespace Verifiable.Query;

internal enum ElmColorMode
{
    Auto = 0,
    Always = 1,
    Never = 2
}

/// <summary>
/// Applies Elm-style ANSI terminal colorization to rendered diagnostic blocks
/// (cyan chunk banners, red carets/gutter markers, yellow diffs/clauses, green suggestions,
/// bold section headings) while staying plain text when piped, in JSON mode, or under <c>NO_COLOR</c>.
/// </summary>
internal static class ElmAnsiRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Red = "\x1b[31m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Cyan = "\x1b[36m";

    public static ElmColorMode ParseColorMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ElmColorMode.Auto;
        return value!.Trim().ToLowerInvariant() switch
        {
            "always" or "on" or "true" or "1" => ElmColorMode.Always,
            "never" or "off" or "false" or "0" => ElmColorMode.Never,
            _ => ElmColorMode.Auto
        };
    }

    public static bool ShouldUseColor(ElmColorMode mode, string format, bool agentMode, bool isRedirected)
    {
        // Agents never get ANSI escapes, whatever --color says: they waste context and break parsers.
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase) || agentMode)
        {
            return false;
        }

        if (mode == ElmColorMode.Always) return true;
        if (mode == ElmColorMode.Never) return false;

        if (agentMode || isRedirected)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return false;
        }

        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static string Colorize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var sb = new StringBuilder(text.Length + lines.Length * 12);

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(ColorizeLine(lines[i]));
        }

        return sb.ToString();
    }

    private static string ColorizeLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        // Preserve machine-readable agent payload blocks untouched
        if (line.StartsWith("<verifiable-", StringComparison.Ordinal))
        {
            return line;
        }

        // Elm chunk header banner: -- TITLE ---------------- File.cs
        if (line.StartsWith("-- ", StringComparison.Ordinal) && line.Contains("----"))
        {
            return $"{Cyan}{Bold}{line}{Reset}";
        }

        // Caret underline line: "   |     ^^^^^^"
        var pipeIdx = line.IndexOf('|');
        if (pipeIdx >= 0)
        {
            var beforePipe = line.Substring(0, pipeIdx);
            if (IsWhitespaceOrDigits(beforePipe))
            {
                var afterPipe = line.Substring(pipeIdx + 1);

                // Multi-line highlight gutter marker: "12|> code"
                if (afterPipe.StartsWith("> ", StringComparison.Ordinal))
                {
                    return $"{beforePipe}|{Red}>{Reset}{afterPipe.Substring(1)}";
                }

                // Caret underline: "   |   ^^^^"
                var trimmedAfter = afterPipe.Trim();
                if (trimmedAfter.Length > 0 && IsAllCarets(trimmedAfter))
                {
                    var caretStart = line.IndexOf('^', pipeIdx + 1);
                    return $"{line.Substring(0, caretStart)}{Red}{line.Substring(caretStart)}{Reset}";
                }
            }
        }

        // Elm-style diff & failed clause highlights
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("Actual:", StringComparison.Ordinal) ||
            trimmed.StartsWith("Expected:", StringComparison.Ordinal) ||
            trimmed.StartsWith("Left ", StringComparison.Ordinal) ||
            trimmed.StartsWith("Right (", StringComparison.Ordinal) ||
            trimmed.StartsWith("Failed clause", StringComparison.Ordinal) ||
            trimmed.StartsWith("Type argument mismatch", StringComparison.Ordinal) ||
            trimmed.StartsWith("Tuple item #", StringComparison.Ordinal) ||
            trimmed.StartsWith("The `then` branch", StringComparison.Ordinal) ||
            trimmed.StartsWith("The `else` branch", StringComparison.Ordinal) ||
            trimmed.StartsWith("Arm ", StringComparison.Ordinal))
        {
            return $"{Yellow}{line}{Reset}";
        }

        // Typo & namespace suggestions
        if (trimmed.StartsWith("Did you mean", StringComparison.Ordinal))
        {
            return $"{Green}{line}{Reset}";
        }

        // Section labels
        if (line is "Why this rule exists:" or "Contract:" or "Counterexample:" or "Why it holds (from Z3 proof core):" or "Why this question matters:" ||
            line.StartsWith("Counterexample (", StringComparison.Ordinal))
        {
            return $"{Bold}{line}{Reset}";
        }

        if (line.StartsWith("Fix:", StringComparison.Ordinal))
        {
            return $"{Green}{Bold}Fix:{Reset}{line.Substring(4)}";
        }

        if (line.StartsWith("Note:", StringComparison.Ordinal))
        {
            return $"{Cyan}{Bold}Note:{Reset}{line.Substring(5)}";
        }

        if (line.StartsWith("Warning:", StringComparison.Ordinal))
        {
            return $"{Yellow}{Bold}Warning:{Reset}{line.Substring(8)}";
        }

        return line;
    }

    private static bool IsWhitespaceOrDigits(string s)
    {
        foreach (var ch in s)
        {
            if (ch != ' ' && !char.IsDigit(ch)) return false;
        }
        return true;
    }

    private static bool IsAllCarets(string s)
    {
        foreach (var ch in s)
        {
            if (ch != '^' && ch != ' ') return false;
        }
        return s.IndexOf('^') >= 0;
    }
}
