# ACA Sandboxes baseline (OpenTofu)

Creates a baseline for [Azure Container Apps Sandboxes](https://learn.microsoft.com/en-us/azure/container-apps/sandboxes-overview) in an EU region (default: `swedencentral`).

| Resource | Purpose |
|---|---|
| Resource group | Holds everything |
| VNet + subnet delegated to `Microsoft.App/environments` + NSG | Private networking for sandboxes (toggle: `enable_vnet_integration`) |
| `Microsoft.App/sandboxGroups` (API `2026-07-01`, via azapi) | The sandbox group |
| `Microsoft.App/sandboxGroups/vnetConnections/default` | Binds the group to the subnet (the subnet can't be changed afterwards) |
| ACA managed environment (Consumption workload profile) + Log Analytics + its own delegated subnet | "Cluster" for regular container apps/jobs (toggle: `enable_managed_environment`) |
| Role assignment `Container Apps SandboxGroup Data Owner` | Data-plane access for you and any extra principals |

Sandboxes themselves (disk images, sandboxes, egress policies, snapshots) are **data-plane** objects. Create them with the `aca` CLI or the SDK, not ARM.

## Usage

```sh
az login
cd infra
cp terraform.tfvars.example terraform.tfvars   # optional
cp backend.hcl.example backend.hcl             # fill in the state storage account
tofu init -backend-config=backend.hcl
tofu plan -out tfplan
tofu apply tfplan
```

Then try a sandbox:

```sh
# install: irm https://aka.ms/aca-cli-install-ps | iex   (PowerShell)
aca sandboxgroup get --name <sandbox_group_name> -g <resource_group_name>
aca sandbox create --disk ubuntu --label name=hello
aca sandbox exec -l name=hello -c "echo hi"
aca sandbox delete -l name=hello --yes
```

Role assignments can take about a minute to propagate.

## Notes

- `schema_validation_enabled = false` is set on the azapi resources because azapi 2.13 has no embedded schema for `sandboxGroups` yet. ARM still validates the request.
- Sandbox groups don't need a managed environment. By default the environment is created next to the group but not linked to it. Set `link_sandbox_group_to_environment = true` to set `properties.environmentId`. Once set, the link can't be changed or removed, so changing it replaces the sandbox group.
- The sandbox subnet and the environment subnet are separate. Each must be delegated to `Microsoft.App/environments` and can serve only one consumer.
- State is kept in Azure Blob Storage (`azurerm` backend, Entra ID auth, no shared keys). The state account has blob versioning and 30-day soft delete, and you need `Storage Blob Data Contributor` on it.
