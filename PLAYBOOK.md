# Foundry Hosted Agent — Playbook

> Everything needed to understand, rebuild, or extend this agent from scratch.
> Read this before touching the codebase — it will save you days.

## What This Is

A **Foundry Hosted Agent** (C# / .NET 10) that:

1. Lists files on SharePoint sites via the **caller's identity** (OBO) — fast, ~3-7s
2. Searches document content semantically via M365 Copilot RAG — slower, ~35-65s
3. Summarizes findings with GPT-5 and generates branded **PDF memos**
4. Uploads memos back to SharePoint
5. Stores operational learnings in Cosmos DB (self-improving)

```
User (Entra identity)
  │ POST /responses
  ▼
Foundry Hosted Agent (C# / .NET 10, Responses protocol)
  ├── SharePoint Files MCP (mcp_SharePointRemoteServer) ← FAST path (~3s)
  │     └── Graph API via OBO → list files, sites, folders
  ├── M365 Copilot MCP (mcp_M365Copilot) ← SLOW path (~35s)
  │     └── Full RAG pipeline → semantic content search
  ├── GPT-5 (Sweden Central)
  ├── PdfSharp → branded PDF memo
  ├── SharePoint Upload (Graph API, app credentials)
  └── Cosmos DB (serverless) — process learnings
```

---

## Two MCP Backends — Why and When

This is the most important architectural decision. We have two MCP toolboxes:

### `sharepoint-files` — FAST path (Graph API, ~1-7s)

- **Server**: `mcp_SharePointRemoteServer` (Work IQ SharePoint)
- **How it works**: Direct Microsoft Graph API calls via OBO
- **Use for**: Listing files, getting metadata, finding sites, folder operations
- **Results**: Deterministic — same query always returns same results
- **35+ tools**: `getSiteByPath`, `getDefaultDocumentLibraryInSite`, `getFolderChildren`, `findSite`, `listDocumentLibrariesInSite`, `findFileOrFolder`, `getFileOrFolderMetadataByUrl`, etc.

### `copilot-search` — SLOW path (M365 Copilot RAG, ~35-65s)

- **Server**: `mcp_M365Copilot`
- **How it works**: Query expansion → Semantic Index → Chunk ranking → LLM synthesis
- **Use for**: Searching document content by meaning ("find risk policies")
- **Results**: Non-deterministic — different wording = different results (by design)
- **1 tool**: `copilot_chat` with `message`, `fileUris`, `conversationId`, `enableWebSearch`

### LLM Routing

The agent instructions tell GPT-5 to pick the right tool:
- User says "list files on this site" → `ListSiteFiles` (FAST)
- User says "find a file called budget.xlsx" → `SearchFiles` (FAST — keyword search by name)
- User says "find documents about compliance" → `SearchContent` (SLOW)

---

## Project Structure

```
foundry-memo/
├── azure.yaml                          # azd service config (toolboxes, model, resources)
├── infra/main.bicep                    # All Azure infrastructure
├── PLAYBOOK.md                         # ← You are here
├── README.md                           # Quick overview
└── src/FoundryMemo/
    ├── Program.cs                      # Agent setup, dual MCP clients, 10 tools, instructions
    ├── Dockerfile                      # .NET 10 container + Liberation fonts for PDF
    ├── agent.yaml                      # Container agent definition
    ├── agent.manifest.yaml             # azd manifest
    ├── Tools/
    │   ├── SharePointFilesTool.cs      # FAST: Graph API file ops via Work IQ MCP
    │   ├── ToolboxSearchTool.cs        # SLOW: M365 Copilot RAG search (with dedup cache)
    │   ├── PdfGeneratorTool.cs         # PDF memo generation + SharePoint upload
    │   └── LearningsTool.cs            # Cosmos DB process learnings
    ├── Services/
    │   ├── ToolboxMcpClient.cs         # JSON-RPC MCP client (token cache, 429 retry, SSE)
    │   ├── SharePointUploadService.cs  # Graph Drive API upload
    │   ├── CrossPlatformFontResolver.cs
    │   └── LearningsStore.cs           # Cosmos DB CRUD
    └── Models/
        └── LearningEntry.cs
```

---

## Azure Resources

| Resource | Name | Purpose |
|----------|------|---------|
| Resource Group | `rg-foundry-memo` | Container for everything |
| Foundry Account | `fmemol6o6` | Hosts agent + project |
| Project | `foundry-memo` | Agent workspace |
| GPT-5 | `gpt-5` (GlobalStandard, 40K TPM) | LLM for summarization |
| ACR | auto-provisioned | Container images |
| Cosmos DB | `fmemo-cosmos-l6o6` (serverless) | Learnings store |
| App Insights | `fmemo-insights-l6o6` | Telemetry |
| Agent 365 Tools App | `ea9ffc3e-8a23-4a7d-836d-234d7c7565c1` | OBO audience for both MCP toolboxes |

### Foundry Connections

| Connection | Auth Type | Target | Purpose |
|-----------|-----------|--------|---------|
| `copilot-search-oauth` | `UserEntraToken` | `mcp_M365Copilot` | Semantic content search |
| `sharepoint-files-oauth` | `UserEntraToken` | `mcp_SharePointRemoteServer` | Fast file operations |
| `cosmos-db` | `CustomKeys` | Cosmos endpoint | App credential store |
| `graph-api` | `CustomKeys` | Graph API | SP upload credentials |
| `app-insights` | `AAD` | App Insights | Platform telemetry |

---

## Replicate From Scratch

### Prerequisites

- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd) + agent extension (`azd ext install azure.ai.agents`)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Azure subscription with Foundry access (Sweden Central)
- M365 tenant with Copilot license
- Entra app registration with Graph permissions (`Sites.ReadWrite.All`, `Files.ReadWrite.All`)

