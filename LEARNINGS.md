# Foundry Hosted Agent — Learnings

## Platform & Deployment

| Lesson | Detail |
|--------|--------|
| `session_not_ready` is opaque | Covers image pull failures, container crashes, health check timeouts — no differentiation. Zero container logs if crash is pre-telemetry. |
| Auto-created connections can poison startup | `azd` auto-creates `app-insights` connection with `ApiKey` auth type but no actual key. `AddAgentHostTelemetry()` crashes the container trying to use it. Fix: recreate properly via Bicep with `credentials.key` set to the **full connection string** (not just the instrumentation key). |
| App Insights `credentials.key` must be the full connection string | The platform reads `credentials.key` from the `app-insights` Foundry connection and passes it as `APPLICATIONINSIGHTS_CONNECTION_STRING`. If you set it to just the instrumentation key GUID, the OTel SDK crashes with "Connection string doesn't have value for keyword". Use `appInsights.properties.ConnectionString` in Bicep, not `InstrumentationKey`. |
| Don't add your own OTel SDK | The platform's `AddAgentHostTelemetry()` (called inside `AgentHost.CreateBuilder`) handles all OpenTelemetry setup. Adding `Azure.Monitor.OpenTelemetry.AspNetCore` or `Azure.Monitor.OpenTelemetry.Exporter` yourself causes DI conflicts and startup crashes. Just create the Foundry connection correctly and the platform handles the rest. |
| Dockerfile ENV vars are stripped by platform | `ENV` instructions in Dockerfile are not honored at runtime. The platform overwrites the container environment. |
| `azd env` values don't reach the container | Only well-known config keys from `azure.yaml` (like model deployment name, toolbox name) are injected as env vars. Arbitrary `azd env set` values are ignored. |
| Use `azd ai agent monitor` for container logs | Shows stdout/stderr from the running container. Essential for debugging startup crashes since App Insights is unavailable. |
| Always clear AZD session cache after force-delete | `~/.azd/config.json` → `extensions.ai-agents.sessions` and `conversations`. Stale session IDs cause the platform to attempt resuming non-existent sessions. |
| `azd ext upgrade` can change required env vars | Extension `0.1.33` requires `FOUNDRY_PROJECT_ENDPOINT` (was `AZURE_AI_PROJECT_ENDPOINT`). Always check after upgrading. |
| Force-delete before redeploy | `DELETE /agents/{name}?force=true` cascade-deletes all sessions. Without this, stale sessions accumulate (limit ~50/subscription/region). |
| Deploy shows "active" even if container will crash | The agent version goes `creating→active` based on image availability, NOT container health. Health is only checked on first invoke. |
| `FOUNDRY_AGENT_TOOLSET_ENDPOINT` not injected | Platform does NOT auto-inject this env var even when `toolboxes` is declared in the agent version definition. MCP toolbox proxy is non-functional without it. Sweden Central as of 2025-07. |
| `azd deploy` strips `toolboxes` from version definition | Only manually-created versions (via REST API with `Foundry-Features=Toolboxes=V1Preview` header) retain the `toolboxes` field. `azd deploy` ignores `config.toolboxes` in `azure.yaml`. |

## SDK & Startup

| Lesson | Detail |
|--------|--------|
| `AddFoundryToolboxes("name")` is dangerous | Eagerly connects to MCP proxy in `StartAsync`. If connection hangs past platform timeout, `OperationCanceledException` kills the host. Use `AddFoundryToolboxes()` (no args) for lazy resolution. |
| `GetConnectionAsync` can hang indefinitely | In hosted mode with new managed identity, credential acquisition may stall. Always wrap in a `CancellationTokenSource` (15s is safe). |
| `DefaultAzureCredential` is lazy | Construction never connects — tokens are only acquired on first use. Safe to construct at startup without timeouts. |
| Package version drift is real | `Azure.AI.AgentServer.Core` jumped from `beta.4` to `beta.23` transitively. Pin versions or check `dotnet list package --include-transitive`. |
| Keep SDK current | Hosting package went `1.3.0→1.6.2` in one month. Preview SDKs move fast — update regularly to avoid compounding breaks. |
| `AIFunctionFactory.Create` with static/inline methods fails silently | Tools created from static local functions or inline `new X().Method` groups may not register in the hosted agent tool list. Only instance methods on pre-existing objects (e.g., `learningsTool.ReadLearnings`) reliably appear. Root cause unknown — likely a delegate/metadata resolution issue. |
| SDK version matters for toolbox approach | v1.3.0 has `GetToolboxToolsAsync` (server-side); v1.6.2 removed it and added `HostedMcpToolboxAITool` marker class. Neither works when platform doesn't inject the proxy endpoint. |

## Docker & Container

| Lesson | Detail |
|--------|--------|
| `.dockerignore` is critical | Without it, `.env` gets baked into the image. `DotNetEnv.Env.TraversePath().Load()` then loads stale/wrong values (e.g., old `FOUNDRY_PROJECT_ENDPOINT` pointing to `cognitiveservices.azure.com`). |
| ACR remote build respects `.dockerignore` | Confirmed — azd uploads source tar to ACR, ACR docker build applies `.dockerignore` normally. |
| `startupCommand` in azure.yaml is local-only | The hosted platform uses the Dockerfile `ENTRYPOINT`, not `startupCommand`. That field is for `azd ai agent run` local dev. |
| `aspnet:10.0` is a floating tag | .NET 10 images update frequently. For reproducibility, pin to a specific SHA in production. |

