// Copyright (c) foundry-memo. All rights reserved.

using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace FoundryMemo.Services;

/// <summary>
/// Lightweight MCP client that connects directly to the Foundry Toolbox MCP endpoint.
/// Handles JSON-RPC initialize → tools/list → tools/call lifecycle.
/// </summary>
public class ToolboxMcpClient
{
    private readonly string _endpoint;
    private readonly TokenCredential _credential;
    private readonly HttpClient _httpClient;
    private bool _initialized;

    private static readonly string[] TokenScopes = ["https://ai.azure.com/.default"];

    public ToolboxMcpClient(string endpoint, TokenCredential credential, HttpClient? httpClient = null)
    {
        _endpoint = endpoint;
        _credential = credential;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>
    /// Initialize the MCP session (required before tools/list or tools/call).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var initPayload = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "foundry-memo", version = "1.0" }
            }
        };

        await SendAsync(initPayload, ct);

        // Send initialized notification
        var notifPayload = new { jsonrpc = "2.0", method = "notifications/initialized" };
        await SendAsync(notifPayload, ct);

        _initialized = true;
    }

    /// <summary>
    /// List available tools from the toolbox.
    /// Returns tool definitions suitable for conversion to AITool instances.
    /// </summary>
    public async Task<List<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        // Always re-initialize — each HTTP call is independent (stateless MCP transport)
        await InitializeAsync(ct);

        var payload = new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } };
        var response = await SendAsync(payload, ct)
            ?? throw new InvalidOperationException("MCP tools/list returned no content (204)");

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.GetProperty("code").GetInt32();
            var message = error.GetProperty("message").GetString() ?? "Unknown error";

            if (code == -32006 || code == -32007)
            {
                // Extract consent URL from the error message (may be embedded in JSON)
                var consentUrl = ExtractConsentUrl(message);
                throw new McpConsentRequiredException(consentUrl ?? message);
            }

            throw new InvalidOperationException($"MCP tools/list failed ({code}): {message}");
        }

        var tools = new List<McpToolDefinition>();
        if (response.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("tools", out var toolsArray))
        {
            foreach (var tool in toolsArray.EnumerateArray())
            {
                tools.Add(new McpToolDefinition
                {
                    Name = tool.GetProperty("name").GetString()!,
                    Description = tool.TryGetProperty("description", out var desc)
                        ? desc.GetString() ?? ""
                        : "",
                    InputSchema = tool.TryGetProperty("inputSchema", out var schema)
                        ? schema.GetRawText()
                        : "{\"type\":\"object\"}",
                    RequireApproval = tool.TryGetProperty("_meta", out var meta)
                        && meta.TryGetProperty("tool_configuration", out var cfg)
                        && cfg.TryGetProperty("require_approval", out var approval)
                        && approval.GetString() == "always"
                });
            }
        }

        return tools;
    }

    /// <summary>
    /// Call a tool by name with the given arguments.
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        // Always re-initialize — each HTTP call is independent (stateless MCP transport)
        await InitializeAsync(ct);

        var payload = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name = toolName, arguments }
        };

        var response = await SendAsync(payload, ct)
            ?? throw new InvalidOperationException("MCP tools/call returned no content (204)");

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.GetProperty("code").GetInt32();
            var message = error.GetProperty("message").GetString() ?? "Unknown error";

            if (code == -32006 || code == -32007)
                throw new McpConsentRequiredException(ExtractConsentUrl(message) ?? message);

            return JsonSerializer.Serialize(new { error = true, code, message });
        }

        if (response.RootElement.TryGetProperty("result", out var result))
        {
            // Extract text content from the MCP response
            if (result.TryGetProperty("content", out var content))
            {
                var texts = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && item.TryGetProperty("text", out var text))
                    {
                        texts.Add(text.GetString() ?? "");
                    }
                }
                return string.Join("\n", texts);
            }
            return result.GetRawText();
        }

        return "{}";
    }

    /// <summary>
    /// Extract the consent URL from an MCP error message that may contain embedded JSON.
    /// The message format is: "tools/list failed... {\"errors\":[{...\"message\":\"https://...\"}]}"
    /// </summary>
    private static string? ExtractConsentUrl(string message)
    {
        // Try to find an https://...consent... URL directly in the message
        var idx = message.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            // Find the end of the URL (first quote, space, or end of string)
            var end = message.IndexOfAny(['"', ' ', '}'], idx);
            return end > idx ? message[idx..end] : message[idx..];
        }
        return null;
    }

    private async Task<JsonDocument?> SendAsync(object payload, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(TokenScopes), ct);

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(request, ct);

        // 204 No Content is valid for notifications and initialize acknowledgments
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP request failed ({(int)response.StatusCode}): {body}");

        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException(
                $"MCP endpoint returned empty response (HTTP {(int)response.StatusCode}, Content-Type: {response.Content.Headers.ContentType})");

        // Handle SSE format: extract JSON from "data: " lines
        if (body.StartsWith("event:") || body.StartsWith("data:"))
        {
            var jsonLines = body.Split('\n')
                .Where(l => l.StartsWith("data: "))
                .Select(l => l[6..])
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (jsonLines.Count > 0)
                body = jsonLines.Last();
            else
                throw new InvalidOperationException($"MCP SSE response contained no data lines: {body[..Math.Min(200, body.Length)]}");
        }

        return JsonDocument.Parse(body);
    }
}

public record McpToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string InputSchema { get; init; }
    public bool RequireApproval { get; init; }
}

public class McpConsentRequiredException : Exception
{
    public McpConsentRequiredException(string message) : base(message) { }
}
