using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace russian_flashcard_api;

public class GetEntityByRowId
{
    private readonly ILogger<GetEntityByRowId> _logger;

    public GetEntityByRowId(ILogger<GetEntityByRowId> logger)
    {
        _logger = logger;
    }

    [Function("GetEntityByRowId")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "entity/{tableName}/{rowKey}")] HttpRequest req,
        string tableName,
        string rowKey)
    {
        _logger.LogInformation("GetEntityByRowId called for table {table} row {row}", tableName, rowKey);

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var tableClient = new TableClient(connectionString, tableName);

            // Optional partitionKey query parameter
            var partitionKey = req.Query["partitionKey"].FirstOrDefault();

            if (!string.IsNullOrEmpty(partitionKey))
            {
                try
                {
                    var resp = await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey);
                    return new OkObjectResult(resp.Value);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return new NotFoundResult();
                }
            }

            // If no partitionKey provided, query by RowKey across partitions (may be slower)
            var filter = $"RowKey eq '{rowKey.Replace("'","''")}'";
            await foreach (var page in tableClient.QueryAsync<TableEntity>(filter))
            {
                // return first match
                return new OkObjectResult(page);
            }

            return new NotFoundResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching table entity");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
