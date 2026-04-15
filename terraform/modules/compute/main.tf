# ============================================================
# modules/compute/main.tf
# Lambda (AWS) + Azure Functions + シークレット共有
# ============================================================

# ── AWS Lambda ────────────────────────────────────────────────────────────

resource "aws_iam_role" "lambda_exec" {
  name = "traceability-lambda-exec-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "lambda_basic" {
  role       = aws_iam_role.lambda_exec.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_iam_role_policy" "lambda_secrets" {
  name = "traceability-lambda-secrets-${var.environment}"
  role = aws_iam_role.lambda_exec.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = aws_secretsmanager_secret.azure_ledger_endpoint.arn
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"]
        Resource = aws_sqs_queue.iot_events.arn
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = aws_sqs_queue.dlq.arn
      }
    ]
  })
}

resource "aws_lambda_function" "traceability" {
  function_name = "traceability-processor-${var.environment}"
  role          = aws_iam_role.lambda_exec.arn
  runtime       = "dotnet8"
  handler       = "Traceability.AWS.Lambda::Traceability.AWS.Lambda.TraceabilityFunction::FunctionHandlerAsync"
  timeout       = 60
  memory_size   = 512

  # デプロイパッケージは CI/CD で S3 にアップロード済みを想定
  s3_bucket = var.lambda_artifact_bucket
  s3_key    = "lambda/${var.environment}/traceability.zip"

  environment {
    variables = {
      ENVIRONMENT             = var.environment
      AZURE_LEDGER_SECRET_ARN = aws_secretsmanager_secret.azure_ledger_endpoint.arn
      FABRIC_CHANNEL          = var.fabric_channel_name
      ONNX_MODEL_KEY          = var.onnx_model_s3_key
    }
  }

  dead_letter_config {
    target_arn = aws_sqs_queue.dlq.arn
  }

  depends_on = [aws_iam_role_policy_attachment.lambda_basic]
}

# Geofence Lambda (IoT Core ルールから直呼び出し)
resource "aws_lambda_function" "geofence" {
  function_name = "traceability-geofence-${var.environment}"
  role          = aws_iam_role.lambda_exec.arn
  runtime       = "dotnet8"
  handler       = "Traceability.AWS.Lambda::Traceability.AWS.Lambda.GeofenceEventFunction::FunctionHandlerAsync"
  timeout       = 30
  memory_size   = 256

  s3_bucket = var.lambda_artifact_bucket
  s3_key    = "lambda/${var.environment}/traceability.zip"
}

# SQS キュー (IoT → Lambda)
resource "aws_sqs_queue" "iot_events" {
  name                       = "traceability-iot-events-${var.environment}"
  visibility_timeout_seconds = 90
  message_retention_seconds  = 86400 # 24h
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.dlq.arn
    maxReceiveCount     = 3
  })
}

# Dead Letter Queue
resource "aws_sqs_queue" "dlq" {
  name                      = "traceability-dlq-${var.environment}"
  message_retention_seconds = 1209600 # 14日
}

# Lambda ← SQS トリガー
resource "aws_lambda_event_source_mapping" "sqs_trigger" {
  event_source_arn = aws_sqs_queue.iot_events.arn
  function_name    = aws_lambda_function.traceability.arn
  batch_size       = 10
}

# ── AWS Secrets Manager ───────────────────────────────────────────────────

resource "aws_secretsmanager_secret" "azure_ledger_endpoint" {
  name                    = "traceability/${var.environment}/azure-ledger-endpoint"
  description             = "Azure Confidential Ledger のエンドポイント URL (OIDC 経由で取得)"
  recovery_window_in_days = 7
}

# 実際の値は CI/CD パイプラインや手動設定で注入する
resource "aws_secretsmanager_secret_version" "azure_ledger_endpoint" {
  secret_id = aws_secretsmanager_secret.azure_ledger_endpoint.id
  secret_string = jsonencode({
    ledger_endpoint = "https://<your-ledger>.confidential-ledger.azure.com"
    collection_id   = "traceability"
  })

  lifecycle {
    ignore_changes = [secret_string] # CI/CD で上書きするため
  }
}