### Steps

```bash
# 1. Set env vars
azd env set GRAPH_APP_CLIENT_ID <your-app-client-id>
azd env set GRAPH_APP_CLIENT_SECRET <your-app-client-secret>

# 2. Provision infrastructure
azd provision

# 3. Create MCP toolboxes (requires preview header — not yet supported by azd deploy)
#    See "Creating Toolboxes" section below

# 4. Deploy agent
azd deploy

# 5. Test
azd ai agent invoke foundry-memo "List files on https://tenant.sharepoint.com/sites/MySite"

# 6. Monitor
azd ai agent monitor --follow
```

### Creating Toolboxes

`azd deploy` declares toolboxes in `azure.yaml` but they must also be created via REST API:

```bash
TOKEN=$(az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv)
ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>"

# Copilot search toolbox
curl -X POST "$ENDPOINT/toolboxes/copilot-search/versions?api-version=v1" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Foundry-Features: Toolboxes=V1Preview" \
  -d '{
    "description": "M365 Copilot MCP — semantic content search",
    "tools": [{
      "type": "mcp",
      "server_label": "copilot-search",
      "server_url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_M365Copilot",
      "require_approval": "never",
      "project_connection_id": "copilot-search-oauth"
    }]
  }'

# SharePoint files toolbox
curl -X POST "$ENDPOINT/toolboxes/sharepoint-files/versions?api-version=v1" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Foundry-Features: Toolboxes=V1Preview" \
  -d '{
    "description": "Work IQ SharePoint — fast file ops via Graph API",
    "tools": [{
      "type": "mcp",
      "server_label": "sharepoint-files",
      "server_url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_SharePointRemoteServer",
      "require_approval": "never",
      "project_connection_id": "sharepoint-files-oauth"
    }]
  }'
```

### Verifying Connections

After provisioning, verify the MCP connections have `audience` at ARM properties level:

