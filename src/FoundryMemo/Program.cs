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

// --- Cosmos DB (optional — reads endpoint from Foundry connection or env var) ---
LearningsTool learningsTool;
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT");

if (string.IsNullOrEmpty(cosmosEndpoint))
{
    // Try reading from Foundry project connection
    try
    {
        var projectClient = new AIProjectClient(projectEndpoint, credential);
        var conn = await projectClient.Connections.GetConnectionAsync("cosmos-db", includeCredentials: false);
        cosmosEndpoint = conn.Value.Target;
        Console.WriteLine($"✓ Cosmos endpoint from Foundry connection: {cosmosEndpoint}");
    }
    catch
    {
        Console.WriteLine("⚠ No Cosmos connection found — learnings disabled");
    }
}

if (!string.IsNullOrEmpty(cosmosEndpoint) && Uri.TryCreate(cosmosEndpoint, UriKind.Absolute, out _))
{
    try
    {
        var cosmosClient = new CosmosClient(cosmosEndpoint, credential);
        var learningsStore = new LearningsStore(cosmosClient);
        learningsTool = new LearningsTool(learningsStore);
        Console.WriteLine($"✓ Cosmos DB learnings store connected: {cosmosEndpoint}");
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
    // Hosted mode: MI for uploads, content retrieval via MCP toolbox
    uploadService = new SharePointUploadService(credential);
    Console.WriteLine("✓ SharePoint upload enabled (managed identity)");
    Console.WriteLine("✓ Content retrieval via MCP toolbox (caller identity)");
}

var sharePointTool = new SharePointRetrievalTool(retrievalService);
var pdfTool = new PdfGeneratorTool(uploadService);

AIAgent agent = new AIProjectClient(projectEndpoint, credential)
    .AsAIAgent(
        model: deployment,
        instructions: """
            You are a memo-generation assistant called "Memo".
            
            ## IMPORTANT: Always start by reading learnings
            Before doing ANY work, call ReadLearnings to load process improvements
            from previous runs. Apply these learnings to improve your output.

            ## Workflow
            1. Call ReadLearnings first
            2. When a user provides a SharePoint URL, retrieve the document content.
               In hosted mode, use the MCP toolbox tools (search_site_content or
               get_document_text from the copilot-search toolbox) which run with the
               caller's identity and respect Purview/MIP labels.
               If those tools are unavailable, fall back to RetrieveSharePointContent.
            3. Synthesize the content into a well-structured, concise memo summary
            4. Use GenerateMemoPdf to render the summary into a PDF and upload it
               to the same SharePoint folder. Always pass the original SharePoint URL.
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
                "Retrieves document content from a SharePoint URL using the Copilot Retrieval API. Returns text chunks with citations. Use MCP toolbox tools (copilot-search) when available — they use the caller's identity."),

            AIFunctionFactory.Create(
                pdfTool.GenerateMemoPdf,
                "GenerateMemoPdf",
                "Generates a branded PDF memo and uploads it to the source SharePoint folder. Pass the SharePoint URL so the PDF is uploaded to the same location."),

            AIFunctionFactory.Create(
                learningsTool.WriteLearning,
                "WriteLearning",
                "Writes a new process learning. Only operational insights — NEVER content, URLs, or user data.")
        ]);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);

// Register MCP toolbox for SharePoint content retrieval with caller identity (OAuth passthrough).
// The toolbox name must match a toolbox configured in the Foundry project.
// When FOUNDRY_AGENT_TOOLSET_ENDPOINT is absent (local dev), this is a no-op.
builder.Services.AddFoundryToolboxes("copilot-search");

builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
