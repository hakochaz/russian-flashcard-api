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

public class GenerateImageFromPrompt
{
    private readonly ILogger<GenerateImageFromPrompt> _logger;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    public GenerateImageFromPrompt(ILogger<GenerateImageFromPrompt> logger)
    {
        _logger = logger;
    }

    [Function("GenerateImageFromPrompt")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/generate-image-from-prompt")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? prompt = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("prompt", out var p))
                    prompt = p.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore - validation below
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new BadRequestObjectResult(new
            {
                error = "Please provide 'prompt' in the JSON body."
            });
        }

        var openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(openaiApiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        _logger.LogInformation("GenerateImageFromPrompt called with prompt: {prompt}", prompt);

        try
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                model = "gpt-image-1",
                prompt,
                n = 1,
                size = "1024x1024",
                quality = "medium"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openaiApiKey);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(request);
            var respText = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("DALL-E API error: {status} {body}", resp.StatusCode, respText);
                return new StatusCodeResult(StatusCodes.Status502BadGateway);
            }

            using var doc = JsonDocument.Parse(respText);
            var imageBase64 = doc.RootElement
                .GetProperty("data")[0]
                .GetProperty("b64_json")
                .GetString();

            return new OkObjectResult(new { imageBase64 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GenerateImageFromPrompt");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
