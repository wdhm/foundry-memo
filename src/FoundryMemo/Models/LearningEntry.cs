// Copyright (c) foundry-memo. All rights reserved.

using System.Text.Json.Serialization;

namespace FoundryMemo.Models;

/// <summary>
/// A process learning entry. Never stores sensitive content, user data, or file excerpts.
/// Only operational improvements about how the agent performs its task.
/// </summary>
public record LearningEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Fixed partition key — all learnings are global.
    /// </summary>
    [JsonPropertyName("pk")]
    public string PartitionKey { get; init; } = "global";

    /// <summary>
    /// The process learning or insight.
    /// Examples:
    /// - "Excel files return raw cell data — restructure as tables before summarizing"
    /// - "Retrieval API max 25 chunks — use multiple queries for large sites"
    /// </summary>
    [JsonPropertyName("learning")]
    public required string Learning { get; init; }

    /// <summary>
    /// Category: retrieval, summarization, pdf_generation, error_handling, general
    /// </summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Categories for learning entries.
/// </summary>
public static class LearningCategory
{
    public const string Retrieval = "retrieval";
    public const string Summarization = "summarization";
    public const string PdfGeneration = "pdf_generation";
    public const string ErrorHandling = "error_handling";
    public const string General = "general";
}
