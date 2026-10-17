// Abastalia cloud V1 (ADR-0008). Deploy into an existing resource group, e.g. rg-abastalia-prod:
//   az group create -n rg-abastalia-prod -l eastus2
//   az deployment group create -g rg-abastalia-prod -f deploy/azure/main.bicep -p deploy/azure/main.parameters.json
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

@description('SQL database edition: Basic (2 GB max, ~US$5/month) or a Standard objective such as S0 (~US$15).')
param databaseSku string = 'Basic'

@description('The database\'s maximum size in GB (Basic allows up to 2; Standard S0 up to 250).')
@minValue(1)
@maxValue(250)
param databaseMaxGigabytes int = 2

@description('Thousands of tokens per minute for the deployment. R12 F: 50K, so a few owners asking at once and the invoice reader do not hit 429s; GlobalStandard gpt-5-nano quotas are far above it (check az cognitiveservices usage list).')
param openAiCapacity int = 50

@description('TK-222, optional: the Azure Communication Services endpoint (https://<name>.communication.azure.com) the API e-mails through with its managed identity. Empty = no e-mail (the default).')
param emailEndpoint string = ''

@description('TK-222: the verified sender of the ACS e-mail domain (DoNotReply@<domain>). Needed with emailEndpoint.')
param emailSender string = ''

@description('TK-241: name of that Communication Services resource in this resource group (created with az, see docs/despliegue-azure.md). The API gets "Communication and Email Service Owner" on it. Empty = no role.')
param emailResourceName string = ''

@description('TK-241: where the Azure Monitor alerts go (5xx, inbox failures, health check). Empty = the alerts exist but notify nobody.')
param alertEmail string = ''

@description('TK-249: Paddle Billing, sandbox or live. Empty = billing off (the default). Set it only after the secrets Billing--Paddle--ApiKey and Billing--Paddle--WebhookSecret exist in the vault (docs/cobro-paddle.md).')
@allowed(['', 'sandbox', 'live'])
param paddleEnvironment string = ''

@description('TK-249: Paddle client-side token (test_… / live_…) for Paddle.js. Not secret.')
param paddleClientToken string = ''

@description('TK-249: name of the cloud license key in the vault (EC P-256, created with az, docs/cobro-paddle.md). Empty = the cloud issues no licenses.')
param licenseKeyName string = ''

@description('TK-246 / R11 B-Low7: CloudCustody (a copy of the stores\' certificates in Key Vault). Off until ADR-0011\'s conditions; while off the API gets no Key Vault certificate role, and the key role only for the license key.')
param allowCloudCustody bool = false

@description('TK-267: the site and panel\'s own domain (e.g. abastalia.com, bound to the Static Web App with its www). Empty = only the Static Web App\'s default host.')
param panelDomain string = ''

var suffix = uniqueString(resourceGroup().id)
var name = 'abastalia-${env}'
var tags = { app: 'abastalia', env: env }

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
  name: 'kv-abastalia-${env}-${take(suffix, 6)}'
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

// Basic (5 DTU, 2 GB, ~US$5/month fixed): serverless never paused because the API polls it (measured 17/10/2026: ~US$310/month), and the owner
// must stay under the US$150 monthly credit. Move up to Standard S0 (~US$15) online if DTU stays above 80 %.
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'abastalia'
  location: sqlLocation
  tags: tags
  sku: { name: databaseSku, tier: databaseSku == 'Basic' ? 'Basic' : 'Standard' }
  properties: {
    maxSizeBytes: databaseMaxGigabytes * 1024 * 1024 * 1024
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
    disableLocalAuth: true // R11 B-Low7: no API keys; the API signs in with its managed identity (apiUsesOpenAi)
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


// TK-207: cold archive of the signed e-CF (10 years, DGII). The API moves XML older than Archive:HotDays (7) out of SQL into here, as Cool.
// Only the API's managed identity reaches it (no account keys, no public blobs).
resource archive 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stabastalia${env}${take(suffix, 6)}'
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

// TK-238: the stores' backups, as each store encrypted them with the owner's passphrase (the cloud cannot read them). Private, no immutability:
// the API keeps 7 daily + 4 weekly per store and deletes the rest (CloudBackup:KeepDaily / KeepWeekly).
resource backupContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: archiveBlobs
  name: 'store-backups'
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
        {
          // TK-238: the API prunes the backups itself (the oldest weekly one is ~5 weeks old); this only catches what it missed.
          name: 'store-backups-safety-net'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['store-backups/'] }
            // R12 #8: a year, not 60 days: a store that stopped uploading (a stolen PC) keeps its last backups; the API never prunes the newest.
            actions: { baseBlob: { delete: { daysAfterCreationGreaterThan: 400 } } }
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
  // R12 B: exactly one instance, no autoscale: several in-memory guards (docs/despliegue-azure.md "Una sola instancia") assume it.
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
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

// R11 B-Low7: no publishing user names and passwords (FTP, SCM/Kudu). Deploys go with Entra ID (az webapp deploy, docs/despliegue-azure.md).
resource apiNoScmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: api
  name: 'scm'
  properties: { allow: false }
}

