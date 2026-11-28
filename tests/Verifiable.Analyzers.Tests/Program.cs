using Verifiable.Analyzers.Tests.Soundness;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// The test assembly doubles as the corpus analysis worker: <c>dotnet Verifiable.Analyzers.Tests.dll --corpus-worker</c>.
/// Run normally (by vstest) the entry point is never used.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) =>
        args.Length > 0 && args[0] == "--corpus-worker" ? CorpusWorker.Run() : 0;
}
