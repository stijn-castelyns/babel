# Hosting on Azure

The web app (PWA) is part of the daemon: it is embedded in the `harness` binary and served from the same origin as the
API, its `SameSite=Strict` cookie and its passkeys. So hosting the PWA means hosting the daemon, and the daemon needs
a persistent disk (sessions, SQLite), has to stay up (schedules, Web Push) and needs user namespaces for bubblewrap
sandboxes. That rules out static hosting (Static Web Apps), and App Service and Container Apps (no user namespaces;
scale-to-zero would stop triggers). The cheapest fit is one small burstable Linux VM:

- **No public IP and no open ports.** The VM is reached over [Tailscale](https://tailscale.com): Tailscale Serve gives it
  `https://harness.<tailnet>.ts.net` with a real certificate (which passkeys need) and forwards to the daemon's
  `http://127.0.0.1:7443` listener (`listeners.behindProxy: true`). Only devices on your tailnet can reach it, so your
  phone needs the Tailscale app.
- **Deploys without SSH.** GitHub Actions signs in to Azure with OIDC (no stored credentials), uploads the release to a
  storage account and runs [`vm/install.sh`](vm/install.sh) on the VM through `az vm run-command`, which sets up the
  machine (packages, swap, an AppArmor profile for `bwrap`, the `harness` user, Tailscale, the systemd unit) and
  installs the release.

| Resource | Default | About, per month |
| --- | --- | --- |
| VM | `Standard_B2pts_v2`: Arm64, 2 vCPUs (burstable), 1 GiB, Ubuntu 24.04 | $6–7 |
| OS disk | 32 GiB standard HDD (S4) | $1.5–2 |
| Storage account | a few release blobs, deleted after 7 days | < $0.10 |
| Boot diagnostics | managed (for the serial console) | cents |
| Public IP / NAT gateway | none | $0 |
| Tailscale | Personal plan | $0 |

That is about **$8–9 a month** at pay-as-you-go list prices (check the [pricing
calculator](https://azure.microsoft.com/pricing/calculator/) for your region). On an [Azure free
account](https://azure.microsoft.com/free/) the first 12 months include 750 hours a month of `B2pts_v2` and two 64 GiB P6
SSDs, so set `AZURE_OS_DISK_TYPE=Premium_LRS` and `AZURE_OS_DISK_SIZE_GB=64` and the machine costs nothing that year.

1 GiB is enough for the daemon and a remote model (Azure OpenAI); `install.sh` adds 2 GiB of swap. Agents that build
code in their sandbox want `Standard_B2pls_v2` (Arm64, 4 GiB) or `Standard_B2als_v2` (x64, 4 GiB), about four times the
price. Where the Arm64 sizes are not offered, set `AZURE_VM_ARCHITECTURE=x64` (`Standard_B2ats_v2`, slightly dearer).

## One-time setup

**1. Tailscale.** In the [admin console](https://login.tailscale.com/admin/dns) turn on MagicDNS and HTTPS certificates.
Under Settings → Keys, generate an auth key (not ephemeral, so the VM stays on the tailnet across restarts; pre-approved
if you use device approval). It is used once, when the VM first joins.

**2. Azure.** A resource group and an identity GitHub Actions can sign in as, with Contributor on that group only
(enough to deploy the template, fetch the storage key and use run-command):

```bash
rg=harness
az group create --name $rg --location westeurope
app=$(az ad app create --display-name harness-github --query appId --output tsv)
az ad sp create --id $app
az ad app federated-credential create --id $app --parameters '{
  "name": "github-environment-azure",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<owner>/<repo>:environment:azure",
  "audiences": ["api://AzureADTokenExchange"]
}'
az role assignment create --assignee $app --role Contributor --scope "$(az group show --name $rg --query id --output tsv)"
```

**3. GitHub.** The workflows run in the `azure` environment (created on first use; add protection rules there to limit
deploys to `main` or require a reviewer). Set these repository or environment variables and the one secret:

| Name | Kind | Value |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | variable | `$app` from step 2 |
| `AZURE_TENANT_ID` | variable | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | variable | `az account show --query id -o tsv` |
| `AZURE_RESOURCE_GROUP` | variable | `harness` |
| `VM_SSH_PUBLIC_KEY` | variable | your SSH public key (SSH works over the tailnet only); it cannot change once the VM exists |
| `TAILSCALE_AUTHKEY` | secret | the auth key from step 1 |
| `TAILSCALE_HOSTNAME` | variable, optional | the machine's tailnet name, default `harness` |
| `AZURE_VM_ARCHITECTURE`, `AZURE_VM_SIZE`, `AZURE_OS_DISK_TYPE`, `AZURE_OS_DISK_SIZE_GB`, `AZURE_VM_PUBLIC_IP` | variables, optional | overrides of the [template](main.bicep)'s parameters |

**4. Deploy.** Run the *Azure infrastructure* workflow (Actions → Run workflow). When it succeeds, *Deploy to Azure*
runs on its own, and its summary links to `https://harness.<tailnet>.ts.net/`. In the Tailscale admin console, disable
key expiry for the new machine, or it drops off the tailnet when its key expires (180 days by default).

**5. Create the owner.** Get a one-time setup code and open the link it belongs to:

```bash
ssh azureuser@harness sudo -u harness -H harness admin setup
# then open https://harness.<tailnet>.ts.net/setup?code=<code>
```

Without SSH: `az vm run-command invoke -g harness -n harness-vm --command-id RunShellScript --scripts "runuser -u harness
-- env HOME=/var/lib/harness harness admin setup"`. The deploy never prints the code, so it stays out of workflow logs.

**6. Point it at a model.** The first install writes the sample config (`harness init`) with the listener settings
filled in; from then on `/var/lib/harness/.harness/config.yaml` is yours and deploys leave it alone. For Azure OpenAI
without a key, give the VM's managed identity (the `vmPrincipalId` output) access and use `auth: { type: entra }`:

```bash
az role assignment create --role "Cognitive Services OpenAI User" \
  --assignee "$(az deployment group show -g harness -n harness --query properties.outputs.vmPrincipalId.value -o tsv)" \
  --scope <resource id of the Azure OpenAI account>
```

Then `sudo systemctl restart harness` (model profiles hot-reload, but listener changes need a restart).

## Day to day

- Every push to `main` that touches `src/`, `infra/vm/` or the build props tests, publishes and installs. Installing
  restarts the daemon: runs that are executing are marked failed, runs parked on an approval carry on. To deploy only
  by hand, remove the `push:` trigger from `azure-deploy.yml`.
- Changes to `main.bicep` run *Azure infrastructure*, then a deploy. A new VM size reboots the VM (or fails if the
  current hardware does not offer it; deallocate it first); a disk can grow but not shrink.
- On the VM: `sudo -u harness -H harness status`, `journalctl -u harness -f`, `sudo systemctl restart harness`.
- From a laptop on the tailnet: `harness login https://harness.<tailnet>.ts.net`, approve the pairing in the web app,
  then `harness --remote https://harness.<tailnet>.ts.net …`.
- Removing everything: `az group delete --name harness`, and the machine in the Tailscale admin console.

The template keeps outbound traffic free by setting `defaultOutboundAccess` on the subnet instead of paying for a NAT
gateway. If your subscription refuses that, set `AZURE_VM_PUBLIC_IP=true`: a static public IP for outbound traffic,
with inbound still closed by the network security group.
