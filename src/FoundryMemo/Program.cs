// Copyright (c) foundry-memo. All rights reserved.

using Azure.AI.AgentServer.Core;
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

var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? throw new InvalidOperationException("COSMOS_ENDPOINT is not set.");

var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
var credential = tenantId != null
    ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId })
    : new DefaultAzureCredential();

// Initialize Cosmos DB for process learnings
var cosmosClient = new CosmosClient(cosmosEndpoint, credential);
var learningsStore = new LearningsStore(cosmosClient);
var learningsTool = new LearningsTool(learningsStore);

// Initialize Copilot Retrieval API service (requires Entra app with delegated permissions)
var graphClientId = Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID");
CopilotRetrievalService? retrievalService = null;

if (!string.IsNullOrEmpty(graphClientId))
{
    // Use DeviceCodeCredential for delegated Graph access (local dev).
    // Prints a device code to the console — user authenticates in a browser.
    // In production, this would use OBO from the user's session token.
    var graphCredential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
    {
        TenantId = tenantId ?? "organizations",
        ClientId = graphClientId,
        DeviceCodeCallback = (info, cancel) =>
        {
            Console.WriteLine();
            Console.WriteLine($"🔑 Graph auth required: {info.Message}");
            Console.WriteLine();
            return Task.CompletedTask;
        }
    });
    retrievalService = new CopilotRetrievalService(graphCredential);
    Console.WriteLine("✓ Copilot Retrieval API enabled (device code auth — will prompt on first use)");
}
else
{
    Console.WriteLine("⚠ GRAPH_CLIENT_ID not set — using stub SharePoint data");
}

var sharePointTool = new SharePointRetrievalTool(retrievalService);

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
            2. When a user provides a SharePoint URL, use RetrieveSharePointContent
               to fetch document content from that location
            3. Synthesize the content into a well-structured, concise memo summary
            4. Use GenerateMemoPdf to render the summary into a downloadable PDF
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
                "Retrieves document content from a SharePoint site URL using the Copilot Retrieval API. Returns text chunks with citations."),

            AIFunctionFactory.Create(
                PdfGeneratorTool.GenerateMemoPdf,
                "GenerateMemoPdf",
                "Generates a branded PDF memo from a title and markdown-formatted content. Returns the file path of the generated PDF."),

            AIFunctionFactory.Create(
                learningsTool.WriteLearning,
                "WriteLearning",
                "Writes a new process learning. Only operational insights — NEVER content, URLs, or user data.")
        ]);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
