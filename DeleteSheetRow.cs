using System;
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

public class DeleteSheetRow
{
    private readonly ILogger<DeleteSheetRow> _logger;

    public DeleteSheetRow(ILogger<DeleteSheetRow> logger)
    {
        _logger = logger;
    }

    [Function("DeleteSheetRow")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sheets/row")] HttpRequest req)
    {
        var text = req.Query["text"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
            return new BadRequestObjectResult(new { error = "Please provide a 'text' query parameter." });

        _logger.LogInformation("DeleteSheetRow called for text: {text}", text);

        var spreadsheetId = Environment.GetEnvironmentVariable("GOOGLE_SHEETS_SPREADSHEET_ID");
        var clientEmail = Environment.GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT_EMAIL");
        var privateKey = Environment.GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT_PRIVATE_KEY");

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

            var metaRequest = service.Spreadsheets.Get(spreadsheetId);
            metaRequest.Fields = "sheets.properties(sheetId,title)";
            var meta = await metaRequest.ExecuteAsync();
            var sheet = meta.Sheets?.FirstOrDefault(s => s.Properties.Title == sheetName);
            if (sheet == null)
                return new NotFoundObjectResult(new { error = $"Sheet '{sheetName}' not found" });
            var sheetId = sheet.Properties.SheetId;

            var range = $"'{sheetName}'!A:A";
            var valuesRequest = service.Spreadsheets.Values.Get(spreadsheetId, range);
            var valuesResponse = await valuesRequest.ExecuteAsync();

            if (valuesResponse.Values == null)
                return new NotFoundObjectResult(new { error = "No data found in column A" });

            int? matchIndex = null;
            for (int i = 0; i < valuesResponse.Values.Count; i++)
            {
                var row = valuesResponse.Values[i];
                if (row.Count > 0 && row[0] != null && string.Equals(row[0].ToString(), text, StringComparison.Ordinal))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex == null)
                return new NotFoundObjectResult(new { error = $"No row found with exact text '{text}' in column A" });

            var deleteRequest = new Request
            {
                DeleteDimension = new DeleteDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = sheetId,
                        Dimension = "ROWS",
                        StartIndex = matchIndex.Value,
                        EndIndex = matchIndex.Value + 1
                    }
                }
            };

            var batchUpdate = new BatchUpdateSpreadsheetRequest { Requests = new[] { deleteRequest } };
            await service.Spreadsheets.BatchUpdate(batchUpdate, spreadsheetId).ExecuteAsync();

            var rowNumber = matchIndex.Value + 1;
            return new OkObjectResult(new { deleted = true, rowNumber, text });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting row from Google Sheet");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
