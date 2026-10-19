using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api;

public class AddStressMarks
{
    // Morpher returns words with ambiguous stress as alternatives separated by "|", e.g. "про́клятый|прокля́тый"
    private static readonly Regex AmbiguousWordRegex = new Regex(@"[\p{L}\u0301\-]+(?:\|[\p{L}\u0301\-]+)+");

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

        var stressed = await StressSentenceAsync(sentence);
        return new OkObjectResult(new { stressed });
    }

    // Never throws - falls back to the original sentence if stressing fails
    public async Task<string> StressSentenceAsync(string sentence)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://ws3.morpher.ru/russian/addstressmarks");
            request.Content = new StringContent(sentence, Encoding.UTF8, "text/plain");

            using var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Morpher API returned status {status}, returning original sentence", resp.StatusCode);
                return sentence;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var respText = await reader.ReadToEndAsync();

            // Parse XML response to extract the stressed text
            var stressedText = ExtractStressedText(respText);
            
            if (string.IsNullOrWhiteSpace(stressedText))
            {
                _logger.LogWarning("Failed to extract stressed text from Morpher response, returning original sentence");
                return sentence;
            }

            // Only resolve with AI when there is surrounding context - a lone word gives nothing to decide from
            if (stressedText.Contains('|') && sentence.Trim().Contains(' '))
            {
                stressedText = await ResolveAmbiguousStress(sentence, stressedText);
            }

            return stressedText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Morpher API, returning original sentence");
            return sentence;
        }
    }

    private async Task<string> ResolveAmbiguousStress(string sentence, string stressedText)
    {
        var ambiguousWords = FindAmbiguousWords(stressedText);
        if (ambiguousWords.Count == 0)
            return stressedText;

        var openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(openaiApiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY is not configured, returning unresolved stress alternatives");
            return stressedText;
        }

        var choices = await AskOpenAIForStressChoices(sentence, ambiguousWords, openaiApiKey);
        if (choices == null)
        {
            _logger.LogWarning("Could not resolve stress alternatives with OpenAI, returning unresolved text");
            return stressedText;
        }

        return ApplyStressChoices(stressedText, choices);
    }

    public static List<string[]> FindAmbiguousWords(string stressedText)
    {
        return AmbiguousWordRegex.Matches(stressedText)
            .Select(m => m.Value.Split('|'))
            .ToList();
    }

    // choices are 1-based option numbers, one per ambiguous word in order of appearance
    public static string ApplyStressChoices(string stressedText, IReadOnlyList<int> choices)
    {
        var index = 0;
        return AmbiguousWordRegex.Replace(stressedText, m =>
        {
            var options = m.Value.Split('|');
            var choice = index < choices.Count ? choices[index] : 1;
            index++;
            return choice >= 1 && choice <= options.Length ? options[choice - 1] : options[0];
        });
    }

    private async Task<List<int>?> AskOpenAIForStressChoices(string sentence, List<string[]> ambiguousWords, string apiKey)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Russian sentence:");
        prompt.AppendLine(sentence);
        prompt.AppendLine();
        prompt.AppendLine("Some words in this sentence have more than one possible stress placement (stress is shown with an acute accent).");
        prompt.AppendLine("For each word below, choose the option that is correct in the context of this sentence.");
        prompt.AppendLine();
        for (int i = 0; i < ambiguousWords.Count; i++)
        {
            var options = string.Join(", ", ambiguousWords[i].Select((o, j) => $"{j + 1}) {o}"));
            prompt.AppendLine($"Word {i + 1}: {options}");
        }
        prompt.AppendLine();
        prompt.AppendLine($"Reply with ONLY the chosen option numbers, one per line, in order ({ambiguousWords.Count} line(s) total).");

        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = "You are a Russian language expert. Reply with ONLY the requested numbers, no explanation." },
                new { role = "user", content = prompt.ToString() }
            },
            temperature = 0
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(request);
            var respText = await resp.Content.ReadAsStringAsync();

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

            var choices = Regex.Matches(message ?? "", @"\d+")
                .Select(m => int.Parse(m.Value))
                .ToList();

            if (choices.Count != ambiguousWords.Count)
            {
                _logger.LogWarning("OpenAI returned {count} stress choices, expected {expected}: {message}", choices.Count, ambiguousWords.Count, message);
                return null;
            }

            return choices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI to resolve stress alternatives");
            return null;
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
