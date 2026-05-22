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
