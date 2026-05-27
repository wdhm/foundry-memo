# foundry-memo

A **Foundry Hosted Agent** (C# / .NET 10) that searches SharePoint content via the **caller's identity** (OBO), summarizes with GPT-5, and generates branded PDF memos.

## How It Works

```
User (Entra identity)
  │ POST /responses
  ▼
Foundry Hosted Agent (Responses protocol)
  ├── SharePoint Files MCP → Graph API (OBO) → list files     (~3s)
  ├── M365 Copilot MCP → semantic search (OBO) → find content  (~35s)
  ├── GPT-5 → summarize + format
  ├── PdfSharp → branded PDF memo
  ├── SharePoint Upload → Graph API (app credentials)
  └── Cosmos DB → process learnings (self-improving)
```

The agent has two MCP backends and routes intelligently:
- **Fast path** — `mcp_SharePointRemoteServer` (Graph API, ~3s) for file listing, site lookup, metadata
- **Slow path** — `mcp_M365Copilot` (RAG pipeline, ~35s) for semantic content search

## Quick Start

```bash
azd env set GRAPH_APP_CLIENT_ID <app-client-id>
azd env set GRAPH_APP_CLIENT_SECRET <app-client-secret>
azd provision
azd deploy
azd ai agent invoke foundry-memo "List files on https://tenant.sharepoint.com/sites/MySite"
```

## Tools

| Tool | Speed | Identity | Purpose |
|------|-------|----------|---------|
| `ListSiteFiles` | ~3s | Caller (OBO) | List all files in a SharePoint site |
| `FindSite` | ~1s | Caller (OBO) | Find sites by name/keyword |
| `GetFileInfo` | ~1s | Caller (OBO) | File metadata by URL |
| `ListDocumentLibraries` | ~2s | Caller (OBO) | List libraries in a site |
| `SearchFiles` | ~3s | Caller (OBO) | Search files by name across a site |
| `SearchContent` | ~35s | Caller (OBO) | Semantic search across M365 content |
| `GetDocumentText` | ~35s | Caller (OBO) | Read document content by URL |
| `GenerateMemoPdf` | ~5s | App credentials | Generate + upload PDF memo |
| `ReadLearnings` | ~1s | App credentials | Load process learnings from Cosmos |
| `WriteLearning` | ~1s | App credentials | Store operational insight |

## Documentation

📖 **[PLAYBOOK.md](PLAYBOOK.md)** — Complete operational playbook: architecture, anti-patterns, debugging, replication steps, and all hard-won learnings from building this agent.

## License

MIT