terraform {
  required_version = ">= 1.8.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.50"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.12"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Remote state in Azure Blob Storage (Entra ID auth, no shared keys).
  # Account details live in backend.hcl (gitignored): tofu init -backend-config=backend.hcl
  backend "azurerm" {
    container_name   = "tfstate"
    key              = "aca-sandboxes.tfstate"
    use_azuread_auth = true
  }
}

provider "azurerm" {
  # Falls back to ARM_SUBSCRIPTION_ID / the current Azure CLI subscription when null.
  subscription_id = var.subscription_id
  features {}
}

provider "azapi" {
  subscription_id = var.subscription_id
}
