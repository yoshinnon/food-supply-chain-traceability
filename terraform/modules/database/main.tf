# ============================================================
# modules/database/main.tf
# Amazon RDS (off-chain AWS) + Azure SQL (off-chain Azure)
# + DynamoDB (べき等性ストア)
# ============================================================

# ── Amazon RDS (PostgreSQL) ───────────────────────────────────────────────

resource "aws_db_subnet_group" "traceability" {
  name       = "traceability-db-subnet-${var.environment}"
  subnet_ids = var.private_subnet_ids
}

resource "aws_security_group" "rds" {
  name        = "traceability-rds-sg-${var.environment}"
  description = "RDS アクセス制御 (Lambda からのみ)"
  vpc_id      = var.vpc_id

  ingress {
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [var.lambda_security_group_id]
  }
  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_db_instance" "traceability" {
  identifier        = "traceability-${var.environment}"
  engine            = "postgres"
  engine_version    = "15.4"
  instance_class    = var.rds_instance_class
  allocated_storage = 20
  storage_encrypted = true

  db_name  = "traceability"
  username = "trace_admin"
  password = var.db_password  # Secrets Manager で管理

  db_subnet_group_name   = aws_db_subnet_group.traceability.name
  vpc_security_group_ids = [aws_security_group.rds.id]
  publicly_accessible    = false
  skip_final_snapshot    = var.environment != "prod"
  deletion_protection    = var.environment == "prod"

  # 自動バックアップ (本番: 7日、開発: 1日)
  backup_retention_period = var.environment == "prod" ? 7 : 1
  backup_window           = "03:00-04:00"
  maintenance_window      = "Mon:04:00-Mon:05:00"

  tags = { Environment = var.environment }
}

# RDS 接続情報を Secrets Manager に保存
resource "aws_secretsmanager_secret" "rds_credentials" {
  name                    = "traceability/${var.environment}/rds-credentials"
  recovery_window_in_days = 7
}

resource "aws_secretsmanager_secret_version" "rds_credentials" {
  secret_id = aws_secretsmanager_secret.rds_credentials.id
  secret_string = jsonencode({
    host     = aws_db_instance.traceability.address
    port     = 5432
    dbname   = "traceability"
    username = "trace_admin"
    password = var.db_password
  })
  lifecycle { ignore_changes = [secret_string] }
}

# ── DynamoDB (べき等性ストア) ──────────────────────────────────────────────

resource "aws_dynamodb_table" "idempotency" {
  name         = "traceability-idempotency-${var.environment}"
  billing_mode = "PAY_PER_REQUEST"  # オンデマンド (トラフィック変動に対応)
  hash_key     = "idempotency_key"

  attribute {
    name = "idempotency_key"
    type = "S"
  }

  # 90日後に自動削除 (TTL)
  ttl {
    attribute_name = "expires_at"
    enabled        = true
  }

  point_in_time_recovery {
    enabled = var.environment == "prod"
  }

  tags = { Environment = var.environment }
}

# ── Azure SQL ─────────────────────────────────────────────────────────────

resource "azurerm_mssql_server" "traceability" {
  name                         = "sql-traceability-${var.environment}"
  resource_group_name          = var.azure_resource_group
  location                     = var.azure_region
  version                      = "12.0"
  administrator_login          = "trace_admin"
  administrator_login_password = var.db_password

  # Azure AD 認証を優先 (Functions の Managed Identity でアクセス)
  azuread_administrator {
    login_username = "TraceabilityFunctionsAdmin"
    object_id      = var.function_app_principal_id
  }

  minimum_tls_version = "1.2"
  tags = { Environment = var.environment }
}

resource "azurerm_mssql_database" "traceability" {
  name      = "traceability"
  server_id = azurerm_mssql_server.traceability.id
  sku_name  = var.environment == "prod" ? "S2" : "S0"

  # 本番: 7日間の短期バックアップ
  short_term_retention_policy {
    retention_days = var.environment == "prod" ? 7 : 1
  }

  tags = { Environment = var.environment }
}

# Azure SQL ファイアウォール: Functions の IP のみ許可
resource "azurerm_mssql_firewall_rule" "allow_azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.traceability.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# Azure Table Storage (ACL べき等性ストア)
resource "azurerm_storage_account" "idempotency" {
  name                     = "saidempotency${var.environment}"
  resource_group_name      = var.azure_resource_group
  location                 = var.azure_region
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
}

resource "azurerm_storage_table" "idempotency" {
  name                 = "traceabilityidempotency"
  storage_account_name = azurerm_storage_account.idempotency.name
}

# ── variables ─────────────────────────────────────────────────────────────

variable "environment" { type = string }
variable "azure_region" { type = string }
variable "azure_resource_group" { type = string }
variable "vpc_id" { type = string }
variable "private_subnet_ids" { type = list(string) }
variable "lambda_security_group_id" { type = string }
variable "function_app_principal_id" { type = string }
variable "db_password" { type = string; sensitive = true }
variable "rds_instance_class" { type = string; default = "db.t3.micro" }

# ── outputs ───────────────────────────────────────────────────────────────

output "rds_endpoint"              { value = aws_db_instance.traceability.address }
output "rds_secret_arn"            { value = aws_secretsmanager_secret.rds_credentials.arn }
output "dynamodb_table_name"       { value = aws_dynamodb_table.idempotency.name }
output "azure_sql_server_fqdn"     { value = azurerm_mssql_server.traceability.fully_qualified_domain_name }
output "azure_sql_db_name"         { value = azurerm_mssql_database.traceability.name }
output "table_storage_endpoint"    { value = azurerm_storage_account.idempotency.primary_table_endpoint }
output "idempotency_table_name"    { value = azurerm_storage_table.idempotency.name }
