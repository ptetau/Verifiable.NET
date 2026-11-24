using System;
using System.IO;
using System.Threading.Tasks;
using GalaxyCheck;
using GalaxyCheck.Stateful;
using Microsoft.Data.Sqlite;

namespace Verifiable.Testing;

/// <summary>
/// Fault scenarios injected into SQLite persistence adapters during stateful GalaxyCheck testing.
/// </summary>
public enum PersistenceFaultKind
{
    /// <summary>Normal atomic execution without fault injection.</summary>
    None = 0,

    /// <summary>Simulates power loss / process crash immediately before SQL COMMIT.</summary>
    PreCommitCrash = 1,

    /// <summary>Simulates crash right after SQL COMMIT before the client receives acknowledgment.</summary>
    PostCommitCrashBeforeAck = 2,

    /// <summary>Simulates duplicate command delivery with the same idempotency key.</summary>
    DuplicateDelivery = 3
}

/// <summary>
/// Deterministic exception thrown when <see cref="FaultInjectedSqliteHarness"/> simulates a crash.
/// </summary>
public sealed class SimulatedPersistenceCrashException(PersistenceFaultKind faultKind, string message)
    : Exception(message)
{
    /// <summary>Gets the simulated fault kind.</summary>
    public PersistenceFaultKind FaultKind { get; } = faultKind;
}

/// <summary>
/// Manages an isolated temporary WAL-mode SQLite database with simulated crash and duplicate delivery injection.
/// </summary>
public sealed class FaultInjectedSqliteHarness : IDisposable
{
    private readonly string _dbPath;

    /// <summary>Gets the SQLite connection string for this isolated test database.</summary>
    public string ConnectionString { get; }

    /// <summary>Gets the path to the backing SQLite file.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>Initializes a new isolated WAL-mode SQLite database in the system temp directory.</summary>
    public FaultInjectedSqliteHarness(string prefix = "verifiable")
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Opens a new connection to this isolated WAL database.</summary>
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Executes an atomic persistence operation under the specified <paramref name="fault"/> mode.
    /// </summary>
    public async Task ExecuteWithFaultAsync(PersistenceFaultKind fault, Func<Task> commitOperation)
    {
        switch (fault)
        {
            case PersistenceFaultKind.PreCommitCrash:
                throw new SimulatedPersistenceCrashException(fault, "Simulated crash before SQLite COMMIT.");

            case PersistenceFaultKind.PostCommitCrashBeforeAck:
                await commitOperation().ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
                throw new SimulatedPersistenceCrashException(fault, "Simulated crash immediately after SQLite COMMIT before ACK.");

            case PersistenceFaultKind.DuplicateDelivery:
                await commitOperation().ConfigureAwait(false);
                await commitOperation().ConfigureAwait(false);
                break;

            default:
                await commitOperation().ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp cleanup
        }
    }
}

/// <summary>
/// Helpers for building GalaxyCheck stateful differential persistence properties.
/// </summary>
public static class DifferentialPersistenceProperty
{
    /// <summary>
    /// Creates a GalaxyCheck stateful property comparing a concrete SQLite-backed system against an in-memory <c>[Verifiable]</c> model.
    /// </summary>
    public static Property Create<TSystem, TModel>(
        Func<TSystem> createSystem,
        Func<TModel> createModel,
        Func<TModel, IGen<Command<TSystem, TModel>>> nextCommand) =>
        StatefulProperty.Create(createSystem, createModel, nextCommand);
}
