// Copyright (c) foundry-memo. All rights reserved.

using System.Text.Json;
using FoundryMemo.Services;

namespace FoundryMemo.Tools;

/// <summary>
/// Bridge tool that calls the MCP toolbox at request time (when user session context
/// is available for identity passthrough). Returns the consent URL if OAuth consent
/// hasn't been completed for the calling user.
/// </summary>
public class ToolboxSearchTool(ToolboxMcpClient mcpClient)
{
    /// <summary>
    /// Search SharePoint content using the caller's identity via the MCP toolbox.
    /// Requires the user to have completed OAuth consent for the copilot-search connection.
    /// </summary>
    public async Task<string> SearchSharePointContent(string query, string? siteUrl = null)
    {
        try
        {
            // Ensure tools are discoverable (triggers consent if needed)
            await mcpClient.ListToolsAsync();

            var args = new Dictionary<string, object> { ["query"] = query };
            if (!string.IsNullOrEmpty(siteUrl))
                args["site_url"] = siteUrl;

            var argsJson = JsonSerializer.SerializeToElement(args);
            var result = await mcpClient.CallToolAsync("search_site_content", argsJson);
            return result;
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. Please visit this URL to authorize access, then try again:\n\n{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error calling MCP toolbox SearchSharePoint: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// Get full document text from SharePoint using the caller's identity via the MCP toolbox.
    /// Requires the user to have completed OAuth consent for the copilot-search connection.
    /// </summary>
    public async Task<string> GetDocumentText(string documentUrl)
    {
        try
        {
            // Ensure tools are discoverable (triggers consent if needed)
            await mcpClient.ListToolsAsync();

            var args = new Dictionary<string, object> { ["url"] = documentUrl };
            var argsJson = JsonSerializer.SerializeToElement(args);
            var result = await mcpClient.CallToolAsync("get_document_text", argsJson);
            return result;
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. Please visit this URL to authorize access, then try again:\n\n{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error calling MCP toolbox GetDocumentText: [{ex.GetType().Name}] {ex.Message}";
        }
    }
}
