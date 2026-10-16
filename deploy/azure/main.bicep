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

@description('Azure SQL region. Some regions stop accepting new servers (eastus2 did in 2026-10); keep it close to the API.')
param sqlLocation string = location

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
    // TK-151: the stores' e-CF certificates live here; a withdrawn consent leaves the key soft-deleted and unusable for 30 days, never purgeable
    // by a stolen credential. Irreversible once deployed.
    enablePurgeProtection: true
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${name}-${take(uniqueString(resourceGroup().id, sqlLocation), 6)}' // region in the hash: ARM keeps a failed server's name tied to its old region
  location: sqlLocation
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
  location: sqlLocation
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

// TK-207: cold archive of the signed e-CF (10 years, DGII). The API moves XML older than Archive:HotDays (90) out of SQL into here, as Cool.
// Only the API's managed identity reaches it (no account keys, no public blobs).
resource archive 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'sttoki${env}${take(suffix, 6)}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' } // shortcut: one region; Standard_GRS doubles the (tiny) storage price if the DGII copy must survive a region loss
  properties: {
    accessTier: 'Cool'
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource archiveBlobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: archive
  name: 'default'
}

resource archiveContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: archiveBlobs
  name: 'ecf-archive'
  properties: { publicAccess: 'None' }
}

// Write once: no blob can be changed or deleted for 10 years from its upload. Free. Left unlocked on purpose: locking is permanent
// (az storage container immutability-policy lock); do it once the owner agrees, see docs/despliegue-azure.md.
resource archiveRetention 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = {
  parent: archiveContainer
  name: 'default'
  properties: {
    immutabilityPeriodSinceCreationInDays: 3653
    allowProtectedAppendWrites: false
  }
}

// Cool → Archive after 180 days (Archive's minimum stay); a document brought back stays Cool for 7 days before it goes back. Deleted after 10 years.
resource archiveLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: archive
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'ecf-archive-tiering'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['ecf-archive/'] }
            actions: {
              baseBlob: {
                tierToArchive: { daysAfterModificationGreaterThan: 180, daysAfterLastTierChangeGreaterThan: 7 }
                delete: { daysAfterCreationGreaterThan: 3660 }
              }
            }
          }
        }
      ]
    }
  }
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

// Storage Blob Data Contributor for the API on the archive (upload, read, bring back from Archive; the retention policy blocks changes).
resource apiWritesArchive 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(archive.id, api.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: archive
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// TK-151: the API imports and deletes the stores' certificates (Key Vault Certificates Officer) and signs receiver acknowledgements with their
// non-exportable keys (Key Vault Crypto User). The cloud signer refuses in code anything but ARECF, ACECF and seeds.
resource apiManagesCertificates 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, api.id, 'a4417e6f-fecd-4de8-b567-7b0420556985')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a4417e6f-fecd-4de8-b567-7b0420556985')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apiSignsWithKeys 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, api.id, '12338af0-0e69-4776-bea7-57ae8d297424')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Set after the Key Vault role exists: App Service resolves Key Vault references once and caches a denial for up to a day.
resource apiSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: api
  name: 'appsettings'
  dependsOn: [apiReadsSecrets, apiWritesArchive, apiManagesCertificates, apiSignsWithKeys]
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    Database__Provider: 'SqlServer'
    Database__ConnectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;Encrypt=True;Connect Timeout=120'
    AzureOpenAI__Endpoint: openAi.properties.endpoint
    AzureOpenAI__Deployment: chatDeployment.name
    AzureOpenAI__ApiVersion: openAiApiVersion
    AzureOpenAI__ApiKey: '@Microsoft.KeyVault(SecretUri=${openAiKey.properties.secretUri})'
    Panel__Origins__0: 'https://${panel.properties.defaultHostname}'
    Archive__Blob__ServiceUri: archive.properties.primaryEndpoints.blob
    Archive__Blob__Container: archiveContainer.name
    Fe__Vault: 'KeyVault'
    Fe__KeyVaultUri: vault.properties.vaultUri
  }
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
output apiName string = api.name
output apiPrincipalId string = api.identity.principalId
output panelUrl string = 'https://${panel.properties.defaultHostname}'
output panelName string = panel.name
output sqlServer string = sqlServer.properties.fullyQualifiedDomainName
output keyVault string = vault.name
output archiveStorage string = archive.name
