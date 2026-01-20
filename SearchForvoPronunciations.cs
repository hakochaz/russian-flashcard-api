using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace russian_flashcard_api;

public class SearchForvoPronunciations
{
    private readonly ILogger<SearchForvoPronunciations> _logger;

    public SearchForvoPronunciations(ILogger<SearchForvoPronunciations> logger)
    {
        _logger = logger;
    }

    [Function("SearchForvoPronunciations")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "forvo/search/{word}")] HttpRequest req,
        string word)
    {
        if (string.IsNullOrEmpty(word))
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
            var allItems = new List<PronunciationResult>();
            int currentPage = 1;
            int totalPages = 1;

            using var httpClient = new HttpClient();

            do
            {
                var url = $"https://apifree.forvo.com/key/{apiKey}/format/json/action/words-search/search/{Uri.EscapeDataString(word)}/language/ru";
                
                if (currentPage > 1)
                {
                    url += $"/pagesize/20/page/{currentPage}";
                }

                _logger.LogInformation($"Fetching page {currentPage} from Forvo API for word: {word}");

                var response = await httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Forvo API returned status code: {response.StatusCode}");
                    return new StatusCodeResult((int)response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync();
                
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                // Get total pages
                if (root.TryGetProperty("attributes", out var attributes) &&
                    attributes.TryGetProperty("total_pages", out var totalPagesElement))
                {
                    if (totalPagesElement.ValueKind == JsonValueKind.Number)
                    {
                        totalPages = totalPagesElement.GetInt32();
                    }
                    else if (totalPagesElement.ValueKind == JsonValueKind.String &&
                             int.TryParse(totalPagesElement.GetString(), out var parsedPages))
                    {
                        totalPages = parsedPages;
                    }
                }

                // Get items
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        string? original = null;
                        string? audio = null;

                        if (item.TryGetProperty("original", out var originalElement))
                        {
                            original = originalElement.GetString();
                        }

                        if (item.TryGetProperty("standard_pronunciation", out var pronunciation) &&
                            pronunciation.TryGetProperty("pathmp3", out var pathmp3Element))
                        {
                            audio = pathmp3Element.GetString();
                        }

                        if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(audio))
                        {
                            allItems.Add(new PronunciationResult
                            {
                                Phrase = original,
                                Audio = audio
                            });
                        }
                    }
                }
                else
                {
                    _logger.LogWarning($"No items found for word: {word}");
                    break;
                }

                currentPage++;

            } while (currentPage <= totalPages);

            _logger.LogInformation($"Found {allItems.Count} pronunciations across {totalPages} page(s) for word: {word}");

            return new OkObjectResult(allItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error searching Forvo for word: {word}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}

public class PronunciationResult
{
    [JsonPropertyName("phrase")]
    public string Phrase { get; set; } = string.Empty;

    [JsonPropertyName("audio")]
    public string Audio { get; set; } = string.Empty;
}
