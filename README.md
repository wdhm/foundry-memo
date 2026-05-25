# foundry-memo

A **Foundry Hosted Agent** that searches M365 content via the **caller's identity**, summarizes it with GPT-5, generates branded **PDF memos**, and uploads them back to SharePoint.

**Self-improving** — after each run the agent stores operational insights (never content) in Cosmos DB and applies them on the next run.

## Architecture

```
User (Entra identity)
  │
  ▼
Foundry Hosted Agent (C# / .NET 10, Responses protocol)
  ├── M365 Copilot MCP (UserEntraToken / OBO)
  │     └── SharePoint, OneDrive, Teams, Mail — permission-trimmed
  ├── GPT-5 (Sweden Central)
  ├── QuestPDF → branded PDF memo
  ├── SharePoint Upload (managed identity, Sites.ReadWrite.All)
  └── Cosmos DB (serverless) — persistent process learnings
```

**Key design decision**: Content retrieval uses the **caller's identity** via the M365 Copilot MCP toolbox with `UserEntraToken` (OBO flow). This means results are permission-trimmed per user and Purview/MIP labels are respected — the agent only sees what the caller can see.

## Tools

| Tool | Purpose | Identity |
|------|---------|----------|
| `SearchSharePoint` | Search M365 content (docs, mail, chats, sites) | Caller (OBO) |
| `GetDocumentText` | Retrieve full document content by URL | Caller (OBO) |
| `RetrieveSharePointContent` | Fallback via Copilot Retrieval API | Delegated (Graph) |
| `GenerateMemoPdf` | Render PDF memo + upload to SharePoint | Agent MI |
| `ReadLearnings` | Load process improvements from Cosmos | Agent MI |
| `WriteLearning` | Store operational insight to Cosmos | Agent MI |

## Playbook — Replicate From Scratch

### Prerequisites

- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd) with agent extension (`azd ext install azure.ai.agents`)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Azure subscription with Foundry access (Sweden Central)
- M365 tenant with Copilot license

### 1. Provision Infrastructure

```bash
azd provision   # Creates: Foundry project, GPT-5 deployment, ACR, App Insights, Cosmos DB
```

This runs `infra/main.bicep` which provisions:
- Foundry Account + Project (Sweden Central)
- GPT-5 model deployment (GlobalStandard, 40 TPM)
- Azure Container Registry (Basic, admin disabled)
- Cosmos DB (serverless, NoSQL)
- Application Insights + Log Analytics workspace
- Role assignments: ACR Pull for project MI, Foundry User for project MI

### 2. Create the OAuth Connection (UserEntraToken)

Create a `UserEntraToken` connection for M365 Copilot MCP. This enables identity passthrough via OBO — **no consent URL needed**.

```bash
# Via Azure CLI / ARM API
az rest --method PUT \
  --url "https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/projects/{project}/connections/copilot-search-oauth?api-version=2025-04-01-preview" \
  --body '{
    "properties": {
      "authType": "UserEntraToken",
      "category": "RemoteTool",
      "target": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_M365Copilot",
      "audience": "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1",
      "isSharedToAll": true,
      "metadata": { "type": "custom_MCP" }
    }
  }'
```

> ⚠️ **Do NOT use `OAuth2` auth type** for M365 MCP. It creates an API Hub connector that fails with `AADSTS700025` (public client + secret mismatch). `UserEntraToken` bypasses this entirely.

### 3. Create the Toolbox

```bash
# Via REST API (requires Foundry-Features header)
curl -X POST "{project_endpoint}/toolboxes/copilot-search/versions?api-version=v1" \
  -H "Authorization: Bearer {token}" \
  -H "Content-Type: application/json" \
  -H "Foundry-Features: Toolboxes=V1Preview" \
  -d '{
    "description": "M365 Copilot MCP with UserEntraToken identity passthrough",
    "tools": [{
      "type": "mcp",
      "server_label": "copilot-search",
      "server_url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_M365Copilot",
      "require_approval": "never",
      "project_connection_id": "copilot-search-oauth"
    }]
  }'
```

Token scope: `https://ai.azure.com/.default`

### 4. Create Cosmos DB Connection

Store app credentials for Cosmos access (agent MI doesn't have Cosmos RBAC):

```bash
az rest --method PUT \
  --url "https://management.azure.com/.../connections/cosmos-db?api-version=2025-04-01-preview" \
  --body '{
    "properties": {
      "authType": "CustomKeys",
      "category": "CustomKeys",
      "target": "https://{cosmos-account}.documents.azure.com:443/",
      "isSharedToAll": true,
      "credentials": {
        "keys": {
          "clientId": "{app-client-id}",
          "clientSecret": "{app-client-secret}",
          "tenantId": "{tenant-id}"
        }
      }
    }
  }'
```

### 5. Deploy

```bash
azd deploy                    # Builds container, creates agent version
azd ai agent invoke foundry-memo --new-session "Search SharePoint for risk documents"
```

### 6. Route Traffic (if needed)

`azd deploy` creates a new version but may not route traffic. Use:

```bash
# PATCH {project_endpoint}/agents/foundry-memo?api-version=v1
# Body: { "agent_endpoint": { "version_selector": { "version_selection_rules": [{ "version": "N", "traffic_weight": 100 }] } } }
```

### 7. Grant RBAC

Ensure these identities have `Foundry User` role on the project:
- **Agent managed identity** — auto-assigned on agent creation
- **Developer identity** — for toolbox management
- **End users** — for OBO identity passthrough via `UserEntraToken`

## Project Structure

```
├── azure.yaml                          # azd service definition
├── infra/                              # Bicep IaC (Foundry, GPT-5, ACR, Cosmos, AppInsights)
├── LEARNINGS.md                        # Session learnings and gotchas
└── src/FoundryMemo/
    ├── Program.cs                      # Agent setup + 6-tool registration
    ├── agent.yaml                      # Container agent definition (env vars, resources)
    ├── agent.manifest.yaml             # azd manifest (toolbox + model declarations)
    ├── Dockerfile                      # .NET 10 container image
    ├── Tools/
    │   ├── ToolboxSearchTool.cs        # M365 Copilot MCP bridge (SearchSharePoint, GetDocumentText)
    │   ├── SharePointRetrievalTool.cs  # Fallback: Copilot Retrieval API
    │   ├── PdfGeneratorTool.cs         # QuestPDF memo generation + SharePoint upload
    │   └── LearningsTool.cs            # Cosmos DB process learnings (read/write)
    └── Services/
        ├── ToolboxMcpClient.cs         # Custom JSON-RPC client for Foundry Toolbox MCP
        ├── CopilotRetrievalService.cs  # Graph Retrieval API client (fallback)
        ├── SharePointUploadService.cs  # Graph Drive API upload
        └── LearningsStore.cs           # Cosmos DB CRUD
```

## Key Learnings

See [LEARNINGS.md](LEARNINGS.md) for comprehensive session learnings including:
- Platform deployment gotchas (`session_not_ready`, reserved env vars)
- SDK version drift and startup behavior
- MCP toolbox configuration (UserEntraToken vs OAuth2)
- Docker/container debugging strategies

## License

MIT