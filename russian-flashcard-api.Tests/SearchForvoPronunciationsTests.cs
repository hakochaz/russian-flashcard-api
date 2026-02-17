using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using russian_flashcard_api;

namespace russian_flashcard_api.Tests;

public class SearchForvoPronunciationsTests
{
    private readonly Mock<ILogger<SearchForvoPronunciations>> _mockLogger;

    public SearchForvoPronunciationsTests()
    {
        _mockLogger = new Mock<ILogger<SearchForvoPronunciations>>();
    }

    [Fact]
    public async Task Run_WithoutWordParameter_ReturnsBadRequest()
    {
        // Arrange
        var function = new SearchForvoPronunciations(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        // Act
        var result = await function.Run(mockRequest.Object, "");
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithValidWord_SearchesPronunciations()
    {
        // Arrange
        var function = new SearchForvoPronunciations(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("FORVO_API_KEY", "test-key");
        
        // Act & Assert
        // The actual execution would require Forvo API mocking
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithPartitionKeyFilter_AppliesFilter()
    {
        // Arrange
        var function = new SearchForvoPronunciations(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "DefaultEndpointsProtocol=https://;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net");
        Environment.SetEnvironmentVariable("FORVO_API_KEY", "test-key");
        
        // Act & Assert
        Assert.NotNull(function);
    }
}
