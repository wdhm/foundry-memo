// Copyright (c) foundry-memo. All rights reserved.

namespace FoundryMemo.Services;

/// <summary>
/// HTTP client for the Microsoft 365 Copilot Retrieval API.
/// Will be implemented in M2 when we integrate with the live API.
/// </summary>
public class RetrievalApiClient
{
    private readonly HttpClient _httpClient;
    private const string RetrievalEndpoint = "https://graph.microsoft.com/v1.0/copilot/retrieval/search";

    public RetrievalApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // TODO (M2): Implement live Copilot Retrieval API call
    // POST https://graph.microsoft.com/v1.0/copilot/retrieval/search
    // Authorization: Bearer {user_delegated_token}
    // Body: { "queryString": "...", "dataSource": "sharePoint",
    //         "filterExpression": "path:\"<url>\"", "maximumNumberOfResults": 25 }
}
