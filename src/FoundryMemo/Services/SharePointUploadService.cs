// Copyright (c) foundry-memo. All rights reserved.

using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace FoundryMemo.Services;

/// <summary>
/// Uploads files to SharePoint via Microsoft Graph API using app credentials.
/// Auto-resolves the target tenant from the SharePoint hostname so app credentials
/// from one tenant can work if the app registration is multi-tenant.
/// </summary>
public class SharePointUploadService
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _configuredTenantId;
    private readonly HttpClient _httpClient;
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

    // Graph API simple upload limit — files larger than this use upload sessions
    private const int SimpleUploadLimit = 4 * 1024 * 1024; // 4MB

    public SharePointUploadService(
        string tenantId, string clientId, string clientSecret,
        HttpClient? httpClient = null, ILogger<SharePointUploadService>? logger = null)
    {
        _configuredTenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
    }

    /// <summary>
    /// Uploads a file to a SharePoint document library.
    /// Auto-resolves the target tenant from the SharePoint URL hostname.
    /// For files ≤4MB uses simple PUT, for larger files uses upload sessions.
    /// </summary>
    public async Task<string> UploadAsync(string siteUrl, string folderPath, string fileName, byte[] fileContent)
    {
        var (hostname, sitePath, subFolder) = ParseSharePointUrl(siteUrl);

        _logger?.LogInformation("Uploading {FileName} ({Size} bytes) to {SiteUrl}", fileName, fileContent.Length, siteUrl);

        // Resolve the correct tenant for this SharePoint site
        var targetTenantId = await ResolveTargetTenantAsync(hostname);
        var credential = new ClientSecretCredential(targetTenantId, _clientId, _clientSecret);

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(Scopes), CancellationToken.None);

        var siteId = await GetSiteIdAsync(hostname, sitePath, token.Token);
        var driveId = await GetDriveIdAsync(siteId, token.Token);

        var uploadPath = string.IsNullOrEmpty(subFolder)
            ? fileName
            : $"{subFolder}/{fileName}";

        if (fileContent.Length <= SimpleUploadLimit)
        {
            var uploadUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{uploadPath}:/content";
            return await SimpleUploadWithRetryAsync(uploadUrl, fileContent, token.Token);
        }

        return await LargeFileUploadAsync(driveId, uploadPath, fileContent, token.Token);
    }

    /// <summary>
    /// Resolves the Entra tenant ID from a SharePoint hostname.
    /// E.g. "m365x12929684.sharepoint.com" → queries OpenID config → extracts tenant GUID.
    /// Falls back to the configured tenant ID if resolution fails.
    /// </summary>
    private async Task<string> ResolveTargetTenantAsync(string hostname)
    {
        // Extract the tenant name from the hostname (e.g. "m365x12929684" from "m365x12929684.sharepoint.com")
        var tenantName = hostname.Split('.')[0];
        var domain = $"{tenantName}.onmicrosoft.com";

        try
        {
            var url = $"https://login.microsoftonline.com/{domain}/.well-known/openid-configuration";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                // issuer looks like: "https://sts.windows.net/{tenant-id}/"
                var issuer = json.GetProperty("issuer").GetString();
                if (issuer != null)
                {
                    var segments = new Uri(issuer).AbsolutePath.Trim('/').Split('/');
                    if (segments.Length > 0 && Guid.TryParse(segments[0], out _))
                    {
                        _logger?.LogInformation("Resolved tenant for {Hostname}: {TenantId} (from {Domain})",
                            hostname, segments[0], domain);
                        return segments[0];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tenant resolution failed for {Hostname}, using configured tenant {TenantId}",
                hostname, _configuredTenantId);
        }

        _logger?.LogInformation("Using configured tenant {TenantId} for {Hostname}", _configuredTenantId, hostname);
        return _configuredTenantId;
    }

    private async Task<string> SimpleUploadWithRetryAsync(string uploadUrl, byte[] fileContent, string token)
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

    /// <summary>
    /// Uploads large files (>4MB) using Graph API upload sessions.
    /// Creates an upload session, then uploads in 3.2MB chunks.
    /// </summary>
    private async Task<string> LargeFileUploadAsync(string driveId, string filePath, byte[] fileContent, string token)
    {
        _logger?.LogInformation("Using upload session for large file ({Size} bytes)", fileContent.Length);

        // Create upload session
        var sessionUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{filePath}:/createUploadSession";
        var sessionRequest = new HttpRequestMessage(HttpMethod.Post, sessionUrl)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sessionResponse = await _httpClient.SendAsync(sessionRequest);
        if (!sessionResponse.IsSuccessStatusCode)
        {
            var errorBody = await sessionResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to create upload session ({(int)sessionResponse.StatusCode}): {errorBody}");
        }

        var sessionJson = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uploadUrl = sessionJson.GetProperty("uploadUrl").GetString()!;

        // Upload in chunks (3.2MB aligned to 320KB as required by Graph)
        const int chunkSize = 320 * 1024 * 10; // 3.2MB
        var totalSize = fileContent.Length;
        string? webUrl = null;

        for (int offset = 0; offset < totalSize; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, totalSize - offset);
            var chunk = new byte[length];
            Array.Copy(fileContent, offset, chunk, 0, length);

            var chunkRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(chunk)
            };
            chunkRequest.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                offset, offset + length - 1, totalSize);
            chunkRequest.Content.Headers.ContentLength = length;

            var chunkResponse = await _httpClient.SendAsync(chunkRequest);

            if (chunkResponse.StatusCode == System.Net.HttpStatusCode.OK ||
                chunkResponse.StatusCode == System.Net.HttpStatusCode.Created)
            {
                // Final chunk — response contains the created item
                var result = await chunkResponse.Content.ReadFromJsonAsync<JsonElement>();
                webUrl = result.TryGetProperty("webUrl", out var wu) ? wu.GetString() : null;
                _logger?.LogInformation("Large file upload completed: {WebUrl}", webUrl);
            }
            else if (chunkResponse.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                // More chunks to upload
                _logger?.LogInformation("Uploaded chunk {Offset}-{End}/{Total}",
                    offset, offset + length - 1, totalSize);
            }
            else
            {
                var errorBody = await chunkResponse.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Chunk upload failed ({(int)chunkResponse.StatusCode}): {errorBody}");
            }
        }

        return webUrl ?? $"Upload completed for {filePath}";
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

            // Provide actionable guidance for tenant mismatch
            if (errorBody.Contains("Invalid hostname", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Tenant mismatch: the app credentials cannot access '{hostname}'. " +
                    $"Ensure the app registration is multi-tenant or registered in the M365 tenant.");

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
    /// </summary>
    internal static (string hostname, string sitePath, string? subFolder) ParseSharePointUrl(string url)
    {
        var decoded = Uri.UnescapeDataString(url);
        var uri = new Uri(decoded);
        var hostname = uri.Host;
        var path = uri.AbsolutePath.TrimEnd('/');

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
                if (afterLib.Contains("/Forms/", StringComparison.OrdinalIgnoreCase))
                    afterLib = afterLib[..afterLib.IndexOf("/Forms/", StringComparison.OrdinalIgnoreCase)];
                if (afterLib.Equals("Forms", StringComparison.OrdinalIgnoreCase))
                    afterLib = "";
                subFolder = string.IsNullOrEmpty(afterLib) ? null : afterLib;
                break;
            }
        }

        sitePath ??= path;
        return (hostname, sitePath, subFolder);
    }
}
