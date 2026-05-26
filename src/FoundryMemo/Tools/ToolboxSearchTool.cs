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
public class ToolboxSearchTool(ToolboxMcpClient mcpClient, ILogger<ToolboxSearchTool> logger)
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
    /// Grounds the M365 Copilot response on the given file URI for focused retrieval.
    /// </summary>
    public async Task<string> GetDocumentText(string documentUrl)
    {
        logger.LogInformation("GetDocumentText called — url: {Url}", documentUrl);
        try
        {
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
