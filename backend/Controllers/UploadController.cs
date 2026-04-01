using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Azure.Storage;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.IO;


[ApiController]
[Route("api/[controller]")]
[Microsoft.AspNetCore.Cors.EnableCors("AllowAll")]
public class UploadController : ControllerBase
{
    private readonly BlobServiceClient? _blobServiceClient;
    private readonly string? _containerName;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UploadController> _logger;
    private readonly string? _externalEndpointUrl;
    private readonly string? _externalEndpointXFunctionKey;
    private readonly string? _externalEndpointContentType;

    public UploadController(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<UploadController> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var connectionString = configuration["AzureStorage:ConnectionString"];
        _containerName = configuration["AzureStorage:ContainerName"];

        _externalEndpointUrl = configuration["ExternalEndpoint:Url"];
        _externalEndpointXFunctionKey = configuration["ExternalEndpoint:XFunctionKey"];
        _externalEndpointContentType = configuration["ExternalEndpoint:ContentType"];

        // Only initialize Azure client if credentials are configured
        if (!string.IsNullOrWhiteSpace(connectionString) && !connectionString.Contains("your_connection_string"))
        {
            _blobServiceClient = new BlobServiceClient(connectionString);
        }
    }

    /*
    [HttpGet("test")]
    public IActionResult TestConnection()
    {
        return Ok(new { 
            message = "Backend is connected and running!",
            azureConfigured = !string.IsNullOrWhiteSpace(_configuration["AzureStorage:ConnectionString"]) && 
                            !_configuration["AzureStorage:ConnectionString"].Contains("your_connection_string")
        });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestUpload([FromForm] List<IFormFile> files)
    {
        if (files == null || !files.Any())
            return BadRequest(new { error = "No files uploaded" });

        if (files.Count > 3)
            return BadRequest(new { error = "Maximum 3 files allowed" });

        var uploaded = new List<object>();
        foreach (var file in files)
        {
            if (file.ContentType != "application/pdf" && !file.ContentType.StartsWith("image/"))
                return BadRequest(new { error = "Only PDF and image files allowed" });

            long maxSize = file.ContentType == "application/pdf" ? 10 * 1024 * 1024 : 5 * 1024 * 1024;
            if (file.Length > maxSize)
                return BadRequest(new { error = $"File '{file.FileName}' too large (max {maxSize / (1024 * 1024)}MB)" });

            var testBlobUrl = $"https://test-storage.blob.core.windows.net/uploads/test-{DateTimeOffset.Now.ToUnixTimeSeconds()}-{file.FileName}";
            uploaded.Add(new { file.FileName, file.Length, file.ContentType, blobUrl = testBlobUrl });
        }

        return Ok(new
        {
            message = "✅ Test Upload Successful! Files would be uploaded to blob storage.",
            files = uploaded,
            testMode = true
        });
    }
    */

    [HttpPost]
    public async Task<IActionResult> UploadFile([FromForm] List<IFormFile> files)
    {
        if (files == null || !files.Any())
            return BadRequest(new { error = "No files uploaded" });

        if (files.Count > 3)
            return BadRequest(new { error = "Maximum 3 files allowed" });

        var uploaded = new List<object>();

        foreach (var file in files)
        {
            if (file.ContentType != "application/pdf" && !file.ContentType.StartsWith("image/"))
                return BadRequest(new { error = "Only PDF and image files allowed" });

            long maxSize = file.ContentType == "application/pdf" ? 10 * 1024 * 1024 : 5 * 1024 * 1024;
            if (file.Length > maxSize)
                return BadRequest(new { error = $"File '{file.FileName}' too large (max {maxSize / (1024 * 1024)}MB)" });

            // If Azure is configured, do real upload; otherwise simulate
            string blobUrl;
            if (_blobServiceClient == null || string.IsNullOrWhiteSpace(_containerName))
            {
                blobUrl = $"https://test-storage.blob.core.windows.net/uploads/{DateTimeOffset.Now.ToUnixTimeSeconds()}-{file.FileName}";
            }
            else
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
                var blobName = $"{DateTimeOffset.Now.ToUnixTimeSeconds()}-{file.FileName}";
                var blobClient = containerClient.GetBlobClient(blobName);
                using (var stream = file.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, new BlobHttpHeaders { ContentType = file.ContentType });
                }
                blobUrl = blobClient.Uri.ToString();
            }

