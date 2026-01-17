using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text;

namespace russian_flashcard_api;

public class SynthesizeSpeech
{
    private readonly ILogger<SynthesizeSpeech> _logger;

    public SynthesizeSpeech(ILogger<SynthesizeSpeech> logger)
    {
        _logger = logger;
    }

    [Function("SynthesizeSpeech")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "russian/synthesize")] HttpRequest req)
    {
        _logger.LogInformation("SynthesizeSpeech called");

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return new BadRequestObjectResult(new { error = "Request body is empty" });
        }

        string sentence;
        string? fileName = null;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("sentence", out var sentenceProp))
            {
                return new BadRequestObjectResult(new { error = "Missing 'sentence' in request body" });
            }

            sentence = sentenceProp.GetString() ?? string.Empty;
            if (root.TryGetProperty("fileName", out var fileNameProp))
            {
                fileName = fileNameProp.GetString();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in request");
            return new BadRequestObjectResult(new { error = "Invalid JSON" });
        }

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return new BadRequestObjectResult(new { error = "Sentence is empty" });
        }

        var speechKey = Environment.GetEnvironmentVariable("SPEECH_SERVICE_KEY");
        if (string.IsNullOrEmpty(speechKey))
        {
            _logger.LogError("SPEECH_SERVICE_KEY not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        var storageConnection = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrEmpty(storageConnection))
        {
            _logger.LogError("AzureWebJobsStorage not configured");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            // Use the REST TTS endpoint with the working voice (ru-RU-DmitryNeural)
            var restBytes = await CallRestTtsAsync(speechKey, sentence);
            if (restBytes == null || restBytes.Length == 0)
            {
                _logger.LogError("TTS returned no audio bytes");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            // Prepare filename
            var blobName = fileName;
            if (string.IsNullOrWhiteSpace(blobName))
            {
                blobName = $"russian-{Guid.NewGuid()}.mp3";
            }

            // Upload to blob storage
            var blobService = new BlobServiceClient(storageConnection);
            var container = blobService.GetBlobContainerClient("anki-audio");
            await container.CreateIfNotExistsAsync(PublicAccessType.Blob);

            var blobClient = container.GetBlobClient(blobName);
            // Write REST bytes directly to a temp file and upload
            var tempPath = Path.Combine(Path.GetTempPath(), blobName);
            try
            {
                await File.WriteAllBytesAsync(tempPath, restBytes);
                var headers = new BlobHttpHeaders { ContentType = blobName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav" };
                using var fs = File.OpenRead(tempPath);
                var fi = new FileInfo(tempPath);
                if (fi.Length == 0)
                {
                    _logger.LogError("Synthesized temp file is empty, aborting upload");
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                }

                await blobClient.UploadAsync(fs, new BlobUploadOptions { HttpHeaders = headers });
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            var uri = blobClient.Uri.ToString();
            return new OkObjectResult(new { url = uri });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error synthesizing or uploading audio");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<byte[]?> CallRestTtsAsync(string speechKey, string sentence)
    {
        // Use region-specific TTS endpoint for uksouth
        var ttsEndpoint = Environment.GetEnvironmentVariable("SPEECH_SERVICE_TTS_ENDPOINT") ?? "https://uksouth.tts.speech.microsoft.com/cognitiveservices/v1";

        using var http = new HttpClient();

        // Try a list of candidate voices (male first). Some resources may not support every neural voice,
        // so try alternatives and finally a female fallback.
        var candidateVoices = new[] { "ru-RU-DmitryNeural" };

        foreach (var voice in candidateVoices)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ttsEndpoint);
            req.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
            req.Headers.Add("User-Agent", "russian-flashcard-api");
            req.Headers.Add("X-Microsoft-OutputFormat", "audio-16khz-128kbitrate-mono-mp3");

            var ssml = $@"<speak version='1.0' xml:lang='ru-RU'>
  <voice xml:lang='ru-RU' name='{voice}'>{System.Security.SecurityElement.Escape(sentence)}</voice>
</speak>";

            req.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                _logger.LogInformation("REST TTS returned {len} bytes using voice {voice}", bytes.Length, voice);
                return bytes;
            }

            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("REST TTS with voice {voice} failed: Status={status} Body={body}", voice, (int)resp.StatusCode, body);
            // If 400 unsupported voice, try next; for other errors we also continue to try alternatives.
        }

        _logger.LogError("REST TTS: no candidate voices succeeded");
        return null;
    }
}
