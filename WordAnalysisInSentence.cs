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

public class WordAnalysisInSentence
{
    private readonly ILogger<WordAnalysisInSentence> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = new Version(1, 1)
    };

    public WordAnalysisInSentence(ILogger<WordAnalysisInSentence> logger)
    {
        _logger = logger;
    }

    [Function("WordAnalysisInSentence")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/analyze-word")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string sentence = null;
        string word = null;

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

        if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(word))
        {
            return new BadRequestObjectResult(new 
            { 
                error = "Please provide 'sentence' and 'word' in the JSON body." 
            });
        }

        _logger.LogInformation("WordAnalysisInSentence called for word: {word} in sentence: {sentence}", word, sentence);

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var prompt = BuildPrompt(sentence, word);

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

                    // Try to parse assistant content as JSON object
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
            _logger.LogError(ex, "Error in WordAnalysisInSentence");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private static string BuildPrompt(string sentence, string word)
    {
        return $"Analyze the Russian word '{word}' as it appears in the sentence: \"{sentence}\"\n\n" +
               "Return a JSON object with exactly these three fields:\n" +
               "{\n" +
               "  \"baseForm\": \"<base/lemma form of the word with metadata in brackets>\",\n" +
               "  \"englishTranslation\": \"<English translation of this specific word>\",\n" +
               "  \"russianMeaning\": \"<Russian definition/explanation>\"\n" +
               "}\n\n" +
               "For baseForm:\n" +
               "- For nouns: provide nominative singular (if the word is inflected differently in the sentence)\n" +
               "- For adjectives: provide masculine nominative singular (if the word is inflected differently in the sentence)\n" +
               "- For verbs: provide the infinitive form (e.g., читать, писать)\n" +
               "- For verbs, append the aspect in brackets: (св) for perfective, (нсв) for imperfective\n" +
               "- For nouns ending in soft sign (ь): append the gender in brackets, e.g. дверь (ж)\n" +
               "- If the word in the sentence is already in base form, still include the baseForm field with the word and metadata\n" +
               "Example: baseForm could be \"читать (нсв)\" or \"дверь (ж)\" or \"книга\" or \"новый\" (masculine nominative singular for adjectives)\n\n" +
               "For englishTranslation: provide the most accurate English equivalent in the context of this sentence.\n" +
               "For russianMeaning: provide a clear Russian-language definition or explanation of the word.\n\n" +
               "Return only the JSON object, no other text.";
    }
}
