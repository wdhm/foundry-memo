// Copyright (c) foundry-memo. All rights reserved.

using System.Text.Json;
using FoundryMemo.Services;
using Microsoft.Extensions.Logging;

namespace FoundryMemo.Tools;

/// <summary>
/// Fast SharePoint file operations via the Work IQ SharePoint MCP server (mcp_SharePointRemoteServer).
/// Uses Graph API directly via OBO — 1-3s per call vs 30-60s for copilot_chat.
/// Use for: listing files, getting metadata, folder operations.
/// Keep copilot_chat for: semantic content search, document summarization.
/// </summary>
public class SharePointFilesTool(ToolboxMcpClient mcpClient, ILogger<SharePointFilesTool> logger)
{
    private const int MaxResponseLength = 4_000;

    /// <summary>
    /// List all files and folders in a SharePoint site's document library.
    /// Fast path: resolves site → gets default library → lists children.
    /// Returns file names, types, sizes, and URLs.
    /// </summary>
    public async Task<string> ListSiteFiles(string siteUrl)
    {
        logger.LogInformation("ListSiteFiles called — siteUrl: {SiteUrl}", siteUrl);

        try
        {
            var uri = new Uri(siteUrl);
            var hostname = uri.Host;
            var sitePath = uri.AbsolutePath.TrimEnd('/');

            // Step 1: Resolve site by path
            var siteResult = await CallToolAsync("sharepoint-files___getSiteByPath", new
            {
                hostname,
                serverRelativePath = sitePath
            });

            var siteId = ExtractJsonProperty(siteResult, "siteId");
            if (string.IsNullOrEmpty(siteId))
            {
                logger.LogWarning("Could not resolve site ID from {SiteUrl}. Response: {Response}",
                    siteUrl, siteResult.Length > 200 ? siteResult[..200] : siteResult);
                return $"Could not resolve SharePoint site at {siteUrl}. Response: {siteResult}";
            }

            logger.LogInformation("Resolved site ID: {SiteId}", siteId);

            // Step 2: Get default document library
            var libResult = await CallToolAsync("sharepoint-files___getDefaultDocumentLibraryInSite", new
            {
                siteId
            });

            var documentLibraryId = ExtractJsonProperty(libResult, "documentLibraryId")
                ?? ExtractJsonProperty(libResult, "id");
            if (string.IsNullOrEmpty(documentLibraryId))
            {
                logger.LogWarning("Could not get document library. Response: {Response}",
                    libResult.Length > 200 ? libResult[..200] : libResult);
                return $"Could not get document library for site. Response: {libResult}";
            }

            logger.LogInformation("Got document library: {LibId}", documentLibraryId);

            // Step 3: List files in root folder
            var filesResult = await CallToolAsync("sharepoint-files___getFolderChildren", new
            {
                documentLibraryId
            });

            logger.LogInformation("ListSiteFiles returned {Len} chars", filesResult.Length);
            return TruncateIfNeeded(filesResult);
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required for SharePoint access. Please visit this URL to authorize, then try again:\n\n{ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ListSiteFiles failed for {SiteUrl}", siteUrl);
            return $"Error listing SharePoint files: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// Find SharePoint sites by name or keyword.
    /// </summary>
    public async Task<string> FindSite(string searchQuery)
    {
        logger.LogInformation("FindSite called — query: {Query}", searchQuery);
        try
        {
            var result = await CallToolAsync("sharepoint-files___findSite", new { searchQuery });
            logger.LogInformation("FindSite returned {Len} chars", result.Length);
            return TruncateIfNeeded(result);
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FindSite failed");
            return $"Error finding site: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// Get metadata (name, size, type, dates, URL) for a specific file or folder by URL.
    /// </summary>
    public async Task<string> GetFileInfo(string fileOrFolderUrl)
    {
        logger.LogInformation("GetFileInfo called — url: {Url}", fileOrFolderUrl);
        try
        {
            var result = await CallToolAsync("sharepoint-files___getFileOrFolderMetadataByUrl", new
            {
                fileOrFolderUrl
            });
            logger.LogInformation("GetFileInfo returned {Len} chars", result.Length);
            return TruncateIfNeeded(result);
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFileInfo failed");
            return $"Error getting file info: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// List all document libraries in a SharePoint site.
    /// </summary>
    public async Task<string> ListDocumentLibraries(string siteUrl)
    {
        logger.LogInformation("ListDocumentLibraries called — siteUrl: {SiteUrl}", siteUrl);
        try
        {
            var uri = new Uri(siteUrl);
            var siteResult = await CallToolAsync("sharepoint-files___getSiteByPath", new
            {
                hostname = uri.Host,
                serverRelativePath = uri.AbsolutePath.TrimEnd('/')
            });

            var siteId = ExtractJsonProperty(siteResult, "siteId");
            if (string.IsNullOrEmpty(siteId))
                return $"Could not resolve site. Response: {siteResult}";

            var result = await CallToolAsync("sharepoint-files___listDocumentLibrariesInSite", new { siteId });
            logger.LogInformation("ListDocumentLibraries returned {Len} chars", result.Length);
            return TruncateIfNeeded(result);
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ListDocumentLibraries failed");
            return $"Error listing libraries: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    private async Task<string> CallToolAsync(string toolName, object args)
    {
        var argsJson = JsonSerializer.SerializeToElement(args);
        return await mcpClient.CallToolAsync(toolName, argsJson);
    }

    private static string? ExtractJsonProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(propertyName, out var prop))
                return prop.GetString();
        }
        catch { }
        return null;
    }

    private static string TruncateIfNeeded(string text)
    {
        if (text.Length <= MaxResponseLength)
            return text;
        return text[..MaxResponseLength] + "\n\n[... response truncated for size]";
    }
}
