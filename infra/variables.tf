variable "subscription_id" {
  description = "Target subscription ID. Leave null to use ARM_SUBSCRIPTION_ID or the current Azure CLI subscription."
  type        = string
  default     = null
}

variable "location" {
  description = "Azure region. Must support Microsoft.App/sandboxGroups."
  type        = string
  default     = "swedencentral"

  validation {
    condition = contains([
      "swedencentral", "westeurope", "northeurope", "francecentral",
      "germanywestcentral", "italynorth", "polandcentral", "spaincentral",
      "austriaeast", "norwayeast", "switzerlandnorth", "uksouth", "ukwest",
    ], var.location)
    error_message = "Pick a European region that supports ACA Sandboxes."
  }
}

variable "name_prefix" {
  description = "Short prefix used in resource names (lowercase letters, digits, hyphens)."
  type        = string
  default     = "llmsbx"

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,14}$", var.name_prefix))
    error_message = "The name_prefix must be 2-15 chars, lowercase, and start with a letter."
  }
}

variable "environment" {
  description = "Environment label (dev, test, prod...)."
  type        = string
  default     = "dev"
}

variable "tags" {
  description = "Extra tags applied to every resource."
  type        = map(string)
  default     = {}
}

# --- Networking -------------------------------------------------------------

variable "enable_vnet_integration" {
  description = "Attach the sandbox group to a delegated subnet in a dedicated VNet (sandboxGroups/vnetConnections)."
  type        = bool
  default     = true
}

variable "vnet_address_space" {
  description = "Address space of the sandbox VNet."
  type        = string
  default     = "10.40.0.0/16"
}

variable "sandbox_subnet_prefix" {
  description = "Subnet for the sandbox group, delegated to Microsoft.App/environments. /23 or larger recommended. Immutable once connected."
  type        = string
  default     = "10.40.0.0/23"
}

# --- Container Apps managed environment ------------------------------------

variable "enable_managed_environment" {
  description = "Create an ACA managed environment (workload-profile based, VNet-integrated when VNet is enabled) alongside the sandbox group, for regular container apps/jobs."
  type        = bool
  default     = true
}

variable "aca_env_subnet_prefix" {
  description = "Subnet for the ACA managed environment infrastructure (delegated to Microsoft.App/environments). Must not overlap the sandbox subnet."
  type        = string
  default     = "10.40.2.0/23"
}

variable "aca_env_internal_only" {
  description = "Make the managed environment internal (no public ingress endpoint)."
  type        = bool
  default     = false
}

variable "link_sandbox_group_to_environment" {
  description = "Set properties.environmentId on the sandbox group. WARNING: the link cannot be changed or removed afterwards (changing it forces the group to be replaced)."
  type        = bool
  default     = false
}

variable "log_retention_days" {
  description = "Log Analytics retention for the managed environment."
  type        = number
  default     = 30
}

# --- Access -----------------------------------------------------------------

variable "grant_data_owner_to_current_principal" {
  description = "Grant the Container Apps SandboxGroup Data Owner role on the sandbox group to the identity running tofu."
  type        = bool
  default     = true
}

variable "sandbox_data_owner_principals" {
  description = "Additional principals granted Container Apps SandboxGroup Data Owner on the sandbox group. Map of label => { object_id, type (User | Group | ServicePrincipal) }."
  type = map(object({
    object_id = string
    type      = string
  }))
  default = {}
}
