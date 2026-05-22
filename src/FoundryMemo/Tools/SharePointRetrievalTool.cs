// Copyright (c) foundry-memo. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using FoundryMemo.Services;

namespace FoundryMemo.Tools;

/// <summary>
/// Agent tool that calls the Copilot Retrieval API to fetch SharePoint content.
/// </summary>
public static class SharePointRetrievalTool
{
    /// <summary>
    /// Retrieves document content from a SharePoint site using the Copilot Retrieval API.
    /// </summary>
    [Description("Retrieves document content from a SharePoint URL using the Copilot Retrieval API.")]
    public static async Task<string> RetrieveSharePointContent(
        [Description("The SharePoint site or folder URL to retrieve content from")] string sharePointUrl,
        [Description("Optional search query to filter results. Defaults to retrieving all content.")] string? query = null,
        [Description("Maximum number of results to return (1-25)")] int maxResults = 25)
    {
        // TODO (M2): Replace with real Copilot Retrieval API call using delegated user token.
        // For M1 we return stubbed data to prove tool calling works end-to-end.

        var stubResults = new[]
        {
            new
            {
                Title = "Q4 Strategy Document.docx",
                Excerpt = "The Q4 strategy focuses on three key initiatives: expanding market presence in EMEA, " +
                          "launching the new product line by October, and improving customer retention by 15%.",
                SourceUrl = $"{sharePointUrl}/Q4-Strategy.docx",
                RelevanceScore = 0.95
            },
            new
            {
                Title = "Budget Overview FY25.xlsx",
                Excerpt = "Total projected budget for FY25 is $12.4M, with 40% allocated to R&D, " +
                          "30% to sales and marketing, and 30% to operations.",
                SourceUrl = $"{sharePointUrl}/Budget-FY25.xlsx",
                RelevanceScore = 0.88
            },
            new
            {
                Title = "Team Org Chart.pptx",
                Excerpt = "The organization consists of 4 divisions: Engineering (45 headcount), " +
                          "Product (12), Sales (28), and Operations (15). Total headcount: 100.",
                SourceUrl = $"{sharePointUrl}/Org-Chart.pptx",
                RelevanceScore = 0.72
            }
        };

        return JsonSerializer.Serialize(new
        {
            SharePointUrl = sharePointUrl,
            Query = query ?? "all content",
            ResultCount = stubResults.Length,
            Results = stubResults
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
