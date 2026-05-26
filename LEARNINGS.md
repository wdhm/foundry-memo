# Foundry Hosted Agent — Learnings

> Hard-won operational knowledge from building a Foundry Hosted Agent with M365 Copilot MCP integration.
> Read this before making changes — it will save you days of debugging.

## ⚠️ Anti-Patterns — What NOT To Do

These approaches were tried and failed. Do not revisit them.

| Approach | Why It Fails |
|----------|-------------|
| Prompt-only tool-call limiting ("call SearchSharePoint ONCE") | gpt-5 ignores this instruction and loops 7-10 times. Must enforce in code with a result cache/dedup. |
| `OAuth2` connection for M365 MCP | Creates API Hub "Generic OAuth 2 with PKCE" connector → `AADSTS700025` (public client + secret mismatch). Always use `UserEntraToken`. |
| `AddFoundryToolboxes("name")` SDK method | Requires `FOUNDRY_AGENT_TOOLSET_ENDPOINT` env var that the platform does NOT inject. Entire subsystem is silently disabled. |
| `GetToolboxToolsAsync()` server-side tools | Returns `HostedMcpToolboxAITool` markers, not `AIFunction`. Mixing with local tools causes **empty responses (0 tokens output)**. |
| `ModelContextProtocol` v1.3.0 NuGet package | Pulls `M.E.AI.Abstractions` 10.5.2 which changed `WebSearchToolResultContent.Results` setter → `MissingMethodException` at runtime. SDK pins MCP 1.1.0 transitively. |
| MCP session state via raw HTTP | Stateless transport can't maintain MCP session for consent token resolution. Initialize per-request instead. |
| `FOUNDRY_AGENT_TOOLSET_ENDPOINT` manual set | Setting via `Environment.SetEnvironmentVariable()` gives 401 — the constructed URL isn't the same as what the platform would inject. Reserved in `agent.yaml`. |
| `audience` only in connection `metadata` | OBO token exchange fails: "missing Audience/TokenAudience. Cannot call /obotokenv2 without an audience." Must be at ARM `properties` level. |
| Adding OpenTelemetry SDK packages | `Azure.Monitor.OpenTelemetry.AspNetCore` or `.Exporter` cause DI conflicts with platform's `AddAgentHostTelemetry()`. Let the platform handle it. |

## MCP Toolbox & Identity Passthrough

| Lesson | Detail |
|--------|--------|
| **Use `UserEntraToken`, NOT `OAuth2`** | `UserEntraToken` uses OBO flow — no consent URL, no API Hub connector, just works. This is the only working approach for M365 Copilot MCP. |
| **`audience` MUST be at ARM properties level** | The connection body needs `properties.audience = "ea9ffc3e-..."` — putting it only in `metadata` is NOT sufficient. The OBO token exchange reads from properties. Also keep it in `metadata` for Bicep compatibility. |
| `isSharedToAll` may not stick via PUT | The API sometimes ignores `isSharedToAll: true` on PUT. Verify after creation. Not critical for agent operation — only controls project-wide visibility. |
| Toolbox creation requires preview header | `POST /toolboxes/{name}/versions?api-version=v1` with `Foundry-Features=Toolboxes=V1Preview`. Without it, 404. |
| Tool names are prefixed with server_label | MCP tools get names like `{server_label}___{tool_name}` (triple underscore). Our tool is `copilot-search___copilot_chat`. |
| M365 Copilot MCP URL | `https://agent365.svc.cloud.microsoft/agents/servers/mcp_M365Copilot` — exposes `copilot_chat` with `message`, `fileUris`, `conversationId`, `enableWebSearch` params. |
| Agent 365 Tools App ID | `ea9ffc3e-8a23-4a7d-836d-234d7c7565c1` — this is the `audience` for the UserEntraToken connection. |
| Auth scope for MCP endpoint | `https://ai.azure.com/.default` (NOT `cognitiveservices.azure.com/.default`). |
| `copilot_chat` returns rich M365 content | Searches SharePoint, OneDrive, Teams, Mail — permission-trimmed per caller. Respects Purview/MIP labels. |
| Tool output size limit | Responses protocol rejects tool output >~15KB with "No tool output found for function call" (400). Extract `reply` field and truncate to ~4KB. |
| **LLM loops tool calls (CRITICAL)** | Without a tool-level dedup cache, gpt-5 calls SearchSharePoint 7-10 times per request. Each MCP call takes 30-60s server-side = 400-600s total. Prompt instructions ("call once") are **ignored**. Fix: static result cache with 120s TTL — after 1st real call, subsequent calls return instantly with "SEARCH ALREADY COMPLETE". Reduced 564s → 65s. |
| `FunctionInvokingChatClient` default is 40 iterations | The M.E.AI `FunctionInvokingChatClient` allows up to 40 tool-call iterations per request by default. Combined with slow tools (30-60s each), this creates catastrophic latency. Tool-level dedup is more reliable than prompt constraints. |
| MCP error codes | `-32006`: consent required (single tool). `-32007`: tools/list wrapper error (nested JSON). `-32602`: wrong tool name. `-32603`: internal error (check nested message). |
| Don't delete/recreate OAuth2 connections | Deleting destroys the API Hub connector. Recreating creates a new connector ID that may not provision. `UserEntraToken` doesn't have this issue. |
| `azd provision` overwrites connections | Bicep defines the connection config. If Bicep says `OAuth2`, every provision overwrites `UserEntraToken` back. Keep Bicep updated with the correct config. |

