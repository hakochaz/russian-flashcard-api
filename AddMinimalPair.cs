using System;
using System.IO;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api;

public class AddMinimalPair
{
    private readonly ILogger<AddMinimalPair> _logger;

    public AddMinimalPair(ILogger<AddMinimalPair> logger)
    {
        _logger = logger;
    }

    [Function("AddMinimalPair")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "minimalpairs/add")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? pair = null;
        string? difficulty = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("pair", out var p))
                    pair = p.GetString();
                if (doc.RootElement.TryGetProperty("difficulty", out var d))
                    difficulty = d.GetString();
            }
        }
        catch (JsonException)
        {
            // validation below
        }

        if (string.IsNullOrWhiteSpace(pair))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'pair' in the JSON body." });
        }

        if (string.IsNullOrWhiteSpace(difficulty))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'difficulty' in the JSON body." });
        }

        _logger.LogInformation("AddMinimalPair called for pair: {pair} with difficulty: {difficulty}", pair, difficulty);

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        int nextRowKey = 1;
        try
        {
            var tableClient = new TableClient(connectionString, "MinimalPairs");

            // Check if pair already exists and count total rows in table
            int totalRows = 0;
            
            _logger.LogInformation("Counting all rows in table");
            
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                totalRows++;
                
                // Check for duplicate pair in this partition
                if (entity.PartitionKey == difficulty && entity.TryGetValue("Pair", out var existingPairObj))
                {
                    var existingPair = existingPairObj?.ToString();
                    if (existingPair == pair)
                    {
                        _logger.LogWarning("Pair already exists for difficulty: {difficulty}", difficulty);
                        return new ConflictObjectResult(new { error = "This pair already exists for the given difficulty level." });
                    }
                }
            }
            
            _logger.LogInformation("Total rows in table: {totalRows}, setting nextRowKey to: {nextRowKey}", totalRows, totalRows + 1);
            nextRowKey = totalRows + 1;

            // Create new entity
            var newEntity = new TableEntity(difficulty, nextRowKey.ToString())
            {
                { "Pair", pair },
                { "Timestamp", DateTime.UtcNow }
            };

            // Add entity to table
            await tableClient.AddEntityAsync(newEntity);

            _logger.LogInformation("Successfully added minimal pair with PartitionKey: {partitionKey}, RowKey: {rowKey}", difficulty, nextRowKey);

            return new CreatedResult($"/minimalpairs/{nextRowKey}?partitionKey={difficulty}", new
            {
                partitionKey = difficulty,
                rowKey = nextRowKey.ToString(),
                pair = pair
            });
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogWarning("Entity already exists with PartitionKey: {partitionKey}, RowKey: {rowKey}", difficulty, nextRowKey);
            return new ConflictObjectResult(new { error = "Entity with this key already exists." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding minimal pair");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
