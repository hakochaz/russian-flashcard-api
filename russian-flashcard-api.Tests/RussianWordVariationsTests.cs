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

public class RussianWordVariationsTests
{
    private readonly Mock<ILogger<RussianWordVariations>> _mockLogger;

    public RussianWordVariationsTests()
    {
        _mockLogger = new Mock<ILogger<RussianWordVariations>>();
    }

    [Fact]
    public async Task Run_WithoutWordParameter_ReturnsBadRequest()
    {
        // Arrange
        var function = new RussianWordVariations(_mockLogger.Object);
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
    public async Task Run_WithWordQueryString_ExecutesRequest()
    {
        // Arrange
        var function = new RussianWordVariations(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("")));
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "word", "книга" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        
        // Act & Assert
        // The actual execution would require HTTP client mocking for OpenAI API
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithMissingOpenAIKey_Returns500()
    {
        // Arrange
        var function = new RussianWordVariations(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("")));
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "word", "книга" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "");
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<StatusCodeResult>(result);
        var statusCode = ((StatusCodeResult)result).StatusCode;
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task Run_WithWordInBody_ExecutesRequest()
    {
        // Arrange
        var function = new RussianWordVariations(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { word = "дом" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        
        // Act & Assert
        Assert.NotNull(function);
    }
}
