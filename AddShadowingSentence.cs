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

public class AddShadowingSentence
{
    private readonly ILogger<AddShadowingSentence> _logger;

    public AddShadowingSentence(ILogger<AddShadowingSentence> logger)
    {
        _logger = logger;
    }

    [Function("AddShadowingSentence")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shadowing/add")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string? sentence = null;
        string? difficulty = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("sentence", out var s))
                    sentence = s.GetString();
                if (doc.RootElement.TryGetProperty("difficulty", out var d))
                    difficulty = d.GetString();
            }
        }
        catch (JsonException)
        {
            // validation below
        }

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'sentence' in the JSON body." });
        }

        if (string.IsNullOrWhiteSpace(difficulty))
        {
            return new BadRequestObjectResult(new { error = "Please provide 'difficulty' in the JSON body." });
        }

        _logger.LogInformation("AddShadowingSentence called for sentence: {sentence} with difficulty: {difficulty}", sentence, difficulty);

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        int nextRowKey = 1;
        try
        {
            var tableClient = new TableClient(connectionString, "Shadowing");

            // Check if sentence already exists and count total rows in table
            int totalRows = 0;
            
            _logger.LogInformation("Counting all rows in table");
            
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                totalRows++;
                
                // Check for duplicate sentence in this partition
                if (entity.PartitionKey == difficulty && entity.TryGetValue("Sentence", out var existingSentenceObj))
                {
                    var existingSentence = existingSentenceObj?.ToString();
                    if (existingSentence == sentence)
                    {
                        _logger.LogWarning("Sentence already exists for difficulty: {difficulty}", difficulty);
                        return new ConflictObjectResult(new { error = "This sentence already exists for the given difficulty level." });
                    }
                }
            }
            
            _logger.LogInformation("Total rows in table: {totalRows}, setting nextRowKey to: {nextRowKey}", totalRows, totalRows + 1);
            nextRowKey = totalRows + 1;

            // Create new entity
            var newEntity = new TableEntity(difficulty, nextRowKey.ToString())
            {
                { "Sentence", sentence },
                { "Timestamp", DateTime.UtcNow }
            };

            // Add entity to table
            await tableClient.AddEntityAsync(newEntity);

            _logger.LogInformation("Successfully added shadowing sentence with PartitionKey: {partitionKey}, RowKey: {rowKey}", difficulty, nextRowKey);

            return new CreatedResult($"/shadowing/{nextRowKey}?partitionKey={difficulty}", new
            {
                partitionKey = difficulty,
                rowKey = nextRowKey.ToString(),
                sentence = sentence
            });
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogWarning("Entity already exists with PartitionKey: {partitionKey}, RowKey: {rowKey}", difficulty, nextRowKey);
            return new ConflictObjectResult(new { error = "Entity with this key already exists." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding shadowing sentence");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
