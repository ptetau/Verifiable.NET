using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Verifiable.Generators;

internal static class ElmGeneratorFormatter
{
    private const int DefaultWrapWidth = 78;
    private const int MaxSnippetLines = 6;

    public static string Format(
        string title,
        Location location,
        string intro,
        string? explanation,
        string fixHint,
        string? summary = null,
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

        if (!string.IsNullOrWhiteSpace(explanation))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(Reflow(explanation!.Trim()));
        }

        sb.AppendLine();
        sb.AppendLine();
        var trimmedFix = fixHint.Trim();
        var prefixed = trimmedFix.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase)
            ? trimmedFix
            : $"Fix: {trimmedFix}";
        sb.Append(Reflow(prefixed));
        return sb.ToString();
    }

    private static string ToSingleLine(string text) =>
        text.Trim().Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    private static string? RenderExcerpt(Location location, Location? outerContext)
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
        if (endLine > startLine && span.EndLinePosition.Character == 0)
        {
            endLine--;
        }

        var displayStart = startLine;
        var displayEnd = endLine;

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
                    var indent = new StringBuilder(startCol);
                    for (var i = 0; i < startCol; i++)
                    {
                        indent.Append(lineText[i] == '\t' ? '\t' : ' ');
                    }
                    excerpt.Append($"{gutter} | {indent}{new string('^', caretLen)}");
                    if (lineIdx < displayEnd)
                    {
                        excerpt.AppendLine();
                    }
                }
            }

            return excerpt.ToString();
        }

        var totalLines = displayEnd - displayStart + 1;
        if (totalLines <= MaxSnippetLines)
        {
            for (var lineIdx = displayStart; lineIdx <= displayEnd; lineIdx++)
            {
                AppendMarkedLine(excerpt, text.Lines[lineIdx].ToString(), lineIdx, numWidth, lineIdx >= startLine && lineIdx <= endLine, lineIdx == displayEnd);
            }
        }
        else
        {
            for (var lineIdx = displayStart; lineIdx < displayStart + 3; lineIdx++)
            {
                AppendMarkedLine(excerpt, text.Lines[lineIdx].ToString(), lineIdx, numWidth, lineIdx >= startLine && lineIdx <= endLine, false);
            }
            excerpt.AppendLine($"{gutter} | ... ({totalLines - 5} more lines)");
            for (var lineIdx = displayEnd - 1; lineIdx <= displayEnd; lineIdx++)
            {
                AppendMarkedLine(excerpt, text.Lines[lineIdx].ToString(), lineIdx, numWidth, lineIdx >= startLine && lineIdx <= endLine, lineIdx == displayEnd);
            }
        }

        return excerpt.ToString();
    }

    private static void AppendMarkedLine(StringBuilder sb, string lineText, int lineIdx, int numWidth, bool highlighted, bool isLast)
    {
        var lineNum = (lineIdx + 1).ToString().PadLeft(numWidth);
        var marker = highlighted ? "|> " : " | ";
        if (isLast) sb.Append($"{lineNum}{marker}{lineText}");
        else sb.AppendLine($"{lineNum}{marker}{lineText}");
    }

    private static string Reflow(string text, int maxWidth = DefaultWrapWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalized.Split('\n');
        var output = new List<string>(rawLines.Length);

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine) ||
                (rawLine.Length >= 2 && (rawLine[0] == ' ' || rawLine[0] == '\t')) ||
                rawLine.Length <= maxWidth)
            {
                output.Add(rawLine);
                continue;
            }

            var tokens = TokenizePreservingBackticks(rawLine);
            var current = new StringBuilder();
            foreach (var token in tokens)
            {
                if (current.Length == 0)
                {
                    current.Append(token);
                }
                else if (current.Length + 1 + token.Length <= maxWidth)
                {
                    current.Append(' ').Append(token);
                }
                else
                {
                    output.Add(current.ToString());
                    current.Clear();
                    current.Append(token);
                }
            }
            if (current.Length > 0) output.Add(current.ToString());
        }

        return string.Join("\n", output);
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
                if (ch == '`') { inBacktick = !inBacktick; i++; }
                else if (!inBacktick && char.IsWhiteSpace(ch)) break;
                else i++;
            }
            tokens.Add(line.Substring(start, i - start));
        }
        return tokens;
    }

    private static string ExtractFileLabel(Location location)
    {
        if (location == Location.None || !location.IsInSource) return string.Empty;
        var span = location.GetMappedLineSpan();
        var path = span.Path ?? location.SourceTree?.FilePath ?? string.Empty;
        return ExtractFileName(path);
    }

    private static string BuildHeader(string title, string fileLabel)
    {
        var prefix = $"-- {title.Trim()} ";
        var suffix = string.IsNullOrEmpty(fileLabel) ? string.Empty : $" {fileLabel}";
        var dashCount = Math.Max(4, 72 - prefix.Length - suffix.Length);
        return $"{prefix}{new string('-', dashCount)}{suffix}";
    }

    private static string ExtractFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var idx = path.LastIndexOfAny(['/', '\\']);
        return idx >= 0 && idx + 1 < path.Length ? path.Substring(idx + 1) : path;
    }
}
