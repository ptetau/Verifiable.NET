// The analyzer reads a static readonly Regex field's initializer, but the owning class's static constructor can replace it.
// @expect: rejected
// @known-bug: TryExtractRegexSpecFromInstance trusts the initializer of a static readonly Regex field in a non-[Verifiable] class, whose static constructor can reassign it
// @witness: Corpus.Strings.UsesForeignRegex.IsA("b")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

public static class ForeignPatterns
{
    public static readonly Regex OnlyA = new Regex("^a\\z");

    static ForeignPatterns()
    {
        OnlyA = new Regex("^b\\z");
    }
}

[Verifiable]
public static class UsesForeignRegex
{
    public static bool IsA(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s == "a", "Only \"a\" matches.");
        return ForeignPatterns.OnlyA.IsMatch(s);
    }
}
