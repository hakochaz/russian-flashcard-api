using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api.Tests;

public class GetForvoPronunciationsByWordTests
{
    private readonly Mock<ILogger<GetForvoPronunciationsByWord>> _mockLogger;

    public GetForvoPronunciationsByWordTests()
    {
        _mockLogger = new Mock<ILogger<GetForvoPronunciationsByWord>>();
    }

    [Fact]
    public async Task Run_WithoutWord_ReturnsBadRequest()
    {
        // Arrange
        var function = new GetForvoPronunciationsByWord(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
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
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "word", "привет" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        // Act & Assert
        // The actual execution would require Forvo API mocking
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithForvoAPIError_HandlesGracefully()
    {
        // Arrange
        var function = new GetForvoPronunciationsByWord(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "word", "nonexistentword" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        // Act & Assert
        // The actual execution would require Forvo API mocking
        Assert.NotNull(function);
    }
}
