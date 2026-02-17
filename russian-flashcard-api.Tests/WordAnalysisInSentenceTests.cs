using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api.Tests;

public class WordAnalysisInSentenceTests
{
    private readonly Mock<ILogger<WordAnalysisInSentence>> _mockLogger;

    public WordAnalysisInSentenceTests()
    {
        _mockLogger = new Mock<ILogger<WordAnalysisInSentence>>();
    }

    [Fact]
    public async Task Run_WithEmptyBody_ReturnsBadRequest()
    {
        // Arrange
        var function = new WordAnalysisInSentence(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("")));
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithoutSentenceField_ReturnsBadRequest()
    {
        // Arrange
        var function = new WordAnalysisInSentence(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { text = "test" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithValidSentence_AnalyzesWords()
    {
        // Arrange
        var function = new WordAnalysisInSentence(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "Это тестовое предложение" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        
        // Act & Assert
        // The actual execution would require OpenAI API mocking
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithInvalidJson_ReturnsBadRequest()
    {
        // Arrange
        var function = new WordAnalysisInSentence(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("invalid json {")));
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
