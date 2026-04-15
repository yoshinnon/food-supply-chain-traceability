// chaincode/epcis_chaincode.go
// Hyperledger Fabric 2.2 Chaincode
// Go 1.21 / fabric-contract-api-go v1.2
//
// デプロイ手順:
//   peer lifecycle chaincode package epcis.tar.gz \
//     --path ./chaincode --lang golang --label epcis_1.0
//   peer lifecycle chaincode install epcis.tar.gz
//   peer lifecycle chaincode approveformyorg ...
//   peer lifecycle chaincode commit ...

package main

import (
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"time"

	"github.com/hyperledger/fabric-contract-api-go/contractapi"
)

// ── データ構造 ───────────────────────────────────────────────────────────

// EpcisRecord はチェーン上に保存する EPCIS イベントの簡略表現。
// 完全な EPCIS JSON は data_hash でオフチェーンと紐付ける。
type EpcisRecord struct {
	IdempotencyKey  string `json:"idempotency_key"`
	DataHash        string `json:"data_hash"`
	ChainHash       string `json:"chain_hash"`
	ParentTx        string `json:"parent_tx,omitempty"`
	EventTime       string `json:"event_time"`
	BizStep         string `json:"biz_step"`
	SourceCloud     string `json:"source_cloud"`
	IsImputed       bool   `json:"is_imputed"`
	RepairModelHash string `json:"repair_model_hash,omitempty"`
	RecordedAt      string `json:"recorded_at"`
	TxID            string `json:"tx_id"`
}

// ── スマートコントラクト ──────────────────────────────────────────────────

// EpcisChaincode は EPCIS イベントを Hyperledger Fabric に記録するコントラクト。
type EpcisChaincode struct {
	contractapi.Contract
}

// CreateEpcisEvent は EPCIS イベントをチェーンに追記する。
//
// 引数:
//
//	idempotencyKey : 重複防止キー (SHA256 先頭16文字)
//	epcisEventJSON : 完全な EPCIS 2.0 JSON 文字列
//
// 戻り値: トランザクション ID (hex 文字列)
//
// べき等性: 同一 idempotencyKey が既に存在する場合はエラーを返す。
// C# 側の FabricGatewayStrategy.AnchorAsync で呼び出される。
func (c *EpcisChaincode) CreateEpcisEvent(
	ctx contractapi.TransactionContextInterface,
	idempotencyKey string,
	epcisEventJSON string,
) (string, error) {

	// ── べき等性チェック ──────────────────────────────────────────────
	existing, err := ctx.GetStub().GetState(idempotencyKey)
	if err != nil {
		return "", fmt.Errorf("GetState エラー: %w", err)
	}
	if existing != nil {
		return "", fmt.Errorf("IDEMPOTENCY_CONFLICT: key=%s は既に記録済みです", idempotencyKey)
	}

	// ── EPCIS JSON をパースしてメタデータを抽出 ─────────────────────
	var epcisMap map[string]interface{}
	if err := json.Unmarshal([]byte(epcisEventJSON), &epcisMap); err != nil {
		return "", fmt.Errorf("EPCIS JSON パースエラー: %w", err)
	}

	dataHash, _ := epcisMap["data_hash"].(string)
	chainHash, _ := epcisMap["chain_hash"].(string)
	parentTx, _ := epcisMap["parentTx"].(string)
	eventTime, _ := epcisMap["eventTime"].(string)
	bizStep, _ := epcisMap["bizStep"].(string)
	sourceCloud, _ := epcisMap["source_cloud"].(string)
	isImputed, _ := epcisMap["is_imputed"].(bool)

	repairModelHash := ""
	if rm, ok := epcisMap["repair_metadata"].(map[string]interface{}); ok {
		repairModelHash, _ = rm["model_hash"].(string)
	}

	// ChainHash がなければ DataHash を使用 (起点チェーン)
	if chainHash == "" {
		chainHash = dataHash
	}

	// ── チェーンハッシュ検証 (ParentTx がある場合) ─────────────────
	if parentTx != "" && dataHash != "" {
		expected := computeChainHash(dataHash, parentTx)
		if chainHash != expected {
			return "", fmt.Errorf(
				"CHAIN_HASH_MISMATCH: expected=%s actual=%s",
				expected[:16], chainHash[:16])
		}
	}

	// ── レコード作成 ─────────────────────────────────────────────────
	txID := ctx.GetStub().GetTxID()
	record := EpcisRecord{
		IdempotencyKey:  idempotencyKey,
		DataHash:        dataHash,
		ChainHash:       chainHash,
		ParentTx:        parentTx,
		EventTime:       eventTime,
		BizStep:         bizStep,
		SourceCloud:     sourceCloud,
		IsImputed:       isImputed,
		RepairModelHash: repairModelHash,
		RecordedAt:      time.Now().UTC().Format(time.RFC3339),
		TxID:            txID,
	}

	recordJSON, err := json.Marshal(record)
	if err != nil {
		return "", fmt.Errorf("レコードシリアライズエラー: %w", err)
	}

	// ── ステート書き込み ─────────────────────────────────────────────
	if err := ctx.GetStub().PutState(idempotencyKey, recordJSON); err != nil {
		return "", fmt.Errorf("PutState エラー: %w", err)
	}

	// ── イベント発火 (外部リスナー向け) ────────────────────────────
	eventPayload, _ := json.Marshal(map[string]string{
		"idempotency_key": idempotencyKey,
		"chain_hash":      chainHash,
		"source_cloud":    sourceCloud,
		"tx_id":           txID,
	})
	_ = ctx.GetStub().SetEvent("EpcisEventAnchored", eventPayload)

	return txID, nil
}

