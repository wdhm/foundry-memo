// Copyright (c) foundry-memo. All rights reserved.

using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using FoundryMemo.Services;
using FoundryMemo.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;

Env.TraversePath().Load();

var projectEndpoint = new Uri(
    Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set."));

var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-5";

var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
var credential = tenantId != null
    ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId })
    : new DefaultAzureCredential();

// --- Cosmos DB (optional — reads endpoint + app credentials from Foundry connection) ---
// Foundry instance identity is not supported by Cosmos RBAC, and key auth is disabled
// by subscription policy. We use an Entra app registration with Cosmos RBAC instead.
LearningsTool learningsTool;
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT");
Azure.Core.TokenCredential? cosmosCredential = null;

// Skip unresolved azd template variables
if (cosmosEndpoint?.StartsWith("{{") == true)
    cosmosEndpoint = null;

if (string.IsNullOrEmpty(cosmosEndpoint))
{
    // Read endpoint + app credentials from Foundry connection (CustomKeys auth type)
    try
    {
        var projectClient = new AIProjectClient(projectEndpoint, credential);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var conn = await projectClient.Connections.GetConnectionAsync("cosmos-db", includeCredentials: true, cts.Token);
        cosmosEndpoint = conn.Value.Target;

        if (conn.Value.Credentials is AIProjectConnectionCustomCredential customCreds)
        {
            // Foundry lowercases custom key names — use case-insensitive lookup
            var keysDict = new Dictionary<string, string>(customCreds.Keys, StringComparer.OrdinalIgnoreCase);
            if (keysDict.TryGetValue("clientId", out var clientId)
                && keysDict.TryGetValue("clientSecret", out var clientSecret)
                && keysDict.TryGetValue("tenantId", out var cosmosTenantId))
            {
                cosmosCredential = new ClientSecretCredential(cosmosTenantId, clientId, clientSecret);
                Console.Error.WriteLine($"✓ Cosmos from connection with app credentials: {cosmosEndpoint}");
            }
        }

        if (cosmosCredential == null)
            Console.Error.WriteLine($"✓ Cosmos endpoint from connection: {cosmosEndpoint} (using default identity)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"⚠ Cosmos connection lookup failed: {ex.GetType().Name}: {ex.Message}");
    }
}

if (!string.IsNullOrEmpty(cosmosEndpoint) && Uri.TryCreate(cosmosEndpoint, UriKind.Absolute, out _))
{
    try
    {
        var cosmosClient = new CosmosClient(cosmosEndpoint, cosmosCredential ?? credential);
        var learningsStore = new LearningsStore(cosmosClient);
        learningsTool = new LearningsTool(learningsStore);
        Console.Error.WriteLine($"✓ Cosmos DB connected ({(cosmosCredential != null ? "app credentials" : "default identity")})");
    }
    catch (Exception ex)
    {
        learningsTool = new LearningsTool(null);
        Console.Error.WriteLine($"⚠ Cosmos DB connection failed — learnings disabled: {ex.Message}");
    }
}
else
{
    learningsTool = new LearningsTool(null);
    Console.Error.WriteLine("⚠ COSMOS_ENDPOINT not set — learnings store disabled");
}

// --- SharePoint upload (app credentials from Foundry connection) ---
// Content retrieval uses the caller's identity via MCP toolbox (OBO).
// PDF upload uses app credentials (Sites.ReadWrite.All) from the graph-api Foundry connection.
SharePointUploadService? uploadService = null;

try
{
    var projectClient = new AIProjectClient(projectEndpoint, credential);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var conn = await projectClient.Connections.GetConnectionAsync("graph-api", includeCredentials: true, cts.Token);

    if (conn.Value.Credentials is AIProjectConnectionCustomCredential graphCreds)
    {
        var keysDict = new Dictionary<string, string>(graphCreds.Keys, StringComparer.OrdinalIgnoreCase);
        if (keysDict.TryGetValue("clientId", out var gClientId)
            && keysDict.TryGetValue("clientSecret", out var gClientSecret)
            && keysDict.TryGetValue("tenantId", out var gTenantId))
        {
            uploadService = new SharePointUploadService(
                new ClientSecretCredential(gTenantId, gClientId, gClientSecret));
            Console.Error.WriteLine("✓ SharePoint upload enabled (app credentials from graph-api connection)");
        }
    }

    if (uploadService == null)
        Console.Error.WriteLine("⚠ graph-api connection found but missing clientId/clientSecret/tenantId — upload disabled");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"⚠ graph-api connection lookup failed: {ex.GetType().Name}: {ex.Message}");
}

