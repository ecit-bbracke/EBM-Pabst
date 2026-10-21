using System;
using System.Threading;
using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

public interface IDocumentQueue
{
    void QueueBackgroundWorkItem(Func<CancellationToken, ValueTask> workItem);
    ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem, CancellationToken cancellationToken = default);
    Task<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken);
}
