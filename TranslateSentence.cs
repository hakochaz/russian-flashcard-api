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

public class TranslateSentence
{
    private readonly ILogger<TranslateSentence> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public TranslateSentence(ILogger<TranslateSentence> logger)
    {
        _logger = logger;
    }

    [Function("TranslateSentence")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/translate-sentence")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? sentence = null;

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
        }

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'sentence' in the JSON body." });
        }

        var translation = await TranslateAsync(sentence);
        if (translation == null)
        {
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        return new OkObjectResult(new { sentence, translation });
    }

    // Returns null if DeepL is not configured or the translation fails
    public async Task<string?> TranslateAsync(string sentence)
    {
        var apiKey = Environment.GetEnvironmentVariable("DeepL_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("DeepL_API_KEY is not configured");
            return null;
        }

        // Free-tier keys end with ":fx" and must use the free API host
        var endpoint = apiKey.EndsWith(":fx")
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        try
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                text = new[] { sentence },
                source_lang = "RU",
                target_lang = "EN-GB"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(request);
            var respText = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("DeepL API error: {status} {body}", resp.StatusCode, respText);
                return null;
            }

            using var respDoc = JsonDocument.Parse(respText);
            return respDoc.RootElement
                .GetProperty("translations")[0]
                .GetProperty("text")
                .GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error translating sentence");
            return null;
        }
    }
}
