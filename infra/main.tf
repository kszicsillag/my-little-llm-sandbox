data "azurerm_client_config" "current" {}

locals {
  suffix = random_string.suffix.result
  base   = "${var.name_prefix}-${var.environment}"

  # Sandbox group name rules: 1-32 chars, [A-Za-z0-9-], must not start with '.' or '-'.
  sandbox_group_name = substr("sbg-${local.base}-${local.suffix}", 0, 32)

  tags = merge({
    workload    = "aca-sandboxes"
    environment = var.environment
    managed_by  = "opentofu"
  }, var.tags)

  # Built-in role "Container Apps SandboxGroup Data Owner":
  # data-plane access to create / exec / delete sandboxes in the group.
  sandbox_data_owner_role_id = "c24cf47c-5077-412d-a19c-45202126392c"

  link_environment = var.enable_managed_environment && var.link_sandbox_group_to_environment

  data_owner_principals = merge(
    var.grant_data_owner_to_current_principal ? {
      current = {
        object_id = data.azurerm_client_config.current.object_id
        type      = null # let Azure resolve User vs. ServicePrincipal
      }
    } : {},
    var.sandbox_data_owner_principals,
  )
}

resource "random_string" "suffix" {
  length  = 4
  upper   = false
  special = false
}

resource "azurerm_resource_group" "this" {
  name     = "rg-${local.base}-${local.suffix}"
  location = var.location
  tags     = local.tags
}

# --- Networking -------------------------------------------------------------

resource "azurerm_virtual_network" "this" {
  count               = var.enable_vnet_integration ? 1 : 0
  name                = "vnet-${local.base}-${local.suffix}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  address_space       = [var.vnet_address_space]
  tags                = local.tags
}

resource "azurerm_network_security_group" "sandboxes" {
  count               = var.enable_vnet_integration ? 1 : 0
  name                = "nsg-${local.base}-sandboxes"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  tags                = local.tags
}

resource "azurerm_subnet" "sandboxes" {
  count                = var.enable_vnet_integration ? 1 : 0
  name                 = "snet-sandboxes"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this[0].name
  address_prefixes     = [var.sandbox_subnet_prefix]

  delegation {
    name = "aca-sandboxes"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet_network_security_group_association" "sandboxes" {
  count                     = var.enable_vnet_integration ? 1 : 0
  subnet_id                 = azurerm_subnet.sandboxes[0].id
  network_security_group_id = azurerm_network_security_group.sandboxes[0].id
}

# --- ACA managed environment ------------------------------------------------

resource "azurerm_log_analytics_workspace" "this" {
  count               = var.enable_managed_environment ? 1 : 0
  name                = "log-${local.base}-${local.suffix}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  tags                = local.tags
}

resource "azurerm_subnet" "aca_env" {
  count                = var.enable_managed_environment && var.enable_vnet_integration ? 1 : 0
  name                 = "snet-aca-env"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this[0].name
  address_prefixes     = [var.aca_env_subnet_prefix]

  delegation {
    name = "aca-env"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_container_app_environment" "this" {
  count                      = var.enable_managed_environment ? 1 : 0
  name                       = "cae-${local.base}-${local.suffix}"
  location                   = azurerm_resource_group.this.location
  resource_group_name        = azurerm_resource_group.this.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this[0].id

  infrastructure_subnet_id           = var.enable_vnet_integration ? azurerm_subnet.aca_env[0].id : null
  internal_load_balancer_enabled     = var.enable_vnet_integration ? var.aca_env_internal_only : null
  infrastructure_resource_group_name = var.enable_vnet_integration ? "ME_rg-${local.base}-${local.suffix}" : null

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }

  tags = local.tags
}

# --- Sandbox group ----------------------------------------------------------

resource "azapi_resource" "sandbox_group" {
  type      = "Microsoft.App/sandboxGroups@2026-07-01"
  name      = local.sandbox_group_name
  parent_id = azurerm_resource_group.this.id
  location  = azurerm_resource_group.this.location
  tags      = local.tags

  # azapi <= 2.13 has no embedded schema for sandboxGroups yet; ARM still validates the body.
  schema_validation_enabled = false

  # Standalone by default. properties.environmentId is immutable once set:
  # changing/removing it returns 409, so it is treated as replace-on-change.
  body = {
    properties = local.link_environment ? {
      environmentId = azurerm_container_app_environment.this[0].id
    } : {}
  }

  replace_triggers_refs = ["properties.environmentId"]

  response_export_values = ["*"]
}

resource "azapi_resource" "sandbox_vnet_connection" {
  count     = var.enable_vnet_integration ? 1 : 0
  type      = "Microsoft.App/sandboxGroups/vnetConnections@2026-07-01"
  name      = "default"
  parent_id = azapi_resource.sandbox_group.id
  location  = azurerm_resource_group.this.location # required by the API although undocumented

  # ARM returns subnetId lower-cased; avoid a perpetual (and immutable-field) diff.
  ignore_casing = true

  schema_validation_enabled = false

  body = {
    properties = {
      subnetId = azurerm_subnet.sandboxes[0].id
    }
  }

  depends_on = [azurerm_subnet_network_security_group_association.sandboxes]
}

# --- Data-plane RBAC --------------------------------------------------------

resource "azurerm_role_assignment" "sandbox_data_owner" {
  for_each = local.data_owner_principals

  scope              = azapi_resource.sandbox_group.id
  role_definition_id = "/subscriptions/${data.azurerm_client_config.current.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${local.sandbox_data_owner_role_id}"
  principal_id       = each.value.object_id
  principal_type     = each.value.type
}
