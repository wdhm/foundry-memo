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
            return GetStubResults(sharePointUrl, query, maxResults);
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

    private static string GetStubResults(string sharePointUrl, string? query, int maxResults)
    {
        var stubResults = new[]
        {
            new
            {
                Title = "Q4 Strategy Document.docx",
                Extracts = new[]
                {
                    new
                    {
                        Text = "The Q4 strategy focuses on three key initiatives: expanding market presence in EMEA, " +
                               "launching the new product line by October, and improving customer retention by 15%.",
                        RelevanceScore = 0.95
                    }
                },
                SourceUrl = $"{sharePointUrl}/Q4-Strategy.docx",
            },
            new
            {
                Title = "Budget Overview FY25.xlsx",
                Extracts = new[]
                {
                    new
                    {
                        Text = "Total projected budget for FY25 is $12.4M, with 40% allocated to R&D, " +
                               "30% to sales and marketing, and 30% to operations.",
                        RelevanceScore = 0.88
                    }
                },
                SourceUrl = $"{sharePointUrl}/Budget-FY25.xlsx",
            },
            new
            {
                Title = "Team Org Chart.pptx",
                Extracts = new[]
                {
                    new
                    {
                        Text = "The organization consists of 4 divisions: Engineering (45 headcount), " +
                               "Product (12), Sales (28), and Operations (15). Total headcount: 100.",
                        RelevanceScore = 0.72
                    }
                },
                SourceUrl = $"{sharePointUrl}/Org-Chart.pptx",
            }
        };

        return JsonSerializer.Serialize(new
        {
            SharePointUrl = sharePointUrl,
            Query = query ?? "all content",
            ResultCount = stubResults.Length,
            Results = stubResults,
            Note = "STUB DATA — Graph credentials not configured. Set GRAPH_CLIENT_ID in .env to enable live retrieval."
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
