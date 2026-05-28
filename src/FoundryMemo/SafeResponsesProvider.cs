// Copyright (c) foundry-memo. All rights reserved.

using Azure.AI.AgentServer.Core;
using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;
using Microsoft.Extensions.Logging;

namespace FoundryMemo;

/// <summary>
/// Wraps the platform's <c>FoundryStorageProvider</c> and catches storage errors
/// on write operations instead of letting them propagate to the orchestrator.
///
/// The Foundry platform's storage service crashes (HTTP 500) when persisting
/// responses containing <c>function_call_output</c> items (SDK ≥1.7.0). If the
/// exception propagates, the orchestrator replaces <c>response.completed</c> with
/// <c>response.failed(storage_error)</c> and the Playground shows an error banner.
///
/// This wrapper lets the storage call happen (so the Gateway sees the POST attempt)
/// but swallows failures so the orchestrator emits a clean <c>response.completed</c>.
/// Read operations delegate directly — they're needed for history resolution.
/// </summary>
public class SafeResponsesProvider(ResponsesProvider inner, ILogger<SafeResponsesProvider> logger) : ResponsesProvider
{
    public override async Task CreateResponseAsync(
        CreateResponseRequest request, IsolationContext isolation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.CreateResponseAsync(request, isolation, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storage CreateResponseAsync failed (swallowed)");
        }
    }

    public override async Task UpdateResponseAsync(
        ResponseObject response, IsolationContext isolation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.UpdateResponseAsync(response, isolation, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storage UpdateResponseAsync failed (swallowed)");
        }
    }

    public override Task<ResponseObject> GetResponseAsync(
        string responseId, IsolationContext isolation,
        CancellationToken cancellationToken = default)
        => inner.GetResponseAsync(responseId, isolation, cancellationToken);

    public override Task DeleteResponseAsync(
        string responseId, IsolationContext isolation,
        CancellationToken cancellationToken = default)
        => inner.DeleteResponseAsync(responseId, isolation, cancellationToken);

    public override Task<AgentsPagedResultOutputItem> GetInputItemsAsync(
        string responseId, IsolationContext isolation, int limit = 20,
        bool ascending = false, string? after = null, string? before = null,
        CancellationToken cancellationToken = default)
        => inner.GetInputItemsAsync(responseId, isolation, limit, ascending, after, before, cancellationToken);

    public override Task<IEnumerable<OutputItem?>> GetItemsAsync(
        IEnumerable<string> itemIds, IsolationContext isolation,
        CancellationToken cancellationToken = default)
        => inner.GetItemsAsync(itemIds, isolation, cancellationToken);

    public override Task<IEnumerable<string>> GetHistoryItemIdsAsync(
        string? previousResponseId, string? conversationId, int limit,
        IsolationContext isolation, CancellationToken cancellationToken = default)
        => inner.GetHistoryItemIdsAsync(previousResponseId, conversationId, limit, isolation, cancellationToken);
}
