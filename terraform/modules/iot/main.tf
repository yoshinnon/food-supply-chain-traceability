# ============================================================
# modules/iot/main.tf
# AWS IoT Core + Azure IoT Hub + ジオフェンスルール
# ============================================================

# ── AWS IoT Core ──────────────────────────────────────────────────────────

resource "aws_iot_policy" "traceability" {
  name = "traceability-iot-policy-${var.environment}"
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["iot:Connect", "iot:Publish", "iot:Subscribe", "iot:Receive"]
      Resource = "*"
    }]
  })
}

# IoT → SQS へ転送するトピックルール
resource "aws_iot_topic_rule" "sensor_to_sqs" {
  name        = "traceability_sensor_to_sqs_${var.environment}"
  description = "農地センサーデータを SQS に転送"
  enabled     = true
  sql         = "SELECT * FROM 'traceability/sensors/#'"
  sql_version = "2016-03-23"

  sqs {
    queue_url  = var.sqs_queue_url
    role_arn   = aws_iam_role.iot_sqs.arn
    use_base64 = false
  }

  error_action {
    sqs {
      queue_url  = var.dlq_url
      role_arn   = aws_iam_role.iot_sqs.arn
      use_base64 = false
    }
  }
}

# ジオフェンス通過イベント → Lambda 直呼び出しルール
resource "aws_iot_topic_rule" "geofence_to_lambda" {
  name        = "traceability_geofence_${var.environment}"
  description = "ジオフェンス通過イベントを Geofence Lambda に転送"
  enabled     = true
  sql         = "SELECT * FROM 'traceability/geofence/+' WHERE eventType = 'exit'"
  sql_version = "2016-03-23"

  lambda {
    function_arn = var.geofence_lambda_arn
  }
}

resource "aws_lambda_permission" "iot_geofence" {
  statement_id  = "AllowIoTCoreInvoke"
  action        = "lambda:InvokeFunction"
  function_name = var.geofence_lambda_arn
  principal     = "iot.amazonaws.com"
  source_arn    = aws_iot_topic_rule.geofence_to_lambda.arn
}

# IoT → SQS 転送用 IAM ロール
resource "aws_iam_role" "iot_sqs" {
  name = "traceability-iot-sqs-${var.environment}"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "iot.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy" "iot_sqs" {
  name = "iot-sqs-send-${var.environment}"
  role = aws_iam_role.iot_sqs.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["sqs:SendMessage"]
      Resource = [var.sqs_queue_arn, var.dlq_arn]
    }]
  })
}

# ── Azure IoT Hub ──────────────────────────────────────────────────────────

resource "azurerm_iothub" "traceability" {
  name                = "iothub-traceability-${var.environment}"
  resource_group_name = var.azure_resource_group
  location            = var.azure_region

  sku {
    name     = "S1"
    capacity = 1
  }

  endpoint {
    type                       = "AzureIotHub.ServiceBusQueue"
    connection_string          = var.service_bus_connection_string
    name                       = "traceability-queue-endpoint"
    resource_group_name        = var.azure_resource_group
    subscription_id            = var.azure_subscription_id
    entity_path                = var.service_bus_queue_name
  }

  route {
    name           = "sensor-data-route"
    source         = "DeviceMessages"
    condition      = "true"
    endpoint_names = ["traceability-queue-endpoint"]
    enabled        = true
  }

  # ジオフェンス通過イベントルート
  route {
    name           = "geofence-route"
    source         = "DeviceMessages"
    condition      = "messagetype = 'geofence'"
    endpoint_names = ["traceability-queue-endpoint"]
    enabled        = true
  }

  tags = {
    Environment = var.environment
    Project     = "TraceabilitySystem"
  }
}

# Azure Service Bus (IoT Hub → Functions キュー)
resource "azurerm_servicebus_namespace" "traceability" {
  name                = "sb-traceability-${var.environment}"
  resource_group_name = var.azure_resource_group
  location            = var.azure_region
  sku                 = "Standard"
}

resource "azurerm_servicebus_queue" "iot_events" {
  name         = "traceability-iot-events"
  namespace_id = azurerm_servicebus_namespace.traceability.id

  max_delivery_count              = 3        # 3回失敗でDLQへ
  lock_duration                   = "PT1M30S" # 90秒 (Lambda タイムアウトと合わせる)
  default_message_ttl             = "P1D"    # 24時間
  dead_lettering_on_message_expiration = true
}

# ── variables ─────────────────────────────────────────────────────────────

variable "environment" { type = string }
variable "azure_region" { type = string }
variable "azure_resource_group" { type = string }
variable "azure_subscription_id" { type = string }
variable "sqs_queue_url" { type = string }
variable "sqs_queue_arn" { type = string }
variable "dlq_url" { type = string }
variable "dlq_arn" { type = string }
variable "geofence_lambda_arn" { type = string }
variable "service_bus_connection_string" { type = string; sensitive = true }
variable "service_bus_queue_name" { type = string; default = "traceability-iot-events" }

# ── outputs ───────────────────────────────────────────────────────────────

output "iot_rule_sensor_arn"   { value = aws_iot_topic_rule.sensor_to_sqs.arn }
output "iot_rule_geofence_arn" { value = aws_iot_topic_rule.geofence_to_lambda.arn }
output "iothub_name"           { value = azurerm_iothub.traceability.name }
output "service_bus_namespace" { value = azurerm_servicebus_namespace.traceability.name }
output "service_bus_queue"     { value = azurerm_servicebus_queue.iot_events.name }
