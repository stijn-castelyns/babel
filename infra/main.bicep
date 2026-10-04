// The cheapest Azure home for the harness daemon, which also serves the web app (PWA).
//
// One small burstable Linux VM with no public IP, reached over Tailscale (Tailscale Serve gives it a stable *.ts.net
// name with a real certificate, which passkeys need). A storage account carries each release from GitHub Actions to the
// VM. Deployed by .github/workflows/azure-infra.yml; the VM itself is set up by infra/vm/install.sh on every deploy, so
// nothing here has to change when the machine setup does (custom data cannot change after a VM is created).

targetScope = 'resourceGroup'

@description('Prefix for resource names, and the VM host name.')
@minLength(3)
@maxLength(15)
param name string = 'harness'

param location string = resourceGroup().location

@description('arm64 (Ampere) is the cheapest; x64 for regions without the Bpsv2 sizes.')
@allowed(['arm64', 'x64'])
param architecture string = 'arm64'

@description('2 vCPUs and 1 GiB by default (about $6/month); Standard_B2pls_v2 / Standard_B2als_v2 have 4 GiB for agents that build code.')
param vmSize string = architecture == 'arm64' ? 'Standard_B2pts_v2' : 'Standard_B2ats_v2'

@description('Standard_LRS is a standard HDD (cheapest); StandardSSD_LRS is faster for a little more.')
@allowed(['Standard_LRS', 'StandardSSD_LRS', 'Premium_LRS'])
param osDiskType string = 'Standard_LRS'

@description('32 GiB is the smallest billing tier that fits the Ubuntu image.')
@minValue(30)
param osDiskSizeGB int = 32

param adminUsername string = 'azureuser'

@description('SSH public key for the admin user. SSH is only reachable over the tailnet (or the VNet).')
param sshPublicKey string

@description('A public IP (about $3.65/month) only for outbound traffic; inbound stays closed. Leave off unless the subnet\'s default outbound access is unavailable.')
param publicIp bool = false

var compact = toLower(replace(name, '-', ''))
var storageName = take('${compact}${uniqueString(resourceGroup().id)}', 24)
var image = {
  arm64: { publisher: 'Canonical', offer: 'ubuntu-24_04-lts', sku: 'server-arm64', version: 'latest' }
  x64: { publisher: 'Canonical', offer: 'ubuntu-24_04-lts', sku: 'server', version: 'latest' }
}

// Nothing is allowed in from the internet; Tailscale only needs outbound connections.
resource nsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: '${name}-nsg'
  location: location
  properties: { securityRules: [] }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${name}-vnet'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.40.0.0/24'] }
    subnets: [
      {
        name: 'default'
        properties: {
          addressPrefix: '10.40.0.0/26'
          networkSecurityGroup: { id: nsg.id }
          // Free outbound internet without a public IP or a NAT gateway (about $32/month).
          defaultOutboundAccess: true
        }
      }
    ]
  }
}

resource pip 'Microsoft.Network/publicIPAddresses@2024-05-01' = if (publicIp) {
  name: '${name}-ip'
  location: location
  sku: { name: 'Standard' }
  properties: { publicIPAllocationMethod: 'Static' }
}

resource nic 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: '${name}-nic'
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig'
        properties: {
          subnet: { id: vnet.properties.subnets[0].id }
          privateIPAllocationMethod: 'Dynamic'
          publicIPAddress: publicIp ? { id: pip.id } : null
        }
      }
    ]
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2024-11-01' = {
  name: '${name}-vm'
  location: location
  // Lets the daemon use Azure OpenAI with `auth: { type: entra }` once this identity has a role on the resource.
  identity: { type: 'SystemAssigned' }
  properties: {
    hardwareProfile: { vmSize: vmSize }
    osProfile: {
      computerName: name
      adminUsername: adminUsername
      linuxConfiguration: {
        disablePasswordAuthentication: true
        ssh: { publicKeys: [{ path: '/home/${adminUsername}/.ssh/authorized_keys', keyData: sshPublicKey }] }
      }
    }
    storageProfile: {
      imageReference: image[architecture]
      osDisk: {
        createOption: 'FromImage'
        diskSizeGB: osDiskSizeGB
        managedDisk: { storageAccountType: osDiskType }
        deleteOption: 'Delete'
      }
    }
    networkProfile: {
      networkInterfaces: [{ id: nic.id, properties: { deleteOption: 'Delete' } }]
    }
    // Trusted launch costs nothing, but the Arm64 sizes do not support it.
    securityProfile: architecture == 'x64'
      ? { securityType: 'TrustedLaunch', uefiSettings: { secureBootEnabled: true, vTpmEnabled: true } }
      : null
    // Managed boot diagnostics (cents a month) make the serial console work on a VM without a public IP.
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}

// Releases travel from GitHub Actions to the VM through here, fetched with a short-lived read-only SAS.
resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  parent: storage
  name: 'default'
}

resource releases 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobs
  name: 'releases'
  properties: { publicAccess: 'None' }
}

// Old releases are only needed while a deploy runs.
resource cleanup 'Microsoft.Storage/storageAccounts/managementPolicies@2024-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-old-releases'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['releases/'] }
            actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 7 } } }
          }
        }
      ]
    }
  }
}

output vmName string = vm.name
output vmPrincipalId string = vm.identity.principalId
output storageAccountName string = storage.name
output releaseContainer string = releases.name
output runtimeIdentifier string = architecture == 'arm64' ? 'linux-arm64' : 'linux-x64'