// GetEpcisEvent はチェーン上の EPCIS レコードを取得する。
func (c *EpcisChaincode) GetEpcisEvent(
	ctx contractapi.TransactionContextInterface,
	idempotencyKey string,
) (*EpcisRecord, error) {

	data, err := ctx.GetStub().GetState(idempotencyKey)
	if err != nil {
		return nil, fmt.Errorf("GetState エラー: %w", err)
	}
	if data == nil {
		return nil, fmt.Errorf("NOT_FOUND: key=%s", idempotencyKey)
	}

	var record EpcisRecord
	if err := json.Unmarshal(data, &record); err != nil {
		return nil, fmt.Errorf("レコードデシリアライズエラー: %w", err)
	}
	return &record, nil
}

// ExistsEpcisEvent はべき等性キーの存在確認のみを行う (読み取り専用)。
func (c *EpcisChaincode) ExistsEpcisEvent(
	ctx contractapi.TransactionContextInterface,
	idempotencyKey string,
) (bool, error) {
	data, err := ctx.GetStub().GetState(idempotencyKey)
	if err != nil {
		return false, err
	}
	return data != nil, nil
}

// VerifyChainIntegrity は ParentTx から ChainHash を再計算し整合性を検証する。
// 監査・法務目的で使用する。
func (c *EpcisChaincode) VerifyChainIntegrity(
	ctx contractapi.TransactionContextInterface,
	idempotencyKey string,
) (bool, error) {
	record, err := c.GetEpcisEvent(ctx, idempotencyKey)
	if err != nil {
		return false, err
	}

	// 起点チェーン (ParentTx なし) は DataHash = ChainHash
	if record.ParentTx == "" {
		return record.DataHash == record.ChainHash, nil
	}

	expected := computeChainHash(record.DataHash, record.ParentTx)
	return record.ChainHash == expected, nil
}

// ── ヘルパー ─────────────────────────────────────────────────────────────

// computeChainHash は C# の HashUtil.ComputeChainHash と同じロジック。
// SHA256(dataHash + parentTxId) を hex 文字列で返す。
func computeChainHash(dataHash, parentTxID string) string {
	h := sha256.Sum256([]byte(dataHash + parentTxID))
	return fmt.Sprintf("%x", h)
}

// ── エントリポイント ──────────────────────────────────────────────────────

func main() {
	chaincode, err := contractapi.NewChaincode(&EpcisChaincode{})
	if err != nil {
		panic(fmt.Sprintf("Chaincode 作成エラー: %v", err))
	}
	if err := chaincode.Start(); err != nil {
		panic(fmt.Sprintf("Chaincode 起動エラー: %v", err))
	}
}
