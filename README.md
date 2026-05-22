# foundry-memo

A **Foundry Hosted Agent** that retrieves SharePoint content via the **Copilot Retrieval API** and generates summarized **PDF memos** using GPT-5.

**This is a self-improving agent** — it gets better at the mechanical process of fetching, merging, summarizing, and generating PDFs over time. After each run, the agent reflects on what it learned about the *process* (never the content) and stores operational insights in a persistent learnings store. On the next run, it reads all past learnings before starting work, applying improvements automatically.

## Architecture

- **Microsoft Agent Framework** (C# / .NET 10) with Responses protocol
- **Copilot Retrieval API** — permission-trimmed, Purview-aware content retrieval from SharePoint
- **QuestPDF** — branded PDF memo generation
- **Azure AI Foundry** — hosted agent with scale-to-zero compute, GPT-5 (Sweden Central)
- **Cosmos DB** — persistent process learnings store (operational insights only, never sensitive data)

## How the Learnings Loop Works

```
1. Agent receives a SharePoint URL
2. ReadLearnings → loads ALL process improvements from previous runs
3. Agent applies learnings to its retrieval, summarization, and PDF generation
4. Agent produces the memo
5. WriteLearning → stores any new operational insights discovered
   Examples:
   - "Large sites need multiple retrieval queries with different terms"
   - "Excel data renders better as bullet comparisons than raw tables"
   - "Memos over 3 pages benefit from a table of contents"
   ❌ Never stores: file content, user data, SharePoint URLs, or business information
```

## Quick Start

### Prerequisites

- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd) with agent extension
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Azure subscription with Foundry access
- M365 Copilot license (for Retrieval API)

### Setup

```bash
# Install azd agent extension
azd ext install azure.ai.agents

# Provision Azure resources (Foundry project, GPT-5, ACR, App Insights)
azd provision

# Run locally
azd ai agent run

# Test
azd ai agent invoke --local "Summarize https://contoso.sharepoint.com/sites/docs"

# Deploy to Foundry
azd deploy
```

## Project Structure

```
src/FoundryMemo/
├── Program.cs                      # Agent setup + tool registration
├── Tools/
│   ├── SharePointRetrievalTool.cs  # Copilot Retrieval API integration
│   └── PdfGeneratorTool.cs         # QuestPDF memo generation
├── Services/
│   └── RetrievalApiClient.cs       # HTTP client for Retrieval API
├── Models/
│   └── RetrievalResult.cs          # API response DTOs
├── agent.manifest.yaml             # azd agent manifest
└── Dockerfile                      # Container image
```

## License

MIT