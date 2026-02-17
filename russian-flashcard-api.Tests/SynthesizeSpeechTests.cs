using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api.Tests;

public class SynthesizeSpeechTests
{
    private readonly Mock<ILogger<SynthesizeSpeech>> _mockLogger;

    public SynthesizeSpeechTests()
    {
        _mockLogger = new Mock<ILogger<SynthesizeSpeech>>();
    }

    [Fact]
    public async Task Run_WithEmptyBody_ReturnsBadRequest()
    {
        // Arrange
        var function = new SynthesizeSpeech(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("")));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithoutSentenceField_ReturnsBadRequest()
    {
        // Arrange
        var function = new SynthesizeSpeech(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { fileName = "test.mp3" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithEmptySentence_ReturnsBadRequest()
    {
        // Arrange
        var function = new SynthesizeSpeech(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithMissingSpeechKey_Returns500()
    {
        // Arrange
        var function = new SynthesizeSpeech(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "Hello world" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        Environment.SetEnvironmentVariable("SPEECH_SERVICE_KEY", "");
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<StatusCodeResult>(result);
        var statusCode = ((StatusCodeResult)result).StatusCode;
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task Run_WithMissingStorageConnection_Returns500()
    {
        // Arrange
        var function = new SynthesizeSpeech(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "Hello world" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        Environment.SetEnvironmentVariable("SPEECH_SERVICE_KEY", "test-key");
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "");
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<StatusCodeResult>(result);
        var statusCode = ((StatusCodeResult)result).StatusCode;
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task Run_WithInvalidJson_ReturnsBadRequest()
    {
        // Arrange
        var function = new SynthesizeSpeech(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("invalid json {")));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
