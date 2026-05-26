// Copyright (c) foundry-memo. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using FoundryMemo.Services;

namespace FoundryMemo.Tools;

/// <summary>
/// Agent tool that calls the Copilot Retrieval API to fetch SharePoint content.
/// Falls back to stub data when no Graph credentials are configured.
/// </summary>
public class SharePointRetrievalTool
{
    private readonly CopilotRetrievalService? _retrievalService;

    public SharePointRetrievalTool(CopilotRetrievalService? retrievalService)
    {
        _retrievalService = retrievalService;
    }

    /// <summary>
    /// Retrieves document content from a SharePoint site using the Copilot Retrieval API.
    /// </summary>
    [Description("Retrieves document content from a SharePoint URL using the Copilot Retrieval API.")]
    public async Task<string> RetrieveSharePointContent(
        [Description("The SharePoint site or folder URL to retrieve content from")] string sharePointUrl,
        [Description("Optional search query to filter results. Defaults to retrieving all content.")] string? query = null,
        [Description("Maximum number of results to return (1-25)")] int maxResults = 25)
    {
        if (_retrievalService is null)
        {
            return JsonSerializer.Serialize(new
            {
                Error = true,
                Message = "Graph API credentials not configured. Use SearchSharePoint (MCP toolbox) instead — it uses the caller's identity.",
                SharePointUrl = sharePointUrl
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        return await GetLiveResults(sharePointUrl, query, maxResults);
    }

    private async Task<string> GetLiveResults(string sharePointUrl, string? query, int maxResults)
    {
        try
        {
            var searchQuery = query ?? "summarize all content and key information";
            var filter = CopilotRetrievalService.BuildPathFilter(sharePointUrl);

            var response = await _retrievalService!.RetrieveAsync(searchQuery, filter, maxResults);

            var results = response.RetrievalHits.Select(hit => new
            {
            Title = hit.ResourceMetadata?.GetValueOrDefault("title") ?? Path.GetFileName(hit.WebUrl),
            Author = hit.ResourceMetadata?.GetValueOrDefault("author"),
            SourceUrl = hit.WebUrl,
            Extracts = hit.Extracts.Select(e => new
            {
                e.Text,
                e.RelevanceScore
            }).ToList()
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            SharePointUrl = sharePointUrl,
            Query = query ?? "all content",
            ResultCount = results.Count,
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                Error = true,
                Message = $"Failed to retrieve SharePoint content: {ex.Message}",
                SharePointUrl = sharePointUrl,
                Hint = ex.Message.Contains("AADSTS") || ex.Message.Contains("authentication")
                    ? "Authentication failed. The device code may have expired — try invoking again."
                    : "Check the Copilot Retrieval API permissions and SharePoint URL."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

}
