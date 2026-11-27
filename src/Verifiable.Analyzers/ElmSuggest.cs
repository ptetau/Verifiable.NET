using System;
using System.Collections.Generic;
using System.Linq;

namespace Verifiable.Analyzers;

/// <summary>
/// Elm-style typo ranking using case-insensitive restricted Damerau-Levenshtein distance
/// (modeled on Elm's <c>Reporting.Suggest</c>).
/// </summary>
internal static class ElmSuggest
{
    /// <summary>
    /// Computes the Optimal String Alignment (restricted Damerau-Levenshtein) edit distance
    /// between <paramref name="a"/> and <paramref name="b"/> (insertions, deletions,
    /// substitutions, and adjacent character transpositions each cost 1).
    /// </summary>
    public static int Distance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                var del = d[i - 1, j] + 1;
                var ins = d[i, j - 1] + 1;
                var sub = d[i - 1, j - 1] + cost;
                var best = del < ins ? (del < sub ? del : sub) : (ins < sub ? ins : sub);

                if (i > 1 && j > 1 &&
                    char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2]) &&
                    char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    var trans = d[i - 2, j - 2] + cost;
                    if (trans < best)
                    {
                        best = trans;
                    }
                }

                d[i, j] = best;
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// Returns up to <paramref name="maxResults"/> closest unique candidate names for <paramref name="target"/>,
    /// filtering out distant unrelated identifiers.
    /// </summary>
    public static IReadOnlyList<string> Rank(
        string target,
        IEnumerable<string> candidates,
        int maxResults = 3,
        int? maxDistance = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Array.Empty<string>();
        }

        var trimmed = target.Trim();
        var threshold = maxDistance ?? ComputeThreshold(trimmed.Length);
        var targetLower = trimmed.ToLowerInvariant();
        var targetTail = ExtractTail(trimmed);

        var scored = new List<(int Score, int LengthDiff, string Candidate)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw) || !seen.Add(raw))
            {
                continue;
            }

            var cand = raw.Trim();
            var fullDist = Distance(trimmed, cand);

            // Also compare method/member tail for dotted names like "Type.Method" vs "Method"
            var bestDist = fullDist;
            var candTail = ExtractTail(cand);
            if (!string.Equals(candTail, cand, StringComparison.Ordinal) ||
                !string.Equals(targetTail, trimmed, StringComparison.Ordinal))
            {
                var tailDist = Distance(targetTail, candTail);
                if (tailDist < bestDist && tailDist <= ComputeThreshold(targetTail.Length))
                {
                    bestDist = tailDist;
                }
            }

            // Small bonus when one starts with the other (e.g. "Bal" -> "Balance")
            var candLower = cand.ToLowerInvariant();
            if (bestDist > threshold &&
                trimmed.Length >= 3 &&
                (candLower.StartsWith(targetLower, StringComparison.Ordinal) ||
                 candTail.ToLowerInvariant().StartsWith(targetTail.ToLowerInvariant(), StringComparison.Ordinal)))
            {
                bestDist = threshold;
            }

            if (bestDist <= threshold)
            {
                scored.Add((bestDist, Math.Abs(cand.Length - trimmed.Length), cand));
            }
        }

        return scored
            .OrderBy(x => x.Score)
            .ThenBy(x => x.LengthDiff)
            .ThenBy(x => x.Candidate, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(x => x.Candidate)
            .ToList();
    }

    public static string? FormatDidYouMean(IReadOnlyList<string> suggestions)
    {
        if (suggestions.Count == 0)
        {
            return null;
        }

        if (suggestions.Count == 1)
        {
            return $"Did you mean `{suggestions[0]}`?";
        }

        return "Did you mean one of these?\n" +
            string.Join("\n", suggestions.Select(s => $"    `{s}`"));
    }

    private static int ComputeThreshold(int length) => length switch
    {
        <= 2 => 1,
        <= 5 => 2,
        <= 9 => 3,
        _ => Math.Max(3, length / 3)
    };

    private static string ExtractTail(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot + 1 < name.Length ? name.Substring(dot + 1) : name;
    }
}