var pdfTool = new PdfGeneratorTool(uploadService);

// --- Toolbox MCP bridge (v27 approach: UserEntraToken + custom MCP client) ---
// The copilot-search toolbox uses UserEntraToken (OBO) — the platform proxies the
// caller's Entra identity directly to the M365 Copilot MCP server. We wrap this as
// local AIFunction tools via a lightweight JSON-RPC client.
var toolboxName = Environment.GetEnvironmentVariable("TOOLBOX_NAME") ?? "copilot-search";
var toolboxEndpoint = $"{projectEndpoint.ToString().TrimEnd('/')}/toolboxes/{toolboxName}/mcp?api-version=v1";
Console.Error.WriteLine($"✓ Toolbox MCP endpoint (copilot-search): {toolboxEndpoint}");
var mcpClient = new ToolboxMcpClient(toolboxEndpoint, credential);
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

// Wire ILogger into tools that were created before loggerFactory
pdfTool = new PdfGeneratorTool(uploadService, loggerFactory.CreateLogger<PdfGeneratorTool>());

var toolboxSearchTool = new ToolboxSearchTool(mcpClient, loggerFactory.CreateLogger<ToolboxSearchTool>());

// --- SharePoint Files MCP (Work IQ SharePoint — Graph API via OBO, ~1-3s) ---
// Separate toolbox for fast file operations: listing, metadata, folder ops.
// Uses mcp_SharePointRemoteServer instead of mcp_M365Copilot.
var spToolboxName = Environment.GetEnvironmentVariable("SP_TOOLBOX_NAME") ?? "sharepoint-files";
var spToolboxEndpoint = $"{projectEndpoint.ToString().TrimEnd('/')}/toolboxes/{spToolboxName}/mcp?api-version=v1";
Console.Error.WriteLine($"✓ Toolbox MCP endpoint (sharepoint-files): {spToolboxEndpoint}");
var spMcpClient = new ToolboxMcpClient(spToolboxEndpoint, credential);
var spFilesTool = new SharePointFilesTool(spMcpClient, loggerFactory.CreateLogger<SharePointFilesTool>());

var allTools = new List<AITool>
{
    // --- Fast SharePoint file tools (Graph API via OBO, ~1-3s) ---
    // Use these for listing files, finding sites, getting file metadata
    AIFunctionFactory.Create(
        spFilesTool.ListSiteFiles,
        "ListSiteFiles",
        "List all files and folders in a SharePoint site's document library. FAST (~2s). Use when user provides a site URL and wants to see files. Returns file names, types, sizes."),

    AIFunctionFactory.Create(
        spFilesTool.FindSite,
        "FindSite",
        "Find SharePoint sites by name or keyword. FAST (~1s). Use when user asks about sites but doesn't provide a URL."),

    AIFunctionFactory.Create(
        spFilesTool.GetFileInfo,
        "GetFileInfo",
        "Get metadata (name, size, type, dates, URL) for a specific file or folder by URL. FAST (~1s)."),

    AIFunctionFactory.Create(
        spFilesTool.ListDocumentLibraries,
        "ListDocumentLibraries",
        "List all document libraries in a SharePoint site. FAST (~2s). Use when user wants to see available libraries."),

    AIFunctionFactory.Create(
        spFilesTool.SearchFiles,
        "SearchFiles",
        "Search for files or folders by name in a SharePoint site. FAST (~3s). Use when user asks to find a specific file by name. Requires site URL + search query."),

    // --- Semantic content search (M365 Copilot MCP, ~35s) ---
    // Use ONLY for searching document content by meaning, not for listing files
    AIFunctionFactory.Create(
        toolboxSearchTool.SearchSharePointContent,
        "SearchContent",
        "Semantic search of document CONTENT across SharePoint/OneDrive/Teams. SLOW (~35s). Only use when user asks about document content or topics, NOT for listing files."),

    AIFunctionFactory.Create(
        toolboxSearchTool.GetDocumentText,
        "GetDocumentText",
        "Get full text of one SharePoint document by URL. SLOW (~35s). Only use when user specifically asks to read a document."),

    // --- PDF and learnings ---
    AIFunctionFactory.Create(
        pdfTool.GenerateMemoPdf,
        "GenerateMemoPdf",
        "Generate a branded PDF memo and upload to SharePoint. Only after explicit user confirmation."),

    AIFunctionFactory.Create(
        learningsTool.ReadLearnings,
        "ReadLearnings",
        "Read process learnings. Only call when generating a memo, not for search queries."),

    AIFunctionFactory.Create(
        learningsTool.WriteLearning,
        "WriteLearning",
        "Save a process learning. Only operational insights, never content/URLs/user data."),
};

