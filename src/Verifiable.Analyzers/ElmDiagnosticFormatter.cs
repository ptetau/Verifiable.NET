using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers;

internal static class ElmDiagnosticFormatter
{
    private const int DefaultWrapWidth = 78;
    private const int MaxSnippetLines = 6;

    public static string Format(
        string title,
        Location location,
        string intro,
        string? spirit = null,
        string? contract = null,
        string? counterexample = null,
        string? explanation = null,
        string? fixHint = null,
        string? agentPayload = null,
        string? summary = null,
        Location? relatedLocation = null,
        string? relatedHeading = null,
        Location? contextLocation = null)
    {
        var sb = new StringBuilder();
        var fileLabel = ExtractFileLabel(location);
        var sourceExcerpt = RenderExcerpt(location, contextLocation);

        sb.AppendLine(ToSingleLine(string.IsNullOrWhiteSpace(summary) ? intro : summary!));
        sb.Append(BuildHeader(title, fileLabel));
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(Reflow(intro.Trim()));

        if (sourceExcerpt is not null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(sourceExcerpt);
        }

        if (relatedLocation is { } relLoc &&
            relLoc != Location.None &&
            relLoc.IsInSource &&
            !IsSameSpan(location, relLoc))
        {
            var relExcerpt = RenderExcerpt(relLoc, outerContext: null);
            if (relExcerpt is not null)
            {
                var heading = FormatRelatedHeading(relatedHeading, relLoc);
                sb.AppendLine();
                sb.AppendLine();
                sb.Append(Reflow(heading));
                sb.AppendLine();
                sb.AppendLine();
                sb.Append(relExcerpt);
            }
        }

        if (!string.IsNullOrWhiteSpace(spirit))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Why this rule exists:");
            sb.AppendLine();
            sb.Append(ReflowIndented(spirit!.Trim(), "    "));
        }

