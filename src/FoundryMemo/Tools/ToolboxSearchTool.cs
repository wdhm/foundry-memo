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
            return result;
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
                ["message"] = $"Summarize the full content of this document: {documentUrl}",
                ["fileUris"] = new[] { documentUrl }
            };
            var argsJson = JsonSerializer.SerializeToElement(args);
            var result = await mcpClient.CallToolAsync(CopilotChatTool, argsJson);
            return result;
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
}