AIAgent agent = new AIProjectClient(projectEndpoint, credential)
    .AsAIAgent(
        model: deployment,
        instructions: """
            You are "Memo" — a SharePoint memo assistant.
            Introduce yourself briefly on first message.

            ## Tool routing — pick the right tool for the job
            You have FAST tools (Graph API, ~1-3s) and SLOW tools (semantic search, ~35s).

            ### FAST tools — use for file operations:
            - **ListSiteFiles** — "list files on this site", "what documents are here"
            - **FindSite** — "find a site called X", "what SharePoint sites exist"
            - **GetFileInfo** — "get details about this file"
            - **ListDocumentLibraries** — "what libraries does this site have"
            - **SearchFiles** — "find a file called X on this site", "search for budget.xlsx"

            ### SLOW tools — use ONLY for content search:
            - **SearchContent** — "find documents about compliance", "search for risk policies"
              Do NOT use for listing files — it's 10x slower and gives inconsistent results.
            - **GetDocumentText** — "read the contents of this document"

            ## Rules
            - When user provides a site URL + asks to list files → use ListSiteFiles (FAST)
            - When user asks to find a specific file by name → use SearchFiles (FAST)
            - When user asks about document content/topics → use SearchContent (SLOW)
            - Call each tool at most ONCE per query. Do not retry.
            - Never auto-generate PDFs. Ask first, call GenerateMemoPdf only after "yes".

            ## Learnings (self-improvement)
            - Only call ReadLearnings/WriteLearning during memo generation.
            - Before calling WriteLearning, review the ReadLearnings output to avoid
              writing duplicate insights you've already recorded.
            - Only write genuinely new operational insights — not content or URLs.
            - Write learnings even when things went well (e.g., "single retrieval query
              was sufficient for small sites with <10 files").

            ## Memo format
            Sections: Executive Summary, Key Findings, Details, Sources. Cite sources.
            """,
        name: "foundry-memo",
        description: "Searches SharePoint content via caller identity and generates PDF memos.",
        tools: allTools,
        clientFactory: inner => inner.AsBuilder()
            .ConfigureOptions(opts =>
            {
                var prev = opts.RawRepresentationFactory;
                opts.RawRepresentationFactory = state =>
                {
                    var ro = prev?.Invoke(state) as CreateResponseOptions ?? new CreateResponseOptions();
                    // Disable platform response storage to avoid HTTP 500 from Foundry Storage
                    // when persisting function_call_output items (platform bug).
                    // The hosted agent's AgentSessionStore handles session state independently.
                    ro.StoredOutputEnabled = false;
                    return ro;
                };
            })
            .Build());

// --- Application Insights ---
// Telemetry is handled by the platform's AddAgentHostTelemetry() which reads
// the "app-insights" Foundry connection (provisioned via Bicep in infra/main.bicep).
// No custom OTel packages needed — the platform wires up everything.

var builder = AgentHost.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddFoundryResponses(agent);

builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