resource apiNoFtpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: api
  name: 'ftp'
  properties: { allow: false }
}

// R11 B-Low7: Cognitive Services OpenAI User for the API's managed identity (the resource takes no keys). No Key Vault secret is read any more.
resource apiUsesOpenAi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, api.id, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: openAi
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
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
// non-exportable keys (Key Vault Crypto User): the CloudCustody mode. TK-246: that mode stays off (allowCloudCustody false) until the conditions of
// ADR-0011; the default is StoreSigned (no certificate in the cloud). R11 B-Low7: so the roles exist only with allowCloudCustody. The signer refuses
// in code anything but ARECF and ACECF.
resource apiManagesCertificates 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (allowCloudCustody) {
  name: guid(vault.id, api.id, 'a4417e6f-fecd-4de8-b567-7b0420556985')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a4417e6f-fecd-4de8-b567-7b0420556985')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apiSignsWithKeys 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (allowCloudCustody) {
  name: guid(vault.id, api.id, '12338af0-0e69-4776-bea7-57ae8d297424')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// TK-249, R11 B-Low7: Key Vault Crypto User on the license key only (not the whole vault), when the cloud issues licenses.
resource licenseKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = if (!empty(licenseKeyName)) {
  parent: vault
  name: empty(licenseKeyName) ? 'none' : licenseKeyName
}

resource apiSignsLicenses 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(licenseKeyName)) {
  name: guid(vault.id, api.id, licenseKeyName, '12338af0-0e69-4776-bea7-57ae8d297424')
  scope: licenseKey
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// TK-249, R11 B-Low7: Key Vault Secrets User only while Paddle is on (its two secrets are Key Vault references); nothing else reads secrets.
resource apiReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(paddleEnvironment)) {
  name: guid(vault.id, api.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Set after the roles exist: App Service resolves Key Vault references once and caches a denial for up to a day.
resource apiSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: api
  name: 'appsettings'
  dependsOn: [apiUsesOpenAi, apiWritesArchive, apiManagesCertificates, apiSignsWithKeys, apiSignsLicenses, apiReadsSecrets]
  properties: union({
    ASPNETCORE_ENVIRONMENT: 'Production'
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    Database__Provider: 'SqlServer'
    Database__ConnectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;Encrypt=True;Connect Timeout=120'
    AzureOpenAI__Endpoint: openAi.properties.endpoint
    AzureOpenAI__Deployment: chatDeployment.name
    AzureOpenAI__ApiVersion: openAiApiVersion
    Panel__Origins__0: 'https://${panel.properties.defaultHostname}'
    Archive__Blob__ServiceUri: archive.properties.primaryEndpoints.blob
    Archive__Blob__Container: archiveContainer.name
    CloudBackup__Blob__Container: backupContainer.name // TK-238: same account (Archive__Blob__ServiceUri), same managed identity role
    Fe__Vault: 'KeyVault'
    Fe__KeyVaultUri: vault.properties.vaultUri
    Fe__DefaultSigningMode: 'StoreSigned' // TK-246 (ADR-0011)
    Fe__AllowCloudCustody: string(allowCloudCustody)
  }, empty(panelDomain) ? {} : {
    // TK-267: CORS for the own domain and its www; e-mail links (invites, password reset) point to it.
    Panel__Origins__1: 'https://${panelDomain}'
    Panel__Origins__2: 'https://www.${panelDomain}'
    Panel__Url: 'https://${panelDomain}'
  }, empty(emailEndpoint) ? {} : {
    // TK-222: alerts and invites by e-mail. Off (logged only) until both params are set; see docs/despliegue-azure.md.
    Email__Provider: 'AzureCommunicationServices'
    Email__Endpoint: emailEndpoint
    Email__Sender: emailSender
  }, empty(paddleEnvironment) ? {} : {
    // TK-249: the secrets are created by hand in the vault (never in this repo or its parameters) and read as Key Vault references.
    // Products, prices and limits come from Paddle itself (catalog sync), not from here.
    Billing__Paddle__Environment: paddleEnvironment
    Billing__Paddle__ClientToken: paddleClientToken
    Billing__Paddle__ApiKey: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=Billing--Paddle--ApiKey)'
    Billing__Paddle__WebhookSecret: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=Billing--Paddle--WebhookSecret)'
  }, empty(licenseKeyName) ? {} : {
    // TK-249: the API signs store licenses with this key (Key Vault Crypto User above); the private half never leaves the vault.
    Billing__License__KeyId: '${vault.properties.vaultUri}keys/${licenseKeyName}'
  })
}