            uploaded.Add(new { file.FileName, file.Length, file.ContentType, blobUrl });
        }

        return Ok(new
        {
            message = "✅ Upload successful",
            files = uploaded,
            testMode = _blobServiceClient == null
        });
    }

    [HttpGet("sas")]
    public async Task<IActionResult> GetSasUrl([FromQuery] string blobName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(blobName))
                return BadRequest(new { error = "blobName query parameter is required." });

            if (_blobServiceClient == null || string.IsNullOrWhiteSpace(_containerName))
                return BadRequest(new { error = "Azure storage is not configured properly." });

            // Decode URL-encoded names (including double-encoded names)
            var decodedBlobName = blobName;
            for (var i = 0; i < 5; i++) // max 5 unescapes
            {
                var next = Uri.UnescapeDataString(decodedBlobName);
                if (next == decodedBlobName) break;
                decodedBlobName = next;
            }

            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
            var blobClient = containerClient.GetBlobClient(decodedBlobName);

            if (!await blobClient.ExistsAsync())
                return NotFound(new { error = "Blob not found.", blobName, decodedBlobName });

            // Try to create SAS via account key
            var connectionString = _configuration["AzureStorage:ConnectionString"];
            if (string.IsNullOrWhiteSpace(connectionString))
                return BadRequest(new { error = "AzureStorage connection string missing." });

        var parties = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var accountName = parties.FirstOrDefault(p => p.TrimStart().StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1].Trim();
        var accountKey = parties.FirstOrDefault(p => p.TrimStart().StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1].Trim();

        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(accountKey))
            return BadRequest(new { error = "Storage account credentials are not available for SAS generation." });

        // Remove any trailing character noise from parsing
        accountName = accountName.Trim().Trim('"', '\'', ';');
        accountKey = accountKey.Trim().Trim('"', '\'', ';');

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _containerName,
                BlobName = decodedBlobName,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var credential = new StorageSharedKeyCredential(accountName, accountKey);
            var sasUri = new BlobUriBuilder(blobClient.Uri)
            {
                Sas = sasBuilder.ToSasQueryParameters(credential)
            };

            var sasUrl = sasUri.ToUri().ToString();

            // Return just sasUrl; forwarding is done via POST /api/upload/forward-sas.
            return Ok(new { sasUrl });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Internal server error", details = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    [HttpPost("forward-sas")]
    public async Task<IActionResult> ForwardSasUrl([FromBody] ForwardSasRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SasUrl))
            return BadRequest(new { error = "sasUrl is required in request body." });

        var forwardResult = await ForwardSasToExternalEndpointAsync(request.SasUrl);

        if (forwardResult.success)
        {
            var storeResult = await StoreForwardedSasUrlAsync(request.SasUrl);
            if (!storeResult.success)
            {
                _logger.LogWarning("SAS URL forwarded but storage failed: {Message}", storeResult.message);
            }
            return Ok(new
            {
                sasUrl = request.SasUrl,
                forwarded = true,
                stored = storeResult.success,
                storeMessage = storeResult.message,
                storeDetails = storeResult.details
            });
        }

        return StatusCode(500, new
        {
            sasUrl = request.SasUrl,
            forwarded = false,
            error = forwardResult.message,
            details = forwardResult.details
        });
    }

    private async Task<(bool success, string message, object? details)> StoreForwardedSasUrlAsync(string sasUrl)
    {
        if (_blobServiceClient == null || string.IsNullOrWhiteSpace(_containerName))
            return (false, "Azure storage not configured, cannot store forwarded SAS URL.", null);

        try
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

            var record = new 
            {
                sasUrl,
                forwardedAtUtc = DateTimeOffset.UtcNow
            };
            var blobName = $"forwarded-sas/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid()}.json";
            var blobClient = containerClient.GetBlobClient(blobName);

            var json = JsonSerializer.Serialize(record);
            var bytes = Encoding.UTF8.GetBytes(json);
            using var stream = new MemoryStream(bytes);
            await blobClient.UploadAsync(stream, new BlobHttpHeaders { ContentType = "application/json" });

            return (true, "SAS URL stored successfully.", new { blobName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store forwarded SAS URL");
            return (false, "Failed to store forwarded SAS URL.", new { error = ex.Message });
        }
    }

    private async Task<(bool success, string message, object? details)> ForwardSasToExternalEndpointAsync(string sasUrl, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(_externalEndpointUrl))
            return (false, "External endpoint URL is not configured in appsettings (ExternalEndpoint:Url).", null);

        var client = _httpClientFactory.CreateClient();

        if (!string.IsNullOrWhiteSpace(_externalEndpointXFunctionKey))
        {
            client.DefaultRequestHeaders.Remove("x-functions-key");
            client.DefaultRequestHeaders.Add("x-functions-key", _externalEndpointXFunctionKey);
        }

        var payload = new { sasUrl };
        var json = JsonSerializer.Serialize(payload);
        var contentType = string.IsNullOrWhiteSpace(_externalEndpointContentType) ? "application/json" : _externalEndpointContentType;
        var content = new StringContent(json, Encoding.UTF8, contentType);

        try
        {
            var response = await client.PostAsync(_externalEndpointUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
                return (true, "SAS URL forwarded to external endpoint.", new { statusCode = (int)response.StatusCode, body = responseBody });

            return (false, "External endpoint returned non-success status code.", new { statusCode = (int)response.StatusCode, body = responseBody });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding SAS URL to external endpoint");
            return (false, "Exception while forwarding SAS URL.", new { error = ex.Message });
        }
    }

    // Local SAS storage removed; forwarding only to external endpoint is now enforced via GetSasUrl.

    // Minimal flow: no external forward required for scenario.
    // Remaining forwarding implementation is commented out.
    /*
    private async Task<(bool success, string? error)> ForwardSasToExternalEndpoint(string sasUrl, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(_externalEndpointUrl))
            return (false, "External endpoint URL is not configured in appsettings (ExternalEndpoint:Url).");

        // ... forwarding logic was here, but for this simplified scenario we keep it disabled.
        return (false, "Forwarding not used in minimal mode.");
    }
    */
}

public class ForwardSasRequest
{
    public string SasUrl { get; set; } = string.Empty;
}

public class ForwardResult
{
    public string File { get; set; } = string.Empty;
    public string SasUrl { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

