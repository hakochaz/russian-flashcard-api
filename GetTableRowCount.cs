using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace russian_flashcard_api;

public class GetTableRowCount
{
    private readonly ILogger<GetTableRowCount> _logger;

    public GetTableRowCount(ILogger<GetTableRowCount> logger)
    {
        _logger = logger;
    }

    private void AddCorsHeaders(HttpContext context)
    {
        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    }

    [Function("GetTableRowCount")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "options", Route = "table/{tableName}/count")] HttpRequest req,
        string tableName)
    {
        // Add CORS headers to all responses
        AddCorsHeaders(req.HttpContext);

        // Handle CORS preflight
        if (req.Method == "OPTIONS")
        {
            return new OkResult();
        }

        _logger.LogInformation("GetTableRowCount called for table {table}", tableName);

        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("AzureWebJobsStorage is not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var tableClient = new TableClient(connectionString, tableName);
            
            int count = 0;
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                count++;
            }

            return new OkObjectResult(new { rowCount = count });
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Table {table} not found", tableName);
            return new NotFoundResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting table row count");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
