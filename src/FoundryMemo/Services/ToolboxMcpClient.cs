// Copyright (c) foundry-memo. All rights reserved.

using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace FoundryMemo.Services;

/// <summary>
/// Lightweight MCP client for the Foundry Toolbox MCP endpoint.
/// Optimized: skips initialize handshake (Foundry proxy is stateless),
/// caches tokens, and retries on 429 (Too Many Requests).
/// </summary>
public class ToolboxMcpClient
{
    private readonly string _endpoint;
    private readonly TokenCredential _credential;
    private readonly HttpClient _httpClient;

    private static readonly string[] TokenScopes = ["https://ai.azure.com/.default"];

    // Token cache — avoid re-acquiring on every HTTP call
    private AccessToken _cachedToken;
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(2);

    // Retry config for 429 Too Many Requests
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    public ToolboxMcpClient(string endpoint, TokenCredential credential, HttpClient? httpClient = null)
    {
        _endpoint = endpoint;
        _credential = credential;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    }

    /// <summary>
    /// Call a tool by name with the given arguments.
    /// Sends tools/call directly — no initialize handshake needed (stateless proxy).
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = toolName, arguments }
        };

        var response = await SendWithRetryAsync(payload, ct)
            ?? throw new InvalidOperationException("MCP tools/call returned no content (204)");

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.GetProperty("code").GetInt32();
            var message = error.GetProperty("message").GetString() ?? "Unknown error";
            Console.Error.WriteLine($"[MCP] tools/call error: code={code}, message={message[..Math.Min(300, message.Length)]}");

            if (code == -32006 || code == -32007)
                throw new McpConsentRequiredException(ExtractConsentUrl(message) ?? message);

            return JsonSerializer.Serialize(new { error = true, code, message });
        }

        if (response.RootElement.TryGetProperty("result", out var result))
        {
            if (result.TryGetProperty("content", out var content))
            {
                var texts = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && item.TryGetProperty("text", out var text))
                    {
                        texts.Add(text.GetString() ?? "");
                    }
                }
                return string.Join("\n", texts);
            }
            return result.GetRawText();
        }

        return "{}";
    }

    private static string? ExtractConsentUrl(string message)
    {
        var idx = message.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var end = message.IndexOfAny(['"', ' ', '}'], idx);
            return end > idx ? message[idx..end] : message[idx..];
        }
        return null;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow + TokenRefreshBuffer)
            return _cachedToken.Token;

        _cachedToken = await _credential.GetTokenAsync(new TokenRequestContext(TokenScopes), ct);
        return _cachedToken.Token;
    }

    private async Task<JsonDocument?> SendWithRetryAsync(object payload, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await SendAsync(payload, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (attempt >= MaxRetries) throw;
                var delay = RetryDelays[attempt];
                Console.Error.WriteLine($"[MCP] 429 Too Many Requests — retrying in {delay.TotalSeconds}s (attempt {attempt + 1}/{MaxRetries})");
                await Task.Delay(delay, ct);
            }
        }
        throw new InvalidOperationException("Retry logic exhausted");
    }

    private async Task<JsonDocument?> SendAsync(object payload, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        var payloadJson = JsonSerializer.Serialize(payload);

        using var payloadDoc = JsonDocument.Parse(payloadJson);
        var method = payloadDoc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : "unknown";
        Console.Error.WriteLine($"[MCP] → {method} to {_endpoint}");

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            Console.Error.WriteLine($"[MCP] ← {method}: 204 No Content");
            return null;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            Console.Error.WriteLine($"[MCP] ← {method}: 429 Too Many Requests");
            throw new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        Console.Error.WriteLine($"[MCP] ← {method}: HTTP {(int)response.StatusCode}, body length={body.Length}");

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[MCP] ERROR {method}: {body[..Math.Min(500, body.Length)]}");
            throw new InvalidOperationException($"MCP request failed ({(int)response.StatusCode}): {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException(
                $"MCP endpoint returned empty response (HTTP {(int)response.StatusCode}, Content-Type: {response.Content.Headers.ContentType})");

        // Handle SSE format: extract JSON from "data: " lines
        if (body.StartsWith("event:") || body.StartsWith("data:"))
        {
            var jsonLines = body.Split('\n')
                .Where(l => l.StartsWith("data: "))
                .Select(l => l[6..])
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (jsonLines.Count > 0)
                body = jsonLines.Last();
            else
                throw new InvalidOperationException($"MCP SSE response contained no data lines: {body[..Math.Min(200, body.Length)]}");
        }

        return JsonDocument.Parse(body);
    }
}

public class McpConsentRequiredException : Exception
{
    public McpConsentRequiredException(string message) : base(message) { }
}
