// Copyright (c) foundry-memo. All rights reserved.

using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace FoundryMemo.Services;

/// <summary>
/// Uploads files to SharePoint via Microsoft Graph API.
/// Supports both delegated credentials (local dev) and managed identity (hosted).
/// </summary>
public class SharePointUploadService
{
    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly ILogger<SharePointUploadService>? _logger;

    // App scope (requires Sites.ReadWrite.All app permission on the app registration)
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    // Retry for transient failures
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
    ];

    public SharePointUploadService(TokenCredential credential, HttpClient? httpClient = null, ILogger<SharePointUploadService>? logger = null)
    {
        _credential = credential;
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
    }

    /// <summary>
    /// Uploads a file to a SharePoint document library.
    /// Uses the Graph API: PUT /drives/{driveId}/root:/{path}:/content
    /// </summary>
    /// <param name="siteUrl">SharePoint site URL (e.g., https://tenant.sharepoint.com/sites/MySite)</param>
    /// <param name="folderPath">Folder path within the document library (e.g., "Shared Documents")</param>
    /// <param name="fileName">Name of the file to upload</param>
    /// <param name="fileContent">File content as bytes</param>
    /// <returns>The web URL of the uploaded file</returns>
    public async Task<string> UploadAsync(string siteUrl, string folderPath, string fileName, byte[] fileContent)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(Scopes),
            CancellationToken.None);

        // Parse site URL to extract hostname, site path, and any subfolder
        var (hostname, sitePath, subFolder) = ParseSharePointUrl(siteUrl);

        _logger?.LogInformation("Uploading {FileName} ({Size} bytes) to {SiteUrl}", fileName, fileContent.Length, siteUrl);

        // Step 1: Get the site ID
        var siteId = await GetSiteIdAsync(hostname, sitePath, token.Token);

        // Step 2: Get the default drive (document library = "Shared Documents")
        var driveId = await GetDriveIdAsync(siteId, token.Token);

        // Step 3: Upload the file with retry for transient failures
        var uploadPath = string.IsNullOrEmpty(subFolder)
            ? fileName
            : $"{subFolder}/{fileName}";

        var uploadUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{uploadPath}:/content";

        return await UploadWithRetryAsync(uploadUrl, fileContent, token.Token);
    }

    private async Task<string> UploadWithRetryAsync(string uploadUrl, byte[] fileContent, string token)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(fileContent)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                var webUrl = result.GetProperty("webUrl").GetString() ?? uploadUrl;
                _logger?.LogInformation("Upload succeeded: {WebUrl}", webUrl);
                return webUrl;
            }

            var statusCode = (int)response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync();

            // Retry on transient failures
            if (IsTransient(statusCode) && attempt < MaxRetries)
            {
                var delay = RetryDelays[attempt];
                _logger?.LogWarning("Upload got {StatusCode}, retrying in {Delay}s (attempt {Attempt}/{Max}): {Error}",
                    statusCode, delay.TotalSeconds, attempt + 1, MaxRetries, errorBody[..Math.Min(200, errorBody.Length)]);
                await Task.Delay(delay);
                continue;
            }

            _logger?.LogError("SharePoint upload failed with {StatusCode}: {Error}", statusCode, errorBody);
            throw new InvalidOperationException(
                $"SharePoint upload failed with {statusCode}: {errorBody}");
        }

        throw new InvalidOperationException("Upload retry logic exhausted");
    }

    private static bool IsTransient(int statusCode) =>
        statusCode is 429 or 503 or 504 or 408;

    private async Task<string> GetSiteIdAsync(string hostname, string sitePath, string token)
    {
        var url = $"https://graph.microsoft.com/v1.0/sites/{hostname}:{sitePath}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger?.LogError("GetSiteId failed for {Hostname}:{SitePath} — {StatusCode}: {Error}",
                hostname, sitePath, (int)response.StatusCode, errorBody);
            throw new InvalidOperationException(
                $"Failed to resolve SharePoint site '{hostname}:{sitePath}' — Graph API returned {(int)response.StatusCode}: {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("id").GetString()!;
    }

    private async Task<string> GetDriveIdAsync(string siteId, string token)
    {
        var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drive";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger?.LogError("GetDriveId failed for site {SiteId} — {StatusCode}: {Error}",
                siteId, (int)response.StatusCode, errorBody);
            throw new InvalidOperationException(
                $"Failed to get document library for site '{siteId}' — Graph API returned {(int)response.StatusCode}: {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Parses a SharePoint URL into hostname, site path, and optional subfolder.
    /// E.g., "https://tenant.sharepoint.com/sites/MySite/Shared Documents/Reports"
    ///   → ("tenant.sharepoint.com", "/sites/MySite", "Reports")
    /// The default library ("Shared Documents") is stripped since Graph's default drive is that library.
    /// </summary>
    private static (string hostname, string sitePath, string? subFolder) ParseSharePointUrl(string url)
    {
        var decoded = Uri.UnescapeDataString(url);
        var uri = new Uri(decoded);
        var hostname = uri.Host;
        var path = uri.AbsolutePath.TrimEnd('/');

        // Known document library names to strip from the path
        string[] libraryNames = ["Shared Documents", "Shared%20Documents", "Documents"];

        string? sitePath = null;
        string? subFolder = null;

        foreach (var lib in libraryNames)
        {
            var libIndex = path.IndexOf($"/{lib}", StringComparison.OrdinalIgnoreCase);
            if (libIndex >= 0)
            {
                sitePath = path[..libIndex];
                var afterLib = path[(libIndex + lib.Length + 1)..].TrimStart('/');
                // Remove /Forms/AllItems.aspx or similar view URLs
                if (afterLib.Contains("/Forms/", StringComparison.OrdinalIgnoreCase))
                    afterLib = afterLib[..afterLib.IndexOf("/Forms/", StringComparison.OrdinalIgnoreCase)];
                if (afterLib.Equals("Forms", StringComparison.OrdinalIgnoreCase))
                    afterLib = "";
                subFolder = string.IsNullOrEmpty(afterLib) ? null : afterLib;
                break;
            }
        }

        // If no library name found, assume path is just the site path
        sitePath ??= path;

        return (hostname, sitePath, subFolder);
    }
}