```bash
# Test tools/list — should return available MCP tools
curl -X POST "$ENDPOINT/toolboxes/sharepoint-files/mcp?api-version=v1" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Foundry-Features: Toolboxes=V1Preview" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

If you get `"missing Audience/TokenAudience"`, fix with:

```bash
az rest --method PUT \
  --url "https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/connections/<name>?api-version=2025-04-01-preview" \
  --body '{
    "properties": {
      "authType": "UserEntraToken",
      "category": "RemoteTool",
      "target": "<mcp-server-url>",
      "audience": "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1",
      "isSharedToAll": true,
      "metadata": { "audience": "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1" }
    }
  }'
```

---

## Tool-Call Loop Prevention (Critical)

### The Problem

`FunctionInvokingChatClient` in `Microsoft.Extensions.AI` defaults to `MaximumIterationsPerRequest = 40`. When GPT-5 calls a slow MCP tool (~35s), it often retries 7-10 times with different query variations. Prompt instructions ("call SearchSharePoint ONCE") are **ignored**.

**Result**: 564 seconds (9.4 minutes) for a single query. Each MCP call takes 30-60s × 10 calls.

### The Fix

`ToolboxSearchTool.cs` uses a **static result cache with call limiter**:

- `_cachedSearchResult` + `_cacheTime` with 120s TTL
- `_callCount` + `_windowStart` — max 2 MCP calls per window
- **Smart content quality detection**: 18 negative patterns ("no documents", "no results", "couldn't find") — only caches responses that contain meaningful content

After fix: 1 MCP call, ~65s total.

### What Doesn't Work

| Approach | Result |
|----------|--------|
| Prompt: "call SearchSharePoint ONCE" | Ignored by GPT-5 |
| Per-site `ConcurrentDictionary` cache | LLM passes different URL variations |
| Cache by response length > 200 chars | Caches "no results found" messages (long but empty) |

---

## MCP Response Format

The Foundry toolbox wraps MCP responses in JSON-RPC:

```json
{
  "result": {
    "content": [
      { "type": "text", "text": "{...actual JSON response...}" },
      { "type": "text", "text": "CorrelationId: xxx, TimeStamp: yyy" }
    ]
  }
}
```

`ToolboxMcpClient.CallToolAsync` extracts and joins the `text` fields with `\n`. Tool code must then parse the first JSON object while ignoring trailing metadata lines.

### SharePoint MCP Field Names

The Graph API returns `"id"` for identifiers — **not** `"siteId"` or `"documentLibraryId"`:

```json
// getSiteByPath response
{ "id": "m365cpi54615665.sharepoint.com,0c0836f1-...,8518e725-...", "name": "MySite", ... }

// getDefaultDocumentLibraryInSite response
{ "id": "b!8TYIDAtx...", "name": "Documents", "driveType": "documentLibrary", ... }

// getFolderChildren response
{ "value": [ { "name": "file.docx", "file": {...}, "size": 25000, ... }, ... ] }
```

### Graph API Response Size

`getFolderChildren` returns ~2KB of verbose JSON per file (nested `createdBy`, `lastModifiedBy` objects). For 8 files = ~17K chars. The `SummarizeFileList` method in `SharePointFilesTool.cs` pre-processes this into a compact format (~200 bytes per file) to avoid the 4K tool output truncation.

---

## Anti-Patterns — Don't Repeat These

### Connections & Auth

| ❌ Don't | Why |
|----------|-----|
| Use `OAuth2` connection type for M365 MCP | Creates API Hub connector → `AADSTS700025` (public client + secret mismatch) |
| Put `audience` only in `metadata` | OBO token exchange reads from ARM `properties.audience`, not metadata |
| Delete/recreate OAuth2 connections | Destroys API Hub connector, new one may not provision |

### SDK & Tools

| ❌ Don't | Why |
|----------|-----|
| Use `AddFoundryToolboxes("name")` | Requires `FOUNDRY_AGENT_TOOLSET_ENDPOINT` env var — platform doesn't inject it |
| Mix server-side + local tools | `GetToolboxToolsAsync()` returns markers → 0-token responses when combined with `AIFunctionFactory` tools |
| Use `ModelContextProtocol` NuGet v1.3.0 | Pulls incompatible `M.E.AI.Abstractions` → `MissingMethodException` at runtime |
| Add OpenTelemetry SDK packages | DI conflicts with platform's `AddAgentHostTelemetry()`. Let the platform handle it |
| Rely on prompt instructions to limit tool calls | GPT-5 ignores "call once" instructions — enforce in code |

### Deployment & Platform

| ❌ Don't | Why |
|----------|-----|
| Trust `active` status = healthy | Version goes active based on image pull, NOT container health |
| Set env vars via `azd env set` | Platform doesn't pass arbitrary env vars to container |
| Use `Console.WriteLine` for diagnostics | Goes nowhere useful. Use `Console.Error.WriteLine` → visible in `azd ai agent monitor` |

---

## Debugging Guide

### Container logs
```bash
azd ai agent monitor --follow     # Real-time stdout/stderr
```

### App Insights (KQL)
```kusto
# Tool call durations
dependencies
| where timestamp > ago(1h)
| where name has "execute_tool" or name has "toolboxes"
| order by timestamp desc
| project timestamp, name, duration, resultCode, success

