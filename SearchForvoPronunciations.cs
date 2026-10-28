using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace russian_flashcard_api;

public class SearchForvoPronunciations
{
    private readonly ILogger<SearchForvoPronunciations> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = new Version(1, 1)
    };

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

            if (allItems.Count > 1)
            {
                await MoveBestFlashcardSentenceToFrontAsync(word, allItems);
            }

            return new OkObjectResult(allItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error searching Forvo for word: {word}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task MoveBestFlashcardSentenceToFrontAsync(string word, List<PronunciationResult> items)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY is not configured; skipping flashcard sentence selection");
            return;
        }

        try
        {
            var phraseList = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                phraseList.AppendLine($"{i}: {items[i].Phrase}");
            }

            var requestBody = JsonSerializer.Serialize(new
            {
                model = "gpt-5.4-mini",
                messages = new[]
                {
                    new { role = "system", content = "You are a Russian language teaching assistant helping build vocabulary flashcards for a B1-level learner using the i+1 principle. Given a target Russian word and a numbered list of example sentences containing that word, choose the single sentence that is best for a flashcard: it should be otherwise easy for a B1 learner, using common, everyday vocabulary and simple grammar, so that the target word is the only unfamiliar or challenging part of the sentence. Avoid sentences containing rare words, idioms, names, numbers, or complex grammatical constructions. Reply with only the index number of the chosen sentence and nothing else." },
                    new { role = "user", content = $"Target word: {word}\n\nSentences:\n{phraseList}" }
                },
                temperature = 0.0
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            var respText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI API error: {status} {body}", response.StatusCode, respText);
                return;
            }

            using var doc = JsonDocument.Parse(respText);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            var digits = new string((content ?? string.Empty).Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var bestIndex) && bestIndex >= 0 && bestIndex < items.Count)
            {
                if (bestIndex != 0)
                {
                    var best = items[bestIndex];
                    items.RemoveAt(bestIndex);
                    items.Insert(0, best);
                }
            }
            else
            {
                _logger.LogWarning("Could not parse a valid sentence index from OpenAI response: {content}", content);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error selecting best flashcard sentence with OpenAI for word: {word}", word);
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
