using System;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.WebApi.HostedServices;

public class QueuedHostedService : BackgroundService
{
    private readonly IDocumentQueue _taskQueue;
    private readonly ILogger<QueuedHostedService> _logger;

    public QueuedHostedService(IDocumentQueue taskQueue, ILogger<QueuedHostedService> logger)
    {
        _taskQueue = taskQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Queued Hosted Service started and listening on channel.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workItem = await _taskQueue.DequeueAsync(stoppingToken);
                _logger.LogInformation("Dequeued work item; executing in background...");
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Prevent exception being thrown on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing background task.");
            }
        }

        _logger.LogInformation("Queued Hosted Service has stopped.");
    }
}