# End-to-end request time
traces
| where timestamp > ago(1h)
| where message has "Inbound POST /responses"
| project timestamp, message
```

### Direct MCP testing
```bash
TOKEN=$(az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv)
ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>/toolboxes/<toolbox>/mcp?api-version=v1"

# List available tools
curl -X POST "$ENDPOINT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Foundry-Features: Toolboxes=V1Preview" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'

# Call a specific tool
curl -X POST "$ENDPOINT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Foundry-Features: Toolboxes=V1Preview" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"sharepoint-files___getSiteByPath",
    "arguments":{"hostname":"tenant.sharepoint.com","serverRelativePath":"/sites/MySite"}
  }}'
```

### ILSpy decompilation (when docs fail)
```bash
dnx ilspycmd -l class ~/.nuget/packages/<package>/<version>/lib/net10.0/<assembly>.dll
dnx ilspycmd -t Namespace.ClassName <assembly>.dll
```

---

## MCP Error Codes

| Code | Meaning |
|------|---------|
| `-32006` | OAuth consent required (single tool) — extract consent URL from message |
| `-32007` | tools/list wrapper error (nested JSON) |
| `-32602` | Wrong tool name |
| `-32603` | Internal error — check nested message |

---

## Platform Quirks

- **Sessions persist old agent versions** — a playground session started before deploy keeps running old code until it expires (~15 min idle). Always start a NEW session after deploying.
- **50 concurrent sessions per subscription/region** — stale sessions count. Use `DELETE /agents/{name}?force=true` to clean up.
- **Dockerfile `ENV` instructions are stripped** — platform overwrites the container environment at runtime.
- **`azd provision` overwrites connections** — Bicep is the source of truth. If Bicep says the wrong auth type, every provision reverts manual fixes.
- **Tool output max ~12-15KB** — Responses protocol rejects larger outputs. Pre-process verbose MCP responses into compact summaries.

---

## Identity Flow

```
Caller (browser / Teams / API client)
  │ Entra token
  ▼
Foundry Platform (Responses protocol)
  │ Extracts caller identity, creates OBO assertion
  ▼
