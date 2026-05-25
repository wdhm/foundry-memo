// Copyright (c) foundry-memo. All rights reserved.

using System.Text.Json;
using FoundryMemo.Services;

namespace FoundryMemo.Tools;

/// <summary>
/// Bridge tool that calls the M365 Copilot MCP via the Foundry toolbox.
/// Uses UserEntraToken (OBO) — the platform proxies the caller's Entra identity
/// so all results are permission-trimmed per the calling user.
/// </summary>
public class ToolboxSearchTool(ToolboxMcpClient mcpClient)
{
    private const string CopilotChatTool = "copilot-search___copilot_chat";

    // Responses protocol has limits on tool output size — truncate to stay safe
    private const int MaxResponseLength = 12_000;

    /// <summary>
    /// Search SharePoint/M365 content using the caller's identity via M365 Copilot.
    /// When siteUrl is provided, results are scoped to that specific SharePoint site.
    /// </summary>
    public async Task<string> SearchSharePointContent(string query, string? siteUrl = null)
    {
        try
        {
            var scopedQuery = string.IsNullOrEmpty(siteUrl)
                ? query
                : $"Search only within the SharePoint site {siteUrl} — {query}";

            var args = new Dictionary<string, object> { ["message"] = scopedQuery };

            // Ground the response on the site URL to further constrain results
            if (!string.IsNullOrEmpty(siteUrl))
                args["fileUris"] = new[] { siteUrl };

            var argsJson = JsonSerializer.SerializeToElement(args);
            var result = await mcpClient.CallToolAsync(CopilotChatTool, argsJson);
            return TruncateIfNeeded(ExtractReply(result));
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. Please visit this URL to authorize access, then try again:\n\n{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error calling M365 Copilot MCP: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// Get content about a specific SharePoint document using the caller's identity.
    /// Grounds the M365 Copilot response on the given file URI for focused retrieval.
    /// </summary>
    public async Task<string> GetDocumentText(string documentUrl)
    {
        try
        {
            var args = new Dictionary<string, object>
            {
                ["message"] = $"Extract and return the full content of this document: {documentUrl}",
                ["fileUris"] = new[] { documentUrl }
            };
            var argsJson = JsonSerializer.SerializeToElement(args);
            var result = await mcpClient.CallToolAsync(CopilotChatTool, argsJson);
            return TruncateIfNeeded(ExtractReply(result));
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. Please visit this URL to authorize access, then try again:\n\n{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error calling M365 Copilot MCP: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// The MCP response is a JSON string with conversationId, reply, rawResponse.
    /// Extract just the reply text to reduce size and improve LLM readability.
    /// </summary>
    private static string ExtractReply(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("reply", out var reply))
            {
                var replyText = reply.GetString();
                if (!string.IsNullOrEmpty(replyText))
                    return replyText;
            }

            // If reply is empty, try to extract from rawResponse messages
            if (doc.RootElement.TryGetProperty("rawResponse", out var rawResp))
            {
                var rawStr = rawResp.GetString();
                if (!string.IsNullOrEmpty(rawStr))
                {
                    using var rawDoc = JsonDocument.Parse(rawStr);
                    if (rawDoc.RootElement.TryGetProperty("messages", out var messages))
                    {
                        foreach (var msg in messages.EnumerateArray())
                        {
                            if (msg.TryGetProperty("text", out var text))
                            {
                                var t = text.GetString();
                                if (!string.IsNullOrEmpty(t) && t.Length > 100)
                                    return t;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Not JSON or unexpected format — return raw
        }

        return raw;
    }

    private static string TruncateIfNeeded(string text)
    {
        if (text.Length <= MaxResponseLength)
            return text;

        return text[..MaxResponseLength] + "\n\n[... response truncated for size]";
    }
}
