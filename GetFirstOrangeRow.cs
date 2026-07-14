using System;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace russian_flashcard_api;

public class GetFirstOrangeRow
{
    private readonly ILogger<GetFirstOrangeRow> _logger;

    public GetFirstOrangeRow(ILogger<GetFirstOrangeRow> logger)
    {
        _logger = logger;
    }

    [Function("GetFirstOrangeRow")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sheets/first-orange-row")] HttpRequest req)
    {
        _logger.LogInformation("GetFirstOrangeRow called");

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
                    Scopes = new[] { SheetsService.Scope.SpreadsheetsReadonly }
                }.FromPrivateKey(privateKey));

            using var service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential
            });

            const string sheetName = "10,000 most common russian words";

            var getRequest = service.Spreadsheets.Get(spreadsheetId);
            getRequest.Ranges = new[] { $"'{sheetName}'!A:A" };
            getRequest.Fields = "sheets.data.rowData.values(formattedValue,effectiveFormat.backgroundColor)";
            var spreadsheet = await getRequest.ExecuteAsync();

            var sheetData = spreadsheet.Sheets?.FirstOrDefault()?.Data?.FirstOrDefault();
            if (sheetData?.RowData == null)
                return new NotFoundObjectResult(new { error = "No data found in sheet" });

            for (int i = 0; i < sheetData.RowData.Count; i++)
            {
                var row = sheetData.RowData[i];
                var cell = row.Values?.FirstOrDefault();
                if (cell == null)
                    continue;

                var bg = cell.EffectiveFormat?.BackgroundColor;
                if (bg == null)
                    continue;

                var red = bg.Red ?? 0f;
                var green = bg.Green ?? 0f;
                var blue = bg.Blue ?? 0f;

                bool isOrange = red >= 0.9f && green >= 0.4f && green <= 0.75f && blue <= 0.1f;

                if (isOrange)
                {
                    var rowNumber = i + 1;
                    var value = cell.FormattedValue;
                    return new OkObjectResult(new { rowNumber, value });
                }
            }

            return new NotFoundObjectResult(new { error = "No orange background rows found in column A" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading Google Sheet");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
