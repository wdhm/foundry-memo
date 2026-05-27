// Copyright (c) foundry-memo. All rights reserved.

using System.Runtime.CompilerServices;
using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;

namespace FoundryMemo;

/// <summary>
/// Wraps the default <see cref="AgentFrameworkResponseHandler"/> and sets
/// <c>Store = false</c> on every inbound request before delegating.
///
/// The Foundry platform's storage service returns HTTP 500 when persisting
/// responses that contain <c>function_call_output</c> items (SDK ≥1.7.0).
/// Setting <c>Store = false</c> tells the <see cref="ResponseEventStream"/>
/// to skip platform storage entirely. Multi-turn still works via the
/// <c>AgentSessionStore</c> (sticky sessions with <c>isResume</c> bypass).
/// </summary>
public class NoStoreResponseHandler(ResponseHandler inner) : ResponseHandler
{
    public override async IAsyncEnumerable<ResponseStreamEvent> CreateAsync(
        CreateResponse request,
        ResponseContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Store = false;

        await foreach (var evt in inner.CreateAsync(request, context, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return evt;
        }
    }
}
