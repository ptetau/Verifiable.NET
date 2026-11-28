// Correct: in ^a|b$ the anchors bind per alternative; `$` also matches before a final "\n".
// @expect: proved
// @known-bug: a regex with two anchored alternatives (^a|b$) compared with StartsWith/EndsWith makes Z3 return unknown (VER103) instead of proving the right claim
using System;
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class AnchorBranches
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s.StartsWith("a", StringComparison.Ordinal) || s.EndsWith("b", StringComparison.Ordinal) || s.EndsWith("b\n", StringComparison.Ordinal)), "Starts with a or ends with b.");
        return Regex.IsMatch(s, "^a|b$");
    }
}
