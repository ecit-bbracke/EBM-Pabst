using System;
using System.Threading;
using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

public interface IDocumentQueue
{
    void QueueBackgroundWorkItem(Func<CancellationToken, ValueTask> workItem);
    Task<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken);
}
