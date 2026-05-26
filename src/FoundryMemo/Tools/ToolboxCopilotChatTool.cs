// Copyright (c) foundry-memo. All rights reserved.

using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Http;

namespace FoundryMemo.Tools;

/// <summary>
/// Wraps the copilot-search toolbox MCP endpoint as a local function tool.
/// Calls the toolbox via JSON-RPC per-request, handling OAuth consent errors.
/// Uses the agent's managed identity to authenticate with the toolbox proxy;
/// forwards user identity headers so the proxy can resolve the caller's
/// OAuth consent token for identity passthrough.
/// </summary>
public class ToolboxCopilotChatTool
{
    private readonly string _mcpEndpoint;
    private readonly TokenCredential _credential;
    private readonly Func<IHttpContextAccessor?> _httpContextAccessorFactory;
    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };

    // Headers that carry user identity context from the platform to the agent
    private static readonly string[] UserContextHeaders =
    [
        "x-agent-user-isolation-key",
        "x-agent-chat-isolation-key",
        "x-agent-response-id",
        "x-aml-project-id",
        "x-request-id",
        "traceparent",
    ];

    public ToolboxCopilotChatTool(string mcpEndpoint, TokenCredential credential, Func<IHttpContextAccessor?> httpContextAccessorFactory)
    {
        _mcpEndpoint = mcpEndpoint;
        _credential = credential;
        _httpContextAccessorFactory = httpContextAccessorFactory;
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
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://ai.azure.com/.default"]),
                CancellationToken.None);

            // Step 1: List tools first (handles consent flow)
            var (listSuccess, toolNames) = await ListToolsOrGetConsent(token.Token);
            if (!listSuccess)
                return toolNames; // Contains consent URL or error

            // Step 2: Call the first available tool with the query
            var toolName = toolNames; // tools/list returned the tool name to use
            return await CallTool(token.Token, toolName, query);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ CopilotChat exception: {ex.GetType().Name}: {ex.Message}");
            return $"Error calling copilot_chat: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task<(bool success, string result)> ListToolsOrGetConsent(string bearerToken)
    {
        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
            @params = new { }
        };

        var json = JsonSerializer.Serialize(rpcRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, _mcpEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("Authorization", $"Bearer {bearerToken}");
        request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");
        ForwardUserContextHeaders(request);

        Console.WriteLine($"  → MCP tools/list POST {_mcpEndpoint}");
        var response = await s_httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"  ← HTTP {(int)response.StatusCode}, body length={responseBody.Length}");

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            // -32007 is the tools/list consent error wrapper
            if (code == -32006 || code == -32007 ||
                message.Contains("CONSENT_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                return (false, ExtractConsentMessage(message));
            }
            return (false, $"MCP tools/list error ({code}): {message}");
        }

        // Extract tool names from result
        if (root.TryGetProperty("result", out var result) &&
            result.TryGetProperty("tools", out var tools) &&
            tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("name", out var name))
                {
                    var toolName = name.GetString() ?? "";
                    Console.WriteLine($"  ✓ Discovered tool: {toolName}");
                    // Return the first tool name (copilot-search typically has one)
                    return (true, toolName);
                }
            }
        }

        Console.WriteLine($"  ✗ No tools found in response: {responseBody}");
        return (false, "No tools available in the copilot-search toolbox.");
    }

    private async Task<string> CallTool(string bearerToken, string toolName, string query)
    {
        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = new { query }
            }
        };

        var json = JsonSerializer.Serialize(rpcRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, _mcpEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("Authorization", $"Bearer {bearerToken}");
        request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");
        ForwardUserContextHeaders(request);

        Console.WriteLine($"  → MCP tools/call '{toolName}' POST {_mcpEndpoint}");
        var response = await s_httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"  ← HTTP {(int)response.StatusCode}, body length={responseBody.Length}");
        var bodyPreview = responseBody.Length > 500 ? responseBody[..500] : responseBody;
        Console.WriteLine($"  ← Body: {bodyPreview}");

        if (!response.IsSuccessStatusCode)
        {
            if (responseBody.Contains("CONSENT_REQUIRED") || responseBody.Contains("-32006"))
                return ExtractConsentMessage(responseBody);
            return $"Error calling {toolName}: HTTP {(int)response.StatusCode} — {responseBody}";
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (code == -32006 || code == -32007 ||
                message.Contains("consent", StringComparison.OrdinalIgnoreCase))
                return ExtractConsentMessage(error.GetRawText());
            return $"MCP error ({code}): {message}";
        }

        if (root.TryGetProperty("result", out var result))
            return ExtractResultContent(result);

        return responseBody;
    }

    private static string ExtractConsentMessage(string rawJson)
    {
        // The consent URL may be in nested JSON within the error message.
        var unescaped = rawJson.Replace("\\\"", "\"").Replace("\\/", "/");

        var consentPrefix = "https://logic-";
        var urlStart = unescaped.IndexOf(consentPrefix, StringComparison.Ordinal);
        if (urlStart < 0)
            urlStart = unescaped.IndexOf("https://", StringComparison.Ordinal);

        if (urlStart >= 0)
        {
            var urlEnd = unescaped.IndexOfAny(['"', ' ', '}', '\\'], urlStart);
            var url = urlEnd > urlStart ? unescaped[urlStart..urlEnd] : unescaped[urlStart..];
            return $"⚠️ OAuth consent required. Please click this link to authorize access, then tell me to retry:\n{url}";
        }

        return "⚠️ OAuth consent required but no consent URL was found in the response. Please check your OAuth connection configuration.";
    }

    /// <summary>
    /// Forward user identity headers from the incoming platform request
    /// to the MCP proxy so it can resolve the user's OAuth consent token.
    /// </summary>
    private void ForwardUserContextHeaders(HttpRequestMessage outgoing)
    {
        var accessor = _httpContextAccessorFactory();
        var httpContext = accessor?.HttpContext;
        if (httpContext == null)
        {
            Console.WriteLine("  ⚠ No HttpContext available — can't forward user identity headers");
            return;
        }

        var forwarded = 0;
        // Debug: log all incoming headers to find user identity
        Console.WriteLine("  📋 Incoming request headers:");
        foreach (var header in httpContext.Request.Headers)
        {
            var val = header.Value.ToString();
            var preview = val.Length > 60 ? val[..60] + "..." : val;
            Console.WriteLine($"    {header.Key}: {preview}");
        }

        foreach (var headerName in UserContextHeaders)
        {
            if (httpContext.Request.Headers.TryGetValue(headerName, out var values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        outgoing.Headers.TryAddWithoutValidation(headerName, value);
                        Console.WriteLine($"  → Forwarded header: {headerName}={value[..Math.Min(20, value.Length)]}...");
                        forwarded++;
                    }
                }
            }
        }

        if (forwarded == 0)
        {
            Console.WriteLine("  ⚠ No matching user context headers found to forward");
        }
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
