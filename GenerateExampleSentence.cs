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

public class GenerateExampleSentence
{
    private readonly ILogger<GenerateExampleSentence> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = new Version(1, 1)
    };

    public GenerateExampleSentence(ILogger<GenerateExampleSentence> logger)
    {
        _logger = logger;
    }

    [Function("GenerateExampleSentence")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/generate-sentence")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? word = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("word", out var w))
                    word = w.GetString();
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(word))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'word' in the JSON body." });
        }

        _logger.LogInformation("GenerateExampleSentence called for word: {word}", word);

        var openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(openaiApiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var prompt = BuildPrompt(word);
            var sentence = await CallOpenAI(prompt, openaiApiKey);

            if (string.IsNullOrWhiteSpace(sentence))
            {
                return new StatusCodeResult(StatusCodes.Status502BadGateway);
            }

            return new OkObjectResult(new { word, sentence });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating example sentence");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private static string BuildPrompt(string word)
    {
        return $"You are helping a learner of Russian at B1/B2 level.\n\n" +
               $"The word is: \"{word}\"\n\n" +
               "Instructions:\n" +
               "- If the word contains \"/\" it may be presented in two ways:\n" +
               "  1. Two full words (e.g. \"знать/узнать\") — pick whichever is more common by frequency in everyday Russian.\n" +
               "  2. A base word with a prefix hint (e.g. \"гаснуть/по-\") — this means the perfective is formed by adding the prefix (погаснуть). Pick whichever form (prefixed or unprefixed) is more commonly used in everyday Russian.\n" +
               "- Create ONE example sentence using the most common case, conjugation, or form of this word — " +
               "whichever form a native speaker would use most naturally and frequently.\n" +
               "- The sentence should follow the i+1 principle: all surrounding grammar and vocabulary should be simple and " +
               "well within B1 level, so the target word is the only challenging element.\n" +
               "- Keep the sentence short (6-12 words) and natural — something you'd hear in everyday conversation or read in a simple text.\n" +
               "- Do NOT include any English, translations, explanations, or commentary.\n" +
               "- Return ONLY the Russian sentence, nothing else.";
    }

    private async Task<string?> CallOpenAI(string prompt, string apiKey)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-5.4",
            messages = new[]
            {
                new { role = "system", content = "You are a Russian language expert. Reply with ONLY the requested text, no JSON, no explanation, no extra formatting." },
                new { role = "user", content = prompt }
            },
            temperature = 0.7
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
                    return null;
                }

                using var doc = JsonDocument.Parse(respText);
                var message = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return message?.Trim();
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
                return null;
            }
        }

        _logger.LogError("Failed to obtain a valid response from OpenAI after {maxAttempts} attempts", maxAttempts);
        return null;
    }
}
