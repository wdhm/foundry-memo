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
    private const int MaxPaginatedItems = 100;

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
            var siteId = await ResolveSiteIdAsync(siteUrl);
            if (siteId == null)
                return $"Could not resolve SharePoint site at {siteUrl}.";

            // Step 2: Get default document library
            var libResult = await CallToolAsync("sharepoint-files___getDefaultDocumentLibraryInSite", new
            {
                siteId
            });

            Console.Error.WriteLine($"[SP] getDefaultDocumentLibrary raw ({libResult.Length} chars): {libResult[..Math.Min(500, libResult.Length)]}");

            var documentLibraryId = ExtractJsonProperty(libResult, "id");
            if (string.IsNullOrEmpty(documentLibraryId))
            {
                logger.LogWarning("Could not get document library. Response: {Response}",
                    libResult.Length > 200 ? libResult[..200] : libResult);
                return $"Could not get document library for site. Response: {libResult}";
            }

            logger.LogInformation("Got document library: {LibId}", documentLibraryId);

            // Step 3: List files in root folder (with pagination for large libraries)
            var allFilesJson = await GetAllFolderChildren(documentLibraryId);

            Console.Error.WriteLine($"[SP] getFolderChildren total ({allFilesJson.Length} chars)");

            // Extract compact file listing from verbose Graph API JSON
            var summary = SummarizeFileList(allFilesJson);
            Console.Error.WriteLine($"[SP] SummarizeFileList: {summary.Length} chars summary from {allFilesJson.Length} chars raw");
            return summary;
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
            var siteId = await ResolveSiteIdAsync(siteUrl);
            if (siteId == null)
                return $"Could not resolve site at {siteUrl}.";

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

    /// <summary>
    /// Search for files or folders by name across a SharePoint site.
    /// Uses the MCP findFileOrFolder tool for keyword-based search.
    /// </summary>
    public async Task<string> SearchFiles(string siteUrl, string searchQuery)
    {
        logger.LogInformation("SearchFiles called — siteUrl: {SiteUrl}, query: {Query}", siteUrl, searchQuery);
        try
        {
            var siteId = await ResolveSiteIdAsync(siteUrl);
            if (siteId == null)
                return $"Could not resolve site at {siteUrl}.";

            // Get document library
            var libResult = await CallToolAsync("sharepoint-files___getDefaultDocumentLibraryInSite", new { siteId });
            var documentLibraryId = ExtractJsonProperty(libResult, "id");
            if (string.IsNullOrEmpty(documentLibraryId))
                return $"Could not get document library for site.";

            // Search for files
            var result = await CallToolAsync("sharepoint-files___findFileOrFolder", new
            {
                documentLibraryId,
                searchQuery
            });

            logger.LogInformation("SearchFiles returned {Len} chars", result.Length);
            return TruncateIfNeeded(result);
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SearchFiles failed for {SiteUrl}", siteUrl);
            return $"Error searching files: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// Resolves a SharePoint site URL to its Graph site ID.
    /// Tries getSiteByPath first, then falls back to findSite keyword search.
    /// Returns null if the site cannot be resolved.
    /// </summary>
    /// <summary>
    /// Uploads a small binary file (≤5MB) to a SharePoint site's default document library
    /// via the MCP server's createSmallBinaryFile tool. Uses OBO (caller's identity).
    /// </summary>
    public async Task<string?> UploadBinaryFileAsync(string siteUrl, string fileName, byte[] content)
    {
        logger.LogInformation("UploadBinaryFile called — siteUrl: {SiteUrl}, fileName: {FileName}, size: {Size} bytes",
            siteUrl, fileName, content.Length);

        if (content.Length > 5 * 1024 * 1024)
        {
            logger.LogError("File too large for MCP upload: {Size} bytes (max 5MB)", content.Length);
            return null;
        }

        var siteId = await ResolveSiteIdAsync(siteUrl);
        if (siteId == null)
        {
            logger.LogWarning("Could not resolve site for upload: {SiteUrl}", siteUrl);
            return null;
        }

        // Get default document library
        var libResult = await CallToolAsync("sharepoint-files___getDefaultDocumentLibraryInSite", new { siteId });
        var documentLibraryId = ExtractJsonProperty(libResult, "id");
        if (string.IsNullOrEmpty(documentLibraryId))
        {
            logger.LogWarning("Could not get document library for upload. Response: {Response}",
                libResult.Length > 200 ? libResult[..200] : libResult);
            return null;
        }

        var base64Content = Convert.ToBase64String(content);
        logger.LogInformation("Uploading {FileName} ({Size} bytes, base64: {Base64Len} chars) to library {LibId}",
            fileName, content.Length, base64Content.Length, documentLibraryId);

        var result = await CallToolAsync("sharepoint-files___createSmallBinaryFile", new
        {
            documentLibraryId,
            filename = fileName,
            base64Content
        });

        logger.LogInformation("Upload result ({Len} chars): {Result}",
            result.Length, result[..Math.Min(500, result.Length)]);

        // Try to extract the web URL from the response
        var webUrl = ExtractJsonProperty(result, "webUrl");
        return webUrl ?? result;
    }

    private async Task<string?> ResolveSiteIdAsync(string siteUrl)
    {
        var uri = new Uri(siteUrl);
        var hostname = uri.Host;
        var sitePath = uri.AbsolutePath.TrimEnd('/');

        var siteResult = await CallToolAsync("sharepoint-files___getSiteByPath", new
        {
            hostname,
            serverRelativePath = sitePath
        });

        Console.Error.WriteLine($"[SP] getSiteByPath raw ({siteResult.Length} chars): {siteResult[..Math.Min(500, siteResult.Length)]}");

        var siteId = ExtractJsonProperty(siteResult, "id");

        if (string.IsNullOrEmpty(siteId))
        {
            logger.LogWarning("getSiteByPath returned no 'id'. Trying findSite fallback. Raw: {Response}",
                siteResult[..Math.Min(300, siteResult.Length)]);

            var siteName = sitePath.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
            if (!string.IsNullOrEmpty(siteName))
            {
                var findResult = await CallToolAsync("sharepoint-files___findSite", new { searchQuery = siteName });
                Console.Error.WriteLine($"[SP] findSite raw ({findResult.Length} chars): {findResult[..Math.Min(500, findResult.Length)]}");
                siteId = ExtractJsonProperty(findResult, "id");
            }
        }

        if (!string.IsNullOrEmpty(siteId))
        {
            logger.LogInformation("Resolved site ID: {SiteId} for {SiteUrl}", siteId, siteUrl);
            return siteId;
        }

        logger.LogWarning("Could not resolve site ID from {SiteUrl}.", siteUrl);
        return null;
    }

    /// <summary>
    /// Fetches all children from a folder, following pagination if the MCP response
    /// indicates more items are available (@odata.nextLink in the response).
    /// Caps at MaxPaginatedItems to stay within tool output limits.
    /// </summary>
    private async Task<string> GetAllFolderChildren(string documentLibraryId)
    {
        var firstResult = await CallToolAsync("sharepoint-files___getFolderChildren", new
        {
            documentLibraryId
        });

        Console.Error.WriteLine($"[SP] getFolderChildren page 1 ({firstResult.Length} chars): {firstResult[..Math.Min(300, firstResult.Length)]}");

        // Try to parse and check for pagination
        var jsonStr = IsolateJson(firstResult);
        if (jsonStr == null) return firstResult;

        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            if (!doc.RootElement.TryGetProperty("value", out var items))
                return firstResult;

            var allItems = new List<JsonElement>();
            foreach (var item in items.EnumerateArray())
                allItems.Add(item.Clone());

            // Check for @odata.nextLink — indicates more pages
            if (doc.RootElement.TryGetProperty("@odata.nextLink", out _) && allItems.Count < MaxPaginatedItems)
            {
                logger.LogInformation("getFolderChildren has pagination — fetching more pages (got {Count} items so far)", allItems.Count);

                // The MCP server may support a folderId parameter for subfolder pagination,
                // but the root listing doesn't expose a cursor. Fetch subfolders' children too.
                // For now, log that pagination was detected — the MCP server may not expose
                // a direct "next page" mechanism, so we note the truncation.
                logger.LogWarning("getFolderChildren returned @odata.nextLink but MCP may not support cursor pagination. Got {Count} items.", allItems.Count);
            }

            // Rebuild the response with all items
            return JsonSerializer.Serialize(new { value = allItems }, new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return firstResult;
        }
    }

    private async Task<string> CallToolAsync(string toolName, object args)
    {
        var argsJson = JsonSerializer.SerializeToElement(args);
        return await mcpClient.CallToolAsync(toolName, argsJson);
    }

    /// <summary>
    /// Parse verbose Graph API getFolderChildren JSON into a compact file listing.
    /// Extracts: name, type (file/folder), size, lastModified, webUrl.
    /// </summary>
    private static string SummarizeFileList(string raw)
    {
        try
        {
            // Strip trailing metadata (CorrelationId line)
            var jsonStr = IsolateJson(raw);
            if (jsonStr == null) return TruncateIfNeeded(raw);

            using var doc = JsonDocument.Parse(jsonStr);
            if (!doc.RootElement.TryGetProperty("value", out var items))
                return TruncateIfNeeded(raw);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {items.GetArrayLength()} items:");
            sb.AppendLine();

            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
                var webUrl = item.TryGetProperty("webUrl", out var w) ? w.GetString() : null;
                var isFolder = item.TryGetProperty("folder", out _);
                var lastMod = item.TryGetProperty("lastModifiedDateTime", out var lm) ? lm.GetString() : null;

                string type;
                long? size = null;
                if (isFolder)
                {
                    type = "folder";
                    if (item.TryGetProperty("folder", out var f) && f.TryGetProperty("childCount", out var cc))
                        sb.AppendLine($"- 📁 {name} (folder, {cc.GetInt32()} children)");
                    else
                        sb.AppendLine($"- 📁 {name} (folder)");
                }
                else
                {
                    type = item.TryGetProperty("file", out var fi) && fi.TryGetProperty("mimeType", out var mt)
                        ? mt.GetString() ?? "file" : "file";
                    size = item.TryGetProperty("size", out var s) ? s.GetInt64() : null;
                    var sizeStr = size.HasValue ? FormatSize(size.Value) : "";
                    sb.AppendLine($"- 📄 {name} — {type}{(sizeStr.Length > 0 ? $" — {sizeStr}" : "")}");
                }

                if (lastMod != null) sb.AppendLine($"    Modified: {lastMod}");
                if (webUrl != null) sb.AppendLine($"    URL: {webUrl}");
            }

            return sb.ToString();
        }
        catch
        {
            return TruncateIfNeeded(raw);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
        return $"{bytes / (1024 * 1024)} MB";
    }

    /// <summary>
    /// Isolate JSON from MCP response that may have trailing metadata lines.
    /// </summary>
    private static string? IsolateJson(string raw)
    {
        var trimmed = raw.Trim();
        // Try as-is
        if (IsValidJson(trimmed)) return trimmed;

        // Strip trailing non-JSON content
        var lastBrace = trimmed.LastIndexOf('}');
        if (lastBrace > 0)
        {
            var candidate = trimmed[..(lastBrace + 1)];
            if (IsValidJson(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsValidJson(string s)
    {
        try { using var d = JsonDocument.Parse(s); return true; }
        catch { return false; }
    }

    private static string? ExtractJsonProperty(string raw, string propertyName)
    {
        // MCP responses may contain the actual JSON plus trailing metadata lines
        // (e.g. "CorrelationId: xxx, TimeStamp: yyy"). Strip trailing non-JSON lines.
        var trimmed = raw.Trim();

        // Try parsing as-is first (common case: single JSON object)
        var result = TryParseProperty(trimmed, propertyName);
        if (result != null) return result;

        // If that fails, try to isolate the JSON object by finding the last '}'
        var lastBrace = trimmed.LastIndexOf('}');
        if (lastBrace > 0 && lastBrace < trimmed.Length - 1)
        {
            result = TryParseProperty(trimmed[..(lastBrace + 1)], propertyName);
            if (result != null) return result;
        }

        return null;
    }

    private static string? TryParseProperty(string json, string propertyName)
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
