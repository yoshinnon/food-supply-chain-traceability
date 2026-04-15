# システム設計書
## クロスプラットフォーム・AI 駆動型トレーサビリティシステム

**バージョン:** 1.0.0  
**最終更新:** 2025-06-01  
**対象環境:** AWS (米国) / Azure (日本)

---

## 目次

1. [プロジェクト概要](#1-プロジェクト概要)
2. [技術スタック](#2-技術スタック)
3. [システムアーキテクチャ](#3-システムアーキテクチャ)
4. [データ設計](#4-データ設計)
5. [コンポーネント詳細](#5-コンポーネント詳細)
6. [セキュリティ設計](#6-セキュリティ設計)
7. [運用・ガードレール](#7-運用ガードレール)
8. [テスト戦略](#8-テスト戦略)
9. [インフラ設計 (Terraform)](#9-インフラ設計-terraform)
10. [実装完了状況](#10-実装完了状況)
11. [今後の作業](#11-今後の作業)

---

## 1. プロジェクト概要

日本 (Azure) とアメリカ (AWS) の間で農産物（小麦等）を双方向に輸出入する際、ブロックチェーン (BC) を用いて改ざん不可能な履歴を管理する。IoT デバイスからの異常データは AI が自動修復し、その過程も BC に記録する。

### 基本方針

| 軸 | 採用アプローチ |
|---|---|
| データ標準 | GS1 EPCIS 2.0 (JSON-LD) |
| AI ポータビリティ | ONNX Runtime (AWS/Azure 共通) |
| 変換層 | 疎結合な AutoMapper プロファイル |
| 事実の連続性 | ParentHash による親子 BC リンク |

---

## 2. 技術スタック

| カテゴリ | 採用技術 |
|---|---|
| 言語 / FW | C# (.NET 8)、Nethereum |
| クラウド AWS | Lambda、IoT Core、Managed Blockchain (Hyperledger Fabric 2.2)、Secrets Manager、DynamoDB、SQS |
| クラウド Azure | Functions、IoT Hub、Confidential Ledger、Key Vault、Table Storage、Azure SQL |
| AI | ONNX Runtime (`Microsoft.ML.OnnxRuntime`) |
| スキーマ標準 | GS1 EPCIS 2.0 JSON-LD |
| マッピング | AutoMapper 12 |
| インフラ管理 | Terraform >= 1.7 (Multi-Provider: aws + azurerm) |
| テスト | xUnit、Moq |

---

## 3. システムアーキテクチャ

### 3.1 全体データフロー

```
農地 IoT (LoRaWAN)
        │
        ├─────────────────────────────────────────────┐
        ▼                                             ▼
 AWS IoT Core                                 Azure IoT Hub
 (米国側受信)                                  (日本側受信)
        │                                             │
        ▼                                             ▼
 Lambda + ONNX                             Functions + ONNX
 AiRepairService                           AiRepairService
  ① 異常検知                                ① 異常検知
  ② 修復 (ONNX / 移動平均)                  ② 修復 (ONNX / 移動平均)
  ③ RepairMetadata 生成                    ③ RepairMetadata 生成
        │                                             │
        ▼                                             ▼
 Traceability.Mapping                    Traceability.Mapping
 US Schema → EPCIS 2.0                   JP Schema → EPCIS 2.0
 (Bushel→KG, LocalTime→UTC)              (KG, UTC そのまま)
        │                                             │
   ┌────┴────┐                               ┌────────┴────────┐
   ▼         ▼                               ▼                 ▼
Amazon RDS  AWS Fabric BC              Azure SQL      Azure Confidential
(off-chain) on-chain:                  (off-chain)    Ledger  on-chain:
            H_1 + TxID_aws                            H_1 + ParentTx=TxID_aws
                  │                                   ChainHash=SHA256(H_1+TxID_aws)
                  └─────────── ParentHash リンク ──────────────┘
```

### 3.2 輸出イベントトリガー (IoT 駆動)

```
コンテナが港のジオフェンスを通過
        │
        ▼
IoT Core / IoT Hub (ジオフェンスルール)
        │
        ▼
Lambda GeofenceEventFunction
        │
        ▼
SQS キュー (iot-events)
        │
        ▼
Lambda TraceabilityFunction
  ① IoT メッセージ → AwsRawSchema デシリアライズ
  ② EPCIS 2.0 変換
  ③ AI 修復チェック
  ④ CrossChainOrchestrator.AnchorExportEventAsync()
        ├── AWS Fabric へ書き込み → TxID_aws 取得
        └── Azure Ledger へ書き込み (ParentTx = TxID_aws)
```

### 3.3 クロスチェーン ParentHash プロトコル

```
Step 1  AWS Fabric:
          入力: DataHash = SHA256(EpcisEvent)
          出力: TxID_aws = "fabric-abc123..."
          記録: { data_hash: H_1, tx_id: TxID_aws }

Step 2  ChainHash 計算:
          Hash_final = SHA256(H_1 + TxID_aws)

Step 3  Azure Confidential Ledger:
          入力: { data_hash: H_1, parent_tx: TxID_aws, chain_hash: Hash_final }
          出力: TxID_azure = "2.15"
          記録: { data_hash: H_1, parent_tx: TxID_aws, chain_hash: Hash_final }
```

この構造により「米国側のこの BC 記録（TxID_aws）を根拠として、
日本側の記録が開始された」という依存関係が数学的に証明される。
これは法務・通関上の「証拠の連続性 (Chain of Custody)」として機能する。

---

## 4. データ設計

### 4.1 EPCIS 2.0 準拠 オンチェーンデータ構造

オンチェーンに記録する「唯一の真実」となるデータ構造。

| フィールド | 型 | 説明 |
|---|---|---|
| `@context` | string | `https://ref.gs1.org/standards/epcis/epcis-context.jsonld` |
| `type` | string | `"ObjectEvent"` 固定 |
| `eventTime` | DateTimeOffset (UTC) | イベント発生時刻 |
| `recordTime` | DateTimeOffset (UTC) | サーバー記録時刻 |
| `epcList` | List\<string\> | GS1 識別子 (sgtin 形式) |
| `bizStep` | string | ビジネス工程 URI (GS1 CBV) |
| `readPoint` | string | 場所識別子 (sgln 形式) |
| `quantityList` | List\<QuantityElement\> | 数量・単位 (KGM = kg) |
| `is_imputed` | boolean | AI による修復フラグ |
| `data_hash` | string (Hex) | オフチェーンデータの SHA-256 |
| `parent_tx` | string? | 輸出元 BC の TxID (クロスチェーンリンク) |
| `chain_hash` | string? | SHA256(data_hash + parent_tx) |
| `repair_metadata` | object? | AI 修復詳細 (修復時のみ) |
| `source_cloud` | string | `"AWS"` \| `"Azure"` |
| `idempotency_key` | string | 重複書き込み防止キー (SHA256 先頭16文字) |

### 4.2 repair_metadata 構造

| フィールド | 型 | 説明 |
|---|---|---|
| `original_value` | double | 修復前の生センサー値 |
| `repaired_value` | double | ONNX モデルが算出した修復値 |
| `model_hash` | string | 使用した ONNX モデルの SHA-256 |
| `window_size` | int | 移動平均の窓サイズ N |
| `moving_avg` | double | 移動平均値 |
| `threshold_sigma` | double | 異常判定閾値 (σ 倍数) |
| `repaired_at` | DateTimeOffset | 修復実行時刻 |

### 4.3 スキーマ変換定義

```
AWS (米国) 生スキーマ          EPCIS 2.0 共通            Azure (日本) 生スキーマ
─────────────────────         ──────────────            ──────────────────────
Volume_Bushels        →  quantityList[].quantity (KGM)  ← Weight_KG
Farm_Name             →  readPoint (sgln)               ← Wholesale_Market_ID
Local_Time (任意 TZ)  →  eventTime (UTC)                ← UTC_Time
Farm_GLN              →  readPoint / bizLocation        ← Market_GLN
Lot_Number            →  epcList[0] (sgtin serial)      ← Lot_Number
BizStep               →  bizStep (GS1 CBV URI)          ← BizStep
```

単位換算係数 (USDA 公定値):
- 1 bushel of wheat = 60 lb = **27.2155 kg**

---

## 5. コンポーネント詳細

### 5.1 Traceability.Common

| ファイル | 役割 |
|---|---|
| `DTOs/EpcisEvent.cs` | EPCIS 2.0 完全準拠 DTO (C# record) |
| `DTOs/RawSchemas.cs` | AWS/Azure 生スキーマ DTO |
| `Utils/UnitConverter.cs` | Bushel↔KG、°F↔°C 換算 |
| `Utils/UnitConverter.cs` (HashUtil) | SHA-256・ChainHash・IdempotencyKey 計算 |
| `Utils/UnitConverter.cs` (Gs1Builder) | sgtin / sgln URI ビルダー |

### 5.2 Traceability.Mapping

| ファイル | 役割 |
|---|---|
| `Profiles/AwsToEpcisProfile.cs` | AWS Raw ↔ EPCIS 双方向 AutoMapper プロファイル |
| `Profiles/AzureToEpcisProfile.cs` | Azure Raw ↔ EPCIS 双方向 AutoMapper プロファイル |

双方向マッピングにより、輸入側での逆変換（監査・再確認）も可能。

### 5.3 Traceability.AI

| ファイル | 役割 |
|---|---|
| `AiRepairService.cs` | 異常検知・修復オーケストレーター。移動平均統計による異常判定。 |
| `OnnxInferenceEngine.cs` | ONNX Runtime 本番実装。正規化→推論→逆正規化。モデルハッシュ起動時検証。 |

**異常検知アルゴリズム:**

```
1. 過去 N 件 (WindowSize=10) の移動平均 μ と標準偏差 σ を計算
2. |current - μ| > ThresholdSigma × σ (デフォルト 3σ) なら異常
3. 異常の場合:
   - ONNX モデルが存在 → OnnxInferenceEngine.PredictAsync()
   - モデル未存在     → μ (移動平均) をそのまま修復値として使用
4. RepairMetadata を生成して EpcisEvent に付与
```

**ONNX モデルのポータビリティ保証:**

AWS Lambda と Azure Functions の両環境で同一モデルファイルを使用し、
起動時に SHA-256 ハッシュを照合することで、
「どちらのクラウドで修復しても同じ結果になる」ことを数学的に保証する。

### 5.4 Traceability.Blockchain

| ファイル | 役割 |
|---|---|
| `BlockchainClient.cs` | `IBlockchainStrategy` インターフェース + `CrossChainOrchestrator` |
| `FabricGatewayStrategy.cs` | AWS Managed Blockchain (Hyperledger Fabric) 本番実装 |
| `AzureLedgerStrategy.cs` | Azure Confidential Ledger 本番実装 |

**Strategy パターンを採用した理由:**

Azure Confidential Ledger と AWS Managed Blockchain は API 構造が根本的に異なる。

| 特性 | Azure Confidential Ledger | AWS Fabric |
|---|---|---|
| 構造 | append-only KV ledger | Chaincode (スマートコントラクト) |
| 書き込み API | `PostLedgerEntryAsync` | `contract.Submit()` |
| TxID 形式 | `"2.15"` (サブレジャー.シーケンス) | Hex 文字列 |
| 合意形成 | BFT (Intel SGX) | Raft |
| クエリ | コレクション + キー | CouchDB / range query |

Strategy パターンにより `CrossChainOrchestrator` はこの差異を吸収し、
統一されたインターフェースで両チェーンを協調制御する。

**べき等性ストア:**

| クラウド | ストア | TTL |
|---|---|---|
| AWS | Amazon DynamoDB | 90日 (TTL 属性で自動削除) |
| Azure | Azure Table Storage | 手動管理 |

### 5.5 Traceability.AWS.Lambda

| ハンドラ | トリガー | 役割 |
|---|---|---|
| `TraceabilityFunction` | SQS (IoT Core → SQS) | メイン処理: AI修復 → EPCIS変換 → BC書き込み |
| `GeofenceEventFunction` | IoT Core ルール (直接) | ジオフェンス通過を輸出イベントに変換 |

**SQS DLQ 構成:**
- 最大受信回数: 3回
- DLQ 保持期間: 14日
- Lambda タイムアウト: 60秒 (SQS Visibility Timeout: 90秒)

---

## 6. セキュリティ設計

### 6.1 シークレット管理

クラウド間でシークレットそのものを渡さない。
Managed Identity + OIDC フェデレーションで相互認証する。

```
AWS Lambda
  └─ IAM Role (Secrets Manager 読み取り権限)
       └─ SecretsManager: azure-ledger-endpoint (接続 URL のみ)

Azure Functions
  └─ Managed Identity (SystemAssigned)
       └─ Key Vault Reference: @Microsoft.KeyVault(...)
            ├─ azure-ledger-endpoint
            └─ aws-fabric-node-endpoint
```

### 6.2 Fabric 証明書管理

```
AWS Secrets Manager
  ├─ FABRIC_CERT_PEM  (X.509 クライアント証明書)
  └─ FABRIC_KEY_PEM   (秘密鍵)
      ↓ 起動時に環境変数に展開
Lambda 実行環境
  └─ FabricGatewayStrategy (TLS + mTLS で Peer に接続)
```

### 6.3 Confidential Ledger 認証フロー

```
Azure Functions (Managed Identity)
  └─ DefaultAzureCredential / ManagedIdentityCredential
       └─ ConfidentialLedgerClient
            └─ ACL (SGX エンクレーブ内で BFT 合意)
```

---

## 7. 運用・ガードレール

### 7.1 ONNX モデル同期チェック

AWS と Azure で同じ ONNX モデルが使われているか、起動時に自動検証する。

```csharp
// OnnxInferenceEngine コンストラクタで実行
ModelHash = ComputeFileHash(modelBytes);
if (ModelHash != options.ExpectedModelHash)
    throw new InvalidOperationException("モデルハッシュ不一致");
```

両クラウドの Lambda / Functions 環境変数に同一の `ONNX_MODEL_HASH` を設定することで、
デプロイ時のモデル混入を防止する。

### 7.2 リトライ・DLQ 戦略

```
BC 書き込み失敗
  ├─ MVCC_READ_CONFLICT (Fabric) → 即リトライ (最大3回, 指数バックオフ)
  ├─ RequestFailedException 409 (ACL) → IdempotencyException として正常終了
  └─ その他の例外 → Lambda が NACK → SQS 再キュー (最大3回) → DLQ
```

DLQ に入ったメッセージは 14 日以内に手動で確認・再処理する。

### 7.3 べき等性保証

```
IdempotencyKey = SHA256(LotNumber + ":" + BizStep + ":" + EventTime)[0..16]
```

BC 書き込み前に必ず DynamoDB (AWS) または Table Storage (Azure) で
このキーの存在を確認し、重複書き込みを防止する。
同一データが SQS で複数回配信されても、BC には 1 回しか記録されない。

### 7.4 モデル起動時チェックフロー

```
Lambda / Functions コールドスタート
    │
    ├─ ONNX モデルファイル存在確認
    │   ├─ 存在: OnnxInferenceEngine 初期化
    │   │         └─ ModelHash 計算 → ONNX_MODEL_HASH と照合
    │   │             ├─ 一致: 起動完了
    │   │             └─ 不一致: InvalidOperationException → デプロイ停止
    │   └─ 不存在: StubOnnxEngine (移動平均フォールバック)
    │
    └─ Fabric / ACL 接続 (遅延初期化 — 初回リクエスト時)
```

---

## 8. テスト戦略

### 8.1 テスト階層

| 種別 | ファイル | 対象 |
|---|---|---|
| Unit | `TraceabilityTests.cs` | 単位換算・ハッシュ・AutoMapper・AI修復 |
| Integration | `TraceabilityTests.cs` (Integration ns) | CrossChainOrchestrator (スタブ BC) |
| Integration | `ProductionTests.cs` | CrossChainOrchestrator (本番モック BC) |
| Schema | `TraceabilityTests.cs` (Schema ns) | EPCIS 2.0 準拠・GS1 URI 形式 |
| E2E | `ProductionTests.cs` (EndToEnd ns) | IoT異常値 → AI修復 → BC記録 全フロー |

### 8.2 重点テスト項目

BC への書き込みはやり直しが効かないため、以下を重点的に検証する:

1. **単位換算の精度** — Bushel↔KG の往復変換で精度 6 桁維持
2. **ハッシュ一貫性** — 同一オブジェクトが常に同一ハッシュを生成すること
3. **ChainHash の正確性** — `SHA256(dataHash + parentTxId)` の算出ロジック
4. **べき等性** — 同一 IdempotencyKey で 2 回目呼び出しが例外になること
5. **AI 修復フラグ** — 異常値に `is_imputed=true` と `repair_metadata` が付くこと
6. **ONNX ハッシュ不一致** — ハッシュが異なるモデルで起動時例外が出ること

### 8.3 テスト実行

```bash
# 全テスト
dotnet test tests/Traceability.Tests/

# カテゴリ別
dotnet test --filter "FullyQualifiedName~UnitConverterTests"
dotnet test --filter "FullyQualifiedName~Integration"
dotnet test --filter "FullyQualifiedName~Schema"
dotnet test --filter "FullyQualifiedName~EndToEnd"

# ONNX モデルあり環境のみ
TEST_ONNX_MODEL_PATH=/path/to/model.onnx \
  dotnet test --filter "Trait:Category=RequiresOnnxModel"
```

---

## 9. インフラ設計 (Terraform)

### 9.1 モジュール構成

```
terraform/
├── providers.tf              AWS (us-east-1) + Azure (japaneast) プロバイダー
├── variables.tf              共通変数 (region, environment, blockchain_node_id 等)
└── modules/
    ├── compute/main.tf       Lambda + Functions + SQS DLQ + Secrets/KeyVault
    └── blockchain/main.tf    AWS Managed Blockchain + Azure Confidential Ledger
```

### 9.2 主要リソース一覧

| リソース | クラウド | 用途 |
|---|---|---|
| `aws_lambda_function.traceability` | AWS | メイン処理 Lambda |
| `aws_lambda_function.geofence` | AWS | ジオフェンスイベント処理 |
| `aws_sqs_queue.iot_events` | AWS | IoT → Lambda キュー |
| `aws_sqs_queue.dlq` | AWS | Dead Letter Queue |
| `aws_secretsmanager_secret.azure_ledger_endpoint` | AWS | Azure 接続 URL 管理 |
| `aws_managedblockchain_network.traceability` | AWS | Hyperledger Fabric ネットワーク |
| `azurerm_linux_function_app.traceability` | Azure | メイン処理 Functions |
| `azurerm_key_vault.traceability` | Azure | シークレット管理 |
| `azurerm_confidential_ledger.traceability` | Azure | BC ストレージ |

### 9.3 デプロイ手順

```bash
cd terraform

# 1. 初期化
terraform init

# 2. 変数ファイル作成 (gitignore 対象)
cat > env/prod.tfvars <<EOF
aws_region            = "us-east-1"
azure_region          = "japaneast"
environment           = "prod"
azure_subscription_id = "<YOUR_SUBSCRIPTION_ID>"
azure_tenant_id       = "<YOUR_TENANT_ID>"
fabric_admin_password = "<STRONG_PASSWORD>"
EOF

# 3. 差分確認
terraform plan -var-file="env/prod.tfvars"

# 4. 適用 (べき等性: 何度実行しても同じ状態)
terraform apply -var-file="env/prod.tfvars"
```

---

## 10. 実装完了状況

| フェーズ | 内容 | 状態 |
|---|---|---|
| Phase 1 | EPCIS 2.0 DTO・単位換算・ハッシュユーティリティ | ✅ 完了 |
| Phase 2 | AutoMapper AWS/Azure ↔ EPCIS 双方向プロファイル | ✅ 完了 |
| Phase 3 | AiRepairService (移動平均統計 + ONNX スタブ) | ✅ 完了 |
| Phase 3+ | OnnxInferenceEngine 本番実装 (モデルハッシュ検証付き) | ✅ 完了 |
| Phase 4 | CrossChainOrchestrator + Strategy インターフェース | ✅ 完了 |
| Phase 4+ | FabricGatewayStrategy 本番実装 (DynamoDB べき等性) | ✅ 完了 |
| Phase 4+ | AzureConfidentialLedgerStrategy 本番実装 (Table Storage べき等性) | ✅ 完了 |
| Phase 5 | AWS Lambda エントリポイント + Geofence ハンドラ | ✅ 完了 |
| Phase 6 | Terraform compute / blockchain モジュール | ✅ 完了 |
| Phase 7 | Unit / Integration / Schema / E2E テスト | ✅ 完了 |

### ファイル一覧

```
src/
├── Traceability.Common/
│   ├── DTOs/EpcisEvent.cs            EPCIS 2.0 DTO
│   ├── DTOs/RawSchemas.cs            AWS/Azure 生スキーマ
│   └── Utils/UnitConverter.cs        単位換算・ハッシュ・GS1 ビルダー
├── Traceability.Mapping/
│   └── Profiles/
│       ├── AwsToEpcisProfile.cs      AWS ↔ EPCIS 双方向マッピング
│       └── AzureToEpcisProfile.cs    Azure ↔ EPCIS 双方向マッピング
├── Traceability.AI/
│   ├── AiRepairService.cs            修復オーケストレーター
│   └── OnnxInferenceEngine.cs        ONNX 本番エンジン + DI 拡張
├── Traceability.Blockchain/
│   ├── BlockchainClient.cs           インターフェース + オーケストレーター
│   ├── FabricGatewayStrategy.cs      AWS Fabric 本番実装
│   └── AzureLedgerStrategy.cs        Azure ACL 本番実装
└── Traceability.AWS.Lambda/
    └── TraceabilityFunction.cs       Lambda エントリポイント
tests/
└── Traceability.Tests/
    ├── TraceabilityTests.cs          Unit / Integration / Schema テスト
    └── ProductionTests.cs            本番モック統合 / E2E テスト
terraform/
├── providers.tf
├── variables.tf
└── modules/
    ├── compute/main.tf
    └── blockchain/main.tf
docs/
└── SYSTEM_DESIGN.md                  本ドキュメント
```

---

## 11. 残作業・補足

すべての実装が完了しています。以下は環境・実データが揃い次第行う作業のみです。

| 項目 | 状態 | 説明 |
|---|---|---|
| Azure Functions エントリポイント | ✅ 完了 | `src/Traceability.Azure.Function/TraceabilityFunction.cs` |
| RDS / Azure SQL オフチェーン層 | ✅ 完了 | `terraform/modules/database/main.tf` (RDS + Azure SQL + DynamoDB) |
| Hyperledger Fabric Chaincode | ✅ 完了 | `chaincode/epcis_chaincode.go` (Go 実装、ChainHash 検証付き) |
| ONNX モデル学習 Python スクリプト | ✅ 完了 | `ml/01_prepare_data.py` / `02_train_model.py` / `03_export_onnx.py` |
| Terraform IoT モジュール | ✅ 完了 | `terraform/modules/iot/main.tf` (IoT Core + IoT Hub + Geofence) |
| Terraform Database モジュール | ✅ 完了 | `terraform/modules/database/main.tf` |
| CI/CD パイプライン | ✅ 完了 | `.github/workflows/ci-cd.yml` (test→build→terraform→deploy→verify) |
| Polly リトライポリシー | ✅ 完了 | `src/Traceability.Blockchain/FabricRetryPolicy.cs` |

### 残る作業 (環境依存)

| 項目 | 説明 |
|---|---|
| ONNX モデル生成用CSV入手 | [手順書](how_to_get_csv.md) |
| ONNX モデルの実体 (.onnx) | `ml/` の Python スクリプトを Kaggle + NOAA 実データで実行して生成する([手順書](how_to_make_onnx.md)) |
| Fabric Chaincode のデプロイ | AWS Managed Blockchain 環境で `peer lifecycle chaincode` コマンドを実行する |
| Terraform の実行 | AWS / Azure 認証情報と `env/prod.tfvars` を用意して `terraform apply` を実行する |
| GitHub Secrets の設定 | CI/CD YAML に記載の Secrets を GitHub リポジトリに登録する |
