using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>
/// The break-it corpus: programs written to catch the analyzer out. See <c>Soundness/Corpus/README.md</c>.
/// </summary>
public class SoundnessCorpusTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Programs() => CorpusProgram.AllRelativePaths().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Programs))]
    public async Task CorpusProgram_GetsTheVerdictItExpects(string relativePath)
    {
        var program = CorpusProgram.Load(relativePath);
        var report = await CorpusRunner.RunAsync(program);

        foreach (var d in report.Diagnostics.Where(d => d.Id.StartsWith("VER")))
        {
            output.WriteLine($"diagnostic: {d.Id} {d.Severity} at {d.Location.GetLineSpan().StartLinePosition}: {FirstLine(d.GetMessage())}");
        }

        foreach (var (witness, outcome) in report.Witnesses) output.WriteLine($"witness {witness}: {outcome}");
        foreach (var note in report.CrossCheckNotes) output.WriteLine($"cross-check: {note}");

        Assert.True(report.Fatal.Count == 0, string.Join("\n", report.Fatal));

        // Every witness must really show the program is wrong; otherwise the corpus entry itself is mistaken.
        foreach (var (witness, outcome) in report.Witnesses)
        {
            Assert.True(
                outcome is RunOutcome.Broken,
                outcome is RunOutcome.Discarded
                    ? $"@witness {witness} does not meet the method's own preconditions, so it shows nothing."
                    : $"@witness {witness} ran without breaking any contract, so it does not show the program is wrong.");
        }

        var wrong = program.Expect switch
        {
            Expectation.Rejected when !report.Rejected =>
                "The analyzer accepted a program that is wrong (a false proof).",
            Expectation.Proved when report.Rejected =>
                "The analyzer rejected a program that is right.",
            Expectation.Proved when report.CrossCheckFailures.Count > 0 =>
                "The analyzer accepted this program, but running it broke a contract:\n" + string.Join("\n", report.CrossCheckFailures),
            _ when report.CrossCheckFailures.Count > 0 =>
                "A method the analyzer accepted broke a contract when run:\n" + string.Join("\n", report.CrossCheckFailures),
            _ => null,
        };

        if (program.KnownBug is not null)
        {
            Assert.True(
                wrong is not null,
                $"This looks fixed: the analyzer now handles it correctly. Remove `// @known-bug: {program.KnownBug}` from {relativePath}.");
            output.WriteLine($"known bug, still present: {program.KnownBug}");
            return;
        }

        Assert.True(wrong is null, wrong);
    }

    private static string FirstLine(string message) => message.Split('\n')[0].TrimEnd('\r');
}
