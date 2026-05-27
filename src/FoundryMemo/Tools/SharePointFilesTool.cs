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

            Console.Error.WriteLine($"[SP] getSiteByPath raw ({siteResult.Length} chars): {siteResult[..Math.Min(500, siteResult.Length)]}");

            var siteId = ExtractJsonProperty(siteResult, "id");

            // Fallback: try findSite if getSiteByPath didn't return a parseable id
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

            if (string.IsNullOrEmpty(siteId))
            {
                logger.LogWarning("Could not resolve site ID from {SiteUrl}.", siteUrl);
                return $"Could not resolve SharePoint site at {siteUrl}. Response: {siteResult}";
            }

            logger.LogInformation("Resolved site ID: {SiteId}", siteId);

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

            // Step 3: List files in root folder
            var filesResult = await CallToolAsync("sharepoint-files___getFolderChildren", new
            {
                documentLibraryId
            });

            Console.Error.WriteLine($"[SP] getFolderChildren raw ({filesResult.Length} chars): {filesResult[..Math.Min(300, filesResult.Length)]}");

            // Extract compact file listing from verbose Graph API JSON
            var summary = SummarizeFileList(filesResult);
            Console.Error.WriteLine($"[SP] SummarizeFileList: {summary.Length} chars summary from {filesResult.Length} chars raw");
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
            var uri = new Uri(siteUrl);
            var sitePath = uri.AbsolutePath.TrimEnd('/');
            var siteResult = await CallToolAsync("sharepoint-files___getSiteByPath", new
            {
                hostname = uri.Host,
                serverRelativePath = sitePath
            });

            var siteId = ExtractJsonProperty(siteResult, "id");

            // Fallback: try findSite
            if (string.IsNullOrEmpty(siteId))
            {
                var siteName = sitePath.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
                if (!string.IsNullOrEmpty(siteName))
                {
                    var findResult = await CallToolAsync("sharepoint-files___findSite", new { searchQuery = siteName });
                    siteId = ExtractJsonProperty(findResult, "id");
                }
            }

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
