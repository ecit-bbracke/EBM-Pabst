using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Data;
using DocumentRagSystem.Infrastructure.VectorStores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class SqliteFtsStoreAndRrfTests
{
    private class TestDbContextFactory : IDbContextFactory<DocumentDbContext>
    {
        private readonly string _connectionString;
        public TestDbContextFactory(string connectionString) => _connectionString = connectionString;
        public DocumentDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<DocumentDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new DocumentDbContext(options);
        }
    }

    [Fact]
    public void ReciprocalRankFusion_CombinesRanksCorrectly()
    {
        // Arrange
        var chunk1 = new DocumentChunk("c1", "doc1", "Modbus register 40001 actual speed", 0);
        var chunk2 = new DocumentChunk("c2", "doc2", "General airflow specification", 0);
        var chunk3 = new DocumentChunk("c3", "doc3", "Wiring diagram YE/GN earth", 0);

        var vectorResults = new List<DocumentChunk> { chunk2, chunk1 };
        var bm25Results = new List<DocumentChunk> { chunk1, chunk3 };

        // Act - chunk1 is at rank 2 in vector and rank 1 in BM25, so its score is 1/62 + 1/61
        var fused = ReciprocalRankFusion.Fuse(vectorResults, bm25Results, k: 60, maxResults: 3);

        // Assert
        Assert.Equal(3, fused.Count);
        Assert.Equal("c1", fused[0].Id); // Both vector and BM25 found c1, so it takes rank 1!
    }

    [Fact]
    public async Task SqliteFtsStore_IndexesAndSearchesBm25Correctly()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"fts_test_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        var factory = new TestDbContextFactory(connStr);

        try
        {
            var ftsStore = new SqliteFtsStore(factory);

            var chunkA = new DocumentChunk("cA", "docA", "Modbus RTU Register 40001 represents the actual motor speed in RPM.", 0, "A3G910.pdf", "/uploads/A3G910.pdf", DateTime.UtcNow, "A3G910.pdf", "9293512011", "docA");
            var chunkB = new DocumentChunk("cB", "docB", "Mains connection 3~ 400V 50Hz, wiring colors: YE/GN is PE, BU is neutral, BN is phase L1.", 0, "K3G560.pdf", "/uploads/K3G560.pdf", DateTime.UtcNow, "K3G560.pdf", "9295420021", "docB");
            var chunkC = new DocumentChunk("cC", "docC", "Standard acoustic measurements according to ISO 13347.", 0, "4114N.pdf", "/uploads/4114N.pdf", DateTime.UtcNow, "4114N.pdf", "9694300352", "docC");

            // Act 1: Index chunks
            await ftsStore.AddChunksAsync(new[] { chunkA, chunkB, chunkC });

            // Act 2: Search for Modbus Register 40001
            var modbusResults = (await ftsStore.SearchAsync("Register 40001")).ToList();

            // Assert 2
            Assert.NotEmpty(modbusResults);
            Assert.Equal("cA", modbusResults[0].Id);
            Assert.Contains("40001", modbusResults[0].Text);

            // Act 3: Search for wiring color YE/GN
            var wiringResults = (await ftsStore.SearchAsync("YE/GN")).ToList();

            // Assert 3
            Assert.NotEmpty(wiringResults);
            Assert.Equal("cB", wiringResults[0].Id);

            // Act 4: Delete docA chunks
            await ftsStore.DeleteChunksByDocumentIdAsync("docA");
            var afterDelete = (await ftsStore.SearchAsync("40001")).ToList();

            // Assert 4
            Assert.Empty(afterDelete);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task SqliteFtsStore_ShouldPersistAndRestorePageNumber()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"fts_page_test_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        var factory = new TestDbContextFactory(connStr);

        try
        {
            var ftsStore = new SqliteFtsStore(factory);

            var chunkWithPage = new DocumentChunk(
                Id: "cPage3",
                DocumentId: "docP",
                Text: "Technical specifications on pressure and airflow characteristics.",
                Index: 1,
                FileName: "datasheet.pdf",
                FilePath: "/uploads/datasheet.pdf",
                UploadedAt: DateTime.UtcNow,
                PageNumber: 3
            );

            // Act
            await ftsStore.AddChunksAsync(new[] { chunkWithPage });
            var results = (await ftsStore.SearchAsync("airflow characteristics")).ToList();

            // Assert
            Assert.NotEmpty(results);
            Assert.Equal("cPage3", results[0].Id);
            Assert.Equal(3, results[0].PageNumber);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }
}
