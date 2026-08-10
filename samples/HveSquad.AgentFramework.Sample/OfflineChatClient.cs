using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Sample;

/// <summary>
/// A chat client that never calls a model. It lets the graph be assembled and rendered without
/// credentials; any attempt to actually run the workflow fails loudly.
/// </summary>
internal sealed class OfflineChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("OfflineChatClient renders graphs only; supply a real IChatClient to run.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("OfflineChatClient renders graphs only; supply a real IChatClient to run.");
#pragma warning disable CS0162 // Required to satisfy the iterator contract.
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
