using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Verifiable.Query;

/// <summary>
/// The five outcomes every <c>verifiable-query</c> command reports, with one exit code each.
/// The same outcome always gives the same exit code, on every command.
/// </summary>
public enum AgentOutcome
{
    /// <summary>Everything asked was proved (exit code 0).</summary>
    Proved = 0,

    /// <summary>Something was disproved, or a check found errors (exit code 1).</summary>
    Refuted = 1,

    /// <summary>The solver could not decide, and nothing was disproved (exit code 2).</summary>
    Undecided = 2,

    /// <summary>The input was wrong: bad arguments, missing files, code that does not compile (exit code 3).</summary>
    InputError = 3,

    /// <summary>A native tool such as Z3 is not installed (exit code 4).</summary>
    ToolMissing = 4,
}

/// <summary>
/// Shared rules for machine-readable output (requirement R12): versioned schema names, the status
/// words, deterministic JSON, file paths without machine-specific prefixes, and paging.
/// </summary>
internal static class AgentOutput
{
    /// <summary>Version of every JSON schema this tool publishes. Bumped only on a breaking change.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Default page size for list-shaped results in JSON output.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Largest page size a caller may ask for.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The schema identifier written as the first property of every JSON document.</summary>
    public static string SchemaId(string name) => $"verifiable-query/{name}@{SchemaVersion}";

    public static string StatusWord(AgentOutcome outcome) => outcome switch
    {
        AgentOutcome.Proved => "proved",
        AgentOutcome.Refuted => "refuted",
        AgentOutcome.Undecided => "undecided",
        AgentOutcome.InputError => "input_error",
        AgentOutcome.ToolMissing => "tool_missing",
        _ => "input_error",
    };

    public static AgentOutcome FromQueryStatus(QueryOutcomeStatus status) => status switch
    {
        QueryOutcomeStatus.Proved => AgentOutcome.Proved,
        QueryOutcomeStatus.Refuted => AgentOutcome.Refuted,
        QueryOutcomeStatus.Inconclusive => AgentOutcome.Undecided,
        QueryOutcomeStatus.Z3NotFound => AgentOutcome.ToolMissing,
        _ => AgentOutcome.InputError,
    };

    /// <summary>
    /// Writes a JSON document with a fixed property order (the order the writer is called in),
    /// <c>\n</c> line endings on every platform, and no escaping of ordinary non-ASCII text.
    /// </summary>
    public static string Write(bool indented, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            body(writer);
        }

        var text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return text.Replace("\r\n", "\n");
    }

    public static void StringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    /// <summary>
    /// Writes a pre-serialised JSON value (from a diagnostic property) as a property, or null when
    /// it is missing or malformed.
    /// </summary>
    public static void RawOrNull(Utf8JsonWriter w, string name, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            w.WriteNull(name);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json!);
            w.WritePropertyName(name);
            doc.RootElement.WriteTo(w);
        }
        catch (JsonException)
        {
            w.WriteNull(name);
        }
    }

    /// <summary>
    /// Makes a path deterministic across machines: relative to the current directory when the file is
    /// under it, with forward slashes. Paths outside the current directory keep only the part from the
    /// nearest folder named like a project root marker, or else just the file name.
    /// </summary>
    public static string NormalizePath(string? path) => NormalizePath(path, Directory.GetCurrentDirectory());

    internal static string NormalizePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (!Path.IsPathRooted(path)) return path!.Replace('\\', '/');

        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(baseDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            root += Path.DirectorySeparatorChar;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.StartsWith(root, comparison))
        {
            return full.Substring(root.Length).Replace('\\', '/');
        }

        return Path.GetFileName(full);
    }

    /// <summary>Encodes a page offset as an opaque cursor string.</summary>
    public static string EncodeCursor(int offset) =>
        "o" + offset.ToString(CultureInfo.InvariantCulture);

    /// <summary>Decodes a cursor made by <see cref="EncodeCursor"/>. Returns false for anything else.</summary>
    public static bool TryDecodeCursor(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrEmpty(cursor)) return true;
        return cursor!.Length > 1 && cursor[0] == 'o' &&
               int.TryParse(cursor.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out offset) &&
               offset >= 0;
    }

    /// <summary>One page of a list: where it starts, how many items it holds, and the cursor for the next page.</summary>
    public readonly record struct Page(int Offset, int Count, int Total, string? NextCursor);

    public static Page Slice(int total, int offset, int limit)
    {
        var start = Math.Min(Math.Max(offset, 0), total);
        var count = Math.Min(Math.Max(limit, 0), total - start);
        var next = start + count < total ? EncodeCursor(start + count) : null;
        return new Page(start, count, total, next);
    }

    public static void WritePage(Utf8JsonWriter w, Page page, int limit)
    {
        w.WriteStartObject("page");
        w.WriteNumber("offset", page.Offset);
        w.WriteNumber("limit", limit);
        w.WriteNumber("returned", page.Count);
        w.WriteNumber("total", page.Total);
        StringOrNull(w, "nextCursor", page.NextCursor);
        w.WriteEndObject();
    }
}
