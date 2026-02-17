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

public class AddMinimalPairTests
{
    private readonly Mock<ILogger<AddMinimalPair>> _mockLogger;

    public AddMinimalPairTests()
    {
        _mockLogger = new Mock<ILogger<AddMinimalPair>>();
    }

    [Fact]
    public async Task Run_WithoutPairField_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddMinimalPair(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { difficulty = "easy" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        var badRequest = (BadRequestObjectResult)result;
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task Run_WithoutDifficultyField_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddMinimalPair(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { pair = "тот/тот" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithMissingConnectionString_Returns500()
    {
        // Arrange
        var function = new AddMinimalPair(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { pair = "тот/тот", difficulty = "easy" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "");
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<StatusCodeResult>(result);
        var statusCode = ((StatusCodeResult)result).StatusCode;
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task Run_WithEmptyBody_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddMinimalPair(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("")));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithInvalidJson_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddMinimalPair(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("invalid json {")));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
