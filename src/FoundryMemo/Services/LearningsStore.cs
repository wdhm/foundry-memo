// Copyright (c) foundry-memo. All rights reserved.

using FoundryMemo.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace FoundryMemo.Services;

/// <summary>
/// Cosmos DB-backed store for global process learnings.
/// Never stores sensitive content — only operational improvements.
/// </summary>
public class LearningsStore
{
    private readonly Container _container;

    public LearningsStore(CosmosClient cosmosClient, string databaseName = "foundry-memo", string containerName = "learnings")
    {
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    /// <summary>
    /// Reads all learnings. The set is expected to be small (<100 entries).
    /// </summary>
    public async Task<IReadOnlyList<LearningEntry>> ReadAllAsync()
    {
        var query = _container.GetItemLinqQueryable<LearningEntry>()
            .Where(e => e.PartitionKey == "global")
            .OrderByDescending(e => e.CreatedAt);

        var results = new List<LearningEntry>();
        using var iterator = query.ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    /// <summary>
    /// Writes a new learning entry.
    /// </summary>
    public async Task WriteAsync(LearningEntry entry)
    {
        await _container.CreateItemAsync(entry, new PartitionKey(entry.PartitionKey));
    }
}
