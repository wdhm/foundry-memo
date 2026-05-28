// Copyright (c) foundry-memo. All rights reserved.

using System.Text.Json;
using System.Text.RegularExpressions;
using FoundryMemo.Services;
using Microsoft.Extensions.Logging;

namespace FoundryMemo.Tools;

/// <summary>
/// Bridge tool that calls the M365 Copilot MCP via the Foundry toolbox.
/// Uses UserEntraToken (OBO) — the platform proxies the caller's Entra identity
/// so all results are permission-trimmed per the calling user.
///
/// PERF: The MCP copilot_chat call takes 30-60s server-side. Without dedup,
/// the LLM loops calling SearchSharePoint 7-10 times per request (~400s total).
/// We cache successful results for 120s to return instantly on duplicate calls.
/// Only caches responses that contain actual document/file content (not "no results" messages).
/// </summary>
public class ToolboxSearchTool(ToolboxMcpClient mcpClient, ILogger<ToolboxSearchTool> logger, SharePointFilesTool? spFilesTool = null)
{
    private const string CopilotChatTool = "copilot-search___copilot_chat";

    // Responses protocol has limits on tool output size — truncate to stay safe
    private const int MaxResponseLength = 4_000;

    // Search call limiter — prevents the LLM from looping SearchSharePoint.
    // Allows up to MaxSearchCalls real MCP calls per TTL window (to handle empty results),
    // but caches successful results to avoid redundant calls.
    private static string? _cachedSearchResult;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static int _callCount;
    private static DateTime _windowStart = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(120);
    private const int MaxSearchCalls = 2; // hard cap — one real call + one retry at most

    // Patterns that indicate the MCP returned "no results" — do NOT cache these
    private static readonly string[] EmptyResultPatterns =
    [
        "no documents", "no files", "no results", "couldn't find", "could not find",
        "no content", "not found", "no items", "no data", "no permission",
        "no indexed", "no searchable", "no accessible", "unable to find",
        "didn't find", "did not find", "no matching", "zero results"
    ];

