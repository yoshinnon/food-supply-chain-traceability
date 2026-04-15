# TraceabilitySystem
## クロスプラットフォーム AI 駆動型農産物トレーサビリティ

AWS (米国) と Azure (日本) 間で農産物（小麦等）を輸出入する際に、ブロックチェーンを用いた改ざん不可能な履歴を管理するシステム。IoT センサーの異常データは AI が自動修復し、修復過程ごと BC に記録する。

> 詳細は [`docs/SYSTEM_DESIGN.md`](docs/SYSTEM_DESIGN.md) を参照。

---

## アーキテクチャ概要

```
農地 IoT (LoRaWAN)
        │
        ├──────────────────────────────────────┐
        ▼                                      ▼
 AWS IoT Core                          Azure IoT Hub
 Lambda + ONNX                         Functions + ONNX
 AI修復 → EPCIS変換                     AI修復 → EPCIS変換
        │                                      │
   ┌────┴────┐                        ┌────────┴───────┐
   ▼         ▼                        ▼                ▼
Amazon RDS  AWS Fabric BC        Azure SQL     Confidential Ledger
(off-chain) [H_1, TxID_aws]    (off-chain)    [H_1, ParentTx=TxID_aws]
                  │                            ChainHash=SHA256(H_1+TxID_aws)
                  └──── ParentHash リンク ──────────────┘
```

**クロスチェーンハッシュ:**
```
Hash_final = SHA256(DataHash + TxID_aws)
```

---

## プロジェクト構成

```
TraceabilitySystem/
├── docs/
│   └── SYSTEM_DESIGN.md              システム設計書 (全仕様)
├── src/
│   ├── Traceability.Common/          Phase 1: EPCIS 2.0 DTO・単位換算・SHA-256
│   │   ├── DTOs/EpcisEvent.cs
│   │   ├── DTOs/RawSchemas.cs
│   │   └── Utils/UnitConverter.cs
│   ├── Traceability.Mapping/         Phase 2: AutoMapper (US/JP Schema ↔ EPCIS)
│   │   └── Profiles/
│   │       ├── AwsToEpcisProfile.cs
│   │       └── AzureToEpcisProfile.cs
│   ├── Traceability.AI/              Phase 3: AI 修復エンジン
│   │   ├── AiRepairService.cs        修復オーケストレーター + スタブ
│   │   └── OnnxInferenceEngine.cs    ONNX 本番実装 + DI 拡張
│   ├── Traceability.Blockchain/      Phase 4: BC クライアント
│   │   ├── BlockchainClient.cs       インターフェース + CrossChainOrchestrator
│   │   ├── FabricGatewayStrategy.cs  AWS Fabric 本番実装 (DynamoDB べき等性)
│   │   └── AzureLedgerStrategy.cs    Azure ACL 本番実装 (Table Storage べき等性)
│   └── Traceability.AWS.Lambda/      Phase 5: Lambda エントリポイント
│       └── TraceabilityFunction.cs
├── tests/
│   └── Traceability.Tests/
│       ├── TraceabilityTests.cs      Unit / Integration / Schema テスト
│       └── ProductionTests.cs        本番モック統合 / E2E テスト
└── terraform/
    ├── providers.tf                  AWS + Azure プロバイダー定義
    ├── variables.tf                  共通変数
    └── modules/
        ├── compute/main.tf           Lambda + Functions + SQS DLQ + Secrets
        └── blockchain/main.tf        Fabric + Confidential Ledger
```

---

## 必要なツール

| ツール | バージョン | 用途 |
|---|---|---|
| .NET SDK | 8.0 以上 | C# ビルド・テスト |
| Terraform | 1.7 以上 | インフラ管理 |
| AWS CLI | 2.x | AWS 認証・デプロイ |
| Azure CLI | 2.x | Azure 認証・デプロイ |

---

## セットアップ・ビルド