// TK-241: the API e-mails through ACS with its managed identity.
resource emailService 'Microsoft.Communication/communicationServices@2023-04-01' existing = if (!empty(emailResourceName)) {
  name: empty(emailResourceName) ? 'none' : emailResourceName
}

resource apiSendsEmail 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(emailResourceName)) {
  name: guid(resourceGroup().id, emailResourceName, api.id, '09976791-48a7-449e-bb21-39d1a415f350')
  scope: emailService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '09976791-48a7-449e-bb21-39d1a415f350')
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// TK-241: alerts. Only requests reach Application Insights (Serilog writes to the console), so the inbox alert reads the App Service console
// logs, sent to the Log Analytics workspace here.
resource apiConsoleLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'console-to-log-analytics'
  scope: api
  properties: {
    workspaceId: logs.id
    logs: [{ category: 'AppServiceConsoleLogs', enabled: true }]
  }
}

resource alertGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: 'ag-${name}'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'abastalia'
    enabled: true
    emailReceivers: [{ name: 'owner', emailAddress: alertEmail, useCommonAlertSchema: true }]
  }
}

var alertActions = empty(alertEmail) ? [] : [{ actionGroupId: alertGroup.id }]

resource alert5xx 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-${name}-5xx'
  location: 'global'
  tags: tags
  properties: {
    description: 'The API answered more than 5 server errors (5xx) in 15 minutes.'
    severity: 1
    enabled: true
    scopes: [api.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{ name: 'http5xx', criterionType: 'StaticThresholdCriterion', metricName: 'Http5xx', operator: 'GreaterThan', threshold: 5, timeAggregation: 'Total' }]
    }
    actions: alertActions
  }
}

resource alertHealth 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-${name}-health'
  location: 'global'
  tags: tags
  properties: {
    description: 'The App Service health check (/health) is failing.'
    severity: 1
    enabled: true
    scopes: [api.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{ name: 'health', criterionType: 'StaticThresholdCriterion', metricName: 'HealthCheckStatus', operator: 'LessThan', threshold: 100, timeAggregation: 'Average' }]
    }
    actions: alertActions
  }
}

// An event the inbox could not apply (it retries up to InboxApplier.MaxAttempts, then stays dead until a fix) or a failed applier run.
resource alertInbox 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-${name}-inbox'
  location: location
  tags: tags
  properties: {
    description: 'Store events the cloud inbox could not apply (or a failed applier run). The panel shows them in Tiendas.'
    severity: 2
    enabled: true
    scopes: [logs.id]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'AppServiceConsoleLogs | where ResultDescription has "could not be applied" or ResultDescription has "Inbox applier run failed" or ResultDescription has "Ingest cap reached"'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: empty(alertEmail) ? [] : [alertGroup.id] }
  }
  dependsOn: [apiConsoleLogs]
}

// TK-286: a business passed the fair use of receiver signatures (TK-248, FeSigner.MeterAsync). Fair use never refuses: staff review its plan.
// The warning is logged once per business per month (at the first signature over), so each alert is one business to look at.
resource alertFairUse 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-${name}-fair-use'
  location: location
  tags: tags
  properties: {
    description: 'A business passed its fair use of receiver signatures this month (still signing). Review its plan; the tenant id is in the log line.'
    severity: 3
    enabled: true
    scopes: [logs.id]
    evaluationFrequency: 'PT1H'
    windowSize: 'PT1H'
    criteria: {
      allOf: [
        {
          query: 'AppServiceConsoleLogs | where ResultDescription has "passed its fair use of receiver signatures"'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: empty(alertEmail) ? [] : [alertGroup.id] }
  }
  dependsOn: [apiConsoleLogs]
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
output apiName string = api.name
output apiPrincipalId string = api.identity.principalId
output panelUrl string = 'https://${panel.properties.defaultHostname}'
output panelName string = panel.name
output sqlServer string = sqlServer.properties.fullyQualifiedDomainName
output keyVault string = vault.name
output archiveStorage string = archive.name
