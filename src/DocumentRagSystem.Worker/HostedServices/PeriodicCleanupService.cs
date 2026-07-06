using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Worker.HostedServices;

public class PeriodicCleanupService : BackgroundService
{
    private readonly ILogger<PeriodicCleanupService> _logger;
    private readonly string _uploadsDirectory;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _maxFileAge = TimeSpan.FromDays(1);

    public PeriodicCleanupService(ILogger<PeriodicCleanupService> logger)
    {
        _logger = logger;
        _uploadsDirectory = Path.Combine(AppContext.BaseDirectory, "uploads");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Periodic Cleanup Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting scheduled cleanup of uploads directory...");
                CleanupOldFiles();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during scheduled uploads cleanup.");
            }

            // Wait for the next interval
            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    private void CleanupOldFiles()
    {
        if (!Directory.Exists(_uploadsDirectory))
        {
            _logger.LogInformation("Uploads directory '{Path}' does not exist. Skipping cleanup.", _uploadsDirectory);
            return;
        }

        var files = Directory.GetFiles(_uploadsDirectory);
        int deletedCount = 0;

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _maxFileAge)
            {
                try
                {
                    fileInfo.Delete();
                    _logger.LogInformation("Deleted old temporary file: {FileName}", fileInfo.Name);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to delete file '{FileName}': {Message}", fileInfo.Name, ex.Message);
                }
            }
        }

        _logger.LogInformation("Scheduled cleanup completed. Total files deleted: {Count}", deletedCount);
    }
}
