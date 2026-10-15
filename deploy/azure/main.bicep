// Toki cloud V1 (ADR-0008). Deploy into an existing resource group, e.g. rg-toki-prod:
//   az group create -n rg-toki-prod -l eastus2
//   az deployment group create -g rg-toki-prod -f deploy/azure/main.bicep -p deploy/azure/main.parameters.json
// See docs/despliegue-azure.md.

@description('Environment name, used in resource names.')
param env string = 'prod'

@description('Region for everything except the Static Web App.')
param location string = 'eastus2'

@description('Static Web Apps exist in a few regions only (eastus2, centralus, westus2, westeurope, eastasia).')
param staticWebAppLocation string = 'eastus2'

@description('Entra ID login (user or group name) that administers Azure SQL. SQL logins are disabled.')
param sqlAdminLogin string

@description('Object id of that Entra user or group.')
param sqlAdminObjectId string

@description('Azure OpenAI region. The model must be available there with the chosen SKU.')
param openAiLocation string = location

// Pick the cheapest chat model with function calling available in the region:
//   az cognitiveservices model list -l eastus2 --query "[?model.name=='gpt-5-nano']" -o table
@description('Chat model name (cheapest one with function calling in the region).')
param openAiModelName string = 'gpt-5-nano'

@description('Model version, from az cognitiveservices model list.')
param openAiModelVersion string = '2025-08-07'

@description('Chat completions api-version. GPT-5 models need 2025-04-01-preview or newer.')
param openAiApiVersion string = '2025-04-01-preview'

@description('Deployment SKU: GlobalStandard is the cheapest pay-per-token option where available.')
param openAiDeploymentSku string = 'GlobalStandard'

@description('Thousands of tokens per minute for the deployment.')
param openAiCapacity int = 10

var suffix = uniqueString(resourceGroup().id)
var name = 'toki-${env}'
var tags = { app: 'toki', env: env }

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-toki-${env}-${take(suffix, 6)}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${name}-${take(suffix, 6)}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      principalType: 'User'
    }
  }
}

// App Service reaches the server over its public endpoint (no VNet on B1).
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// Serverless, smallest vCore, pauses after 60 minutes without connections (the API never polls it while idle).
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'toki'
  location: location
  tags: tags
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 1 }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368 // 32 GB
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: 'oai-${name}-${take(suffix, 6)}'
  location: openAiLocation
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: 'oai-${name}-${take(suffix, 6)}'
    publicNetworkAccess: 'Enabled'
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: 'chat'
  sku: { name: openAiDeploymentSku, capacity: openAiCapacity }
  properties: {
    model: { format: 'OpenAI', name: openAiModelName, version: openAiModelVersion }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

// shortcut: the app reads an API key from Key Vault; switch to its managed identity ("Cognitive Services OpenAI User") when the code takes Entra tokens.
resource openAiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'AzureOpenAI--ApiKey'
  properties: { value: openAi.listKeys().key1 }
}

resource panel 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'stapp-${name}'
  location: staticWebAppLocation
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${name}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic' }
  properties: { reserved: true }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: 'app-${name}-${take(suffix, 6)}'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: 'SystemAssigned'
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health' // liveness only: it never touches the database, so the database can pause
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'ApplicationInsightsAgent_EXTENSION_VERSION', value: '~3' }
        { name: 'Database__Provider', value: 'SqlServer' }
        { name: 'Database__ConnectionString', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;Encrypt=True;Connect Timeout=120' }
        { name: 'AzureOpenAI__Endpoint', value: openAi.properties.endpoint }
        { name: 'AzureOpenAI__Deployment', value: chatDeployment.name }
        { name: 'AzureOpenAI__ApiVersion', value: openAiApiVersion }
        { name: 'AzureOpenAI__ApiKey', value: '@Microsoft.KeyVault(SecretUri=${openAiKey.properties.secretUri})' }
        { name: 'Panel__Origins__0', value: 'https://${panel.properties.defaultHostname}' }
      ]
    }
  }
}

// Key Vault Secrets User for the API's managed identity (Key Vault references).
resource apiReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, api.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
output apiName string = api.name
output apiPrincipalId string = api.identity.principalId
output panelUrl string = 'https://${panel.properties.defaultHostname}'
output panelName string = panel.name
output sqlServer string = sqlServer.properties.fullyQualifiedDomainName
output keyVault string = vault.name