## Platform & Deployment

| Lesson | Detail |
|--------|--------|
| `session_not_ready` is opaque | Covers image pull failures, container crashes, health check timeouts — no differentiation. Zero container logs if crash is pre-telemetry. |
| Auto-created connections poison startup | `azd` auto-creates `app-insights` with `ApiKey` auth but no key. `AddAgentHostTelemetry()` crashes trying to use it. Fix: define properly in Bicep. |
| App Insights `credentials.key` = full connection string | Platform reads `credentials.key` and passes as `APPLICATIONINSIGHTS_CONNECTION_STRING`. Use `appInsights.properties.ConnectionString` in Bicep, not just the instrumentation key. |
| Dockerfile ENV vars are stripped | `ENV` instructions are not honored at runtime. Platform overwrites the container environment. |
| `azd env` values don't reach container | Only well-known config keys from `azure.yaml` are injected. Arbitrary `azd env set` values are ignored. |
| Use `azd ai agent monitor` for container logs | Shows stdout/stderr. Essential for debugging since App Insights is unavailable during crashes. |
| Force-delete before redeploy | `DELETE /agents/{name}?force=true` cascade-deletes sessions. Stale sessions accumulate (limit ~50/subscription/region). |
| Deploy shows "active" even if container crashes | Version goes `creating→active` based on image availability, NOT health. Health checked on first invoke. |
| `azd deploy` strips `toolboxes` from version | Only manually-created versions retain `toolboxes`. `azd deploy` ignores `config.toolboxes` in `azure.yaml`. |
| Clear AZD session cache after force-delete | `~/.azd/config.json` → `extensions.ai-agents.sessions`. Stale IDs cause resume failures. |

## SDK & Startup

| Lesson | Detail |
|--------|--------|
| `GetConnectionAsync` can hang indefinitely | In hosted mode with new MI, credential acquisition may stall. Always wrap in `CancellationTokenSource` (15s). |
| `DefaultAzureCredential` is lazy | Construction never connects — tokens acquired on first use. Safe at startup without timeouts. |
| `AIFunctionFactory.Create` with static methods fails silently | Only instance methods on pre-existing objects reliably register in the hosted agent tool list. |
| Package version drift is real | Preview SDKs move fast. Pin versions or check `dotnet list package --include-transitive`. |

## Docker & Container

| Lesson | Detail |
|--------|--------|
| `.dockerignore` is critical | Without it, `.env` gets baked in. `DotNetEnv.Env.TraversePath().Load()` loads stale values. |
| `startupCommand` in azure.yaml is local-only | Hosted platform uses Dockerfile `ENTRYPOINT`, not `startupCommand`. |
| Install fonts for PDF generation | Container needs `fonts-liberation` (Arial-compatible) for PdfSharp. Add to Dockerfile. |

## Infrastructure & Auth

| Lesson | Detail |
|--------|--------|
| `disableLocalAuth: true` is fine | Platform uses MI internally. Agents use `DefaultAzureCredential` (Entra auth). |
| Project MI pulls ACR images | Project's system-assigned MI needs `AcrPull`. Agent instance MIs do NOT. |
| Agent MI gets `Foundry User` automatically | Platform auto-assigns on agent creation. No manual RBAC needed. |
| Cosmos uses `CustomKeys` for app credentials | Agent MI isn't in Cosmos RBAC. Store `clientId/clientSecret/tenantId` in connection custom keys. |

## Debugging Strategy

| Lesson | Detail |
|--------|--------|
| Isolate with official sample | If the `local-tools` sample also fails, it's platform/infra. |
| Binary search connections | Delete one at a time to find which crashes the container. |
| Decompile the SDK | `dnx ilspycmd` on NuGet packages reveals exact startup behavior. |
| `Console.Error.WriteLine` → `azd ai agent monitor` | Visible in monitor but NOT in App Insights. Use `ILogger` for App Insights. |
| `Console.WriteLine` → nowhere useful | Not visible in App Insights or monitor. Avoid for diagnostics. |
