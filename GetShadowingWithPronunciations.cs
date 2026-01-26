using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace russian_flashcard_api;

public class GetShadowingWithPronunciations
{
    private readonly ILogger<GetShadowingWithPronunciations> _logger;

    public GetShadowingWithPronunciations(ILogger<GetShadowingWithPronunciations> logger)
    {
        _logger = logger;
    }

    [Function("GetShadowingWithPronunciations")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shadowing/{id}")] HttpRequest req,
        string id)
    {
        _logger.LogInformation("GetShadowingWithPronunciations called for ID: {id}", id);

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        var apiKey = Environment.GetEnvironmentVariable("FORVO_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("FORVO_API_KEY is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var tableClient = new TableClient(connectionString, "Shadowing");

            // Try to get entity by RowKey (assuming id is the RowKey)
            TableEntity? entity = null;
            var partitionKey = req.Query["partitionKey"].ToString();

            if (!string.IsNullOrEmpty(partitionKey))
            {
                try
                {
                    var resp = await tableClient.GetEntityAsync<TableEntity>(partitionKey, id);
                    entity = resp.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return new NotFoundObjectResult("Entity not found");
                }
            }
            else
            {
                // If no partitionKey provided, query by RowKey
                var filter = $"RowKey eq '{id.Replace("'", "''")}'";
                await foreach (var foundEntity in tableClient.QueryAsync<TableEntity>(filter))
                {
                    entity = foundEntity;
                    break;
                }

                if (entity == null)
                {
                    return new NotFoundObjectResult("Entity not found");
                }
            }

            // Extract sentence from entity
            string? sentence = null;
            if (entity.TryGetValue("Sentence", out var sentenceValue))
            {
                sentence = sentenceValue?.ToString();
            }

            if (string.IsNullOrEmpty(sentence))
            {
                _logger.LogWarning("Entity found but no Sentence field present");
                return new OkObjectResult(new ShadowingWithPronunciationsResult
                {
                    Entity = entity,
                    Pronunciations = new List<ForvoPronunciation>()
                });
            }

            // Fetch pronunciations from Forvo
            var pronunciations = await FetchForvoPronunciations(apiKey, sentence);

            var result = new ShadowingWithPronunciationsResult
            {
                Entity = entity,
                Pronunciations = pronunciations
            };

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching shadowing entity with pronunciations");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<List<ForvoPronunciation>> FetchForvoPronunciations(string apiKey, string sentence)
    {
        var pronunciations = new List<ForvoPronunciation>();

        try
        {
            using var httpClient = new HttpClient();
            var url = $"https://apifree.forvo.com/key/{apiKey}/format/json/action/word-pronunciations/word/{Uri.EscapeDataString(sentence)}/language/ru";

            _logger.LogInformation($"Fetching pronunciations from Forvo for: {sentence}");

            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Forvo API returned status code: {response.StatusCode}");
                return pronunciations;
            }

            var content = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var pronunciation = new ForvoPronunciation();

                    if (item.TryGetProperty("pathmp3", out var pathmp3))
                    {
                        pronunciation.AudioMp3 = pathmp3.GetString() ?? string.Empty;
                    }

                    if (item.TryGetProperty("sex", out var sex))
                    {
                        pronunciation.Sex = sex.GetString() ?? string.Empty;
                    }

                    if (item.TryGetProperty("username", out var username))
                    {
                        pronunciation.Username = username.GetString() ?? string.Empty;
                    }

                    if (item.TryGetProperty("country", out var country))
                    {
                        pronunciation.Country = country.GetString() ?? string.Empty;
                    }

                    pronunciations.Add(pronunciation);
                }
            }

            _logger.LogInformation($"Found {pronunciations.Count} pronunciations for sentence: {sentence}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching Forvo pronunciations for sentence: {sentence}");
        }

        return pronunciations;
    }
}

public class ShadowingWithPronunciationsResult
{
    [JsonPropertyName("entity")]
    public TableEntity? Entity { get; set; }

    [JsonPropertyName("pronunciations")]
    public List<ForvoPronunciation> Pronunciations { get; set; } = new();
}

public class ForvoPronunciation
{
    [JsonPropertyName("audioMp3")]
    public string AudioMp3 { get; set; } = string.Empty;

    [JsonPropertyName("sex")]
    public string Sex { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;
}
