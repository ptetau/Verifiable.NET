using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Verifiable.Analyzers.Smt;

internal sealed record CounterexampleInsight(
    string? EvaluatedResult,
    string? FailedClauseBlock,
    string? BoundaryHint);

/// <summary>
/// Turns raw Z3 SAT models into Elm-style semantic diffs: pinpoints which conjunct
/// of a compound contract failed, shows evaluated left/right comparison values, and
/// detects boundary / off-by-one hits.
/// </summary>
internal static class SmtCounterexampleAnalyzer
{
    public const string EvalKeyPrefix = "__eval_";
    private const string ResultKey = "__eval_result";

    public static IReadOnlyList<(string Key, string SmtExpr)>? BuildSatEvalExprs(
        string? resultSmtTerm,
        IReadOnlyList<GoalClauseInfo>? clauses)
    {
        var list = new List<(string Key, string SmtExpr)>();

        if (!string.IsNullOrWhiteSpace(resultSmtTerm))
        {
            list.Add((ResultKey, resultSmtTerm!));
        }

        if (clauses is { Count: > 0 })
        {
            for (var i = 0; i < clauses.Count; i++)
            {
                var c = clauses[i];
                list.Add(($"__eval_c_{i}", c.ClauseSmtTerm));
                if (!string.IsNullOrWhiteSpace(c.LeftSmtTerm))
                {
                    list.Add(($"__eval_l_{i}", c.LeftSmtTerm!));
                }
                if (!string.IsNullOrWhiteSpace(c.RightSmtTerm))
                {
                    list.Add(($"__eval_r_{i}", c.RightSmtTerm!));
                }
            }
        }

        return list.Count > 0 ? list : null;
    }

    public static CounterexampleInsight Analyze(
        IReadOnlyDictionary<string, string> model,
        IReadOnlyList<GoalClauseInfo>? clauses)
    {
        string? evalResult = null;
        if (model.TryGetValue(ResultKey, out var rawRes) && !string.IsNullOrWhiteSpace(rawRes))
        {
            evalResult = rawRes;
        }

        if (clauses is null || clauses.Count == 0)
        {
            return new CounterexampleInsight(evalResult, null, null);
        }

        var failedIndices = new List<int>();
        for (var i = 0; i < clauses.Count; i++)
        {
            if (model.TryGetValue($"__eval_c_{i}", out var truth) &&
                truth.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                failedIndices.Add(i);
            }
        }

        // Single-clause contract where Z3 didn't explicitly return "false" still has clause 0 as the failing clause
        if (failedIndices.Count == 0 && clauses.Count == 1)
        {
            failedIndices.Add(0);
        }

        if (failedIndices.Count == 0)
        {
            return new CounterexampleInsight(evalResult, null, null);
        }

        var sb = new StringBuilder();
        string? boundaryHint = null;

        for (var k = 0; k < failedIndices.Count && k < 2; k++)
        {
            var idx = failedIndices[k];
            var clause = clauses[idx];
            var hasLeft = model.TryGetValue($"__eval_l_{idx}", out var leftVal) && !string.IsNullOrWhiteSpace(leftVal);
            var hasRight = model.TryGetValue($"__eval_r_{idx}", out var rightVal) && !string.IsNullOrWhiteSpace(rightVal);

            // Skip single-clause breakdown if we have neither side values nor multi-clause pinpointing
            if (clauses.Count == 1 && (!hasLeft || !hasRight))
            {
                continue;
            }

            if (sb.Length > 0) sb.AppendLine();

            if (clauses.Count > 1)
            {
                sb.Append($"Failed clause ({idx + 1} of {clauses.Count}): `{clause.ClauseText}`");
            }
            else
            {
                sb.Append($"Why the check failed for `{clause.ClauseText}`:");
            }

            if (hasLeft && hasRight && !string.IsNullOrWhiteSpace(clause.Operator))
            {
                sb.AppendLine();
                sb.Append($"    Left  ({clause.LeftText}):  {leftVal}");
                sb.AppendLine();
                sb.Append($"    Right ({clause.RightText}): {rightVal}");
                sb.AppendLine();
                sb.Append($"    Expected `{leftVal} {clause.Operator} {rightVal}`, which is false.");

                boundaryHint ??= DetectBoundaryHint(clause.Operator!, leftVal!, rightVal!);
            }
        }

        return new CounterexampleInsight(
            evalResult,
            sb.Length > 0 ? sb.ToString() : null,
            boundaryHint);
    }

    private static string? DetectBoundaryHint(string op, string leftVal, string rightVal)
    {
        if (!TryParseNumeric(leftVal, out var l) || !TryParseNumeric(rightVal, out var r))
        {
            return null;
        }

        if (l == r && (op is ">" or "<"))
        {
            var inclusive = op == ">" ? ">=" : "<=";
            return $"Boundary hit: both sides evaluate to `{leftVal}` on a strict `{op}` check. Check whether `{inclusive}` or a stricter precondition is intended.";
        }

        if (l == r && op == "!=")
        {
            return $"Boundary hit: both sides evaluate to `{leftVal}`, violating `!=`. Add a guard or precondition preventing equal values.";
        }

        if ((op == ">=" && r - l == 1m) || (op == "<=" && l - r == 1m))
        {
            return $"Off-by-one boundary: `{leftVal}` misses `{rightVal}` by exactly 1. Check for an off-by-one condition or missing bound.";
        }

        return null;
    }

    private static bool TryParseNumeric(string text, out decimal value)
    {
        var cleaned = text.Trim();
        if (cleaned.EndsWith("m", StringComparison.OrdinalIgnoreCase) && cleaned.Length > 1)
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 1);
        }

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
