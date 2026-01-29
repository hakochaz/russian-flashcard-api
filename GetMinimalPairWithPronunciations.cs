using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace russian_flashcard_api;

public class GetMinimalPairWithPronunciations
{
    private readonly ILogger<GetMinimalPairWithPronunciations> _logger;

    public GetMinimalPairWithPronunciations(ILogger<GetMinimalPairWithPronunciations> logger)
    {
        _logger = logger;
    }

    [Function("GetMinimalPairWithPronunciations")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "minimalpairs/{id}")] HttpRequest req,
        string id)
    {
        _logger.LogInformation("GetMinimalPairWithPronunciations called for ID: {id}", id);

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
            var tableClient = new TableClient(connectionString, "MinimalPairs");

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

            // Extract pair from entity
            string? pair = null;
            if (entity.TryGetValue("Pair", out var pairValue))
            {
                pair = pairValue?.ToString();
            }

            if (string.IsNullOrEmpty(pair))
            {
                _logger.LogWarning("Entity found but no Pair field present");
                return new OkObjectResult(new MinimalPairWithPronunciationsResult
                {
                    Entity = entity,
                    Pronunciations1 = new List<ForvoPronunciation>(),
                    Pronunciations2 = new List<ForvoPronunciation>()
                });
            }

            var parts = pair
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (parts.Count == 0)
            {
                return new OkObjectResult(new MinimalPairWithPronunciationsResult
                {
                    Entity = entity,
                    Pronunciations1 = new List<ForvoPronunciation>(),
                    Pronunciations2 = new List<ForvoPronunciation>()
                });
            }

            // Fetch pronunciations for each part
            var pronunciations1 = parts.Count > 0
                ? await FetchForvoPronunciations(apiKey, parts[0])
                : new List<ForvoPronunciation>();

            var pronunciations2 = parts.Count > 1
                ? await FetchForvoPronunciations(apiKey, parts[1])
                : new List<ForvoPronunciation>();

            var result = new MinimalPairWithPronunciationsResult
            {
                Entity = entity,
                Pronunciations1 = pronunciations1,
                Pronunciations2 = pronunciations2
            };

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching minimal pair entity with pronunciations");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<List<ForvoPronunciation>> FetchForvoPronunciations(string apiKey, string text)
    {
        var pronunciations = new List<ForvoPronunciation>();

        try
        {
            using var httpClient = new HttpClient();
            var url = $"https://apifree.forvo.com/key/{apiKey}/format/json/action/word-pronunciations/word/{Uri.EscapeDataString(text)}/language/ru";

            _logger.LogInformation($"Fetching pronunciations from Forvo for: {text}");

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

            _logger.LogInformation($"Found {pronunciations.Count} pronunciations for text: {text}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching Forvo pronunciations for text: {text}");
        }

        return pronunciations;
    }
}

public class MinimalPairWithPronunciationsResult
{
    [JsonPropertyName("entity")]
    public TableEntity? Entity { get; set; }

    [JsonPropertyName("pronunciations1")]
    public List<ForvoPronunciation> Pronunciations1 { get; set; } = new();

    [JsonPropertyName("pronunciations2")]
    public List<ForvoPronunciation> Pronunciations2 { get; set; } = new();
}

