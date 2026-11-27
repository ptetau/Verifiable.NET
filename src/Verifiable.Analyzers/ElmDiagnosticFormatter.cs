using System;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers;

internal static class ElmDiagnosticFormatter
{
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
        string? summary = null)
    {
        var sb = new StringBuilder();
        var fileLabel = string.Empty;
        string? sourceExcerpt = null;

        if (location != Location.None && location.IsInSource)
        {
            var span = location.GetMappedLineSpan();
            var path = span.Path ?? location.SourceTree?.FilePath ?? string.Empty;
            fileLabel = ExtractFileName(path);

            var text = location.SourceTree?.GetText();
            if (text is not null)
            {
                var startLine = span.StartLinePosition.Line;
                if (startLine >= 0 && startLine < text.Lines.Count)
                {
                    var lineText = text.Lines[startLine].ToString();
                    var lineNumber = (startLine + 1).ToString();
                    var gutter = new string(' ', lineNumber.Length);

                    var startCol = Math.Max(0, Math.Min(span.StartLinePosition.Character, lineText.Length));
                    var endCol = span.EndLinePosition.Line == startLine
                        ? Math.Max(startCol + 1, Math.Min(span.EndLinePosition.Character, lineText.Length))
                        : Math.Max(startCol + 1, lineText.Length);
                    var caretLen = Math.Max(1, endCol - startCol);

                    var indent = new StringBuilder(startCol);
                    for (var i = 0; i < startCol; i++)
                    {
                        indent.Append(lineText[i] == '\t' ? '\t' : ' ');
                    }

                    var excerpt = new StringBuilder();
                    excerpt.AppendLine($"{gutter} |");
                    excerpt.AppendLine($"{lineNumber} | {lineText}");
                    excerpt.Append($"{gutter} | {indent}{new string('^', caretLen)}");
                    sourceExcerpt = excerpt.ToString();
                }
            }
        }

        sb.AppendLine(ToSingleLine(string.IsNullOrWhiteSpace(summary) ? intro : summary!));
        sb.Append(BuildHeader(title, fileLabel));
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(intro.Trim());

        if (sourceExcerpt is not null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(sourceExcerpt);
        }

        if (!string.IsNullOrWhiteSpace(spirit))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Why this rule exists:");
            sb.AppendLine();
            sb.Append("    ").Append(spirit!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(contract))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Contract:");
            sb.AppendLine();
            sb.Append("    ").Append(contract!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(counterexample))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Counterexample:");
            sb.AppendLine();
            sb.Append("    ").Append(counterexample!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(explanation))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(explanation!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(fixHint))
        {
            sb.AppendLine();
            sb.AppendLine();
            var trimmedFix = fixHint!.Trim();
            sb.Append(trimmedFix.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase)
                ? trimmedFix
                : $"Fix: {trimmedFix}");
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

    private static string BuildHeader(string title, string fileLabel)
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
