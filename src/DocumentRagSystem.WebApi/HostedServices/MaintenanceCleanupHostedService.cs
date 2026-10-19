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
    private readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(5);
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

        var allDocs = (await repository.GetAllDocumentsAsync()).ToList();
        int healedCount = 0;

        foreach (var doc in allDocs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (doc.Status == DocumentStatus.Failed && doc.ErrorMessage == "Filen blev ikke fundet på disk.")
            {
                var restoredStatus = !string.IsNullOrWhiteSpace(doc.Language) && !doc.Language.Equals("en", StringComparison.OrdinalIgnoreCase)
                    ? DocumentStatus.Skipped
                    : DocumentStatus.Processed;

                await repository.UpdateDocumentStatusAsync(doc.Id, restoredStatus, errorMessage: null);
                healedCount++;
            }
        }

        sw.Stop();
        _logger.LogInformation("[MaintenanceCleanup] Reconciliation cycle completed in {DurationMs}ms (Checked {Total} records, Healed {Healed} records).",
            sw.ElapsedMilliseconds, allDocs.Count, healedCount);
    }
}
