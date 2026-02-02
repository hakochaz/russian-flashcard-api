using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace russian_flashcard_api;

public class GetForvoPronunciationsByWord
{
    private readonly ILogger<GetForvoPronunciationsByWord> _logger;

    public GetForvoPronunciationsByWord(ILogger<GetForvoPronunciationsByWord> logger)
    {
        _logger = logger;
    }

    [Function("GetForvoPronunciationsByWord")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "forvo/pronunciations/{word}")] HttpRequest req,
        string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return new BadRequestObjectResult("Word parameter is required");
        }

        var apiKey = Environment.GetEnvironmentVariable("FORVO_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("FORVO_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var pronunciations = await FetchForvoPronunciations(apiKey, word.Trim());
            return new OkObjectResult(pronunciations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Forvo pronunciations for word: {word}", word);
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<List<ForvoPronunciation>> FetchForvoPronunciations(string apiKey, string word)
    {
        var pronunciations = new List<ForvoPronunciation>();

        try
        {
            using var httpClient = new HttpClient();
            var url = $"https://apifree.forvo.com/key/{apiKey}/format/json/action/word-pronunciations/word/{Uri.EscapeDataString(word)}/language/ru";

            _logger.LogInformation("Fetching pronunciations from Forvo for word: {word}", word);

            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Forvo API returned status code: {statusCode}", response.StatusCode);
                return pronunciations;
            }

            var content = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var pronunciation = new ForvoPronunciation();

                    if (item.TryGetProperty("pathmp3", out var pathmp3))
                    {
                        pronunciation.AudioMp3 = pathmp3.GetString() ?? string.Empty;
                    }

                    if (item.TryGetProperty("sex", out var sex))
                    {
                        pronunciation.Sex = sex.GetString() ?? string.Empty;
                    }

                    if (item.TryGetProperty("username", out var username))
                    {
                        pronunciation.Username = username.GetString() ?? string.Empty;
                    }

                    if (item.TryGetProperty("country", out var country))
                    {
                        pronunciation.Country = country.GetString() ?? string.Empty;
                    }

                    pronunciations.Add(pronunciation);
                }
            }

            _logger.LogInformation("Found {count} pronunciations for word: {word}", pronunciations.Count, word);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Forvo pronunciations for word: {word}", word);
        }

        return pronunciations;
    }
}
