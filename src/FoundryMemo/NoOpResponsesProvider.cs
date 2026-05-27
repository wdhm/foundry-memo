// Copyright (c) foundry-memo. All rights reserved.

using Azure.AI.AgentServer.Core;
using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;

namespace FoundryMemo;

/// <summary>
/// No-op <see cref="ResponsesProvider"/> that silently discards all persistence calls.
///
/// The Foundry platform's HTTP-backed <c>FoundryStorageProvider</c> crashes (HTTP 500)
/// when persisting responses that contain <c>function_call_output</c> items (SDK ≥1.7.0).
/// This provider replaces it so the orchestrator's <c>execution.Store</c> path succeeds
/// without making any network calls. Multi-turn still works via the
/// <c>AgentSessionStore</c> (sticky sessions with <c>isResume</c> bypass).
/// </summary>
public class NoOpResponsesProvider : ResponsesProvider
{
    public override Task CreateResponseAsync(
        CreateResponseRequest request, IsolationContext isolation,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override Task<ResponseObject> GetResponseAsync(
        string responseId, IsolationContext isolation,
        CancellationToken cancellationToken = default)
        => throw new ResourceNotFoundException($"Response '{responseId}' not found.");

    public override Task UpdateResponseAsync(
        ResponseObject response, IsolationContext isolation,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override Task DeleteResponseAsync(
        string responseId, IsolationContext isolation,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override Task<AgentsPagedResultOutputItem> GetInputItemsAsync(
        string responseId, IsolationContext isolation, int limit = 20,
        bool ascending = false, string? after = null, string? before = null,
        CancellationToken cancellationToken = default)
        => throw new ResourceNotFoundException($"Response '{responseId}' not found.");

    public override Task<IEnumerable<OutputItem?>> GetItemsAsync(
        IEnumerable<string> itemIds, IsolationContext isolation,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<OutputItem?>>(Array.Empty<OutputItem?>());

    public override Task<IEnumerable<string>> GetHistoryItemIdsAsync(
        string? previousResponseId, string? conversationId, int limit,
        IsolationContext isolation, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
}