        if (!string.IsNullOrWhiteSpace(contract))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Contract:");
            sb.AppendLine();
            sb.Append(IndentPreservingLines(contract!.Trim(), "    "));
        }

        if (!string.IsNullOrWhiteSpace(counterexample))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Counterexample:");
            sb.AppendLine();
            sb.Append(IndentPreservingLines(counterexample!.Trim(), "    "));
        }

        if (!string.IsNullOrWhiteSpace(explanation))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(Reflow(explanation!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(fixHint))
        {
            sb.AppendLine();
            sb.AppendLine();
            var trimmedFix = fixHint!.Trim();
            var prefixedFix = trimmedFix.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase)
                ? trimmedFix
                : $"Fix: {trimmedFix}";
            sb.Append(Reflow(prefixedFix));
        }

        if (!string.IsNullOrWhiteSpace(agentPayload))
        {
            sb.AppendLine();
            sb.Append(agentPayload!.Trim());
        }

        return sb.ToString();
    }

    private static string ToSingleLine(string text) =>
        text.Trim().Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Renders an Elm-style code snippet for <paramref name="location"/>, optionally framed
    /// inside an enclosing <paramref name="outerContext"/> region. Single-line spans get
    /// <c>^^^</c> carets; multi-line spans get Elm-style <c>&gt;</c> gutter markers.
    /// </summary>
    internal static string? RenderExcerpt(Location location, Location? outerContext = null)
    {
        if (location == Location.None || !location.IsInSource)
        {
            return null;
        }

        var text = location.SourceTree?.GetText();
        if (text is null || text.Lines.Count == 0)
        {
            return null;
        }

        var span = location.GetMappedLineSpan();
        var startLine = Math.Max(0, Math.Min(span.StartLinePosition.Line, text.Lines.Count - 1));
        var endLine = Math.Max(startLine, Math.Min(span.EndLinePosition.Line, text.Lines.Count - 1));

        // Trim trailing empty end line if span ended cleanly at column 0 of the next line
        if (endLine > startLine && span.EndLinePosition.Character == 0)
        {
            endLine--;
        }

        var displayStart = startLine;
        var displayEnd = endLine;

        // If an outer context is provided in the same tree, frame the inner highlight inside it (up to MaxSnippetLines)
        if (outerContext is { } ctx &&
            ctx != Location.None &&
            ctx.IsInSource &&
            ReferenceEquals(ctx.SourceTree, location.SourceTree))
        {
            var ctxSpan = ctx.GetMappedLineSpan();
            var ctxStart = Math.Max(0, Math.Min(ctxSpan.StartLinePosition.Line, text.Lines.Count - 1));
            var ctxEnd = Math.Max(ctxStart, Math.Min(ctxSpan.EndLinePosition.Line, text.Lines.Count - 1));
            if (ctxEnd > ctxStart && ctxSpan.EndLinePosition.Character == 0)
            {
                ctxEnd--;
            }

            if (ctxStart <= startLine && ctxEnd >= endLine && (ctxEnd - ctxStart + 1) <= MaxSnippetLines)
            {
                displayStart = ctxStart;
                displayEnd = ctxEnd;
            }
        }

        var numWidth = (displayEnd + 1).ToString().Length;
        var gutter = new string(' ', numWidth);
        var excerpt = new StringBuilder();
        excerpt.AppendLine($"{gutter} |");

        // Case 1: Single-line highlight (with optional surrounding outer context lines)
        if (startLine == endLine)
        {
            for (var lineIdx = displayStart; lineIdx <= displayEnd; lineIdx++)
            {
                var lineText = text.Lines[lineIdx].ToString();
                var lineNum = (lineIdx + 1).ToString().PadLeft(numWidth);
                excerpt.AppendLine($"{lineNum} | {lineText}");

                if (lineIdx == startLine)
                {
                    var startCol = Math.Max(0, Math.Min(span.StartLinePosition.Character, lineText.Length));
                    var endCol = Math.Max(startCol + 1, Math.Min(span.EndLinePosition.Character, lineText.Length));
                    var caretLen = Math.Max(1, endCol - startCol);
                    var indent = BuildIndent(lineText, startCol);
                    excerpt.Append($"{gutter} | {indent}{new string('^', caretLen)}");
                    if (lineIdx < displayEnd)
                    {
                        excerpt.AppendLine();
                    }
                }
            }

            return excerpt.ToString();
        }

        // Case 2: Multi-line span — render with Elm-style '>' gutter markers (and ellipsis for very tall blocks)
        var totalLines = displayEnd - displayStart + 1;
        if (totalLines <= MaxSnippetLines)
        {
            for (var lineIdx = displayStart; lineIdx <= displayEnd; lineIdx++)
            {
                AppendMarkedLine(excerpt, text.Lines[lineIdx].ToString(), lineIdx, numWidth, lineIdx >= startLine && lineIdx <= endLine, isLast: lineIdx == displayEnd);
            }
        }
        else
        {
            for (var lineIdx = displayStart; lineIdx < displayStart + 3; lineIdx++)
            {
                AppendMarkedLine(excerpt, text.Lines[lineIdx].ToString(), lineIdx, numWidth, lineIdx >= startLine && lineIdx <= endLine, isLast: false);
            }

            excerpt.AppendLine($"{gutter} | ... ({totalLines - 5} more lines)");

            for (var lineIdx = displayEnd - 1; lineIdx <= displayEnd; lineIdx++)
            {
                AppendMarkedLine(excerpt, text.Lines[lineIdx].ToString(), lineIdx, numWidth, lineIdx >= startLine && lineIdx <= endLine, isLast: lineIdx == displayEnd);
            }
        }

        return excerpt.ToString();
    }

    private static void AppendMarkedLine(
        StringBuilder sb,
        string lineText,
        int lineIdx,
        int numWidth,
        bool isHighlighted,
        bool isLast)
    {
        var lineNum = (lineIdx + 1).ToString().PadLeft(numWidth);
        var marker = isHighlighted ? "|> " : " | ";
        if (isLast)
        {
            sb.Append($"{lineNum}{marker}{lineText}");
        }
        else
        {
            sb.AppendLine($"{lineNum}{marker}{lineText}");
        }
    }

    /// <summary>
    /// Reflows prose to <paramref name="maxWidth"/> columns while preserving explicit blank lines,
    /// indented blocks (e.g. bullet/code lines), and backtick-delimited inline code spans.
    /// </summary>
    internal static string Reflow(string text, int maxWidth = DefaultWrapWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalized.Split('\n');
        var output = new List<string>(rawLines.Length);

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                output.Add(string.Empty);
                continue;
            }

            // Preserve already-indented lines (code blocks, structured key-value lines, suggestions)
            if (rawLine.Length >= 2 && (rawLine[0] == ' ' || rawLine[0] == '\t'))
            {
                output.Add(rawLine);
                continue;
            }

            if (rawLine.Length <= maxWidth)
            {
                output.Add(rawLine);
                continue;
            }

            WrapLine(rawLine, string.Empty, maxWidth, output);
        }

        return string.Join("\n", output);
    }

    internal static string ReflowIndented(string text, string indent, int maxWidth = DefaultWrapWidth)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalized.Split('\n');
        var output = new List<string>(rawLines.Length);
        var effectiveWidth = Math.Max(32, maxWidth - indent.Length);

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                output.Add(string.Empty);
                continue;
            }

            var trimmed = rawLine.Trim();
            if (trimmed.Length <= effectiveWidth)
            {
                output.Add(indent + trimmed);
            }
            else
            {
                WrapLine(trimmed, indent, maxWidth, output);
            }
        }

        return string.Join("\n", output);
    }

    private static string IndentPreservingLines(string text, string indent)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var sb = new StringBuilder(normalized.Length + lines.Length * indent.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.AppendLine();
            var line = lines[i];
            if (line.StartsWith(indent, StringComparison.Ordinal))
            {
                sb.Append(line);
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                sb.Append(indent).Append(line);
            }
        }
        return sb.ToString();
    }

    private static void WrapLine(string line, string indent, int maxWidth, List<string> output)
    {
        var tokens = TokenizePreservingBackticks(line);
        var current = new StringBuilder(indent);
        var hasContent = false;

        foreach (var token in tokens)
        {
            if (!hasContent)
            {
                current.Append(token);
                hasContent = true;
            }
            else if (current.Length + 1 + token.Length <= maxWidth)
            {
                current.Append(' ').Append(token);
            }
            else
            {
                output.Add(current.ToString());
                current.Clear();
                current.Append(indent).Append(token);
            }
        }

        if (hasContent)
        {
            output.Add(current.ToString());
        }
    }

    private static List<string> TokenizePreservingBackticks(string line)
    {
        var tokens = new List<string>();
        var i = 0;
        var len = line.Length;
        while (i < len)
        {
            while (i < len && char.IsWhiteSpace(line[i])) i++;
            if (i >= len) break;

            var start = i;
            var inBacktick = false;
            while (i < len)
            {
                var ch = line[i];
                if (ch == '`')
                {
                    inBacktick = !inBacktick;
                    i++;
                }
                else if (!inBacktick && char.IsWhiteSpace(ch))
                {
                    break;
                }
                else
                {
                    i++;
                }
            }

            tokens.Add(line.Substring(start, i - start));
        }

        return tokens;
    }

    private static string BuildIndent(string lineText, int startCol)
    {
        var indent = new StringBuilder(startCol);
        for (var i = 0; i < startCol; i++)
        {
            indent.Append(lineText[i] == '\t' ? '\t' : ' ');
        }
        return indent.ToString();
    }

    private static string FormatRelatedHeading(string? relatedHeading, Location relLoc)
    {
        var baseHeading = string.IsNullOrWhiteSpace(relatedHeading)
            ? "Related declaration:"
            : relatedHeading!.Trim();

        var span = relLoc.GetMappedLineSpan();
        var file = ExtractFileLabel(relLoc);
        var line = span.StartLinePosition.Line + 1;

        if (!string.IsNullOrEmpty(file) && !baseHeading.Contains(file))
        {
            if (baseHeading.EndsWith(":", StringComparison.Ordinal))
            {
                return $"{baseHeading.Substring(0, baseHeading.Length - 1)} ({file}:{line}):";
            }

            return $"{baseHeading} ({file}:{line}):";
        }

        return baseHeading;
    }

    private static bool IsSameSpan(Location a, Location b)
    {
        if (a == Location.None || b == Location.None) return false;
        return ReferenceEquals(a.SourceTree, b.SourceTree) && a.SourceSpan == b.SourceSpan;
    }

    private static string ExtractFileLabel(Location location)
    {
        if (location == Location.None || !location.IsInSource)
        {
            return string.Empty;
        }

        var span = location.GetMappedLineSpan();
        var path = span.Path ?? location.SourceTree?.FilePath ?? string.Empty;
        return ExtractFileName(path);
    }

    internal static string BuildHeader(string title, string fileLabel)
    {
        var prefix = $"-- {title.Trim()} ";
        var suffix = string.IsNullOrEmpty(fileLabel) ? string.Empty : $" {fileLabel}";
        var dashCount = Math.Max(4, 72 - prefix.Length - suffix.Length);
        return $"{prefix}{new string('-', dashCount)}{suffix}";
    }

    private static string ExtractFileName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var idx = path.LastIndexOfAny(['/', '\\']);
        return idx >= 0 && idx + 1 < path.Length ? path.Substring(idx + 1) : path;
    }
}
