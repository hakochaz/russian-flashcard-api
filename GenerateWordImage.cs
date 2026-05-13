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

public class GenerateWordImage
{
    private readonly ILogger<GenerateWordImage> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    public GenerateWordImage(ILogger<GenerateWordImage> logger)
    {
        _logger = logger;
    }

    [Function("GenerateWordImage")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/generate-word-image")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? word = null;
        string? sentence = null;
        string? englishTranslation = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("word", out var w))
                    word = w.GetString();
                if (doc.RootElement.TryGetProperty("sentence", out var s))
                    sentence = s.GetString();
                if (doc.RootElement.TryGetProperty("englishTranslation", out var t))
                    englishTranslation = t.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore - validation below
        }

        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(sentence))
        {
            return new BadRequestObjectResult(new
            {
                error = "Please provide 'word' and 'sentence' in the JSON body."
            });
        }

        var openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(openaiApiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        _logger.LogInformation("GenerateWordImage called for word: {word} in sentence: {sentence}", word, sentence);

        try
        {
            var imagePrompt = await BuildImagePromptWithOpenAI(word, sentence, englishTranslation, openaiApiKey);
            if (string.IsNullOrWhiteSpace(imagePrompt))
            {
                _logger.LogError("Failed to build image prompt");
                return new StatusCodeResult(StatusCodes.Status502BadGateway);
            }

            _logger.LogInformation("Generated image prompt: {prompt}", imagePrompt);

            var imageBase64 = await GenerateImageWithDallE(imagePrompt, openaiApiKey);
            if (string.IsNullOrWhiteSpace(imageBase64))
            {
                return new StatusCodeResult(StatusCodes.Status502BadGateway);
            }

            return new OkObjectResult(new
            {
                imageBase64,
                prompt = imagePrompt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GenerateWordImage");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<string?> BuildImagePromptWithOpenAI(string word, string sentence, string? englishTranslation, string apiKey)
    {
        var wordMeaning = string.IsNullOrWhiteSpace(englishTranslation)
            ? $"\"{word}\""
            : $"\"{word}\" (meaning: \"{englishTranslation}\")";

        var prompt = $"I am making a Russian language flashcard. The word I need to illustrate is {wordMeaning}.\n" +
                     $"Example sentence for context only: \"{sentence}\"\n\n" +
                     $"Write a DALL-E image generation prompt whose SOLE PURPOSE is to make the meaning of {wordMeaning} unmistakable.\n\n" +
                     "Rules:\n" +
                     $"- The word {wordMeaning} is the ONLY subject. The entire composition must exist to convey that one concept.\n" +
                     "- Ask yourself: if someone saw this image with no captions, would their first thought be the word's meaning? If not, redesign.\n" +
                     "- The example sentence is background flavour only — do NOT let its characters, setting, or action become the focus.\n" +
                     "- Depict the most iconic, universally recognisable visual form of the word's meaning.\n" +
                     "- Describe a single, concrete scene — no collages, no split panels, no text overlays.\n" +
                     "- Style: clean digital illustration, vivid colours, simple composition suitable for a flashcard.\n" +
                     "- Do NOT mention Russian, Cyrillic, letters, textbooks, or language-learning in the prompt.\n" +
                     "- Do NOT include any English text, labels, letters, or words in the image.\n" +
                     "Return ONLY the image generation prompt, nothing else.";

        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new { role = "system", content = "You are an expert at writing DALL-E image prompts for educational flashcards. Your prompts make the target word's meaning instantly obvious. Reply with ONLY the image prompt, no explanation." },
                new { role = "user", content = prompt }
            },
            temperature = 0.7
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(request);
        var respText = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI chat error: {status} {body}", resp.StatusCode, respText);
            return null;
        }

        using var doc = JsonDocument.Parse(respText);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim();
    }

    private async Task<string?> GenerateImageWithDallE(string imagePrompt, string apiKey)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-image-1",
            prompt = imagePrompt,
            n = 1,
            size = "1024x1024",
            quality = "medium"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(request);
        var respText = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("DALL-E API error: {status} {body}", resp.StatusCode, respText);
            return null;
        }

        using var doc = JsonDocument.Parse(respText);
        return doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("b64_json")
            .GetString();
    }
}
