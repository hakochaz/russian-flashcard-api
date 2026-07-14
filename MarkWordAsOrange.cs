using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api;

public class MarkWordAsOrange
{
    private readonly ILogger<MarkWordAsOrange> _logger;

    public MarkWordAsOrange(ILogger<MarkWordAsOrange> logger)
    {
        _logger = logger;
    }

    [Function("MarkWordAsOrange")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sheets/mark-orange")] HttpRequest req)
    {
        _logger.LogInformation("MarkWordAsOrange called");

        var word = req.Query["word"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(word))
            return new BadRequestObjectResult(new { error = "Query parameter 'word' is required" });

        var spreadsheetId = Environment.GetEnvironmentVariable("GOOGLE_SHEETS_SPREADSHEET_ID");
        var clientEmail = Environment.GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT_EMAIL");
        var privateKey = Environment.GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT_PRIVATE_KEY")
            ?.Replace("\\n", "\n");

        if (string.IsNullOrWhiteSpace(spreadsheetId) || string.IsNullOrWhiteSpace(clientEmail) || string.IsNullOrWhiteSpace(privateKey))
        {
            _logger.LogError("Google Sheets configuration is missing");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var credential = new ServiceAccountCredential(
                new ServiceAccountCredential.Initializer(clientEmail)
                {
                    Scopes = new[] { SheetsService.Scope.Spreadsheets }
                }.FromPrivateKey(privateKey));

            using var service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential
            });

            const string sheetName = "10,000 most common russian words";

            // Find the word in column A
            var getRequest = service.Spreadsheets.Get(spreadsheetId);
            getRequest.Ranges = new[] { $"'{sheetName}'!A:A" };
            getRequest.Fields = "sheets(properties.sheetId,data.rowData.values.formattedValue)";
            var spreadsheet = await getRequest.ExecuteAsync();

            var sheet = spreadsheet.Sheets?.FirstOrDefault();
            var sheetData = sheet?.Data?.FirstOrDefault();
            if (sheetData?.RowData == null)
                return new NotFoundObjectResult(new { error = "No data found in sheet" });

            int rowIndex = -1;
            for (int i = 0; i < sheetData.RowData.Count; i++)
            {
                var cellValue = sheetData.RowData[i].Values?.FirstOrDefault()?.FormattedValue;
                if (string.Equals(cellValue, word, StringComparison.OrdinalIgnoreCase))
                {
                    rowIndex = i;
                    break;
                }
            }

            if (rowIndex == -1)
                return new NotFoundObjectResult(new { error = $"Word '{word}' not found in column A" });

            var sheetId = (int)(sheet!.Properties.SheetId ?? 0);

            var orange = new Color { Red = 1f, Green = 0.6f, Blue = 0f };

            var batchRequest = new BatchUpdateSpreadsheetRequest
            {
                Requests = new List<Request>
                {
                    new Request
                    {
                        RepeatCell = new RepeatCellRequest
                        {
                            Range = new GridRange
                            {
                                SheetId = sheetId,
                                StartRowIndex = rowIndex,
                                EndRowIndex = rowIndex + 1,
                                StartColumnIndex = 0,
                                EndColumnIndex = 1
                            },
                            Cell = new CellData
                            {
                                UserEnteredFormat = new CellFormat
                                {
                                    BackgroundColor = orange
                                }
                            },
                            Fields = "userEnteredFormat.backgroundColor"
                        }
                    }
                }
            };

            await service.Spreadsheets.BatchUpdate(batchRequest, spreadsheetId).ExecuteAsync();

            return new OkObjectResult(new { rowNumber = rowIndex + 1, word });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Google Sheet");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