Agent Container (our code)
  │ Uses DefaultAzureCredential (agent's MI) for platform calls
  │ Platform proxies OBO token to MCP servers
  ▼
MCP Toolbox Proxy
  │ OBO token exchange: caller token → Graph API token
  │ Audience: ea9ffc3e-8a23-4a7d-836d-234d7c7565c1
  ▼
MCP Server (mcp_SharePointRemoteServer or mcp_M365Copilot)
  │ Calls Graph API as the CALLER (permission-trimmed results)
  ▼
Results back to agent → GPT-5 → Response to caller
```

**Key**: Content retrieval always uses the **caller's identity**. The agent never sees content the caller can't see. Purview/MIP labels are respected.

---

## Known Issue: Multi-Turn Conversations Break After Tool Calls

**Status**: Platform bug (as of July 2025). Confirmed through source-code analysis of
[`AgentFrameworkResponseHandler`](https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Foundry.Hosting/AgentFrameworkResponseHandler.cs),
[`OutputConverter`](https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Foundry.Hosting/OutputConverter.cs),
and [`InputConverter`](https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Foundry.Hosting/InputConverter.cs).

### Symptom

After a successful tool-using turn (e.g., `GetDocumentText` → M365 Copilot RAG → summary),
**all subsequent messages** in the same conversation fail with:

```
HTTP 400 (invalid_request_error): No tool output found for function call call_XXXX
```

Even simple messages like "who are you?" fail. Affects ANY client (Playground, custom UI, CLI).

### Root Cause

The Foundry platform's `FoundryStorageProvider` persists conversation items between turns.
When the agent container is recycled (which happens frequently — containers idle-out after ~15 min),
the next turn starts fresh and loads history from Foundry Storage.

The stored history contains `OutputItemFunctionToolCall` (the tool call from GPT-5) but is
**missing** the matching `OutputItemFunctionToolCallOutput` (the tool result). When GPT-5 sees
an orphaned tool call with no result, it rejects the entire conversation.

### How We Verified

1. **Framework source confirms both items are emitted**: `OutputConverter.ConvertUpdatesToEventsAsync()`
   handles both `FunctionCallContent` → `OutputItemFunctionToolCall` AND
   `FunctionResultContent` → `OutputItemFunctionToolCallOutput`. Both are yielded as SSE events.

2. **Framework source confirms both items are consumed**: `InputConverter.ConvertOutputItemToMessage()`
   has explicit case handlers for both `OutputItemFunctionToolCall` AND
   `OutputItemFunctionToolCallOutput`. The deserialization is correct.

3. **Agent logs show zero tool execution on failed turns**: Container starts, loads history
   (item_ids → batch/retrieve → HTTP 200), sends to GPT-5, gets HTTP 400 — all in ~2 seconds.
   No tool calls are made. The error is in history reconstruction, not tool execution.

4. **Same `call_id` appears in all failed requests**: Confirms the error traces back to the
   same orphaned tool call from the successful first turn.

### Where the Bug Lives

The gap is between `OutputConverter` emitting the SSE events and `FoundryStorageProvider`
persisting them. The platform's Responses Server SDK receives the streamed output items and
should store them all, but the `function_call_output` item is not being persisted.

This is NOT in our code — it's in the platform layer:
- `Azure.AI.AgentServer.Responses` → SSE event handling
- `Azure.AI.AgentServer.Responses.Internal.FoundryStorageProvider` → item persistence

### Python Clue

The Python hosted agent docs explicitly recommend `default_options={"store": False}` with the note:
*"Setting store to False avoids duplicating conversation history, since the hosting infrastructure
manages history automatically."* There is no C# equivalent exposed through `AsAIAgent()`.

### Workarounds

1. **Start new sessions after tool-using turns** — avoids the corrupted history issue entirely.
   Not ideal but functional.

2. **Switch to Invocations protocol** — manage conversation history ourselves (in Cosmos DB or
   in-memory with state persistence). Gives full control but requires significant rework.

3. **Avoid multi-turn tool calls** — if the agent can answer without tools on follow-up turns,
   the issue doesn't trigger. Only tool-using turns corrupt the history.

4. **Wait for platform fix** — this is a preview product. File a bug and monitor for fixes.

---

## References

- [Hosted Agents concepts](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents)
- [Quickstart: Deploy your first Hosted agent](https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/quickstart-hosted-agent)
- [C# Hosted Agent samples](https://github.com/microsoft-foundry/foundry-samples/tree/main/samples/csharp/hosted-agents)
- [Agent Framework local-tools sample](https://github.com/microsoft-foundry/foundry-samples/tree/main/samples/csharp/hosted-agents/agent-framework/local-tools)
- [M365 Copilot extensibility](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/)
