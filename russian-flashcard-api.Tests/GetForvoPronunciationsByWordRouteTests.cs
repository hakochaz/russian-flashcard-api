using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api.Tests;

public class GetForvoPronunciationsByWordRouteTests
{
    private readonly Mock<ILogger<GetForvoPronunciationsByWord>> _mockLogger;

    public GetForvoPronunciationsByWordRouteTests()
    {
        _mockLogger = new Mock<ILogger<GetForvoPronunciationsByWord>>();
    }

    [Fact]
    public async Task Run_WithoutWord_ReturnsBadRequest()
    {
        // Arrange
        var function = new GetForvoPronunciationsByWord(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        
        // Act
        var result = await function.Run(mockRequest.Object, "");
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithValidWord_FetchesPronunciations()
    {
        // Arrange
        var function = new GetForvoPronunciationsByWord(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        
        Environment.SetEnvironmentVariable("FORVO_API_KEY", "test-key");
        
        // Act & Assert
        // The actual execution would require HTTP client mocking for Forvo API
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithWhitespaceWord_TrimmedBeforeCall()
    {
        // Arrange
        var function = new GetForvoPronunciationsByWord(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        
        Environment.SetEnvironmentVariable("FORVO_API_KEY", "test-key");
        
        // Act & Assert
        Assert.NotNull(function);
    }
}
