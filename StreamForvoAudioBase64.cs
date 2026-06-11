using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace russian_flashcard_api;

public class StreamForvoAudioBase64
{
    private readonly ILogger<StreamForvoAudioBase64> _logger;
    private static readonly HttpClient _httpClient = new HttpClient();

    public StreamForvoAudioBase64(ILogger<StreamForvoAudioBase64> logger)
    {
        _logger = logger;
    }

    [Function("StreamForvoAudioBase64")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "forvo/stream-base64")] HttpRequest req)
    {
        _logger.LogInformation("StreamForvoAudioBase64 called");
        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("url", out var urlElement))
            {
                return new BadRequestObjectResult(new { error = "Missing 'url' in request body" });
            }
            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                return new BadRequestObjectResult(new { error = "URL is empty" });
            }

            // Download audio
            var audioBytes = await _httpClient.GetByteArrayAsync(url);
            if (audioBytes == null || audioBytes.Length == 0)
            {
                return new StatusCodeResult(StatusCodes.Status502BadGateway);
            }
            // Convert to base64
            var base64 = Convert.ToBase64String(audioBytes);
            return new OkObjectResult(new { base64 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming Forvo audio as base64");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
