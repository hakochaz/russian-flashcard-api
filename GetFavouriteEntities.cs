using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace russian_flashcard_api;

public class GetFavouriteEntities
{
    private readonly ILogger<GetFavouriteEntities> _logger;

    public GetFavouriteEntities(ILogger<GetFavouriteEntities> logger)
    {
        _logger = logger;
    }

    [Function("GetFavouriteEntities")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "favourites/{tableName}")] HttpRequest req,
        string tableName)
    {
        _logger.LogInformation("GetFavouriteEntities called for table {table}", tableName);

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var tableClient = new TableClient(connectionString, tableName);

            var filter = "Favourite eq true";

            var partitionKey = req.Query["partitionKey"].ToString();
            if (!string.IsNullOrEmpty(partitionKey))
            {
                filter = $"{filter} and PartitionKey eq '{partitionKey.Replace("'", "''")}'";
            }

            var results = new List<TableEntity>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter))
            {
                results.Add(entity);
            }

            return new OkObjectResult(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching favourite entities for table {table}", tableName);
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
