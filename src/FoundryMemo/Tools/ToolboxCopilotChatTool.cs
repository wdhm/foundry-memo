// Copyright (c) foundry-memo. All rights reserved.

using System.Text;
using System.Text.Json;
using Azure.Core;

namespace FoundryMemo.Tools;

/// <summary>
/// Wraps the copilot-search toolbox MCP endpoint as a local function tool.
/// Calls the toolbox via JSON-RPC per-request, handling OAuth consent errors.
/// Uses the agent's managed identity to authenticate with the toolbox proxy;
/// the proxy handles OAuth identity passthrough for the caller's token.
/// </summary>
public class ToolboxCopilotChatTool
{
    private readonly string _mcpEndpoint;
    private readonly TokenCredential _credential;
    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };

    public ToolboxCopilotChatTool(string mcpEndpoint, TokenCredential credential)
    {
        _mcpEndpoint = mcpEndpoint;
        _credential = credential;
    }

    /// <summary>
    /// Resolves the MCP endpoint, preferring platform-injected env vars.
    /// </summary>
    public static string ResolveEndpoint(string projectEndpoint)
    {
        // Platform may inject these at runtime (not visible in agent config)
        var candidates = new[]
        {
            "TOOLBOX_COPILOT_SEARCH_MCP_ENDPOINT",
            "FOUNDRY_AGENT_TOOLBOX_ENDPOINT",
            "FOUNDRY_AGENT_TOOLSET_ENDPOINT",
        };

        foreach (var envVar in candidates)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                Console.WriteLine($"  ✓ Toolbox MCP endpoint from {envVar}: {value}");
                return value;
            }
        }

        // Fallback: construct from project endpoint
        var endpoint = $"{projectEndpoint.TrimEnd('/')}/toolboxes/copilot-search/mcp?api-version=v1";
        Console.WriteLine($"  → Toolbox MCP endpoint (constructed): {endpoint}");
        return endpoint;
    }

    public async Task<string> CopilotChat(string query)
    {
        try
        {
            // Try ai.azure.com scope first (for toolbox proxy), fall back to cognitiveservices
            var scopes = new[] { "https://ai.azure.com/.default", "https://cognitiveservices.azure.com/.default" };
            string? lastError = null;

            foreach (var scope in scopes)
            {
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext([scope]),
                    CancellationToken.None);

                var (success, result) = await CallMcpEndpoint(token.Token, query);
                if (success)
                    return result;

                // If 401/403, try next scope
                if (result.Contains("HTTP 401") || result.Contains("HTTP 403"))
                {
                    Console.WriteLine($"  ⚠ Scope {scope} returned auth error, trying next...");
                    lastError = result;
                    continue;
                }

                // Any other error (including consent required), return it
                return result;
            }

            return lastError ?? "Error: all auth scopes failed";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ CopilotChat exception: {ex.GetType().Name}: {ex.Message}");
            return $"Error calling copilot_chat: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task<(bool success, string result)> CallMcpEndpoint(string bearerToken, string query)
    {
        // MCP JSON-RPC: tools/call with copilot_chat tool
        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "copilot_chat",
                arguments = new { query }
            }
        };

        var json = JsonSerializer.Serialize(rpcRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, _mcpEndpoint);
        request.Content = content;
        request.Headers.Add("Authorization", $"Bearer {bearerToken}");
        request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");

        Console.WriteLine($"  → MCP POST {_mcpEndpoint}");
        var response = await s_httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"  ← HTTP {(int)response.StatusCode}, body length={responseBody.Length}");
        // Log response body for debugging (truncate at 1000 chars)
        var bodyPreview = responseBody.Length > 1000 ? responseBody[..1000] : responseBody;
        Console.WriteLine($"  ← Body: {bodyPreview}");

        if (!response.IsSuccessStatusCode)
        {
            if (responseBody.Contains("CONSENT_REQUIRED") || responseBody.Contains("-32006"))
                return (true, ExtractConsentMessage(responseBody));

            // Log first 500 chars of error for debugging
            var preview = responseBody.Length > 500 ? responseBody[..500] : responseBody;
            Console.WriteLine($"  ✗ Error body: {preview}");

            return (false, $"Error calling copilot_chat: HTTP {(int)response.StatusCode} — {responseBody}");
        }

        // Parse JSON-RPC response
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            if (code == -32006 || message.Contains("consent", StringComparison.OrdinalIgnoreCase))
                return (true, ExtractConsentMessage(error.GetRawText()));

            return (true, $"MCP error ({code}): {message}");
        }

        if (root.TryGetProperty("result", out var result))
            return (true, ExtractResultContent(result));

        return (true, responseBody);
    }

    private static string ExtractConsentMessage(string message)
    {
        // Extract consent URL from error message
        var urlStart = message.IndexOf("https://", StringComparison.Ordinal);
        if (urlStart >= 0)
        {
            var urlEnd = message.IndexOfAny(['"', ' ', '}'], urlStart);
            var url = urlEnd > urlStart ? message[urlStart..urlEnd] : message[urlStart..];
            return $"⚠️ OAuth consent required. Please click this link to authorize access, then try again:\n{url}";
        }
        return "⚠️ OAuth consent required but no consent URL was provided. Please check your OAuth connection configuration.";
    }

    private static string ExtractResultContent(JsonElement result)
    {
        var sb = new StringBuilder();

        if (result.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contentArray.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(text.GetString());
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : result.GetRawText();
    }
}
