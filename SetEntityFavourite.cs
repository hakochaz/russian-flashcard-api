using System.IO;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace russian_flashcard_api;

public class SetEntityFavourite
{
    private readonly ILogger<SetEntityFavourite> _logger;

    public SetEntityFavourite(ILogger<SetEntityFavourite> logger)
    {
        _logger = logger;
    }

    [Function("SetEntityFavourite")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "entity/{tableName}/{rowKey}/favourite")] HttpRequest req,
        string tableName,
        string rowKey)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        bool? favourite = null;
        string? partitionKey = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("favourite", out var f))
                    favourite = f.GetBoolean();
                if (doc.RootElement.TryGetProperty("partitionKey", out var p))
                    partitionKey = p.GetString();
            }
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new { error = "Request body must be valid JSON." });
        }

        if (favourite is null)
        {
            return new BadRequestObjectResult(new { error = "Please provide a boolean 'favourite' in the JSON body." });
        }

        // Allow partitionKey to come from the query string as an alternative to the body
        if (string.IsNullOrEmpty(partitionKey))
        {
            partitionKey = req.Query["partitionKey"].ToString();
        }

        _logger.LogInformation("SetEntityFavourite called for table {table} row {row} favourite {favourite}", tableName, rowKey, favourite);

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var tableClient = new TableClient(connectionString, tableName);

            if (string.IsNullOrEmpty(partitionKey))
            {
                // Find the entity's partition key by scanning for the RowKey
                var filter = $"RowKey eq '{rowKey.Replace("'", "''")}'";
                await foreach (var match in tableClient.QueryAsync<TableEntity>(filter))
                {
                    partitionKey = match.PartitionKey;
                    break;
                }

                if (string.IsNullOrEmpty(partitionKey))
                {
                    return new NotFoundResult();
                }
            }

            try
            {
                var resp = await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey);
                var entity = resp.Value;
                entity["Favourite"] = favourite.Value;

                await tableClient.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge);

                _logger.LogInformation("Set Favourite={favourite} for PartitionKey {partitionKey}, RowKey {rowKey}", favourite, partitionKey, rowKey);

                return new OkObjectResult(new
                {
                    partitionKey,
                    rowKey,
                    favourite = favourite.Value
                });
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return new NotFoundResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting Favourite on table entity");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
