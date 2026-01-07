using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace russian_flashcard_api;

public class SearchByPhrase
{
    private readonly ILogger<SearchByPhrase> _logger;

    public SearchByPhrase(ILogger<SearchByPhrase> logger)
    {
        _logger = logger;
    }

    [Function("SearchByPhrase")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "search/{tableName}")] HttpRequest req,
        string tableName)
    {
        var q = req.Query["q"].ToString();
        if (string.IsNullOrEmpty(q))
        {
            return new BadRequestObjectResult("Query parameter 'q' is required (text to search for)");
        }

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var tableClient = new TableClient(connectionString, tableName);

            var partitionKey = req.Query["partitionKey"].ToString();

            string? filter = null;
            if (!string.IsNullOrEmpty(partitionKey))
            {
                filter = $"PartitionKey eq '{partitionKey.Replace("'","''")}'";
            }

            var results = new List<TableEntity>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter))
            {
                if (entity.TryGetValue("Phrase", out var phraseObj) && phraseObj != null)
                {
                    var phrase = phraseObj.ToString();
                    if (!string.IsNullOrEmpty(phrase) &&
                        phrase.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        results.Add(entity);
                    }
                }
            }

            return new OkObjectResult(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching table {table} for phrase '{q}'", tableName, q);
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
