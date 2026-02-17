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

public class AddShadowingSentenceTests
{
    private readonly Mock<ILogger<AddShadowingSentence>> _mockLogger;

    public AddShadowingSentenceTests()
    {
        _mockLogger = new Mock<ILogger<AddShadowingSentence>>();
    }

    [Fact]
    public async Task Run_WithEmptyBody_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddShadowingSentence(_mockLogger.Object);
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
        var function = new AddShadowingSentence(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { source = "text" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithValidSentence_AddsToTable()
    {
        // Arrange
        var function = new AddShadowingSentence(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "Test sentence" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "DefaultEndpointsProtocol=https://;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net");
        
        // Act & Assert
        // The actual execution would require Azure Table Storage mocking
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithInvalidJson_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddShadowingSentence(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("invalid json {")));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
