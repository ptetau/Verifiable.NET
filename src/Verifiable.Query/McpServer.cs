using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Verifiable.Query;

/// <summary>
/// <c>verifiable-query mcp</c> (requirement R17): the same commands as Model Context Protocol tools over
/// stdio (newline-delimited JSON-RPC 2.0). Each tool's input schema is built from the command's options,
/// and each call runs the command exactly as the CLI does with <c>--format json</c>, so the JSON out is
/// the same document. The command line stays the main interface.
/// </summary>
internal static class McpServer
{
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>Commands that only make sense from a shell.</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "build" };

    /// <summary>Options an MCP client cannot use (stdin is the protocol channel; colour is for people).</summary>
    private static readonly HashSet<string> ExcludedOptions = new(StringComparer.Ordinal) { "--stdin", "--format", "--color", "--verbose" };

    public static string PropertyName(string flag)
    {
        var parts = flag.TrimStart('-').Split('-');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
    }

    private static string JsonType(OptionInfo o) =>
        o.Value is null ? "boolean" : o.Value is "<n>" or "<ms>" ? "integer" : "string";

    public static IEnumerable<CommandInfo> Tools() => CommandCatalog.Commands.Where(c => !Excluded.Contains(c.Name));

    public static int Run(TextReader input, TextWriter output)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Trim().Length == 0) continue;
            var response = Handle(line);
            if (response is not null)
            {
                output.WriteLine(response);
                output.Flush();
            }
        }
        return 0;
    }

    /// <summary>Answers one JSON-RPC message; null for a notification.</summary>
    public static string? Handle(string message)
    {
        JsonElement request;
        try
        {
            using var doc = JsonDocument.Parse(message);
            request = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        JsonElement? id = request.TryGetProperty("id", out var idEl) ? idEl.Clone() : null;
        var method = request.TryGetProperty("method", out var m) ? m.GetString() : null;
        if (id is null) return null; // notifications (e.g. notifications/initialized) need no answer

        switch (method)
        {
            case "initialize":
                return Result(id, w =>
                {
                    w.WriteString("protocolVersion", ProtocolVersion);
                    w.WriteStartObject("capabilities");
                    w.WriteStartObject("tools");
                    w.WriteBoolean("listChanged", false);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", "verifiable-query");
                    w.WriteString("version", "0.1.0");
                    w.WriteEndObject();
                    w.WriteString("instructions", "Verifiable.NET checks. Every tool returns the same JSON as `verifiable-query <command> --format json`; `status` and `exitCode` mean the same on every tool (0 proved, 1 refuted, 2 undecided, 3 input_error, 4 tool_missing). Call `describe` for every command and `explain` for any rule.");
                });
            case "ping":
                return Result(id, _ => { });
            case "tools/list":
                return Result(id, WriteTools);
            case "tools/call":
                return Call(id, request.TryGetProperty("params", out var p) ? p : default);
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static void WriteTools(Utf8JsonWriter w)
    {
        var byFlag = CommandCatalog.Options.ToDictionary(o => o.Flag, StringComparer.Ordinal);
        w.WriteStartArray("tools");
        foreach (var c in Tools())
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("description", $"{c.Summary} Returns the `{AgentOutput.SchemaId(c.Schema)}` JSON document.");
            w.WriteStartObject("inputSchema");
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            if (c.Name == "explain")
            {
                w.WriteStartObject("rules");
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "string");
                w.WriteEndObject();
                w.WriteString("description", "Rule IDs such as VER102; empty for every rule.");
                w.WriteEndObject();
            }
            if (c.Name == "tools")
            {
                w.WriteStartObject("action");
                w.WriteString("type", "string");
                w.WriteStartArray("enum");
                w.WriteStringValue("status");
                w.WriteStringValue("install");
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartObject("tool");
                w.WriteString("type", "string");
                w.WriteString("description", "A tool name such as z3; empty for all.");
                w.WriteEndObject();
            }
            foreach (var flag in c.Options.Where(f => !ExcludedOptions.Contains(f)))
            {
                var o = byFlag[flag];
                w.WriteStartObject(PropertyName(flag));
                if (flag == "--file")
                {
                    w.WriteString("type", "array");
                    w.WriteStartObject("items");
                    w.WriteString("type", "string");
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteString("type", JsonType(o));
                }
                w.WriteString("description", o.Description);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            if (c.Name == "tools")
            {
                w.WriteStartArray("required");
                w.WriteStringValue("action");
                w.WriteEndArray();
            }
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>The CLI arguments a tool call stands for.</summary>
    public static (string[]? Args, string? Error) ToArgs(string name, JsonElement arguments)
    {
        var command = Tools().FirstOrDefault(c => c.Name == name);
        if (command is null) return (null, $"Unknown tool `{name}`.");
        var args = new List<string> { name };
        var allowed = command.Options.Where(f => !ExcludedOptions.Contains(f)).ToDictionary(PropertyName, f => f, StringComparer.Ordinal);
        var byFlag = CommandCatalog.Options.ToDictionary(o => o.Flag, StringComparer.Ordinal);

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
            {
                if (name == "explain" && prop.Name == "rules" && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    args.AddRange(prop.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty));
                    continue;
                }
                if (name == "tools" && prop.Name is "action" or "tool")
                {
                    continue;
                }
                if (!allowed.TryGetValue(prop.Name, out var flag)) return (null, $"Tool `{name}` has no argument `{prop.Name}`.");
                var option = byFlag[flag];
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.True when option.Value is null: args.Add(flag); break;
                    case JsonValueKind.False when option.Value is null: break;
                    case JsonValueKind.Array when flag == "--file":
                        foreach (var f in prop.Value.EnumerateArray()) { args.Add(flag); args.Add(f.GetString() ?? string.Empty); }
                        break;
                    case JsonValueKind.String when option.Value is not null && JsonType(option) == "string": args.Add(flag); args.Add(prop.Value.GetString()!); break;
                    case JsonValueKind.Number when JsonType(option) == "integer" && prop.Value.TryGetInt32(out var number):
                        args.Add(flag); args.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                    default: return (null, $"Argument `{prop.Name}` of tool `{name}` has the wrong type.");
                }
            }
        }

        if (name == "tools")
        {
            var action = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("action", out var a) ? a.GetString() : null;
            if (action is not ("status" or "install")) return (null, "Tool `tools` needs `action`: `status` or `install`.");
            args.Insert(1, action);
            if (arguments.TryGetProperty("tool", out var t) && t.GetString() is { Length: > 0 } toolName) args.Insert(2, toolName);
        }

        args.Add("--format");
        args.Add("json");
        return (args.ToArray(), null);
    }

    private static string Call(JsonElement? id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var n) || n.GetString() is not { } name)
        {
            return Error(id, -32602, "tools/call needs `name`.");
        }
        var arguments = parameters.TryGetProperty("arguments", out var argsEl) ? argsEl : default;
        var (args, error) = ToArgs(name, arguments);
        if (args is null) return Error(id, -32602, error!);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, null, stdout, stderr);
        var text = stdout.ToString().Trim();
        if (text.Length == 0) text = stderr.ToString().Trim();

        JsonElement? structured = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            structured = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not JSON (should not happen with --format json); return the text only.
        }

        return Result(id, w =>
        {
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            if (structured is { ValueKind: JsonValueKind.Object } s)
            {
                w.WritePropertyName("structuredContent");
                s.WriteTo(w);
            }
            // Refuted or undecided is a valid answer; only bad input or a missing tool is a tool error.
            w.WriteBoolean("isError", exit is 3 or 4);
        });
    }

    private static string Result(JsonElement? id, Action<Utf8JsonWriter> body) => AgentOutput.Write(indented: false, w =>
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        id!.Value.WriteTo(w);
        w.WriteStartObject("result");
        body(w);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    private static string Error(JsonElement? id, int code, string message) => AgentOutput.Write(indented: false, w =>
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        if (id is { } i) i.WriteTo(w); else w.WriteNullValue();
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
        w.WriteEndObject();
    });
}
