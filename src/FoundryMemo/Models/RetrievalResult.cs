// Copyright (c) foundry-memo. All rights reserved.

using System.Text.Json.Serialization;

namespace FoundryMemo.Models;

/// <summary>
/// Represents a single result from the Copilot Retrieval API.
/// </summary>
public record RetrievalResult
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("excerpt")]
    public required string Excerpt { get; init; }

    [JsonPropertyName("sourceUrl")]
    public required string SourceUrl { get; init; }

    [JsonPropertyName("relevanceScore")]
    public double RelevanceScore { get; init; }
}

/// <summary>
/// Wrapper for the Copilot Retrieval API response.
/// </summary>
public record RetrievalResponse
{
    [JsonPropertyName("sharePointUrl")]
    public required string SharePointUrl { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("resultCount")]
    public int ResultCount { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<RetrievalResult> Results { get; init; }
}
