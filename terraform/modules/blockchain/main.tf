# ============================================================
# modules/blockchain/main.tf
# AWS Managed Blockchain (Hyperledger Fabric) + Azure Confidential Ledger
# ============================================================

# ── AWS Managed Blockchain ────────────────────────────────────────────────

resource "aws_managedblockchain_network" "traceability" {
  count = var.create_fabric_network ? 1 : 0

  name        = "traceability-${var.environment}"
  description = "農産物トレーサビリティ Hyperledger Fabric ネットワーク"

  framework         = "HYPERLEDGER_FABRIC"
  framework_version = "2.2"

  voting_policy {
    approval_threshold_percentage  = 50
    proposal_duration_in_hours     = 24
    threshold_comparator           = "GREATER_THAN_OR_EQUAL_TO"
  }

  member_configuration {
    name        = "TraceabilityOrg1"
    description = "日米間トレーサビリティ組織"

    framework_configuration {
      fabric {
        admin_username = "TraceAdmin"
        admin_password = var.fabric_admin_password
      }
    }
  }
}

resource "aws_managedblockchain_member" "traceability" {
  count      = var.create_fabric_network ? 1 : 0
  network_id = aws_managedblockchain_network.traceability[0].id

  membership_configuration {
    name        = "TraceabilityMember"
    description = "トレーサビリティシステムメンバー"

    framework_configuration {
      fabric {
        admin_username = "MemberAdmin"
        admin_password = var.fabric_admin_password
      }
    }
  }
}

# ── Azure Confidential Ledger ─────────────────────────────────────────────

resource "azurerm_confidential_ledger" "traceability" {
  name                = "acl-traceability-${var.environment}"
  resource_group_name = var.azure_resource_group
  location            = var.azure_region

  ledger_type = "Private"

  # Functions の Managed Identity に書き込み権限を付与
  azuread_based_service_principal {
    principal_id = var.function_app_principal_id
    tenant_id    = var.azure_tenant_id
    ledger_role_name = "Contributor"
  }

  tags = {
    Project     = "TraceabilitySystem"
    Environment = var.environment
  }
}

# ── variables ─────────────────────────────────────────────────────────────

variable "environment" { type = string }
variable "azure_region" { type = string }
variable "azure_resource_group" { type = string }
variable "azure_tenant_id" { type = string }
variable "function_app_principal_id" { type = string }
variable "create_fabric_network" {
  type    = bool
  default = true
}
variable "fabric_admin_password" {
  type      = string
  sensitive = true
}

# ── outputs ───────────────────────────────────────────────────────────────

output "fabric_network_id" {
  value = var.create_fabric_network ? aws_managedblockchain_network.traceability[0].id : ""
}

output "confidential_ledger_endpoint" {
  value = azurerm_confidential_ledger.traceability.ledger_endpoint
}
