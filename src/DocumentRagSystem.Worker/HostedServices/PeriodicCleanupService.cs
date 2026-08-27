using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Worker.HostedServices;

public class PeriodicCleanupService : BackgroundService
{
    private readonly ILogger<PeriodicCleanupService> _logger;
    private readonly IVectorStore _vectorStore;
    private readonly string _uploadsDirectory;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _maxFileAge = TimeSpan.FromDays(1);

    public PeriodicCleanupService(ILogger<PeriodicCleanupService> logger, IVectorStore vectorStore)
    {
        _logger = logger;
        _vectorStore = vectorStore;
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
                await CleanupOldFilesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during scheduled uploads cleanup.");
            }

            // Wait for the next interval
            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    private async Task CleanupOldFilesAsync()
    {
        if (!Directory.Exists(_uploadsDirectory))
        {
            _logger.LogInformation("Uploads directory '{Path}' does not exist. Skipping cleanup.", _uploadsDirectory);
            return;
        }

        // Fetch all active filenames from the vector store to avoid deleting them
        HashSet<string> activeFileNames = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var documents = await _vectorStore.GetDocumentsAsync();
            if (documents != null)
            {
                foreach (var doc in documents)
                {
                    if (!string.IsNullOrWhiteSpace(doc.FilePath))
                    {
                        var fileName = Path.GetFileName(Uri.UnescapeDataString(doc.FilePath));
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            activeFileNames.Add(fileName);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(doc.FileName))
                    {
                        activeFileNames.Add(doc.FileName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch active documents from vector store. Skipping cleanup to prevent data loss.");
            return;
        }

        var files = Directory.GetFiles(_uploadsDirectory);
        int deletedCount = 0;

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            var name = fileInfo.Name;

            // If the file is still active in the vector store, NEVER delete it
            if (activeFileNames.Contains(name))
            {
                continue;
            }

            // Also check if we can match by stripping Guid prefix (36 characters followed by '_')
            if (name.Length > 37 && name[36] == '_')
            {
                var rawName = name.Substring(37);
                if (activeFileNames.Contains(rawName))
                {
                    continue;
                }
            }

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