# ── Azure Functions ───────────────────────────────────────────────────────

resource "azurerm_resource_group" "traceability" {
  name     = "rg-traceability-${var.environment}"
  location = var.azure_region
}

resource "azurerm_storage_account" "functions" {
  name                     = "satrace${var.environment}${random_id.suffix.hex}"
  resource_group_name      = azurerm_resource_group.traceability.name
  location                 = var.azure_region
  account_tier             = "Standard"
  account_replication_type = "LRS"
}

resource "random_id" "suffix" {
  byte_length = 4
}

resource "azurerm_service_plan" "functions" {
  name                = "asp-traceability-${var.environment}"
  resource_group_name = azurerm_resource_group.traceability.name
  location            = var.azure_region
  os_type             = "Linux"
  sku_name            = "Y1" # Consumption プラン
}

resource "azurerm_linux_function_app" "traceability" {
  name                       = "func-traceability-${var.environment}"
  resource_group_name        = azurerm_resource_group.traceability.name
  location                   = var.azure_region
  storage_account_name       = azurerm_storage_account.functions.name
  storage_account_access_key = azurerm_storage_account.functions.primary_access_key
  service_plan_id            = azurerm_service_plan.functions.id

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version              = "8.0"
      use_dotnet_isolated_runtime = true
    }
  }

  app_settings = {
    FUNCTIONS_WORKER_RUNTIME    = "dotnet-isolated"
    ENVIRONMENT                 = var.environment
    AZURE_LEDGER_ENDPOINT       = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.ledger_endpoint.id})"
    AWS_FABRIC_ENDPOINT_SECRET  = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.aws_fabric_endpoint.id})"
  }
}

# ── Azure Key Vault ────────────────────────────────────────────────────────

resource "azurerm_key_vault" "traceability" {
  name                = "kv-trace-${var.environment}-${random_id.suffix.hex}"
  resource_group_name = azurerm_resource_group.traceability.name
  location            = var.azure_region
  tenant_id           = var.azure_tenant_id
  sku_name            = "standard"

  # Functions の Managed Identity にシークレット読み取り権限を付与
  access_policy {
    tenant_id = var.azure_tenant_id
    object_id = azurerm_linux_function_app.traceability.identity[0].principal_id

    secret_permissions = ["Get", "List"]
  }
}

resource "azurerm_key_vault_secret" "ledger_endpoint" {
  name         = "azure-ledger-endpoint"
  value        = "https://<your-ledger>.confidential-ledger.azure.com"
  key_vault_id = azurerm_key_vault.traceability.id

  lifecycle {
    ignore_changes = [value]
  }
}

resource "azurerm_key_vault_secret" "aws_fabric_endpoint" {
  name         = "aws-fabric-node-endpoint"
  value        = "grpcs://<fabric-node>.amazonaws.com:30001"
  key_vault_id = azurerm_key_vault.traceability.id

  lifecycle {
    ignore_changes = [value]
  }
}

# ── variables (module-level) ──────────────────────────────────────────────

variable "environment" { type = string }
variable "aws_region" { type = string }
variable "azure_region" { type = string }
variable "azure_tenant_id" { type = string }
variable "lambda_artifact_bucket" { type = string }
variable "fabric_channel_name" { type = string }
variable "onnx_model_s3_key" { type = string }

# ── outputs ───────────────────────────────────────────────────────────────

output "lambda_function_arn" {
  value = aws_lambda_function.traceability.arn
}

output "sqs_queue_url" {
  value = aws_sqs_queue.iot_events.url
}

output "dlq_url" {
  value = aws_sqs_queue.dlq.url
}

output "function_app_url" {
  value = "https://${azurerm_linux_function_app.traceability.default_hostname}"
}
