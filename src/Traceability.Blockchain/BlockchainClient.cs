using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;

namespace Traceability.Blockchain;

// ════════════════════════════════════════════════════════════════════════════
// インターフェース
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// ブロックチェーンへの書き込みを抽象化する Strategy インターフェース。
/// Azure Confidential Ledger と AWS Managed Blockchain (Fabric) の
/// 非対称な API を統一する。
/// </summary>
public interface IBlockchainStrategy
{
    /// <summary>EPCIS イベントをチェーンに記録する。</summary>
    /// <returns>チェーン上のトランザクション ID</returns>
    Task<string> AnchorAsync(EpcisEvent epcisEvent, CancellationToken ct = default);

    /// <summary>指定のべき等性キーが既にチェーン上に存在するか確認する。</summary>
    Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default);

    string CloudName { get; }
}

/// <summary>BC 書き込み結果</summary>
public sealed record BlockchainAnchorResult
{
    public string TxId { get; init; } = string.Empty;
    public string CloudName { get; init; } = string.Empty;
    public DateTimeOffset AnchoredAt { get; init; }
    public string ChainHash { get; init; } = string.Empty;
}

// ════════════════════════════════════════════════════════════════════════════
// クロスチェーン・オーケストレーター
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// AWS と Azure の両チェーンへの書き込みを協調させるオーケストレーター。
///
/// フロー (輸出: AWS → Azure):
///   1. AWS Fabric に dataHash を書き込み TxID_aws を取得
///   2. Azure Ledger に dataHash + ParentTx=TxID_aws を書き込み
///   3. ChainHash = SHA256(dataHash + TxID_aws) を最終ハッシュとして記録
///
/// べき等性:
///   書き込み前に idempotencyKey の存在を確認し、
///   既存の場合は既存 TxID を返してスキップする。
/// </summary>
public sealed class CrossChainOrchestrator
{
    private readonly IBlockchainStrategy _awsStrategy;
    private readonly IBlockchainStrategy _azureStrategy;
    private readonly ILogger<CrossChainOrchestrator> _logger;

    public CrossChainOrchestrator(
        IBlockchainStrategy awsStrategy,
        IBlockchainStrategy azureStrategy,
        ILogger<CrossChainOrchestrator> logger)
    {
        _awsStrategy = awsStrategy;
        _azureStrategy = azureStrategy;
        _logger = logger;
    }

    /// <summary>
    /// AWS (起点) → Azure (受領) の順にアンカーリングを実行する。
    /// </summary>
    public async Task<(BlockchainAnchorResult aws, BlockchainAnchorResult azure)>
        AnchorExportEventAsync(EpcisEvent epcisEvent, CancellationToken ct = default)
    {
        // ── Step 0: データハッシュ計算 ────────────────────────────────────
        var dataHash = HashUtil.ComputeSha256(epcisEvent);
        var eventWithHash = epcisEvent with { DataHash = dataHash };

        // ── Step 1: べき等性チェック (AWS) ───────────────────────────────
        if (await _awsStrategy.ExistsAsync(epcisEvent.IdempotencyKey, ct))
        {
            _logger.LogInformation("AWS: 重複イベントをスキップ key={Key}", epcisEvent.IdempotencyKey);
            throw new IdempotencyException(epcisEvent.IdempotencyKey, "AWS");
        }

        // ── Step 2: AWS Fabric へ書き込み ────────────────────────────────
        _logger.LogInformation("AWS: アンカーリング開始 lot={Lot}", epcisEvent.EpcList.Count > 0 ? epcisEvent.EpcList[0] : "?");
        var awsTxId = await _awsStrategy.AnchorAsync(eventWithHash, ct);
        var awsResult = new BlockchainAnchorResult
        {
            TxId = awsTxId,
            CloudName = "AWS",
            AnchoredAt = DateTimeOffset.UtcNow,
            ChainHash = dataHash
        };
        _logger.LogInformation("AWS: アンカー完了 TxId={TxId}", awsTxId);

        // ── Step 3: ChainHash 計算 ─────────────────────────────────────
        var chainHash = HashUtil.ComputeChainHash(dataHash, awsTxId);

        // ── Step 4: Azure Ledger へ書き込み (ParentTx = awsTxId) ─────────
        if (await _azureStrategy.ExistsAsync(epcisEvent.IdempotencyKey, ct))
        {
            _logger.LogWarning("Azure: 重複イベントをスキップ (AWS は書き込み済み) key={Key}", epcisEvent.IdempotencyKey);
            throw new IdempotencyException(epcisEvent.IdempotencyKey, "Azure");
        }

        var eventWithChain = eventWithHash with
        {
            ParentTx = awsTxId,
            ChainHash = chainHash
        };

        var azureTxId = await _azureStrategy.AnchorAsync(eventWithChain, ct);
        var azureResult = new BlockchainAnchorResult
        {
            TxId = azureTxId,
            CloudName = "Azure",
            AnchoredAt = DateTimeOffset.UtcNow,
            ChainHash = chainHash
        };
        _logger.LogInformation("Azure: アンカー完了 TxId={TxId} ParentTx={Parent}", azureTxId, awsTxId);

        return (awsResult, azureResult);
    }
}

// AwsFabricOptions, AzureLedgerOptions, FabricGatewayStrategy, AzureConfidentialLedgerStrategy
// → FabricGatewayStrategy.cs / AzureLedgerStrategy.cs に移動済み

// ════════════════════════════════════════════════════════════════════════════
// 例外
// ════════════════════════════════════════════════════════════════════════════

public sealed class IdempotencyException : Exception
{
    public string IdempotencyKey { get; }
    public string Cloud { get; }

    public IdempotencyException(string key, string cloud)
        : base($"べき等性違反: key={key} cloud={cloud} は既に記録済みです。")
    {
        IdempotencyKey = key;
        Cloud = cloud;
    }
}
