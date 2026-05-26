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
                Console.WriteLine($"✓ Cosmos from connection with app credentials: {cosmosEndpoint}");
            }
        }

        if (cosmosCredential == null)
            Console.WriteLine($"✓ Cosmos endpoint from connection: {cosmosEndpoint} (using default identity)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠ Cosmos connection lookup failed: {ex.GetType().Name}: {ex.Message}");
    }
}

if (!string.IsNullOrEmpty(cosmosEndpoint) && Uri.TryCreate(cosmosEndpoint, UriKind.Absolute, out _))
{
    try
    {
        var cosmosClient = new CosmosClient(cosmosEndpoint, cosmosCredential ?? credential);
        var learningsStore = new LearningsStore(cosmosClient);
        learningsTool = new LearningsTool(learningsStore);
        Console.WriteLine($"✓ Cosmos DB connected ({(cosmosCredential != null ? "app credentials" : "default identity")})");
    }
    catch (Exception ex)
    {
        learningsTool = new LearningsTool(null);
        Console.WriteLine($"⚠ Cosmos DB connection failed — learnings disabled: {ex.Message}");
    }
}
else
{
    learningsTool = new LearningsTool(null);
    Console.WriteLine("⚠ COSMOS_ENDPOINT not set — learnings store disabled");
}

// --- SharePoint upload (app credentials from Foundry connection) ---
// Content retrieval uses the caller's identity via MCP toolbox (OBO).
// PDF upload uses app credentials (Sites.ReadWrite.All).
SharePointUploadService? uploadService = null;
var graphClientId = Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID");

if (!string.IsNullOrEmpty(graphClientId))
{
    // Local dev: DeviceCodeCredential for delegated Graph access
    var graphCredential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
    {
        TenantId = tenantId ?? "organizations",
        ClientId = graphClientId,
        DeviceCodeCallback = (info, cancel) =>
        {
            Console.WriteLine($"\n🔑 Graph auth required: {info.Message}\n");
            return Task.CompletedTask;
        }
    });
    uploadService = new SharePointUploadService(graphCredential);
    Console.WriteLine("✓ SharePoint upload enabled (device code auth — local dev)");
}
else
{
    // Hosted mode: Graph app credentials from connection
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
                    new ClientSecretCredential(gTenantId, gClientId, gClientSecret),
                    useManagedIdentity: true);
                Console.WriteLine("✓ SharePoint upload enabled (app credentials)");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠ graph-api connection lookup failed: {ex.GetType().Name}: {ex.Message}");
    }
}

var pdfTool = new PdfGeneratorTool(uploadService);

// --- Toolbox MCP bridge (v27 approach: UserEntraToken + custom MCP client) ---
// The copilot-search toolbox uses UserEntraToken (OBO) — the platform proxies the
// caller's Entra identity directly to the M365 Copilot MCP server. We wrap this as
// local AIFunction tools via a lightweight JSON-RPC client.
var toolboxName = Environment.GetEnvironmentVariable("TOOLBOX_NAME") ?? "copilot-search";
var toolboxEndpoint = $"{projectEndpoint.ToString().TrimEnd('/')}/toolboxes/{toolboxName}/mcp?api-version=v1";
Console.WriteLine($"✓ Toolbox MCP endpoint: {toolboxEndpoint}");
var mcpClient = new ToolboxMcpClient(toolboxEndpoint, credential);
var toolboxSearchTool = new ToolboxSearchTool(mcpClient);

var allTools = new List<AITool>
{
    AIFunctionFactory.Create(
        toolboxSearchTool.SearchSharePointContent,
        "SearchSharePoint",
        "Search SharePoint/OneDrive/Teams as the caller. Returns titles, links, summaries. ONE call returns all results."),

    AIFunctionFactory.Create(
        toolboxSearchTool.GetDocumentText,
        "GetDocumentText",
        "Get full text of one SharePoint document by URL."),

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

            ## CRITICAL: Tool call rules
            - Call SearchSharePoint ONCE per query. It returns all results in one call.
              Do NOT call it multiple times or retry — the results are complete.
            - Only call ReadLearnings/WriteLearning during memo generation, not searches.
            - Never auto-generate PDFs. Ask first, call GenerateMemoPdf only after "yes".

            ## Workflow
            1. SearchSharePoint with site URL (one call). Results are permission-trimmed.
            2. Present findings. Use GetDocumentText only if user asks about a specific doc.
            3. For memos: call ReadLearnings, then generate content, then GenerateMemoPdf.

            ## Memo format
            Sections: Executive Summary, Key Findings, Details, Sources. Cite sources.
            """,
        name: "foundry-memo",
        description: "Searches SharePoint content via caller identity and generates PDF memos.",
        tools: allTools);

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
