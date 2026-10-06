using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ArticleDocumentNameParserTests
{
    [Theory]
    [InlineData("Art_9293512011-Doc_3258_J_2H3PU-011_PDB_EN.pdf", "9293512011", "3258_J_2H3PU-011_PDB_EN")]
    [InlineData("Art_8300100799-Doc_KM313793.pdf", "8300100799", "KM313793")]
    [InlineData("art_12345-doc_67890.pdf", "12345", "67890")]
    [InlineData("ART_ABC123-DOC_XYZ456.PDF", "ABC123", "XYZ456")]
    [InlineData("Art_9694300352 - Doc_4114N_2H6PU_PDB_EN.pdf", "9694300352", "4114N_2H6PU_PDB_EN")]
    [InlineData("Art_9694300357_Doc_4114N_2H6PU-357_PDB_EN.pdf", "9694300357", "4114N_2H6PU-357_PDB_EN")]
    public void Parse_WhenArtDocPattern_ExtractsArticleIdAndDocumentId(string fileName, string expectedArticleId, string expectedDocId)
    {
        // Act
        var (articleId, documentId) = ArticleDocumentNameParser.Parse(fileName);

        // Assert
        Assert.Equal(expectedArticleId, articleId);
        Assert.Equal(expectedDocId, documentId);
    }

    [Theory]
    [InlineData("9293512011-3258_J_2H3PU-011_PDB_EN.pdf", "9293512011", "3258_J_2H3PU-011_PDB_EN")]
    [InlineData("ART12345-DOC67890.PDF", "ART12345", "DOC67890")]
    [InlineData("8300100799-KM313793.pdf", "8300100799", "KM313793")]
    [InlineData("9694300352-4114N_2H6PU_PDB_EN.pdf", "9694300352", "4114N_2H6PU_PDB_EN")]
    [InlineData("ARTICLE99-SPEC01.pdf", "ARTICLE99", "SPEC01")]
    public void Parse_WhenHyphenatedPattern_ExtractsArticleIdAndDocumentId(string fileName, string expectedArticleId, string expectedDocId)
    {
        // Act
        var (articleId, documentId) = ArticleDocumentNameParser.Parse(fileName);

        // Assert
        Assert.Equal(expectedArticleId, articleId);
        Assert.Equal(expectedDocId, documentId);
    }

    [Fact]
    public void Parse_WhenDataSheetPrefixPattern_ExtractsArticleIdAndDocumentId()
    {
        // Act
        var (articleId, documentId) = ArticleDocumentNameParser.Parse("Data_sheet_US_-_8300100799_VWT0400CTPFS_KM313793_.pdf");

        // Assert
        Assert.Equal("8300100799", articleId);
        Assert.Equal("VWT0400CTPFS_KM313793_", documentId);
    }

    [Fact]
    public void Parse_WhenUnderscorePatternWithNumericCode_ExtractsArticleIdAndDocumentId()
    {
        // Act
        var (articleId, documentId) = ArticleDocumentNameParser.Parse("9293512011_3258_J_2H3PU-011_PDB_EN.PDF");

        // Assert
        Assert.Equal("9293512011", articleId);
        Assert.Equal("3258_J_2H3PU-011_PDB_EN", documentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sample.pdf")]
    [InlineData("datasheet.pdf")]
    public void Parse_WhenUnmatchedOrEmpty_ReturnsNulls(string? fileName)
    {
        // Act
        var (articleId, documentId) = ArticleDocumentNameParser.Parse(fileName);

        // Assert
        Assert.Null(articleId);
        Assert.Null(documentId);
    }
}
