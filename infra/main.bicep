// foundry-memo infrastructure
// Deploys: AI Foundry Account + Project, GPT-5 model, ACR, App Insights

targetScope = 'resourceGroup'

// ──────────────────────────────────────────
// Parameters
// ──────────────────────────────────────────

@description('Base name for all resources (max 9 chars for Foundry naming)')
@maxLength(9)
param baseName string = 'fmemo'

@description('Azure region for all resources')
@allowed([
  'swedencentral'
  'eastus'
  'eastus2'
  'westus'
  'westus3'
  'northcentralus'
  'canadacentral'
  'southeastasia'
  'polandcentral'
  'southafricanorth'
  'koreacentral'
  'southindia'
  'brazilsouth'
  'norwayeast'
  'japaneast'
  'francecentral'
  'switzerlandnorth'
  'australiaeast'
])
param location string = 'swedencentral'

@description('Name of the GPT model to deploy')
param modelName string = 'gpt-5'

@description('Model format')
param modelFormat string = 'OpenAI'

@description('Model version')
param modelVersion string = '2025-08-07'

@description('Model SKU')
param modelSkuName string = 'GlobalStandard'

@description('Model capacity in TPM (tokens per minute, in thousands)')
param modelCapacity int = 40

@description('Foundry project name')
param projectName string = 'foundry-memo'

@description('Foundry project description')
param projectDescription string = 'Hosted agent that generates PDF memos from SharePoint content via the Copilot Retrieval API.'

@description('Client ID of the Entra app registration for MCP OAuth (foundry-memo-graph)')
param graphAppClientId string = ''

@secure()
@description('Client secret for the Entra app registration (pass via azd env)')
param graphAppClientSecret string = ''

@description('App ID of Agent 365 Tools service principal')
param agent365ToolsAppId string = 'ea9ffc3e-8a23-4a7d-836d-234d7c7565c1'

// Unique suffix for globally unique names
var uniqueSuffix = substring(uniqueString(resourceGroup().id), 0, 4)
var accountName = toLower('${baseName}${uniqueSuffix}')

// ──────────────────────────────────────────
// AI Foundry Account
// ──────────────────────────────────────────

resource aiFoundry 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: accountName
  location: location
  sku: {
    name: 'S0'
  }
  kind: 'AIServices'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: accountName
    networkAcls: {
      defaultAction: 'Allow'
      virtualNetworkRules: []
      ipRules: []
    }
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

// ──────────────────────────────────────────
// AI Foundry Project
// ──────────────────────────────────────────

resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-04-01-preview' = {
  parent: aiFoundry
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    description: projectDescription
    displayName: 'Foundry Memo'
  }
}

// ──────────────────────────────────────────
// GPT-5 Model Deployment
// ──────────────────────────────────────────

resource gpt5Deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: aiFoundry
  name: modelName
  sku: {
    capacity: modelCapacity
    name: modelSkuName
  }
  properties: {
    model: {
      name: modelName
      format: modelFormat
      version: modelVersion
    }
  }
}

// ──────────────────────────────────────────
// Azure Container Registry (for hosted agent images)
// ──────────────────────────────────────────

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${baseName}acr${uniqueSuffix}'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// ──────────────────────────────────────────
// Log Analytics Workspace
// ──────────────────────────────────────────

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-logs-${uniqueSuffix}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// ──────────────────────────────────────────
// Application Insights
// ──────────────────────────────────────────

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-insights-${uniqueSuffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ──────────────────────────────────────────
// Cosmos DB (Agent Memory Store)
// ──────────────────────────────────────────

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: '${baseName}-cosmos-${uniqueSuffix}'
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true // Policy-enforced; using app credentials for RBAC instead
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'foundry-memo'
  properties: {
    resource: {
      id: 'foundry-memo'
    }
  }
}

