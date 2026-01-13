using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api;

public class RussianWordVariations
{
    private readonly ILogger<RussianWordVariations> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = new Version(1, 1)
    };

    public RussianWordVariations(ILogger<RussianWordVariations> logger)
    {
        _logger = logger;
    }

    [Function("RussianWordVariations")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "russian/variations")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string word = null;

        if (string.IsNullOrWhiteSpace(word))
        {
            word = req.Query["word"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(word))
        {
            return new BadRequestObjectResult(new { error = "Please provide a 'word' in the JSON body or ?word= query string." });
        }

        _logger.LogInformation("RussianVariations called for word: {word}", word);

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var prompt = BuildPrompt(word);

            // Use shared _httpClient; authorization header set per-request below

            var requestBody = JsonSerializer.Serialize(new
            {
                model = "gpt-3.5-turbo",
                messages = new[]
                {
                    new { role = "system", content = "You are a Russian morphology assistant. Reply with a JSON array of strings only. Detect the input word's primary lemma/part-of-speech and return only inflected forms (declensions, conjugations, participles, gerunds, etc.) of that exact lemma. Do NOT include derived words, adjectives/verbs from different lemmas, translations, explanations, or any surrounding text. Provide canonical Cyrillic forms only." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.0
            });

            // Retry a small number of times on transient HTTP errors
            const int maxAttempts = 2;
            string respText = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                    using var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    respText = await reader.ReadToEndAsync();

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("OpenAI API error: {status} {body}", resp.StatusCode, respText);
                        return new StatusCodeResult(StatusCodes.Status502BadGateway);
                    }

                    using var doc = JsonDocument.Parse(respText);
                    var message = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString();

                    // Try to parse assistant content as a JSON array of strings and return it
                    try
                    {
                        var parsedArray = JsonSerializer.Deserialize<string[]>(message);
                        if (parsedArray != null)
                            return new OkObjectResult(parsedArray);

                        // fallback to parsing as generic JSON
                        var parsed = JsonSerializer.Deserialize<object>(message);
                        return new OkObjectResult(parsed);
                    }
                    catch (JsonException)
                    {
                        // If assistant response isn't strict JSON, return raw text
                        return new OkObjectResult(new { raw = message });
                    }
                }
                catch (Exception ex) when (attempt < maxAttempts && (ex is HttpRequestException || ex is IOException))
                {
                    _logger.LogWarning(ex, "Transient HTTP error calling OpenAI (attempt {attempt}), retrying...", attempt);
                    await Task.Delay(500 * attempt);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calling OpenAI");
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                }
            }

            _logger.LogError("Failed to obtain a valid response from OpenAI after {maxAttempts} attempts", maxAttempts);
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private static string BuildPrompt(string word)
    {
        return $"Provide a JSON array (only the array) of all distinct inflected forms of the exact lemma '{word}'. Detect the word's primary part of speech and return only morphological variants of that same lemma (for nouns: all cases singular+plural; for verbs: conjugations and past forms; for adjectives: gender/number/case forms; etc.). Do NOT include derived lemmas or unrelated word forms. Example output: [\"мама\", \"мамы\", \"маме\", ...]. Return only a JSON array of strings with canonical Cyrillic forms.";
    }
}
