using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace russian_flashcard_api.Tests;

public class TrimClozeSentenceTests
{
    private static TrimClozeSentence CreateFunction()
    {
        return new TrimClozeSentence(new Mock<ILogger<TrimClozeSentence>>().Object, NullLoggerFactory.Instance);
    }

    private static Mock<HttpRequest> CreateRequest(string body)
    {
        var mockRequest = new Mock<HttpRequest>();
        mockRequest.Setup(r => r.Body).Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        return mockRequest;
    }

    [Fact]
    public async Task Run_WithEmptyBody_ReturnsBadRequest()
    {
        var result = await CreateFunction().Run(CreateRequest("").Object);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Run_WithoutClozeMarker_ReturnsBadRequest()
    {
        var body = JsonSerializer.Serialize(new { cloze = "Посмотрите, вот это батарея." });

        var result = await CreateFunction().Run(CreateRequest(body).Object);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("Посмотрите, вот это {{c1::батарея}}.", "Посмотрите, вот это батарея.")]
    [InlineData("У меня нету {{c1::блоков::blocks}} и нету", "У меня нету блоков и нету")]
    public void StripCloze_RemovesMarkersAndHints(string cloze, string expected)
    {
        Assert.Equal(expected, TrimClozeSentence.StripCloze(cloze));
    }

    [Theory]
    [InlineData(
        "о драме или там о саде, о ремонте, а то есть в общих {{c1::чертах}} я понимала, но вот",
        "о драме или там о саде, о ремонте, а то есть в общих {{c1::чертах}} я понимала.")]
    [InlineData(
        "всем морнинг, монинг. Я тоже в Англии со всеми {{c1::здороваюсь}}, но это прямо, знаете,",
        "Я тоже в Англии со всеми {{c1::здороваюсь}}")]
    // Subject pronoun added to make the verb form clear
    [InlineData("ну, {{c1::приеду}} попозже", "ну, я {{c1::приеду}} попозже")]
    [InlineData("а потом {{c1::купили}} новую машину", "а потом они {{c1::купили}} новую машину")]
    public void IsValidTrim_AcceptsTrimmedSentences(string original, string trimmed)
    {
        Assert.True(TrimClozeSentence.IsValidTrim(original, trimmed));
    }

    [Theory]
    // Cloze marker lost
    [InlineData("Я тоже в Англии со всеми {{c1::здороваюсь}}, но", "Я тоже в Англии со всеми здороваюсь")]
    // Cloze target changed
    [InlineData("Я тоже в Англии со всеми {{c1::здороваюсь}}, но", "Я тоже в Англии со всеми {{c1::здоровался}}")]
    // New word added
    [InlineData("Я тоже в Англии со всеми {{c1::здороваюсь}}, но", "Я тоже в Англии всегда со всеми {{c1::здороваюсь}}")]
    public void IsValidTrim_RejectsRewrittenSentences(string original, string trimmed)
    {
        Assert.False(TrimClozeSentence.IsValidTrim(original, trimmed));
    }
}
