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

public class PhraseBaseFormHighlighter
{
    private readonly ILogger<PhraseBaseFormHighlighter> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = new Version(1, 1)
    };

    public PhraseBaseFormHighlighter(ILogger<PhraseBaseFormHighlighter> logger)
    {
        _logger = logger;
    }

    [Function("PhraseBaseFormHighlighter")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/phrase-base-form")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? sentence = null;
        string? words = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("sentence", out var s))
                    sentence = s.GetString();
                if (doc.RootElement.TryGetProperty("words", out var w))
                    words = w.GetString();
            }
        }
        catch (JsonException)
        {
            // validation handled below
        }

        if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(words))
        {
            return new BadRequestObjectResult(new
            {
                error = "Please provide 'sentence' and 'words' in the JSON body."
            });
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        _logger.LogInformation("PhraseBaseFormHighlighter for phrase: {phrase}", words);

        string? baseFormPhrase;
        try
        {
            baseFormPhrase = await GetBaseFormPhrase(sentence, words, apiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting base forms from OpenAI");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        if (string.IsNullOrWhiteSpace(baseFormPhrase))
        {
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        var bracketed = BuildBracketedSentence(sentence, words, baseFormPhrase);

        return new OkObjectResult(new
        {
            phraseAnswer = sentence,
            bracketedSentence = bracketed,
            baseForm = baseFormPhrase
        });
    }

    private async Task<string?> GetBaseFormPhrase(string sentence, string words, string apiKey)
    {
        var prompt = $"Sentence: \"{sentence}\"\n" +
                     $"Selected words: \"{words}\"\n\n" +
                     "Return the lemma/base form for each selected token in order, joined by spaces, and nothing else.\n" +
                     "- Nouns: nominative case, preserving the original number (singular/plural); if ending with ь, append (m), (f), or (n).\n" +
                     "- Adjectives/pronouns: nominative case, preserving the original gender and number without gender marker.\n" +
                     "- Verbs: infinitive with aspect marker (i) or (p) that matches usage in the sentence.\n" +
                     "- Prepositions with fixed case: append (+d), (+a), (+g), (+i), or (+p).\n" +
                     "Example outputs: 'эта студентка', 'эти спортсмены'\n" +
                     "Return only the base-form phrase, no JSON or explanation.";

        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = "You are a precise Russian linguist. Reply with the base-form phrase only." },
                new { role = "user", content = prompt }
            },
            temperature = 0.1
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
        }

        _logger.LogError("Failed to obtain a valid response from OpenAI after {maxAttempts} attempts", maxAttempts);
        return null;
    }

    private static string BuildBracketedSentence(string sentence, string originalPhrase, string baseFormPhrase)
    {
        var index = sentence.IndexOf(originalPhrase, StringComparison.Ordinal);
        if (index < 0)
        {
            return $"{sentence} ({baseFormPhrase})";
        }

        var before = sentence.Substring(0, index);
        var after = sentence.Substring(index + originalPhrase.Length);
        return $"{before}({baseFormPhrase}){after}";
    }
}
