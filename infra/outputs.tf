output "resource_group_name" {
  value = azurerm_resource_group.this.name
}

output "location" {
  value = azurerm_resource_group.this.location
}

output "sandbox_group_name" {
  value = azapi_resource.sandbox_group.name
}

output "sandbox_group_id" {
  value = azapi_resource.sandbox_group.id
}

output "sandbox_subnet_id" {
  value = var.enable_vnet_integration ? azurerm_subnet.sandboxes[0].id : null
}

output "container_app_environment_id" {
  value = var.enable_managed_environment ? azurerm_container_app_environment.this[0].id : null
}

output "container_app_environment_default_domain" {
  value = var.enable_managed_environment ? azurerm_container_app_environment.this[0].default_domain : null
}

output "aca_cli_hint" {
  description = "Command to inspect the group with the aca CLI."
  value       = "aca sandboxgroup get --name ${azapi_resource.sandbox_group.name} -g ${azurerm_resource_group.this.name}"
}