## Infrastructure & Auth

| Lesson | Detail |
|--------|--------|
| `disableLocalAuth: true` on Foundry account is fine | Platform uses managed identity internally. Agents use `DefaultAzureCredential` which uses Entra auth. API keys aren't needed. |
| Project identity pulls ACR images | The Foundry project's system-assigned MI needs `AcrPull` on the container registry. Agent instance identities do NOT need it. |
| Agent instance identity gets `Foundry User` automatically | Platform auto-assigns this on agent creation. No manual RBAC needed for the agent to call its own project. |
| Cosmos connection uses `CustomKeys` for app credentials | Because agent instance identity isn't in Cosmos RBAC, we store `clientId/clientSecret/tenantId` in the connection's custom keys and create a `ClientSecretCredential` at runtime. |
| Cosmos DB `publicNetworkAccess` must be Enabled for VNET agents | Hosted agents run in VNETs with service endpoints. If Cosmos has `publicNetworkAccess: Disabled`, service endpoint traffic is blocked (403). Bicep may show `Enabled` but live state can differ — verify via ARM API. |

## MCP Toolbox & Identity Passthrough

| Lesson | Detail |
|--------|--------|
| Toolbox creation requires preview header | `POST /toolboxes/{name}/versions?api-version=v1` with `Foundry-Features=Toolboxes=V1Preview`. Without it, 404. |
| `project_connection_id` not accepted by Responses API | Toolbox tools with `project_connection_id` auth format are rejected: "Missing mutually exclusive parameters: 'tools[0]'. Ensure you are providing exactly one of: 'server_url' or 'connector_id'". Format mismatch between toolbox definition and runtime resolution. |
| `connector_id` is a closed enum | Values like `M365Copilot` aren't valid. It's an internal enum of platform-supported connectors. Using a connection name gets "Unknown MCPToolConnectorId value". |
| Client-side MCP (`AddFoundryToolboxes`) requires proxy env var | Decompiled `FoundryToolboxService` shows it checks `FOUNDRY_AGENT_TOOLSET_ENDPOINT`. When absent, logs "toolbox support is disabled" and does nothing. |
| MCP OAuth passthrough is platform-dependent | The entire flow (caller token → MCP proxy → OAuth connection → M365 Copilot server) requires platform infrastructure that injects the proxy endpoint into the container. This is a hard platform dependency — no workaround exists in user code. |
| **Use `UserEntraToken` for M365 MCP, NOT `OAuth2`** | `OAuth2` connections create API Hub "Generic OAuth 2 with PKCE" connectors that fail with `AADSTS700025` (public client + secret mismatch). `UserEntraToken` uses OBO flow — no consent URL, no API Hub connector, just works. |
| `UserEntraToken` needs `audience` field | Set `audience` to the target app ID (e.g., `ea9ffc3e-8a23-4a7d-836d-234d7c7565c1` for M365 Copilot MCP). Without it, `tools/list` returns zero tools. |
| M365 Copilot MCP URL | `https://agent365.svc.cloud.microsoft/agents/servers/mcp_M365Copilot` — exposes a single `copilot_chat` tool with `message`, `fileUris`, `conversationId`, `enableWebSearch` params. |
| Tool names are prefixed with server_label | MCP tools in toolboxes get names like `{server_label}___{tool_name}` (triple underscore). Our tool becomes `copilot-search___copilot_chat`. |
| `copilot_chat` returns rich M365 content | Searches across SharePoint, OneDrive, Teams, Mail — returns permission-trimmed results with citations. Respects Purview/MIP labels natively. |
| Don't delete/recreate connections via ARM | Deleting an `OAuth2` connection destroys its API Hub connector. Recreating creates a NEW connector ID that may not provision correctly. For `UserEntraToken`, this isn't an issue since no API Hub connector is involved. |
| Toolbox `server_url` can be set in version | When creating a new toolbox version, include `server_url` in the tool definition even if it was set in the connection. The toolbox stores both. |

## Debugging Strategy

| Lesson | Detail |
|--------|--------|
| Isolate by deploying the official sample | If the official `local-tools` sample fails too, it's platform/infra, not your code. Fastest way to rule out code issues. |
| Binary search connections | Delete connections one at a time to find which one crashes the container. App Insights was the culprit here. |
| Decompile the SDK when docs are missing | `dnx ilspycmd` on NuGet packages reveals exact startup behavior (eager vs lazy, exception handling, auth flows). |
| Check `az role assignment list --scope` | After force-deleting agents, verify the new identities have required roles. Stale role assignments point to deleted principals. |
| Write diagnostics to Cosmos at startup | When you can't add diagnostic tools (framework quirk), write env vars/state to Cosmos in startup code and read via existing ReadLearnings tool. |
| Use ILSpy to understand SDK internals | `FoundryToolboxService`, `AIFunctionFactory`, `AsAIAgent` — all decompilable to understand exact behavior when docs are absent or wrong. |
