// Copyright (c) foundry-memo. All rights reserved.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using FoundryMemo.Models;

namespace FoundryMemo.Services;

/// <summary>
/// Calls the Microsoft 365 Copilot Retrieval API to fetch content from SharePoint.
/// Uses delegated user credentials (Files.Read.All + Sites.Read.All).
/// </summary>
public class CopilotRetrievalService
{
    private static readonly string[] GraphScopes =
    [
        "https://graph.microsoft.com/Files.Read.All",
        "https://graph.microsoft.com/Sites.Read.All"
    ];

    private const string RetrievalEndpoint = "https://graph.microsoft.com/v1.0/copilot/retrieval";

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;

    public CopilotRetrievalService(TokenCredential credential, HttpClient? httpClient = null)
    {
        _credential = credential;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Retrieves content from SharePoint using the Copilot Retrieval API.
    /// </summary>
    /// <param name="query">Natural language query (max 1500 chars).</param>
    /// <param name="filterExpression">Optional KQL filter (e.g., "Path:https://contoso.sharepoint.com/sites/hr").</param>
    /// <param name="maxResults">Number of results (1-25).</param>
    public async Task<RetrievalResponse> RetrieveAsync(
        string query,
        string? filterExpression = null,
        int maxResults = 25)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(GraphScopes),
            CancellationToken.None);

        var requestBody = new Dictionary<string, object>
        {
            ["queryString"] = query,
            ["dataSource"] = "sharePoint",
            ["resourceMetadata"] = new[] { "title", "author" },
            ["maximumNumberOfResults"] = Math.Clamp(maxResults, 1, 25)
        };

        if (!string.IsNullOrWhiteSpace(filterExpression))
        {
            requestBody["filterExpression"] = filterExpression;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, RetrievalEndpoint)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Copilot Retrieval API returned {(int)response.StatusCode}: {errorBody}");
        }

        return await response.Content.ReadFromJsonAsync<RetrievalResponse>()
               ?? new RetrievalResponse();
    }

    /// <summary>
    /// Builds a KQL filter expression to scope retrieval to a specific SharePoint site/path.
    /// </summary>
    public static string BuildPathFilter(string sharePointUrl)
    {
        // KQL Path filter scopes to a site or folder
        return $"Path:\"{sharePointUrl}\"";
    }
}
