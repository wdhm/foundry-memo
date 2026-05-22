// Copyright (c) foundry-memo. All rights reserved.

using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using FoundryMemo.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

Env.TraversePath().Load();

var projectEndpoint = new Uri(
    Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set."));

var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-5";

AIAgent agent = new AIProjectClient(projectEndpoint, new DefaultAzureCredential())
    .AsAIAgent(
        model: deployment,
        instructions: """
            You are a memo-generation assistant called "Memo".
            
            When a user provides a SharePoint URL, use the RetrieveSharePointContent tool
            to fetch all document content from that location. Then synthesize the content
            into a well-structured, concise memo summary.

            If the user asks for a PDF, use the GenerateMemoPdf tool to render the summary
            into a downloadable PDF document.

            Always cite the source documents in your summary. Be thorough but concise.
            Format the memo with clear sections: Executive Summary, Key Findings, 
            Details, and Sources.
            """,
        name: "foundry-memo",
        description: "Retrieves SharePoint content via the Copilot Retrieval API and generates summarized PDF memos.",
        tools:
        [
            AIFunctionFactory.Create(
                SharePointRetrievalTool.RetrieveSharePointContent,
                "RetrieveSharePointContent",
                "Retrieves document content from a SharePoint site URL using the Copilot Retrieval API. Returns text chunks with citations."),

            AIFunctionFactory.Create(
                PdfGeneratorTool.GenerateMemoPdf,
                "GenerateMemoPdf",
                "Generates a branded PDF memo from a title and markdown-formatted content. Returns the file path of the generated PDF.")
        ]);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
