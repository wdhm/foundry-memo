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
  ├── PdfSharp → branded PDF memo
  ├── SharePoint Upload (Graph API, app credentials)
  └── Cosmos DB (serverless) — persistent process learnings
```

**Key design decision**: Content retrieval uses the **caller's identity** via the M365 Copilot MCP toolbox with `UserEntraToken` (OBO flow). Results are permission-trimmed per user and Purview/MIP labels are respected — the agent only sees what the caller can see.

## Tools

| Tool | Purpose | Identity |
|------|---------|----------|
| `SearchSharePoint` | Search M365 content (docs, mail, chats, sites) | Caller (OBO) |
| `GetDocumentText` | Retrieve full document content by URL | Caller (OBO) |
| `RetrieveSharePointContent` | Fallback via Copilot Retrieval API | App credentials |
| `GenerateMemoPdf` | Render PDF memo + upload to SharePoint | App credentials |
| `ReadLearnings` | Load process improvements from Cosmos | App credentials |
| `WriteLearning` | Store operational insight to Cosmos | App credentials |

## Playbook — Replicate From Scratch

### Prerequisites

- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd) with agent extension (`azd ext install azure.ai.agents`)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Azure subscription with Foundry access (Sweden Central)
- M365 tenant with Copilot license
- An Entra app registration with Graph permissions (`Sites.ReadWrite.All`, `Files.ReadWrite.All`)

### 1. Set Environment Variables

```bash
# Required: Entra app credentials (used for Cosmos + Graph connections)
azd env set GRAPH_APP_CLIENT_ID <your-app-client-id>
azd env set GRAPH_APP_CLIENT_SECRET <your-app-client-secret>

# Optional: developer principal for local Cosmos access
azd env set DEVELOPER_PRINCIPAL_ID <your-entra-object-id>
```

### 2. Provision Infrastructure

```bash
azd provision
```

This runs `infra/main.bicep` which creates:
- **Foundry Account + Project** (Sweden Central)
- **GPT-5 model deployment** (GlobalStandard, 40K TPM)
- **Azure Container Registry** (Basic, admin disabled)
- **Cosmos DB** (serverless, NoSQL) — learnings store
- **Application Insights + Log Analytics** — telemetry
- **Foundry connections**: `cosmos-db` (CustomKeys), `copilot-search-oauth` (UserEntraToken), `app-insights`
- **RBAC**: ACR Pull for project MI, Cosmos Data Contributor for project + account MI, Foundry User for developer

### 3. Create the MCP Toolbox

The Bicep creates the `copilot-search-oauth` connection, but the **toolbox** must be created via REST API (requires preview header):

```bash
# Get a token
TOKEN=$(az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv)
ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>"

# Create toolbox version
curl -X POST "$ENDPOINT/toolboxes/copilot-search/versions?api-version=v1" \
  -H "Authorization: Bearer $TOKEN" \
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

### 4. Verify the Connection (Important!)

After provisioning, verify the MCP connection has `audience` at properties level:

```bash
# Test MCP tools/list — should return copilot_chat tool
curl -X POST "$ENDPOINT/toolboxes/copilot-search/mcp?api-version=v1" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Foundry-Features: Toolboxes=V1Preview" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
```

If you get `"missing Audience/TokenAudience"`, the `audience` field isn't at the ARM properties level. Fix with:

```bash
# Recreate with audience at properties level
az rest --method PUT \
  --url "https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/connections/copilot-search-oauth?api-version=2025-04-01-preview" \
  --body '{
    "properties": {
      "authType": "UserEntraToken",
      "category": "RemoteTool",
      "target": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_M365Copilot",
      "audience": "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1",
      "isSharedToAll": true,
      "metadata": { "audience": "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1" }
    }
  }'
```

### 5. Deploy

```bash
azd deploy
```

### 6. Test

```bash
azd ai agent invoke foundry-memo --new-session \
  "Search for documents on https://tenant.sharepoint.com/sites/MySite"
```

### 7. Monitor

```bash
azd ai agent monitor --follow   # Container stdout/stderr
# App Insights: traces table in Azure Portal for structured telemetry
```

## ⚠️ Critical Gotchas

1. **`UserEntraToken` connection needs `audience` at properties level** — `metadata.audience` alone is NOT sufficient. The OBO token exchange reads from `properties.audience`. See step 4 above.

2. **Never use `OAuth2` for M365 MCP** — it creates API Hub connectors that fail with `AADSTS700025`.

3. **Don't add OpenTelemetry packages** — the platform's `AddAgentHostTelemetry()` handles everything. Just define the `app-insights` connection correctly in Bicep.

4. **Don't use `AddFoundryToolboxes` SDK method** — `FOUNDRY_AGENT_TOOLSET_ENDPOINT` isn't injected by the platform. Use the custom `ToolboxMcpClient` bridge instead.

5. **Don't mix server-side and local tools** — `GetToolboxToolsAsync()` returns markers that cause 0-token responses when combined with `AIFunctionFactory.Create()` tools.

6. **Tool output max ~12KB** — the Responses protocol rejects larger outputs. Extract the `reply` field from MCP responses and truncate.

7. **`.dockerignore` must exclude `.env`** — otherwise stale env vars get baked into the container image.

## Project Structure

```
├── azure.yaml                          # azd service definition
├── infra/main.bicep                    # All Azure infrastructure
├── LEARNINGS.md                        # Comprehensive operational learnings
└── src/FoundryMemo/
    ├── Program.cs                      # Agent setup, DI, 6-tool registration
    ├── agent.yaml                      # Container agent definition
    ├── agent.manifest.yaml             # azd manifest (toolbox + model)
    ├── Dockerfile                      # .NET 10 container + Liberation fonts
    ├── Tools/
    │   ├── ToolboxSearchTool.cs        # M365 Copilot MCP bridge (OBO identity)
    │   ├── SharePointRetrievalTool.cs  # Fallback: Copilot Retrieval API
    │   ├── PdfGeneratorTool.cs         # PdfSharp memo generation + upload
    │   └── LearningsTool.cs            # Cosmos DB process learnings
    ├── Services/
    │   ├── ToolboxMcpClient.cs         # Custom JSON-RPC client for MCP
    │   ├── CopilotRetrievalService.cs  # Graph Retrieval API (fallback)
    │   ├── SharePointUploadService.cs  # Graph Drive API upload
    │   ├── CrossPlatformFontResolver.cs # PdfSharp font resolver (Win+Linux)
    │   └── LearningsStore.cs           # Cosmos DB CRUD
    └── Models/
        ├── LearningEntry.cs            # Cosmos document model
        └── RetrievalResponse.cs        # Graph Retrieval API response model
```

## Key Learnings

See [LEARNINGS.md](LEARNINGS.md) for comprehensive operational learnings including:
- **Anti-patterns** — approaches that were tried and failed (don't repeat these)
- MCP toolbox configuration (`UserEntraToken` + `audience` at properties level)
- Platform deployment gotchas (`session_not_ready`, reserved env vars, connection poisoning)
- SDK version drift and startup behavior
- Docker/container debugging strategies

## License

MIT