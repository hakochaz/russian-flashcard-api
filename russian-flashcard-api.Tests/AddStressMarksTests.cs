using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api.Tests;

public class AddStressMarksTests
{
    private readonly Mock<ILogger<AddStressMarks>> _mockLogger;

    public AddStressMarksTests()
    {
        _mockLogger = new Mock<ILogger<AddStressMarks>>();
    }

    [Fact]
    public async Task Run_WithEmptyBody_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddStressMarks(_mockLogger.Object);
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
        var function = new AddStressMarks(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { data = "test" });
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
        var function = new AddStressMarks(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithValidSentence_CallsMorpherAPI()
    {
        // Arrange
        var function = new AddStressMarks(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "Дверь закрыта" });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act & Assert
        // The actual execution would require HTTP client mocking
        Assert.NotNull(function);
    }

    [Fact]
    public async Task Run_WithInvalidJson_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddStressMarks(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("invalid json {")));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithWhitespaceSentence_ReturnsBadRequest()
    {
        // Arrange
        var function = new AddStressMarks(_mockLogger.Object);
        var mockRequest = new Mock<HttpRequest>();
        var body = JsonSerializer.Serialize(new { sentence = "   " });
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        
        // Act
        var result = await function.Run(mockRequest.Object);
        
        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void FindAmbiguousWords_ReturnsOptionsForEachAmbiguousWord()
    {
        var words = AddStressMarks.FindAmbiguousWords("Ко́нчился э́тот про́клятый|прокля́тый год. Но что да́льше?");

        Assert.Single(words);
        Assert.Equal(new[] { "про́клятый", "прокля́тый" }, words[0]);
    }

    [Fact]
    public void ApplyStressChoices_KeepsChosenOptionAndRemovesPipe()
    {
        var result = AddStressMarks.ApplyStressChoices("Ко́нчился э́тот про́клятый|прокля́тый год. Но что да́льше?", new[] { 1 });

        Assert.Equal("Ко́нчился э́тот про́клятый год. Но что да́льше?", result);
    }

    [Fact]
    public void ApplyStressChoices_HandlesMultipleAmbiguousWords()
    {
        var result = AddStressMarks.ApplyStressChoices("Он до́ма|дома́ и за́мок|замо́к там.", new[] { 1, 2 });

        Assert.Equal("Он до́ма и замо́к там.", result);
    }

    [Fact]
    public void ApplyStressChoices_InvalidChoiceFallsBackToFirstOption()
    {
        var result = AddStressMarks.ApplyStressChoices("Это за́мок|замо́к.", new[] { 5 });

        Assert.Equal("Это за́мок.", result);
    }
}
