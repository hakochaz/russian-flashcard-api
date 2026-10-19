using System;
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

public class TrimClozeSentence
{
    public const string StatusUnchanged = "unchanged";
    public const string StatusChanged = "changed";
    public const string StatusInsufficientContext = "insufficientContext";

    // Matches Anki cloze deletions such as {{c1::слово}} or {{c1::слово::hint}}
    private static readonly Regex ClozeRegex = new Regex(@"\{\{c\d+::(.+?)(?:::[^}]*)?\}\}");
    private static readonly Regex WordRegex = new Regex(@"[\p{L}́\-]+");

    private readonly ILogger<TrimClozeSentence> _logger;
    private readonly TranslateSentence _translator;
    private readonly AddStressMarks _stresser;
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestVersion = new Version(1, 1)
    };

    public TrimClozeSentence(ILogger<TrimClozeSentence> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _translator = new TranslateSentence(loggerFactory.CreateLogger<TranslateSentence>());
        _stresser = new AddStressMarks(loggerFactory.CreateLogger<AddStressMarks>());
    }

    [Function("TrimClozeSentence")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/trim-cloze")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? cloze = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("cloze", out var c))
                    cloze = c.GetString();
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(cloze) || !ClozeRegex.IsMatch(cloze))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'cloze' in the JSON body, containing a cloze such as {{c1::слово}}." });
        }

        cloze = cloze.Trim();
        _logger.LogInformation("TrimClozeSentence called for cloze: {cloze}", cloze);

        var openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(openaiApiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        var decision = await AskOpenAI(cloze, openaiApiKey);
        if (decision == null)
        {
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        var (status, newCloze, reason) = decision.Value;

        if (status == StatusInsufficientContext)
        {
            return new OkObjectResult(new { status, reason });
        }

        if (status == StatusUnchanged || newCloze == null || newCloze.Trim() == cloze)
        {
            return new OkObjectResult(new { status = StatusUnchanged });
        }

        newCloze = newCloze.Trim();
        if (!IsValidTrim(cloze, newCloze))
        {
            _logger.LogWarning("OpenAI returned an invalid trimmed cloze. Original: {original} Returned: {returned}", cloze, newCloze);
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        var sentence = StripCloze(newCloze);

        var translateTask = _translator.TranslateAsync(sentence);
        var stressTask = _stresser.StressSentenceAsync(sentence);
        await Task.WhenAll(translateTask, stressTask);

        return new OkObjectResult(new
        {
            status = StatusChanged,
            cloze = newCloze,
            sentence,
            sentenceStress = stressTask.Result,
            translation = translateTask.Result
        });
    }

    public static string StripCloze(string cloze)
    {
        return ClozeRegex.Replace(cloze, m => m.Groups[1].Value);
    }

    // The new cloze must keep every cloze deletion intact and only use words from the original,
    // so the AI can trim and fix punctuation but not rewrite the sentence
    public static bool IsValidTrim(string originalCloze, string newCloze)
    {
        var originalDeletions = ClozeRegex.Matches(originalCloze).Select(m => m.Value).ToList();
        var newDeletions = ClozeRegex.Matches(newCloze).Select(m => m.Value).ToList();
        if (!originalDeletions.SequenceEqual(newDeletions))
            return false;

        var originalWords = WordRegex.Matches(StripCloze(originalCloze))
            .Select(m => m.Value.ToLowerInvariant())
            .ToHashSet();

        return WordRegex.Matches(StripCloze(newCloze))
            .All(m => originalWords.Contains(m.Value.ToLowerInvariant()));
    }

    private static string BuildPrompt(string cloze)
    {
        return "You are helping a learner of Russian build cloze flashcards. The cards are often cut from " +
               "transcripts of speech, so they can start or end in the middle of a sentence or carry extra clauses " +
               "that do not help the learner understand the target word.\n\n" +
               "The target word is marked like {{c1::слово}}.\n\n" +
               "Decide one of:\n" +
               $"- \"{StatusUnchanged}\": the text is already a clean, self-contained sentence or phrase with the context needed for the target word.\n" +
               $"- \"{StatusChanged}\": there are dangling fragments before or after the part that gives the target word its meaning " +
               "(e.g. a cut-off clause like \", но вот\" or a stray word like \"И\"). Cut them away. Only cut from the start or end; " +
               "do NOT add, reword, reorder or translate any words, do NOT add punctuation such as a full stop, " +
               "and keep the {{c1::...}} marker exactly as it is. If the cut leaves a trailing comma, remove it.\n" +
               "  Be conservative: only cut text that adds nothing. If removing something would make the sentence vaguer or more ambiguous " +
               "(e.g. it explains who, what or why), keep it. When in doubt, choose \"" + StatusUnchanged + "\".\n" +
               $"- \"{StatusInsufficientContext}\": even the full text does not give enough context for a learner to work out the target word.\n\n" +
               "Examples:\n" +
               "Input: Посмотрите, вот это {{c1::батарея}}.\n" +
               $"Output: {{\"status\": \"{StatusUnchanged}\"}}\n\n" +
               "Input: о драме или там о саде, о ремонте, а то есть в общих {{c1::чертах}} я понимала, но вот\n" +
               $"Output: {{\"status\": \"{StatusChanged}\", \"cloze\": \"о драме или там о саде, о ремонте, а то есть в общих {{{{c1::чертах}}}} я понимала\"}}\n\n" +
               "Input: Да, что-то говорила сама. У меня нету {{c1::блоков}} и нету стеснения никакого. И\n" +
               $"Output: {{\"status\": \"{StatusChanged}\", \"cloze\": \"Да, что-то говорила сама. У меня нету {{{{c1::блоков}}}} и нету стеснения никакого.\"}}\n\n" +
               "Input: всем морнинг, монинг. Я тоже в Англии со всеми {{c1::здороваюсь}}, но это прямо, знаете,\n" +
               $"Output: {{\"status\": \"{StatusChanged}\", \"cloze\": \"всем морнинг, монинг. Я тоже в Англии со всеми {{{{c1::здороваюсь}}}}\"}}\n\n" +
               "Input: ну и {{c1::вот}}\n" +
               $"Output: {{\"status\": \"{StatusInsufficientContext}\", \"reason\": \"Only filler words surround the target, nothing indicates its meaning.\"}}\n\n" +
               $"Input: {cloze}\n\n" +
               "Reply with ONLY a JSON object with \"status\", plus \"cloze\" when changed or a short English \"reason\" when there is insufficient context.";
    }

    private async Task<(string status, string? cloze, string? reason)?> AskOpenAI(string cloze, string apiKey)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = "You are a Russian language expert. Reply with ONLY a JSON object, no explanation." },
                new { role = "user", content = BuildPrompt(cloze) }
            },
            response_format = new { type = "json_object" },
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

            using var result = JsonDocument.Parse(message ?? "{}");
            var root = result.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (status != StatusUnchanged && status != StatusChanged && status != StatusInsufficientContext)
            {
                _logger.LogWarning("OpenAI returned an unexpected status: {message}", message);
                return null;
            }

            var newCloze = root.TryGetProperty("cloze", out var c) ? c.GetString() : null;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;

            return (status, newCloze, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI to trim cloze sentence");
            return null;
        }
    }
}
