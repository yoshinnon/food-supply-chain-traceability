# ============================================================
# variables.tf
# ============================================================

variable "aws_region" {
  description = "AWS デプロイリージョン"
  type        = string
  default     = "us-east-1"
}

variable "azure_region" {
  description = "Azure デプロイリージョン"
  type        = string
  default     = "japaneast"
}

variable "environment" {
  description = "デプロイ環境 (dev / staging / prod)"
  type        = string
  default     = "dev"
  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "environment は dev / staging / prod のいずれかにしてください"
  }
}

variable "azure_subscription_id" {
  description = "Azure サブスクリプション ID"
  type        = string
  sensitive   = true
}

variable "azure_tenant_id" {
  description = "Azure テナント ID"
  type        = string
  sensitive   = true
}

variable "blockchain_network_id" {
  description = "AWS Managed Blockchain ネットワーク ID"
  type        = string
  default     = ""
}

variable "fabric_channel_name" {
  description = "Hyperledger Fabric チャネル名"
  type        = string
  default     = "traceability-channel"
}

variable "onnx_model_s3_key" {
  description = "S3 上の ONNX モデルオブジェクトキー"
  type        = string
  default     = "models/repair-model.onnx"
}

variable "alert_email" {
  description = "CloudWatch アラート通知先メール"
  type        = string
  default     = ""
}
