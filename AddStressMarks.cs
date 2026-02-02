using System;
using System.IO;
using System.Net.Http;
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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://ws3.morpher.ru/russian/addstressmarks");
            request.Content = new StringContent(sentence, Encoding.UTF8, "text/plain");

            using var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Morpher API returned status {status}, returning original sentence", resp.StatusCode);
                return new OkObjectResult(new { stressed = sentence });
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var respText = await reader.ReadToEndAsync();

            // Parse XML response to extract the stressed text
            var stressedText = ExtractStressedText(respText);
            
            if (string.IsNullOrWhiteSpace(stressedText))
            {
                _logger.LogWarning("Failed to extract stressed text from Morpher response, returning original sentence");
                return new OkObjectResult(new { stressed = sentence });
            }

            return new OkObjectResult(new { stressed = stressedText });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Morpher API, returning original sentence");
            return new OkObjectResult(new { stressed = sentence });
        }
    }

    private static string ExtractStressedText(string xmlResponse)
    {
        try
        {
            // Parse simple XML response like: <?xml version="1.0" encoding="utf-8"?><string>Дверь закрыта с внутренней стороны.</string>
            var startTag = "<string>";
            var endTag = "</string>";
            
            var startIndex = xmlResponse.IndexOf(startTag);
            var endIndex = xmlResponse.IndexOf(endTag);
            
            if (startIndex >= 0 && endIndex > startIndex)
            {
                startIndex += startTag.Length;
                return xmlResponse.Substring(startIndex, endIndex - startIndex);
            }
            
            return null;
        }
        catch
        {
            return null;
        }
    }
}
