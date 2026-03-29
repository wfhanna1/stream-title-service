@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Service Bus connection string')
@secure()
param serviceBusConnectionString string

@description('Key Vault URI for the shared Key Vault')
param keyVaultUri string

@description('Blob Storage connection string for YouTube token storage')
@secure()
param blobStorageConnectionString string

@description('Application Insights connection string')
param appInsightsConnectionString string

@description('Azure Communication Services connection string')
@secure()
param acsConnectionString string

@description('ACS sender email address')
param acsSender string

@description('ACS recipient email addresses (comma-separated)')
param acsRecipients string

@description('Name of the shared Key Vault (for role assignment)')
param sharedKeyVaultName string

@description('Name of the shared Storage Account (for role assignment)')
param sharedStorageAccountName string

// Naming suffix based on resource group ID
var suffix = uniqueString(resourceGroup().id)
var appName = 'stream-title-svc-${suffix}'
var storageAccountName = 'strtitlesvc${take(suffix, 10)}'

// Role definition IDs
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'

// Storage Account for Functions runtime
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// App Service Plan - Flex Consumption (FC1, Linux)
resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${appName}-plan'
  location: location
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp'
  properties: {
    reserved: true
  }
}

// Function App
resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'SERVICE_BUS_CONNECTION'
          value: serviceBusConnectionString
        }
        {
          name: 'SERVICE_BUS_TOPIC'
          value: 'stream-title'
        }
        {
          name: 'SERVICE_BUS_SUBSCRIPTION'
          value: 'stream-title-service'
        }
        {
          name: 'KEY_VAULT_URI'
          value: keyVaultUri
        }
        {
          name: 'BLOB_STORAGE_CONNECTION'
          value: blobStorageConnectionString
        }
        {
          name: 'ACS_CONNECTION_STRING'
          value: acsConnectionString
        }
        {
          name: 'ACS_SENDER'
          value: acsSender
        }
        {
          name: 'ACS_RECIPIENTS'
          value: acsRecipients
        }
        {
          name: 'STALENESS_THRESHOLD_SECONDS'
          value: '90'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}deployments'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 100
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '9.0'
      }
    }
  }
}

// Reference to shared Key Vault
resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-02-01' existing = {
  name: sharedKeyVaultName
}

// Key Vault Secrets User role assignment for Function App identity
resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sharedKeyVault.id, functionApp.id, keyVaultSecretsUserRoleId)
  scope: sharedKeyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Reference to shared Storage Account
resource sharedStorageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: sharedStorageAccountName
}

// Storage Blob Data Reader role assignment for Function App identity on shared storage
resource storageBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sharedStorageAccount.id, functionApp.id, storageBlobDataReaderRoleId)
  scope: sharedStorageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Blob Data Owner role assignment for Function App identity on its OWN storage
// Required for identity-based AzureWebJobsStorage (Flex Consumption)
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
resource functionStorageBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageBlobDataOwnerRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output functionAppName string = functionApp.name
output functionAppPrincipalId string = functionApp.identity.principalId
output functionAppDefaultHostname string = functionApp.properties.defaultHostName