    /// <summary>
    /// Search SharePoint/M365 content using the caller's identity via M365 Copilot.
    /// When siteUrl is provided, results are scoped to that specific SharePoint site.
    /// </summary>
    public async Task<string> SearchSharePointContent(string query, string? siteUrl = null)
    {
        logger.LogInformation("SearchSharePoint called — query: {Query}, siteUrl: {SiteUrl}, callCount: {CallCount}",
            query, siteUrl ?? "(none)", _callCount);

        // If we have a cached successful result, return it immediately
        if (_cachedSearchResult != null && DateTime.UtcNow - _cacheTime < CacheTtl)
        {
            logger.LogInformation("SearchSharePoint returning cached result ({Len} chars)", _cachedSearchResult.Length);
            return "SEARCH ALREADY COMPLETE. All accessible results were returned. " +
                   "Present these results to the user now:\n\n" + _cachedSearchResult;
        }

        // Reset window if expired
        if (DateTime.UtcNow - _windowStart > CacheTtl)
        {
            _callCount = 0;
            _windowStart = DateTime.UtcNow;
            _cachedSearchResult = null;
        }

        // Hard limit on MCP calls per window
        if (_callCount >= MaxSearchCalls)
        {
            logger.LogWarning("SearchSharePoint call limit reached ({Max} calls in window)", MaxSearchCalls);
            return "Search limit reached. No additional results available. " +
                   "Present whatever results you have to the user.";
        }

        _callCount++;

        try
        {
            var args = new Dictionary<string, object> { ["message"] = query };

            // Ground on the site URL via fileUris — don't duplicate in message text
            if (!string.IsNullOrEmpty(siteUrl))
                args["fileUris"] = new[] { siteUrl };

            var argsJson = JsonSerializer.SerializeToElement(args);
            var result = await mcpClient.CallToolAsync(CopilotChatTool, argsJson);
            var extracted = TruncateIfNeeded(ExtractReply(result));

            logger.LogInformation("SearchSharePoint MCP returned {Len} chars, hasContent: {HasContent}",
                extracted.Length, HasMeaningfulContent(extracted));

            // Only cache if the response contains actual document/file results
            if (HasMeaningfulContent(extracted))
            {
                _cachedSearchResult = extracted;
                _cacheTime = DateTime.UtcNow;
                logger.LogInformation("SearchSharePoint result cached (call #{Call})", _callCount);
            }
            else
            {
                logger.LogWarning("SearchSharePoint returned empty/negative result on call #{Call}/{Max}: {Preview}",
                    _callCount, MaxSearchCalls, extracted.Length > 150 ? extracted[..150] : extracted);
            }

            return extracted;
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. Please visit this URL to authorize access, then try again:\n\n{ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SearchSharePoint MCP call failed");
            return $"Error calling M365 Copilot MCP: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// Returns true if the response contains actual document/file results,
    /// not just "no results found" messages from M365 Copilot.
    /// </summary>
    private static bool HasMeaningfulContent(string response)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length < 100)
            return false;

        var lower = response.ToLowerInvariant();

        // If the response contains negative indicators, it's not useful
        foreach (var pattern in EmptyResultPatterns)
        {
            if (lower.Contains(pattern))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Get content about a specific SharePoint document using the caller's identity.
    /// Checks sensitivity labels before reading — blocks extraction if the file has
    /// a Purview label that M365 Copilot would also block.
    /// </summary>
    public async Task<string> GetDocumentText(string documentUrl)
    {
        logger.LogInformation("GetDocumentText called — url: {Url}", documentUrl);
        try
        {
            // Check sensitivity label before reading (Purview compliance)
            if (spFilesTool != null)
            {
                var blockReason = await spFilesTool.CheckSensitivityLabelAsync(documentUrl);
                if (blockReason != null)
                {
                    logger.LogWarning("GetDocumentText BLOCKED by sensitivity label for {Url}", documentUrl);
                    return blockReason;
                }
            }

            var args = new Dictionary<string, object>
            {
                ["message"] = $"Extract and return the full content of this document: {documentUrl}",
                ["fileUris"] = new[] { documentUrl }
            };
            var argsJson = JsonSerializer.SerializeToElement(args);
            var result = await mcpClient.CallToolAsync(CopilotChatTool, argsJson);
            var extracted = TruncateIfNeeded(ExtractReply(result));
            logger.LogInformation("GetDocumentText returned {Len} chars", extracted.Length);
            return extracted;
        }
        catch (McpConsentRequiredException ex)
        {
            return $"⚠️ OAuth consent required. Please visit this URL to authorize access, then try again:\n\n{ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetDocumentText MCP call failed");
            return $"Error calling M365 Copilot MCP: [{ex.GetType().Name}] {ex.Message}";
        }
    }

    /// <summary>
    /// Reads multiple documents in parallel via M365 Copilot MCP. Each document is read
    /// concurrently to avoid sequential ~35s waits. Returns combined content with document
    /// headers. Max 8 documents per call (total time ~35-70s instead of 8×35s=280s).
    /// </summary>
    public async Task<string> GetMultipleDocumentContents(string documentUrls)
    {
        var urls = documentUrls
            .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Take(8) // hard cap to avoid excessive parallel calls
            .ToArray();

        logger.LogInformation("GetMultipleDocumentContents called — {Count} documents", urls.Length);

        if (urls.Length == 0)
            return "No valid document URLs provided. Pass comma-separated URLs.";

        if (urls.Length == 1)
            return await GetDocumentText(urls[0]);

        // Read all documents in parallel (with sensitivity label pre-check)
        var tasks = urls.Select(async url =>
        {
            try
            {
                // Check sensitivity label before reading (Purview compliance)
                if (spFilesTool != null)
                {
                    var blockReason = await spFilesTool.CheckSensitivityLabelAsync(url);
                    if (blockReason != null)
                    {
                        var blockedName = Uri.TryCreate(url, UriKind.Absolute, out var bUri)
                            ? Uri.UnescapeDataString(bUri.Segments.LastOrDefault() ?? url)
                            : url;
                        logger.LogWarning("GetMultipleDocuments: BLOCKED by sensitivity label for {Url}", url);
                        return (blockedName, content: "", error: blockReason);
                    }
                }

                var args = new Dictionary<string, object>
                {
                    ["message"] = $"Extract and return the full content of this document: {url}",
                    ["fileUris"] = new[] { url }
                };
                var argsJson = JsonSerializer.SerializeToElement(args);
                var result = await mcpClient.CallToolAsync(CopilotChatTool, argsJson);
                var extracted = ExtractReply(result);
                var fileName = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? url)
                    : url;
                logger.LogInformation("GetMultipleDocuments: {File} returned {Len} chars", fileName, extracted.Length);
                return (fileName, content: extracted, error: (string?)null);
            }
            catch (Exception ex)
            {
                var fileName = url.Split('/').LastOrDefault() ?? url;
                logger.LogWarning(ex, "GetMultipleDocuments: failed to read {Url}", url);
                return (fileName, content: "", error: ex.Message);
            }
        });

        var results = await Task.WhenAll(tasks);

        // Build combined output with per-document truncation to fit in tool output limits
        // Total budget: ~12KB (tool output max is ~12-15KB)
        const int totalBudget = 12_000;
        var perDocBudget = totalBudget / results.Length;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Content from {results.Length} documents:\n");

        foreach (var (fileName, content, error) in results)
        {
            sb.AppendLine($"## {fileName}");
            if (error != null)
            {
                sb.AppendLine($"[Error reading: {error}]\n");
                continue;
            }

            if (content.Length > perDocBudget)
            {
                sb.AppendLine(content[..perDocBudget]);
                sb.AppendLine("[... truncated]\n");
            }
            else
            {
                sb.AppendLine(content);
                sb.AppendLine();
            }
        }

        var combined = sb.ToString();
        logger.LogInformation("GetMultipleDocumentContents: combined {Len} chars from {Count} docs",
            combined.Length, results.Length);
        return combined;
    }

    /// <summary>
    /// The MCP response is a JSON string with conversationId, reply, rawResponse.
    /// Extract just the reply text to reduce size and improve LLM readability.
    /// </summary>
    private static string ExtractReply(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("reply", out var reply))
            {
                var replyText = reply.GetString();
                if (!string.IsNullOrEmpty(replyText))
                    return replyText;
            }

            // If reply is empty, try to extract from rawResponse messages
            if (doc.RootElement.TryGetProperty("rawResponse", out var rawResp))
            {
                var rawStr = rawResp.GetString();
                if (!string.IsNullOrEmpty(rawStr))
                {
                    using var rawDoc = JsonDocument.Parse(rawStr);
                    if (rawDoc.RootElement.TryGetProperty("messages", out var messages))
                    {
                        foreach (var msg in messages.EnumerateArray())
                        {
                            if (msg.TryGetProperty("text", out var text))
                            {
                                var t = text.GetString();
                                if (!string.IsNullOrEmpty(t) && t.Length > 100)
                                    return t;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Not JSON or unexpected format — return raw
        }

        return raw;
    }

    private static string TruncateIfNeeded(string text)
    {
        if (text.Length <= MaxResponseLength)
            return text;

        return text[..MaxResponseLength] + "\n\n[... response truncated for size]";
    }
}