resource memoriesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'learnings'
  properties: {
    resource: {
      id: 'learnings'
      partitionKey: {
        paths: ['/pk']
        kind: 'Hash'
      }
      indexingPolicy: {
        automatic: true
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/*' }
        ]
      }
    }
  }
}

// ──────────────────────────────────────────
// Foundry Project Connections
// ──────────────────────────────────────────

// Cosmos DB connection — lets the hosted agent discover endpoint + app credentials at runtime
// Uses CustomKeys to store Entra app credentials for RBAC auth (Foundry instance identity
// is not supported by Cosmos RBAC, and key auth is disabled by subscription policy)
resource cosmosConnection 'Microsoft.CognitiveServices/accounts/connections@2025-04-01-preview' = {
  parent: aiFoundry
  name: 'cosmos-db'
  properties: {
    category: 'CosmosDb'
    target: cosmosAccount.properties.documentEndpoint
    authType: 'CustomKeys'
    isSharedToAll: true
    credentials: {
      keys: {
        clientId: graphAppClientId
        clientSecret: graphAppClientSecret
        tenantId: subscription().tenantId
      }
    }
    metadata: {
      DatabaseName: cosmosDatabase.name
    }
  }
}

// OAuth connection for MCP toolbox — stores OAuth credentials for identity passthrough
resource mcpOAuthConnection 'Microsoft.CognitiveServices/accounts/connections@2025-04-01-preview' = if (!empty(graphAppClientId)) {
  parent: aiFoundry
  name: 'copilot-search-oauth'
  properties: {
    category: 'RemoteTool'
    target: '${environment().authentication.loginEndpoint}${subscription().tenantId}/oauth2/v2.0'
    authType: 'OAuth2'
    isSharedToAll: true
    credentials: {
      clientId: graphAppClientId
      clientSecret: graphAppClientSecret
      authUrl: '${environment().authentication.loginEndpoint}${subscription().tenantId}/oauth2/v2.0/authorize'
      tenantId: subscription().tenantId
    }
    metadata: {
      Scopes: '${agent365ToolsAppId}/McpServers.CopilotMCP.All ${agent365ToolsAppId}/McpServers.OneDriveSharepoint.All'
    }
  }
}

// Application Insights connection — lets the platform inject APPLICATIONINSIGHTS_CONNECTION_STRING
// into the hosted container and enable telemetry via AddAgentHostTelemetry()
resource appInsightsConnection 'Microsoft.CognitiveServices/accounts/connections@2025-04-01-preview' = {
  parent: aiFoundry
  name: 'app-insights'
  properties: {
    category: 'AppInsights'
    target: appInsights.properties.ConnectionString
    authType: 'ApiKey'
    isSharedToAll: true
    credentials: {
      key: appInsights.properties.ConnectionString
    }
    metadata: {
      ResourceId: appInsights.id
    }
  }
}

// ──────────────────────────────────────────
// Grant ACR Pull to Foundry project managed identity
// ──────────────────────────────────────────

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull built-in role

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, aiProject.id, acrPullRoleId)
  scope: acr
  properties: {
    principalId: aiProject.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalType: 'ServicePrincipal'
  }
}

// ──────────────────────────────────────────
// Grant Cosmos DB data access to Foundry project managed identity
// ──────────────────────────────────────────

// Cosmos DB Built-in Data Contributor (read/write items, no management plane)
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

// Grant Cosmos DB data access to Foundry account managed identity (used by hosted agent containers)
resource cosmosAccountMiRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, aiFoundry.id, cosmosDataContributorRoleId)
  properties: {
    principalId: aiFoundry.identity.principalId
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    scope: cosmosAccount.id
  }
}

// Grant Cosmos DB data access to Foundry project managed identity
resource cosmosRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, aiProject.id, cosmosDataContributorRoleId)
  properties: {
    principalId: aiProject.identity.principalId
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    scope: cosmosAccount.id
  }
}

// Also grant Cosmos data access to the deploying user for local development
@description('Principal ID of the developer for local Cosmos DB access (optional)')
param developerPrincipalId string = ''

resource cosmosDevRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (!empty(developerPrincipalId)) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, developerPrincipalId, cosmosDataContributorRoleId)
  properties: {
    principalId: developerPrincipalId
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    scope: cosmosAccount.id
  }
}

// Grant Foundry User to developer for local agent testing (model access + responses API)
var foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

resource foundryUserDevRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  name: guid(aiFoundry.id, developerPrincipalId, foundryUserRoleId)
  scope: aiFoundry
  properties: {
    principalId: developerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
    principalType: 'User'
  }
}

// ──────────────────────────────────────────
// Outputs
// ──────────────────────────────────────────

output foundryAccountName string = aiFoundry.name
output foundryProjectName string = aiProject.name
output foundryEndpoint string = aiFoundry.properties.endpoint
output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output modelDeploymentName string = gpt5Deployment.name
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output cosmosDatabaseName string = cosmosDatabase.name
output cosmosConnectionName string = cosmosConnection.name
output mcpOAuthConnectionName string = !empty(graphAppClientId) ? mcpOAuthConnection.name : ''