```bash
# ビルド
dotnet build

# テスト (全カテゴリ)
dotnet test tests/Traceability.Tests/ --logger "console;verbosity=detailed"

# カテゴリ別テスト
dotnet test --filter "FullyQualifiedName~UnitConverterTests"
dotnet test --filter "FullyQualifiedName~HashUtil"
dotnet test --filter "FullyQualifiedName~MappingTests"
dotnet test --filter "FullyQualifiedName~AiRepairService"
dotnet test --filter "FullyQualifiedName~Integration"
dotnet test --filter "FullyQualifiedName~Schema"
dotnet test --filter "FullyQualifiedName~EndToEnd"

# ONNX モデルあり環境のみ
TEST_ONNX_MODEL_PATH=/path/to/model.onnx \
  dotnet test --filter "Trait:Category=RequiresOnnxModel"
```

---

## 環境変数

### AWS Lambda

| 変数名 | 説明 |
|---|---|
| `FABRIC_CERT_PEM` | Fabric X.509 クライアント証明書 |
| `FABRIC_KEY_PEM` | Fabric 秘密鍵 |
| `FABRIC_TLS_CA_CERT_PEM` | Fabric TLS CA 証明書 |
| `ONNX_MODEL_HASH` | ONNX モデルの期待 SHA-256 (Azure と同一値) |

### Azure Functions

| 変数名 | 説明 |
|---|---|
| `AZURE_LEDGER_ENDPOINT` | ACL エンドポイント URL (Key Vault 参照) |
| `AWS_FABRIC_ENDPOINT_SECRET` | AWS Fabric ノード URL (Key Vault 参照) |
| `ONNX_MODEL_HASH` | ONNX モデルの期待 SHA-256 (AWS と同一値) |

---

## Terraform デプロイ

```bash
cd terraform
terraform init

cat > env/prod.tfvars <<EOF
aws_region            = "us-east-1"
azure_region          = "japaneast"
environment           = "prod"
azure_subscription_id = "<YOUR_SUBSCRIPTION_ID>"
azure_tenant_id       = "<YOUR_TENANT_ID>"
fabric_admin_password = "<STRONG_PASSWORD>"
EOF

terraform plan  -var-file="env/prod.tfvars"
terraform apply -var-file="env/prod.tfvars"
```

---

## 設計上の重要ポイント

### ParentHash による事実の連続性

```
AWS Fabric:   { data_hash: H_1, tx_id: TxID_aws }
                                    │ ParentHash リンク
                                    ▼
Azure Ledger: { data_hash: H_1, parent_tx: TxID_aws,
                chain_hash: SHA256(H_1 + TxID_aws) }
```

「米国側の BC 記録を根拠として日本の記録が始まった」という依存関係が数学的に証明され、通関上の Chain of Custody として機能する。

### べき等性保証

`IdempotencyKey = SHA256(LotNumber + BizStep + EventTime)[0..16]` を BC 書き込み前に DynamoDB / Table Storage で照合。同一メッセージが SQS で複数配信されても BC には 1 回だけ書き込まれる。

### ONNX モデル同期チェック

両クラウドで `ONNX_MODEL_HASH` を照合し、ハッシュ不一致の場合は起動を即停止する。デプロイ時のモデル混入をゼロコストで検出できる。

### AI 修復の透明性

修復前後の値・使用モデルハッシュ・統計パラメータを `repair_metadata` として BC に記録。「どのモデルがどのように修復したか」を後から完全追跡できる。

---

## 実装完了状況

| フェーズ | 内容 | 状態 |
|---|---|---|
| Phase 1 | EPCIS 2.0 DTO・単位換算・ハッシュ | ✅ 完了 |
| Phase 2 | AutoMapper 双方向プロファイル | ✅ 完了 |
| Phase 3 | AI 修復 (スタブ + ONNX 本番) | ✅ 完了 |
| Phase 4 | CrossChainOrchestrator + Fabric/ACL 本番実装 | ✅ 完了 |
| Phase 5 | Lambda エントリポイント | ✅ 完了 |
| Phase 6 | Terraform compute / blockchain | ✅ 完了 |
| Phase 7 | Unit / Integration / Schema / E2E テスト | ✅ 完了 |

残作業 → [`docs/SYSTEM_DESIGN.md`](docs/SYSTEM_DESIGN.md) §11 参照。
