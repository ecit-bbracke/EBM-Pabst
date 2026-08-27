using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IDocumentProcessor _documentProcessor;
    private readonly IVectorStore _vectorStore;

    public Worker(ILogger<Worker> logger, IDocumentProcessor documentProcessor, IVectorStore vectorStore)
    {
        _logger = logger;
        _documentProcessor = documentProcessor;
        _vectorStore = vectorStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started. Checking for PDFs in data/Generelt...");
        bool processDataDirectory = true;

        if (processDataDirectory)
        {
            try
            {
                var dataDir = ResolveDataDirectory();
                if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir))
                {
                    _logger.LogWarning("PDF data directory not found. Please ensure 'data/Generelt' exists.");
                }
                else
                {
                    _logger.LogInformation("Found PDF data directory at: {DataDir}", dataDir);
                    var pdfFiles = Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories)
                        .Where(file => file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    _logger.LogInformation("Found {Count} PDF file(s) to process.", pdfFiles.Length);

                    List<Document> existingDocs = new();
                    try
                    {
                        var docs = await _vectorStore.GetDocumentsAsync();
                        if (docs != null)
                        {
                            existingDocs = docs.ToList();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch existing documents from vector store. Will proceed with full indexing.");
                    }

                    var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
                    if (!Directory.Exists(uploadsDir))
                    {
                        Directory.CreateDirectory(uploadsDir);
                    }

                    foreach (var pdfFile in pdfFiles)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var fileName = Path.GetFileName(pdfFile);
                        _logger.LogInformation("Processing PDF: {FileName}", fileName);

                        try
                        {
                            // Check if this file has already been processed and is in the vector store
                            var existingDoc = existingDocs.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                            if (existingDoc != null)
                            {
                                _logger.LogInformation("PDF '{FileName}' is already indexed in the vector store.", fileName);

                                // Check if the file is missing in the uploads directory
                                var targetFileName = Path.GetFileName(Uri.UnescapeDataString(existingDoc.FilePath));
                                var targetPath = Path.Combine(uploadsDir, targetFileName);

                                if (!File.Exists(targetPath))
                                {
                                    _logger.LogInformation("Restoring missing file to uploads: {TargetFileName}", targetFileName);
                                    File.Copy(pdfFile, targetPath, overwrite: true);
                                }
                                continue;
                            }

                            using var stream = new FileStream(pdfFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var document = await _documentProcessor.ProcessPdfAsync(stream, fileName);
                            _logger.LogInformation("Successfully processed and indexed: {FileName}. Status: {Status}", fileName, document.Status);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to process PDF: {FileName}", fileName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during startup PDF processing.");
            }

            _logger.LogInformation("Startup PDF processing completed. Entering idle loop.");

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                }
                await Task.Delay(30000, stoppingToken);
            }
        }
    }

    private string? ResolveDataDirectory()
    {
        // 1. Try directly in current working directory
        var currentDir = Directory.GetCurrentDirectory();
        var path = Path.Combine(currentDir, "data", "Generelt");
        if (Directory.Exists(path)) return path;

        // 2. Try AppContext.BaseDirectory
        var baseDir = AppContext.BaseDirectory;
        path = Path.Combine(baseDir, "data", "Generelt");
        if (Directory.Exists(path)) return path;

        // 3. Try traversing upwards from AppContext.BaseDirectory
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            path = Path.Combine(dir.FullName, "data", "Generelt");
            if (Directory.Exists(path)) return path;
            dir = dir.Parent;
        }

        // 4. Try traversing upwards from CurrentDirectory
        dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            path = Path.Combine(dir.FullName, "data", "Generelt");
            if (Directory.Exists(path)) return path;
            dir = dir.Parent;
        }

        return null;
    }
}
