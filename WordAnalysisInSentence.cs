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
using System.Web;
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

    static WordAnalysisInSentence()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "RussianFlashcardAPI/1.0 (Educational flashcard application)");
    }

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

        string? sentence = null;
        string? word = null;

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

        var openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(openaiApiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        var searchApiKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_API_KEY");
        var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT");

        try
        {
            // Step 1: Get base form from OpenAI
            _logger.LogInformation("Step 1: Getting base form from OpenAI");
            var baseForm = await GetBaseFormFromOpenAI(sentence, word, openaiApiKey);
            
            if (string.IsNullOrWhiteSpace(baseForm))
            {
                _logger.LogWarning("Failed to get base form from OpenAI, falling back to full analysis");
                return await GetFullAnalysisFromOpenAI(sentence, word, openaiApiKey);
            }

            _logger.LogInformation("Base form retrieved: {baseForm}", baseForm);

            // Step 2: Search Azure AI Search if configured
            string finalBaseForm = baseForm;
            string? finalTranslation = null;

            if (!string.IsNullOrWhiteSpace(searchApiKey) && !string.IsNullOrWhiteSpace(searchEndpoint))
            {
                _logger.LogInformation("Step 2: Searching Azure AI Search for: {baseForm}", baseForm);
                var searchResult = await SearchAzureAISearch(baseForm, searchEndpoint, searchApiKey);

                if (searchResult != null)
                {
                    finalBaseForm = searchResult.Value.BaseWord;
                    finalTranslation = searchResult.Value.Translation;
                    _logger.LogInformation("Found in Azure Search - Base: {base}, Translation: {translation}", 
                        finalBaseForm, finalTranslation);
                }
                else
                {
                    _logger.LogInformation("No results from Azure Search");
                }
            }
            else
            {
                _logger.LogInformation("Azure Search not configured, skipping search step");
            }

            // Step 3: Fetch Wiktionary data (always use OpenAI base form)
            _logger.LogInformation("Step 3: Fetching Wiktionary data for: {baseForm}", baseForm);
            var wiktionaryData = await FetchWiktionaryData(baseForm);

            // Step 4: Determine translation
            if (string.IsNullOrWhiteSpace(finalTranslation) && wiktionaryData != null && wiktionaryData.Translations.Any())
            {
                _logger.LogInformation("Step 4: Selecting translation from Wiktionary using OpenAI");
                finalTranslation = await SelectRelevantTranslations(sentence, word, finalBaseForm, wiktionaryData.Translations, openaiApiKey);
            }

            // Step 5: Determine meaning - always use Wiktionary if available, otherwise OpenAI
            string? finalMeaning = null;
            if (wiktionaryData != null && wiktionaryData.Meanings.Any())
            {
                _logger.LogInformation("Step 5: Selecting meaning from Wiktionary using OpenAI");
                finalMeaning = await SelectRelevantMeaning(sentence, word, finalBaseForm, wiktionaryData.Meanings, openaiApiKey);
            }
            else
            {
                _logger.LogInformation("Step 5: Getting meaning from OpenAI (no Wiktionary data)");
                finalMeaning = await GetMeaningFromOpenAI(sentence, word, finalBaseForm, openaiApiKey);
            }

            // Step 6: If we still don't have translation, fall back to full OpenAI analysis
            if (string.IsNullOrWhiteSpace(finalTranslation))
            {
                _logger.LogInformation("Step 6: Getting full analysis from OpenAI (no translation found)");
                return await GetFullAnalysisFromOpenAI(sentence, word, openaiApiKey);
            }

            return new OkObjectResult(new
            {
                baseForm = finalBaseForm,
                englishTranslation = finalTranslation,
                russianMeaning = finalMeaning ?? "Не удалось получить значение"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WordAnalysisInSentence");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<string?> GetBaseFormFromOpenAI(string sentence, string word, string apiKey)
    {
        var prompt = $"Analyze the Russian word '{word}' as it appears in the sentence: \"{sentence}\"\n\n" +
                     "Return ONLY the base form of the word as a simple string (no JSON):\n" +
                     "- For nouns: nominative singular\n" +
                     "  - ONLY append gender (m), (f), or (n) if the noun ends with soft sign ь\n" +
                     "  - Examples: \"дверь (f)\", \"день (m)\", \"книга\", \"дом\", \"окно\"\n" +
                     "- For adjectives: masculine nominative singular, no gender markers\n" +
                     "  - Example: \"новый\"\n" +
                     "- For adverbs: return as-is (do NOT convert to adjective)\n" +
                     "  - Examples: \"необходимо\", \"хорошо\", \"быстро\"\n" +
                     "- For verbs: infinitive with aspect marker\n" +
                     "  - Use (i) for imperfective or (p) for perfective\n" +
                     "  - Provide ONLY ONE form based on what is used in the sentence\n" +
                     "  - Examples: \"читать (i)\", \"прочитать (p)\", \"купить (p)\"\n" +
                     "- For prepositions requiring a case: append case, e.g., \"по (+d)\"\n\n" +
                     "Return ONLY the base form string, nothing else.";

        return await CallOpenAI(prompt, apiKey, extractString: true);
    }

    private async Task<(string BaseWord, string Translation)?> SearchAzureAISearch(string baseForm, string endpoint, string apiKey)
    {
        try
        {
            // Clean the base form for search (remove markers and trailing notes)
            var searchTerm = CleanBaseFormForLookup(baseForm);

            var encodedSearch = HttpUtility.UrlEncode(searchTerm);
            var url = $"{endpoint}/indexes/russian-lexicon/docs?api-version=2023-11-01&search={encodedSearch}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("api-key", apiKey);

            using var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure Search API returned: {status}", response.StatusCode);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseContent);

            if (!doc.RootElement.TryGetProperty("value", out var values) || values.GetArrayLength() == 0)
            {
                _logger.LogInformation("No results found in Azure Search for: {searchTerm}", searchTerm);
                return null;
            }

            // Get the first result (highest score)
            var topResult = values[0];
            
            if (!topResult.TryGetProperty("base_word", out var baseWordProp) ||
                !topResult.TryGetProperty("translation", out var translationProp))
            {
                _logger.LogWarning("Top result missing base_word or translation fields");
                return null;
            }

            var baseWord = baseWordProp.GetString();
            var translation = translationProp.GetString();

            if (string.IsNullOrWhiteSpace(baseWord))
            {
                _logger.LogWarning("Top result has empty base_word");
                return null;
            }

            return (baseWord, translation ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Azure AI Search");
            return null;
        }
    }

    private async Task<string?> GetMeaningFromOpenAI(string sentence, string word, string baseForm, string apiKey)
    {
        var prompt = $"For the Russian word '{word}' in the sentence \"{sentence}\" " +
                     $"(base form: {baseForm}), provide ONLY a Russian definition.\n\n" +
                     "Provide a direct definition in Russian - be accurate over brevity.\n" +
                     "Do NOT include meta-language like 'Глагол означает', 'Это значит', etc.\n" +
                     "Just give the core meaning directly, using as many words as needed for clarity.\n" +
                     "Examples:\n" +
                     "- For 'учусь': получать знания и умения в школе или другом учреждении\n" +
                     "- For 'дверь': створка или конструкция для входа в помещение или выхода из него\n" +
                     "- For 'читать': воспринимать напечатанный или написанный текст\n\n" +
                     "Return ONLY the Russian definition, no JSON, no extra text.";

        return await CallOpenAI(prompt, apiKey, extractString: true);
    }

    private async Task<IActionResult> GetFullAnalysisFromOpenAI(string sentence, string word, string apiKey)
    {
        var prompt = BuildFullAnalysisPrompt(sentence, word);
        var jsonResponse = await CallOpenAI(prompt, apiKey, extractString: false);

        if (string.IsNullOrWhiteSpace(jsonResponse))
        {
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        try
        {
            var parsed = JsonDocument.Parse(jsonResponse);
            return new OkObjectResult(JsonSerializer.Deserialize<object>(jsonResponse));
        }
        catch (JsonException)
        {
            return new OkObjectResult(new { raw = jsonResponse });
        }
    }

    private async Task<WiktionaryData?> FetchWiktionaryData(string baseForm)
    {
        try
        {
            // Clean the base form for Wiktionary search
            var searchTerm = CleanBaseFormForLookup(baseForm);

            var wikitext = await FetchWiktionaryWikitext(searchTerm);
            if (string.IsNullOrEmpty(wikitext))
            {
                _logger.LogWarning("No Wiktionary entry found for: {searchTerm}", searchTerm);
                return null;
            }

            return ParseWiktionaryWikitext(wikitext, searchTerm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Wiktionary data");
            return null;
        }
    }

    private async Task<string?> FetchWiktionaryWikitext(string word)
    {
        var encodedWord = Uri.EscapeDataString(word);
        var url = $"https://ru.wiktionary.org/w/api.php?action=query&prop=revisions&rvprop=content&format=json&titles={encodedWord}&rvslots=main";

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);

        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Name == "-1")
            {
                return null;
            }

            if (page.Value.TryGetProperty("revisions", out var revisions) && revisions.GetArrayLength() > 0)
            {
                var revision = revisions[0];
                if (revision.TryGetProperty("slots", out var slots) &&
                    slots.TryGetProperty("main", out var main) &&
                    main.TryGetProperty("*", out var wikitext))
                {
                    return wikitext.GetString();
                }
            }
        }

        return null;
    }

    private static string CleanBaseFormForLookup(string baseForm)
    {
        var cleaned = baseForm
            .Replace(" (i)", "")
            .Replace(" (p)", "")
            .Replace(" (m)", "")
            .Replace(" (f)", "")
            .Replace(" (n)", "")
            .Replace(" (+d)", "")
            .Replace(" (+a)", "")
            .Replace(" (+g)", "")
            .Replace(" (+i)", "")
            .Replace(" (+p)", "")
            .Trim();

        var firstSpace = cleaned.IndexOf(' ');
        return firstSpace > 0 ? cleaned[..firstSpace].Trim() : cleaned;
    }

    private WiktionaryData ParseWiktionaryWikitext(string wikitext, string word)
    {
        var data = new WiktionaryData
        {
            Translations = new List<string>(),
            Meanings = new List<string>()
        };

        // Extract meanings from Значение section
        var meaningsSectionMatch = Regex.Match(wikitext, @"===\s*Значение\s*===\s*(.*?)(?=(^===|^==|\z))", 
            RegexOptions.Singleline | RegexOptions.Multiline);

        if (meaningsSectionMatch.Success)
        {
            var meaningsSection = meaningsSectionMatch.Groups[1].Value;
            var definitionMatches = Regex.Matches(meaningsSection, @"^#\s*(?!\*|:)([^\n]+)", RegexOptions.Multiline);
            
            foreach (Match match in definitionMatches)
            {
                var rawMeaning = match.Groups[1].Value;
                var exampleMatch = Regex.Match(rawMeaning, @"\{\{(?:пример|example)");
                if (exampleMatch.Success)
                {
                    rawMeaning = rawMeaning.Substring(0, exampleMatch.Index);
                }
                
                var meaning = CleanWikitext(rawMeaning);
                if (!string.IsNullOrWhiteSpace(meaning))
                {
                    data.Meanings.Add(meaning);
                }
            }
        }

        // Extract translations from Перевод section
        var translationSectionMatch = Regex.Match(wikitext, @"===\s*Перевод\s*===\s*(.*?)(?=(^===|^==|\z))", 
            RegexOptions.Singleline | RegexOptions.Multiline);
        
        if (translationSectionMatch.Success)
        {
            var translationSection = translationSectionMatch.Groups[1].Value;
            
            var englishMatches = Regex.Matches(translationSection, @"\|\s*en\s*=\s*([^\n|]+)", RegexOptions.Multiline);
            foreach (Match match in englishMatches)
            {
                var translation = CleanWikitext(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(translation) && !data.Translations.Contains(translation))
                {
                    data.Translations.Add(translation);
                }
            }
            
            var translationEntries = Regex.Matches(translationSection, @"\*\s*{{\s*(?:en|английский)\s*}}\s*:\s*(.+?)(?=\n|\|)", RegexOptions.Multiline);
            foreach (Match match in translationEntries)
            {
                var translationLine = match.Groups[1].Value;
                var linkMatches = Regex.Matches(translationLine, @"\[\[(?:[^|\]]+\|)?([^\]]+)\]\]");
                foreach (Match linkMatch in linkMatches)
                {
                    var translation = CleanWikitext(linkMatch.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(translation) && !data.Translations.Contains(translation))
                    {
                        data.Translations.Add(translation);
                    }
                }
            }
        }

        return data;
    }

    private string CleanWikitext(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        while (text.Contains("{{"))
        {
            var oldText = text;
            text = Regex.Replace(text, @"\{\{[^{}]+\}\}", "");
            if (text == oldText) break;
        }
        
        text = Regex.Replace(text, @"\[\[(?:[^|\]]+\|)?([^\]]+)\]\]", "$1");
        text = Regex.Replace(text, @"\.?\}\}+", "");
        text = Regex.Replace(text, @"\{\{+", "");
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = text.Replace("'''", "").Replace("''", "");
        text = Regex.Replace(text, @"\|[^|]+$", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"\s*\.\s*$", ".");
        
        return text;
    }

    private async Task<string?> SelectRelevantTranslations(string sentence, string word, string baseForm, List<string> translations, string apiKey)
    {
        var translationsList = string.Join(", ", translations.Select((t, i) => $"{i + 1}. {t}"));
        
        var prompt = $"Given the Russian word '{word}' (base form: {baseForm}) in the sentence \"{sentence}\", " +
                     $"select the most relevant English translation(s) from this list:\n{translationsList}\n\n" +
                     "Return ONLY the selected translation(s). If multiple are equally relevant, separate them with '; '.\n" +
                     "Examples: 'house' or 'house; home' or 'building; structure'.\n" +
                     "Do not include numbers or explanations, just the translation(s).";

        return await CallOpenAI(prompt, apiKey, extractString: true);
    }

    private async Task<string?> SelectRelevantMeaning(string sentence, string word, string baseForm, List<string> meanings, string apiKey)
    {
        var meaningsList = string.Join("\n", meanings.Select((m, i) => $"{i + 1}. {m}"));
        
        var prompt = $"Given the Russian word '{word}' (base form: {baseForm}) in the sentence \"{sentence}\", " +
                     $"select the most contextually relevant Russian definition from this list:\n{meaningsList}\n\n" +
                     "Return ONLY the selected Russian definition, without the number. Do not modify or explain it.";

        return await CallOpenAI(prompt, apiKey, extractString: true);
    }

    private async Task<string?> CallOpenAI(string prompt, string apiKey, bool extractString)
    {
        var systemMessage = extractString
            ? "You are a Russian language expert. Reply with ONLY the requested text, no JSON, no explanation, no extra formatting."
            : "You are a Russian language expert. Reply with a valid JSON object only. No surrounding text or explanation.";

        var requestBody = JsonSerializer.Serialize(new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemMessage },
                new { role = "user", content = prompt }
            },
            temperature = 0.2
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

    private static string BuildFullAnalysisPrompt(string sentence, string word)
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
               "  - CRITICAL: ONLY append gender (m), (f), or (n) if the noun ends with soft sign ь\n" +
               "  - Examples WITH gender (soft sign endings): \"дверь (f)\", \"день (m)\", \"учитель (m)\", \"площадь (f)\", \"тетрадь (f)\"\n" +
               "  - Examples WITHOUT gender (clear endings): \"книга\", \"дом\", \"окно\", \"стол\", \"машина\", \"город\", \"вода\", \"дело\"\n" +
               "  - DO NOT write: \"книга (f)\", \"дом (m)\", \"окно (n)\", \"машина (f)\", \"город (m)\" - these are WRONG!\n" +
               "  - If the noun doesn't end with ь, never add gender markers\n" +
               "- For adjectives: provide masculine nominative singular WITHOUT gender markers (if the word is inflected differently in the sentence)\n" +
               "  - Example: \"новый\" not \"новый (m)\"\n" +
               "- For adverbs: return the adverb as-is (do NOT convert to adjective form)\n" +
               "  - CRITICAL: Adverbs should stay as adverbs, not be converted to their adjective forms\n" +
               "  - Examples: \"необходимо\", \"хорошо\", \"быстро\", \"красиво\", \"важно\"\n" +
               "  - DO NOT write: \"необходимый\", \"хороший\", \"быстрый\" - these are WRONG for adverbs!\n" +
               "- For verbs: provide the infinitive form with aspect marker\n" +
               "  - Use (i) for imperfective or (p) for perfective\n" +
               "  - Provide ONLY ONE form - the aspect that matches what is used in the sentence\n" +
               "  - Examples: \"читать (i)\", \"прочитать (p)\", \"купить (p)\", \"делать (i)\"\n" +
               "  - If the sentence uses imperfective, provide only the imperfective form\n" +
               "  - If the sentence uses perfective, provide only the perfective form\n" +
               "  - Common imperfective verbs: \"делать (i)\", \"читать (i)\", \"писать (i)\", \"говорить (i)\", \"покупать (i)\"\n" +
               "  - Common perfective verbs: \"сделать (p)\", \"прочитать (p)\", \"написать (p)\", \"сказать (p)\", \"купить (p)\"\n" +
               "- For prepositions that always require a specific case, append the case in brackets with +, e.g., \"по (+d)\" for dative\n" +
               "- If the word in the sentence is already in base form, still include the baseForm field with the word and metadata\n\n" +
               "For englishTranslation:\n" +
               "- Provide the English translation that best fits THIS SPECIFIC CONTEXT in the sentence\n" +
               "- Consider the nuance and register (formal/informal) used in the sentence\n" +
               "- For verbs, translate to match the aspect used (completed action vs ongoing/habitual)\n" +
               "- Keep it concise - typically 1-3 words\n\n" +
               "For russianMeaning:\n" +
               "- Provide a direct definition in Russian - be accurate over artificially brief\n" +
               "- Use as many words as needed to capture the true meaning clearly\n" +
               "- Do NOT include meta-language like 'Глагол означает', 'Это значит', 'Слово обозначает'\n" +
               "- Just give the core meaning directly\n" +
               "- Make it contextually relevant to how the word is used in this sentence\n\n" +
               "COMPLETE EXAMPLES:\n" +
               "1. Word 'читал' in \"Вчера я читал книгу\" (imperfective, ongoing action):\n" +
               "{\n" +
               "  \"baseForm\": \"читать (i)\",\n" +
               "  \"englishTranslation\": \"was reading\",\n" +
               "  \"russianMeaning\": \"воспринимать напечатанный текст с целью понимания его содержания\"\n" +
               "}\n\n" +
               "2. Word 'прочитал' in \"Я прочитал книгу\" (perfective, completed action):\n" +
               "{\n" +
               "  \"baseForm\": \"прочитать (p)\",\n" +
               "  \"englishTranslation\": \"read / finished reading\",\n" +
               "  \"russianMeaning\": \"полностью воспринять и понять напечатанный текст\"\n" +
               "}\n\n" +
               "3. Word 'дверь' in \"Он открыл дверь\":\n" +
               "{\n" +
               "  \"baseForm\": \"дверь (f)\",\n" +
               "  \"englishTranslation\": \"door\",\n" +
               "  \"russianMeaning\": \"створка или конструкция для входа в помещение или выхода из него\"\n" +
               "}\n\n" +
               "4. Word 'купил' in \"Я купил новую машину\" (perfective, completed action):\n" +
               "{\n" +
               "  \"baseForm\": \"купить (p)\",\n" +
               "  \"englishTranslation\": \"bought\",\n" +
               "  \"russianMeaning\": \"приобрести товар или предмет путем обмена денег на требуемую стоимость\"\n" +
               "}\n\n" +
               "Return ONLY the JSON object for the requested word, no other text.";
    }
}

public class WiktionaryData
{
    public List<string> Translations { get; set; }
    public List<string> Meanings { get; set; }
}
