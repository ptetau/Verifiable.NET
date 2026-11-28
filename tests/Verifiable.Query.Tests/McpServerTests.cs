using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R17: the same commands are offered as MCP tools with the same JSON in and out; the
/// command line stays the main interface.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class McpServerTests
{
    private const string Source = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Rates
        {
            public static int Weak(int nights)
            {
                Spec.Requires(nights >= 1 && nights <= 30, "Stays are short.", clause: "C2");
                Spec.Ensures<int>(r => r >= nights * 90 && r <= nights * 100, "Prices are within the band.", clause: "C2");
                return nights * 100;
            }
        }
        """;

    private static JsonElement Send(string json)
    {
        var response = McpServer.Handle(json);
        Assert.NotNull(response);
        return JsonDocument.Parse(response!).RootElement.Clone();
    }

    private static string Call(string tool, string arguments) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments}}}}}";

    private static string Cli(params string[] args)
    {
        using var o = new StringWriter();
        Program.Run(args, null, o, new StringWriter());
        return o.ToString().Trim();
    }

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Rates.cs");
        File.WriteAllText(file, Source);
        return file;
    }

    [Fact]
    public void Initialize_Ping_AndProtocolErrors()
    {
        var init = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}}");
        Assert.Equal(1, init.GetProperty("id").GetInt32());
        Assert.Equal(McpServer.ProtocolVersion, init.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.True(init.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));

        Assert.Equal(JsonValueKind.Object, Send("{\"jsonrpc\":\"2.0\",\"id\":\"p\",\"method\":\"ping\"}").GetProperty("result").ValueKind);
        Assert.Null(McpServer.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        Assert.Equal(-32601, Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"nope\"}").GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32700, Send("{not json").GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void ToolsList_OffersEveryAgentCommandWithAClosedInputSchema()
    {
        var tools = Send("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}").GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToList();
        foreach (var c in CommandCatalog.Commands.Where(c => c.Name != "build")) Assert.Contains(c.Name, names);
        Assert.DoesNotContain("build", names);

        foreach (var t in tools)
        {
            var schema = t.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(schema.GetProperty("properties").TryGetProperty("stdin", out _));
            Assert.False(schema.GetProperty("properties").TryGetProperty("format", out _));
        }

        var pack = tools.Single(t => t.GetProperty("name").GetString() == "pack").GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal("integer", pack.GetProperty("maxTokens").GetProperty("type").GetString());
        Assert.Equal("boolean", pack.GetProperty("holes").GetProperty("type").GetString());
        Assert.Equal("array", pack.GetProperty("file").GetProperty("type").GetString());
    }

    [Fact]
    public void ToolCalls_ReturnTheSameJsonAsTheCommandLine()
    {
        var file = Temp();
        var fileJson = JsonSerializer.Serialize(file);
        var cases = new[]
        {
            (Tool: "check", Args: $"{{\"file\":[{fileJson}]}}", Cli: new[] { "check", "--file", file, "--format", "json" }),
            ("one-answer", $"{{\"file\":[{fileJson}],\"target\":\"Rates.Weak\"}}", new[] { "one-answer", "--file", file, "--target", "Rates.Weak", "--format", "json" }),
            ("list", $"{{\"file\":[{fileJson}],\"limit\":1}}", new[] { "list", "--file", file, "--limit", "1", "--format", "json" }),
            ("explain", "{\"rules\":[\"VER102\"]}", new[] { "explain", "VER102", "--format", "json" }),
            ("describe", "{}", new[] { "describe", "--format", "json" }),
            ("tools", "{\"action\":\"status\",\"tool\":\"z3\"}", new[] { "tools", "status", "z3", "--format", "json" }),
        };

        foreach (var (tool, args, cli) in cases)
        {
            var result = Send(Call(tool, args)).GetProperty("result");
            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal(Cli(cli), text);
            Assert.Equal(JsonDocument.Parse(text!).RootElement.GetProperty("status").GetString(), result.GetProperty("structuredContent").GetProperty("status").GetString());
        }
    }

    [Fact]
    public void RefutedIsAnAnswer_InputErrorIsAToolError_AndBadArgumentsAreRejected()
    {
        var file = JsonSerializer.Serialize(Temp());
        var refuted = Send(Call("one-answer", $"{{\"file\":[{file}]}}")).GetProperty("result");
        Assert.False(refuted.GetProperty("isError").GetBoolean());
        Assert.Equal("refuted", refuted.GetProperty("structuredContent").GetProperty("status").GetString());

        var input = Send(Call("check", "{\"file\":[\"/no/such/file.cs\"]}")).GetProperty("result");
        Assert.True(input.GetProperty("isError").GetBoolean());
        Assert.Equal(3, input.GetProperty("structuredContent").GetProperty("exitCode").GetInt32());

        Assert.Equal(-32602, Send(Call("check", "{\"bogus\":1}")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, Send(Call("check", "{\"limit\":\"many\"}")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, Send(Call("nope", "{}")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, Send(Call("tools", "{}")).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Run_ServesNewlineDelimitedMessages()
    {
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"explain\",\"arguments\":{\"rules\":[\"VER108\"]}}}\n");
        var output = new StringWriter();
        Assert.Equal(0, McpServer.Run(input, output));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("VER108", lines[1]);
    }
}
