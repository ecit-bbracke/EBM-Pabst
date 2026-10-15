using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.WebApi.HostedServices;

public class MaintenanceCleanupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MaintenanceCleanupHostedService> _logger;
    private readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(24);

    public MaintenanceCleanupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<MaintenanceCleanupHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Maintenance Cleanup Hosted Service started.");

        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformMaintenanceCycleAsync(stoppingToken);
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during maintenance cleanup cycle.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task PerformMaintenanceCycleAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[MaintenanceCleanup] Starting maintenance and reconciliation cycle...");

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetService<IDocumentRepository>();
        if (repository == null) return;

        var (allDocs, total) = await repository.GetPagedDocumentsAsync(1, 100, null);
        int missingCount = 0;
        foreach (var doc in allDocs)
        {
            if (doc.Status == DocumentStatus.Processed &&
                !string.IsNullOrWhiteSpace(doc.FilePath) &&
                !doc.FilePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(doc.FilePath))
            {
                await repository.UpdateDocumentStatusAsync(doc.Id, DocumentStatus.Failed, errorMessage: "Filen blev ikke fundet på disk.");
                missingCount++;
            }
        }

        sw.Stop();
        _logger.LogInformation("[MaintenanceCleanup] Cycle completed in {DurationMs}ms (Checked {Total} records, Marked {Missing} missing).",
            sw.ElapsedMilliseconds, total, missingCount);
    }
}
