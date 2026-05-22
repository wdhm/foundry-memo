// Copyright (c) foundry-memo. All rights reserved.

using System.Text.Json.Serialization;

namespace FoundryMemo.Models;

/// <summary>
/// Response from the Microsoft 365 Copilot Retrieval API.
/// POST https://graph.microsoft.com/v1.0/copilot/retrieval
/// </summary>
public record RetrievalResponse
{
    [JsonPropertyName("retrievalHits")]
    public List<RetrievalHit> RetrievalHits { get; init; } = [];
}

public record RetrievalHit
{
    [JsonPropertyName("webUrl")]
    public string WebUrl { get; init; } = string.Empty;

    [JsonPropertyName("extracts")]
    public List<RetrievalExtract> Extracts { get; init; } = [];

    [JsonPropertyName("resourceType")]
    public string? ResourceType { get; init; }

    [JsonPropertyName("resourceMetadata")]
    public Dictionary<string, string>? ResourceMetadata { get; init; }
}

public record RetrievalExtract
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("relevanceScore")]
    public double RelevanceScore { get; init; }
}
