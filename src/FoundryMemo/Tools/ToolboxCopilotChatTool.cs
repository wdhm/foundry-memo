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

    public async Task<string> CopilotChat(string query)
    {
        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
                CancellationToken.None);

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
            request.Headers.Add("Authorization", $"Bearer {token.Token}");
            request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");

            var response = await s_httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Check for consent required in the response
                if (responseBody.Contains("CONSENT_REQUIRED") || responseBody.Contains("-32006"))
                {
                    return ExtractConsentMessage(responseBody);
                }
                return $"Error calling copilot_chat: HTTP {(int)response.StatusCode} — {responseBody}";
            }

            // Parse JSON-RPC response
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Check for JSON-RPC error (consent required, etc.)
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

                if (code == -32006 || message.Contains("consent", StringComparison.OrdinalIgnoreCase))
                {
                    return ExtractConsentMessage(message);
                }
                return $"MCP error ({code}): {message}";
            }

            // Extract result content
            if (root.TryGetProperty("result", out var result))
            {
                return ExtractResultContent(result);
            }

            return responseBody;
        }
        catch (Exception ex)
        {
            return $"Error calling copilot_chat: {ex.GetType().Name}: {ex.Message}";
        }
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
