using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api;

public class LexiconSearch
{
    private readonly ILogger<LexiconSearch> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = new Version(1, 1)
    };

    public LexiconSearch(ILogger<LexiconSearch> logger)
    {
        _logger = logger;
    }

    [Function("LexiconSearch")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/lexicon-search")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? sentence = null;
        string? word = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("sentence", out var s))
                    sentence = s.GetString();
                if (doc.RootElement.TryGetProperty("word", out var w))
                    word = w.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore - validation below
        }

        if (string.IsNullOrWhiteSpace(word))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'word' in the JSON body." });
        }

        _logger.LogInformation("LexiconSearch called for word: {word} in sentence: {sentence}", word, sentence);

        var searchApiKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_API_KEY");
        var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT");

        if (string.IsNullOrWhiteSpace(searchApiKey) || string.IsNullOrWhiteSpace(searchEndpoint))
        {
            _logger.LogError("AZURE_SEARCH_API_KEY or AZURE_SEARCH_ENDPOINT is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            // Without a sentence the word is searched as-is; with one, OpenAI first works out its base form in context
            var baseForm = word;
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                var openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(openaiApiKey))
                {
                    _logger.LogError("OPENAI_API_KEY is not configured");
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                }

                baseForm = await CallOpenAI(WordAnalysisInSentence.BuildBaseFormPrompt(sentence, word), openaiApiKey);
                if (string.IsNullOrWhiteSpace(baseForm))
                {
                    return new StatusCodeResult(StatusCodes.Status502BadGateway);
                }

                _logger.LogInformation("Base form retrieved: {baseForm}", baseForm);
            }

            var baseWord = await SearchTopBaseWord(baseForm, searchEndpoint, searchApiKey);
            if (baseWord == null)
            {
                return new NotFoundObjectResult(new { error = "No lexicon match found.", baseForm });
            }

            return new OkObjectResult(new { baseWord });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in LexiconSearch");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<string?> SearchTopBaseWord(string baseForm, string endpoint, string apiKey)
    {
        var searchTerm = WordAnalysisInSentence.CleanBaseFormForLookup(baseForm);
        var url = $"{endpoint}/indexes/russian-lexicon/docs?api-version=2023-11-01&search={HttpUtility.UrlEncode(searchTerm)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("api-key", apiKey);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Azure Search API returned: {status}", response.StatusCode);
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.GetArrayLength() == 0)
        {
            _logger.LogInformation("No results found in Azure Search for: {searchTerm}", searchTerm);
            return null;
        }

        var topResult = values.EnumerateArray()
            .Where(v => v.TryGetProperty("base_word", out var b) && !string.IsNullOrWhiteSpace(b.GetString()))
            .OrderByDescending(v => v.TryGetProperty("@search.score", out var score) ? score.GetDouble() : 0)
            .FirstOrDefault();

        return topResult.ValueKind == JsonValueKind.Undefined ? null : topResult.GetProperty("base_word").GetString();
    }

    private async Task<string?> CallOpenAI(string prompt, string apiKey)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-5.4-mini",
            messages = new[]
            {
                new { role = "system", content = "You are a Russian language expert. Reply with ONLY the requested text, no JSON, no explanation, no extra formatting." },
                new { role = "user", content = prompt }
            },
            temperature = 0.2
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(request);
        var respText = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI API error: {status} {body}", resp.StatusCode, respText);
            return null;
        }

        using var doc = JsonDocument.Parse(respText);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim();
    }
}
