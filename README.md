# foundry-memo

A **Foundry Hosted Agent** that retrieves SharePoint content via the **Copilot Retrieval API** and generates summarized **PDF memos** using GPT-5.

## Architecture

- **Microsoft Agent Framework** (C# / .NET 10) with Responses protocol
- **Copilot Retrieval API** — permission-trimmed, Purview-aware content retrieval from SharePoint
- **QuestPDF** — branded PDF memo generation
- **Azure AI Foundry** — hosted agent with scale-to-zero compute, GPT-5 (Sweden Central)

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