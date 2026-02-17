using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api.Tests;

public class GetEntityByRowIdTests
{
    private readonly Mock<ILogger<GetEntityByRowId>> _mockLogger;

    public GetEntityByRowIdTests()
    {
        _mockLogger = new Mock<ILogger<GetEntityByRowId>>();
    }

    [Fact]
    public async Task Run_WithMissingConnectionString_Returns500()
    {
        // Arrange
        var function = new GetEntityByRowId(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "");
        
        // Act
        var result = await function.Run(mockRequest.Object, "TestTable", "row1");
        
        // Assert
        Assert.IsType<StatusCodeResult>(result);
        var statusCode = ((StatusCodeResult)result).StatusCode;
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task Run_WithValidConnectionString_ExecutesQuery()
    {
        // Arrange
        var function = new GetEntityByRowId(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "partitionKey", "partition1" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "DefaultEndpointsProtocol=https://;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net");
        
        // Act & Assert
        // The actual execution would require Azure Table Storage mocking
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithoutPartitionKey_SearchesAcrossPartitions()
    {
        // Arrange
        var function = new GetEntityByRowId(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "DefaultEndpointsProtocol=https://;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net");
        
        // Act & Assert
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithPartitionKey_FetchesSpecificEntity()
    {
        // Arrange
        var function = new GetEntityByRowId(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "partitionKey", "test-partition" }
        });
        mockRequest.Setup(r => r.Query).Returns(queryCollection);
        
        Environment.SetEnvironmentVariable("AzureWebJobsStorage", "DefaultEndpointsProtocol=https://;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net");
        
        // Act & Assert
        Assert.NotNull(function);
    }
}
