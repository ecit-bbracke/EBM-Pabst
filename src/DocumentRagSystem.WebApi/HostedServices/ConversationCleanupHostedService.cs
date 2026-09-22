using System;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.WebApi.HostedServices;

public class ConversationCleanupHostedService : BackgroundService
{
    private readonly IConversationStateStore _stateStore;
    private readonly ILogger<ConversationCleanupHostedService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _sessionTtl = TimeSpan.FromHours(24);

    public ConversationCleanupHostedService(
        IConversationStateStore stateStore,
        ILogger<ConversationCleanupHostedService> logger
    )
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Conversation Cleanup Hosted Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
                _logger.LogInformation("Running scheduled conversation state cleanup (TTL: {Ttl})...", _sessionTtl);
                await _stateStore.ClearExpiredStatesAsync(_sessionTtl);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled conversation state cleanup.");
            }
        }
    }
}
