// Copyright (c) foundry-memo. All rights reserved.

#pragma warning disable OPENAI001 // GetToolboxToolsAsync is experimental

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

// --- SharePoint upload (uses managed identity with app-level Graph permissions) ---
// Content retrieval uses the caller's identity via MCP toolbox (OAuth passthrough).
// PDF upload uses the agent's managed identity — the Foundry account MI has
// Sites.ReadWrite.All application permission for writing new files.
var graphClientId = Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID");
SharePointUploadService? uploadService = null;
CopilotRetrievalService? retrievalService = null;

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
    retrievalService = new CopilotRetrievalService(graphCredential);
    uploadService = new SharePointUploadService(graphCredential);
    Console.WriteLine("✓ Graph services enabled (device code auth — local dev)");
}
else
{
    // Hosted mode: Graph app credentials from connection for uploads
    // Agent MI can't receive Graph app roles — use app registration instead
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
                var graphCredential = new ClientSecretCredential(gTenantId, gClientId, gClientSecret);
                uploadService = new SharePointUploadService(graphCredential, useManagedIdentity: true);
                Console.WriteLine("✓ SharePoint upload enabled (app credentials from graph-api connection)");
            }
        }

        if (uploadService == null)
        {
            uploadService = new SharePointUploadService(credential, useManagedIdentity: true);
            Console.WriteLine("⚠ graph-api connection missing credentials — upload may fail");
        }
    }
    catch (Exception ex)
    {
        uploadService = new SharePointUploadService(credential, useManagedIdentity: true);
        Console.WriteLine($"⚠ graph-api connection lookup failed: {ex.GetType().Name}: {ex.Message}");
    }
    Console.WriteLine("✓ Content retrieval via MCP toolbox (caller identity)");
}

var sharePointTool = new SharePointRetrievalTool(retrievalService);
var pdfTool = new PdfGeneratorTool(uploadService);

// MCP toolbox client for request-time identity-passthrough calls
var toolboxName = Environment.GetEnvironmentVariable("TOOLBOX_NAME") ?? "copilot-search";
var toolboxEndpoint = $"{projectEndpoint.ToString().TrimEnd('/')}/toolboxes/{toolboxName}/mcp?api-version=v1";
Console.WriteLine($"✓ Toolbox MCP endpoint: {toolboxEndpoint}");
var mcpClient = new ToolboxMcpClient(toolboxEndpoint, credential);
var toolboxSearchTool = new ToolboxSearchTool(mcpClient);

AIAgent agent = new AIProjectClient(projectEndpoint, credential)
    .AsAIAgent(
        model: deployment,
        instructions: """
            You are a memo-generation assistant called "Memo".
            
            ## First interaction
            When a user first messages you, introduce yourself briefly:
            "Hi! I'm Memo. I can search SharePoint content using your identity
            and generate branded PDF memos. Tell me which SharePoint site to
            work with, and what you'd like me to do."

            ## IMPORTANT: Always start by reading learnings
            Before doing ANY work, call ReadLearnings to load process improvements
            from previous runs. Apply these learnings to improve your output.

            ## Workflow
            1. Call ReadLearnings first
            2. When a user provides a SharePoint URL, search or retrieve content.
               PREFER SearchSharePoint or GetDocumentText — these use the caller's
               identity and respect Purview/MIP labels.
               Always pass siteUrl to SearchSharePoint to scope results to the
               specific SharePoint site the user provided.
               If those return a consent URL, show it to the user and ask them to
               authorize, then retry.
               Only fall back to RetrieveSharePointContent if the MCP tools fail.
            3. Present findings to the user in a clear summary.
            4. **NEVER generate a PDF automatically.** Always ask the user first:
               "Would you like me to generate a PDF memo and upload it to SharePoint?"
               Only call GenerateMemoPdf after the user explicitly confirms.
            5. After completion, call WriteLearning for any process improvements
               you discovered during this run

            ## Writing Learnings
            After each memo generation, reflect on what you learned about the PROCESS:
            - Did the retrieval need multiple queries? Record that.
            - Did certain file types need special handling? Record that.
            - Did the PDF layout need adjustment for the content size? Record that.
            - NEVER store file content, user data, URLs, or sensitive information.
            - Only store operational insights about how to do the job better.

            ## Memo Format
            Structure the memo with clear sections: Executive Summary, Key Findings,
            Details, and Sources. Always cite source documents.
            Be thorough but concise.
            """,
        name: "foundry-memo",
        description: "Retrieves SharePoint content via the Copilot Retrieval API and generates summarized PDF memos. Learns from each run to improve over time.",
        tools:
        [
            AIFunctionFactory.Create(
                learningsTool.ReadLearnings,
                "ReadLearnings",
                "Reads all process learnings from memory. Call this FIRST before starting any memo generation."),

            AIFunctionFactory.Create(
                sharePointTool.RetrieveSharePointContent,
                "RetrieveSharePointContent",
                "Retrieves document content from a SharePoint URL using the Copilot Retrieval API. Returns text chunks with citations. Fallback tool — prefer MCP toolbox tools when available."),

            AIFunctionFactory.Create(
                pdfTool.GenerateMemoPdf,
                "GenerateMemoPdf",
                "Generates a branded PDF memo and uploads it to the source SharePoint folder. Pass the SharePoint URL so the PDF is uploaded to the same location."),

            AIFunctionFactory.Create(
                learningsTool.WriteLearning,
                "WriteLearning",
                "Writes a new process learning. Only operational insights — NEVER content, URLs, or user data."),

            AIFunctionFactory.Create(
                toolboxSearchTool.SearchSharePointContent,
                "SearchSharePoint",
                "Search SharePoint content using the caller's identity via M365 Copilot. Pass siteUrl to scope results to a specific SharePoint site. PREFERRED over RetrieveSharePointContent."),

            AIFunctionFactory.Create(
                toolboxSearchTool.GetDocumentText,
                "GetDocumentText",
                "Get full content of a specific SharePoint document using the caller's identity. Pass the document URL to ground retrieval on that file. PREFERRED over RetrieveSharePointContent.")
        ]);

// --- Application Insights ---
// Telemetry is handled by the platform's AddAgentHostTelemetry() which reads
// the "app-insights" Foundry connection (provisioned via Bicep in infra/main.bicep).
// No custom OTel packages needed — the platform wires up everything.

var builder = AgentHost.CreateBuilder(args);

builder.Services.AddFoundryResponses(agent);

builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
