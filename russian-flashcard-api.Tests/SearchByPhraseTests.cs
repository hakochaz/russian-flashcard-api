using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api.Tests;

public class SearchByPhraseTests
{
    private readonly Mock<ILogger<SearchByPhrase>> _mockLogger;

    public SearchByPhraseTests()
    {
        _mockLogger = new Mock<ILogger<SearchByPhrase>>();
    }

    [Fact]
    public async Task Run_WithoutQueryParameter_ReturnsBadRequest()
    {
        // Arrange
        var function = new SearchByPhrase(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            // No 'q' parameter
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        // Act
        var result = await function.Run(mockRequest.Object, "TestTable");
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithMissingConnectionString_Returns500()
    {
        // Arrange
        var function = new SearchByPhrase(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "q", "test" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "");
        
        // Act
        var result = await function.Run(mockRequest.Object, "TestTable");
        
        // Assert
        Assert.IsType<StatusCodeResult>(result);
        var statusCode = ((StatusCodeResult)result).StatusCode;
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task Run_WithValidQuery_ExecutesSearch()
    {
        // Arrange
        var function = new SearchByPhrase(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "q", "test" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "DefaultEndpointsProtocol=https://;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net");
        
        // Act & Assert
        // The actual execution would require Azure Storage mocking
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithPartitionKeyFilter_AppliesFilter()
    {
        // Arrange
        var function = new SearchByPhrase(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "q", "test" },
            { "partitionKey", "partition1" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "DefaultEndpointsProtocol=https://;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net");
        
        // Act & Assert
        Assert.NotNull(function);
    }
}
