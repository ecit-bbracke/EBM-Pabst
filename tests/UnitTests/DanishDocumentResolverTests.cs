using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class DanishDocumentResolverTests
{
    private readonly Mock<IDocumentRepository> _mockRepo = new();
    private readonly IEbmProductCodeParser _parser = new EbmProductCodeParser();

    [Fact]
    public async Task FindDanishCompanionDocumentsAsync_WhenFilenamesHaveNoLanguageCode_MatchesByArticleIdAndDetectedLanguage()
    {
        // Arrange - Filenames have NO language codes: Art_<articleid>-Doc_<documentid>.pdf
        var englishDoc = new Document(
            Id: "doc-en-1",
            FileName: "Art_9293512011-Doc_1001.pdf",
            FilePath: "/uploads/Art_9293512011-Doc_1001.pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "en",
            ArticleId: "9293512011",
            SourceDocumentId: "1001"
        );

        var danishDoc = new Document(
            Id: "doc-da-1",
            FileName: "Art_9293512011-Doc_2002.pdf",
            FilePath: "/uploads/Art_9293512011-Doc_2002.pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "da",
            ArticleId: "9293512011",
            SourceDocumentId: "2002"
        );

        _mockRepo.Setup(x => x.GetAllDocumentsAsync())
            .ReturnsAsync(new List<Document> { englishDoc, danishDoc });

        var resolver = new DanishDocumentResolver(_mockRepo.Object, codeParser: _parser);

        var referencedChunks = new List<DocumentChunk>
        {
            new("c1", englishDoc.Id, "Specification text", 0, englishDoc.FileName, englishDoc.FilePath, englishDoc.UploadedAt, englishDoc.FileName, "9293512011", "1001")
        };

        // Act
        var result = (await resolver.FindDanishCompanionDocumentsAsync(referencedChunks)).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("Art_9293512011-Doc_2002.pdf", result[0].FileName);
        Assert.Equal("9293512011", result[0].ArticleId);
        Assert.Equal("da", result[0].Language);
    }

    [Fact]
    public async Task FindDanishCompanionDocumentsAsync_WhenDifferentArticleId_DoesNotMatch()
    {
        // Arrange
        var englishDoc = new Document(
            Id: "doc-en-2",
            FileName: "Art_11111-Doc_100.pdf",
            FilePath: "/uploads/Art_11111-Doc_100.pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "en",
            ArticleId: "11111",
            SourceDocumentId: "100"
        );

        var danishDoc = new Document(
            Id: "doc-da-2",
            FileName: "Art_22222-Doc_200.pdf",
            FilePath: "/uploads/Art_22222-Doc_200.pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "da",
            ArticleId: "22222",
            SourceDocumentId: "200"
        );

        _mockRepo.Setup(x => x.GetAllDocumentsAsync())
            .ReturnsAsync(new List<Document> { englishDoc, danishDoc });

        var resolver = new DanishDocumentResolver(_mockRepo.Object, codeParser: _parser);

        var referencedChunks = new List<DocumentChunk>
        {
            new("c2", englishDoc.Id, "Some text", 0, englishDoc.FileName, englishDoc.FilePath, ArticleId: "11111")
        };

        // Act
        var result = (await resolver.FindDanishCompanionDocumentsAsync(referencedChunks)).ToList();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindDanishCompanionDocumentsAsync_WhenProductCodeMatches_ReturnsDanishCompanion()
    {
        // Arrange
        var englishDoc = new Document(
            Id: "doc-en-3",
            FileName: "Data_sheet_US_-_K3G560PC0401_KM260717_.pdf",
            FilePath: "/uploads/Data_sheet_US_-_K3G560PC0401_KM260717_.pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "en"
        );

        var danishDoc = new Document(
            Id: "doc-da-3",
            FileName: "Data_sheet_DA_-_K3G560PC0401_KM260717_ (1).pdf",
            FilePath: "/uploads/Data_sheet_DA_-_K3G560PC0401_KM260717_ (1).pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "da"
        );

        _mockRepo.Setup(x => x.GetAllDocumentsAsync())
            .ReturnsAsync(new List<Document> { englishDoc, danishDoc });

        var resolver = new DanishDocumentResolver(_mockRepo.Object, codeParser: _parser);

        var referencedChunks = new List<DocumentChunk>
        {
            new("c3", englishDoc.Id, "Motor operating curve", 0, englishDoc.FileName, englishDoc.FilePath)
        };

        // Act
        var result = (await resolver.FindDanishCompanionDocumentsAsync(referencedChunks)).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("Data_sheet_DA_-_K3G560PC0401_KM260717_ (1).pdf", result[0].FileName);
    }

    [Fact]
    public async Task FindDanishCompanionDocumentsAsync_DeduplicatesMultipleChunksFromSameDocument()
    {
        // Arrange - Filenames have NO language codes
        var englishDoc = new Document(
            Id: "doc-en-5",
            FileName: "Art_9293512011-Doc_1001.pdf",
            FilePath: "/uploads/Art_9293512011-Doc_1001.pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "en",
            ArticleId: "9293512011"
        );

        var danishDoc = new Document(
            Id: "doc-da-5",
            FileName: "Art_9293512011-Doc_2002.pdf",
            FilePath: "/uploads/Art_9293512011-Doc_2002.pdf",
            UploadedAt: DateTime.UtcNow,
            Language: "da",
            ArticleId: "9293512011"
        );

        _mockRepo.Setup(x => x.GetAllDocumentsAsync())
            .ReturnsAsync(new List<Document> { englishDoc, danishDoc });

        var resolver = new DanishDocumentResolver(_mockRepo.Object, codeParser: _parser);

        // Multiple chunks from the same English document
        var referencedChunks = new List<DocumentChunk>
        {
            new("c1", englishDoc.Id, "Page 1 text", 0, englishDoc.FileName, englishDoc.FilePath, ArticleId: "9293512011"),
            new("c2", englishDoc.Id, "Page 2 text", 1, englishDoc.FileName, englishDoc.FilePath, ArticleId: "9293512011"),
            new("c3", englishDoc.Id, "Page 3 text", 2, englishDoc.FileName, englishDoc.FilePath, ArticleId: "9293512011")
        };

        // Act
        var result = (await resolver.FindDanishCompanionDocumentsAsync(referencedChunks)).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("Art_9293512011-Doc_2002.pdf", result[0].FileName);
    }
}
