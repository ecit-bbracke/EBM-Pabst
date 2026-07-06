using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;

namespace DocumentRagSystem.Core.Services;

public class DocumentQueue : IDocumentQueue
{
    private readonly Channel<Func<CancellationToken, ValueTask>> _queue;

    public DocumentQueue(int capacity = 100)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<Func<CancellationToken, ValueTask>>(options);
    }

    public void QueueBackgroundWorkItem(Func<CancellationToken, ValueTask> workItem)
    {
        if (workItem == null)
            throw new ArgumentNullException(nameof(workItem));

        if (!_queue.Writer.TryWrite(workItem))
        {
            throw new InvalidOperationException("The background work queue is full.");
        }
    }

    public async Task<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}
