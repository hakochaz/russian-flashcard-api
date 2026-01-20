using System;
using System.IO;
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

public class AddStressMarks
{
    private readonly ILogger<AddStressMarks> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = new Version(1, 1)
    };

    public AddStressMarks(ILogger<AddStressMarks> logger)
    {
        _logger = logger;
    }

    [Function("AddStressMarks")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/stress")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string sentence = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("sentence", out var s))
                    sentence = s.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore - validation below
        }

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'sentence' in the JSON body." });
        }

        _logger.LogInformation("AddStressMarks called for sentence: {sentence}", sentence);

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var prompt = BuildPrompt(sentence);

            var requestBody = JsonSerializer.Serialize(new
            {
                model = "gpt-3.5-turbo",
                messages = new[]
                {
                    new { role = "system", content = "You are a Russian language expert. Reply with a valid JSON object only. No surrounding text or explanation." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.0
            });

            const int maxAttempts = 2;
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
                    var respText = await reader.ReadToEndAsync();

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

                    // Expect assistant to return a JSON object like { "stressed": "..." }
                    try
                    {
                        var parsed = JsonDocument.Parse(message);
                        return new OkObjectResult(JsonSerializer.Deserialize<object>(message));
                    }
                    catch (JsonException)
                    {
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
            _logger.LogError(ex, "Error in AddStressMarks");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private static string BuildPrompt(string sentence)
    {
        return "Add stress marks to words in the following Russian sentence using the Unicode combining acute accent (U+0301) immediately after the stressed vowel. "
             + "IMPORTANT: Do NOT add stress marks to single-letter words (like 'я', 'в', 'и', etc.) or to any word that has only one syllable (one vowel). "
             + "DO add stress marks to words with 2 or more syllables (2 or more vowels). For example, 'могу' (2 syllables) must become 'могу\u0301'. "
             + "Full example: \"Я не могу дать тебе определенный ответ сегодня.\" becomes \"Я не могу\u0301 дать тебе\u0301 определённый отве\u0301т сего\u0301дня.\" "
             + "Return a JSON object only in the exact form: { \"stressed\": \"<sentence with stress marks>\" } with canonical Cyrillic characters. "
             + "Do not include any explanation, extra text, or markup.\n\n" 
             + "Sentence: \"" + sentence + "\"";
    }
}
