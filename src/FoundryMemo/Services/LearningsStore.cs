// Copyright (c) foundry-memo. All rights reserved.

using FoundryMemo.Models;
using Microsoft.Azure.Cosmos;

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
    /// Reads all learnings. The set is expected to be small (&lt;100 entries).
    /// </summary>
    public async Task<IReadOnlyList<LearningEntry>> ReadAllAsync()
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.pk = 'global' ORDER BY c.createdAt DESC");
        var results = new List<LearningEntry>();

        using var iterator = _container.GetItemQueryIterator<LearningEntry>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey("global")
        });

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
