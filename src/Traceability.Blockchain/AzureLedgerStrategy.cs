using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.ConfidentialLedger;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;

namespace Traceability.Blockchain;

/// <summary>
/// Azure Confidential Ledger (ACL) への本番書き込み実装。
///
/// 使用 SDK: Azure.Security.ConfidentialLedger
/// NuGet: Azure.Security.ConfidentialLedger
///
/// 認証:
///   Azure Functions の Managed Identity (SystemAssigned) を使用。
///   ローカル / Lambda 側からのアクセスは OIDC Federation で対応。
///
/// べき等性ストア: Azure Table Storage
///   テーブル名: traceabilityidempotency
///   PartitionKey: "AWS" | "Azure"
///   RowKey: idempotency_key
///
/// ACL の特性:
///   - append-only: 一度書いたエントリは変更不可
///   - TransactionId は ACL が返す文字列 (例: "2.15")
///   - コレクション ID でテナント分離可能
///   - 書き込みは非同期コミット → WaitUntil.Completed で確定を待つ
/// </summary>
public sealed class AzureConfidentialLedgerStrategy : IBlockchainStrategy
{
    public string CloudName => "Azure";

    private readonly AzureLedgerOptions _options;
    private readonly ILogger<AzureConfidentialLedgerStrategy> _logger;

    // SDK クライアントはスレッドセーフ — シングルトンとして保持
    private ConfidentialLedgerClient? _ledgerClient;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    // べき等性ストア (Azure Table Storage)
    private readonly IAzureIdempotencyStore _idempotencyStore;

    // ── コンストラクタ ────────────────────────────────────────────────────

    public AzureConfidentialLedgerStrategy(
        IOptions<AzureLedgerOptions> options,
        ILogger<AzureConfidentialLedgerStrategy> logger,
        IAzureIdempotencyStore idempotencyStore)
    {
        _options = options.Value;
        _logger = logger;
        _idempotencyStore = idempotencyStore;
    }

    // ── 初期化 ────────────────────────────────────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            if (string.IsNullOrWhiteSpace(_options.LedgerEndpoint))
                throw new InvalidOperationException(
                    "AzureLedger:LedgerEndpoint が未設定です。Key Vault から注入されているか確認してください。");

            _logger.LogInformation(
                "Azure Confidential Ledger 初期化: endpoint={Endpoint}, collection={Collection}",
                _options.LedgerEndpoint, _options.CollectionId);

            // ── 認証: Functions の Managed Identity を優先、フォールバックで DefaultAzureCredential ─
            TokenCredential credential = _options.UseDefaultCredential
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential();

            _ledgerClient = new ConfidentialLedgerClient(
                new Uri(_options.LedgerEndpoint),
                credential);

            _initialized = true;
            _logger.LogInformation("Azure Confidential Ledger 初期化完了");
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ── IBlockchainStrategy 実装 ──────────────────────────────────────────

    /// <summary>
    /// EPCIS イベントを Azure Confidential Ledger に追記する。
    ///
    /// ペイロード構造:
    /// {
    ///   "idempotency_key": "...",
    ///   "chain_hash":      "SHA256(dataHash + parentTxId)",
    ///   "data_hash":       "SHA256(epcisEvent)",
    ///   "parent_tx":       "fabric-txid-...",   // AWS 側 TxID
    ///   "event_time":      "2025-06-01T12:00:00Z",
    ///   "biz_step":        "https://ref.gs1.org/cbv/BizStep-receiving",
    ///   "source_cloud":    "Azure",
    ///   "epc_list":        ["urn:epc:id:sgtin:..."],
    ///   "is_imputed":      false
    /// }
    /// </summary>
    public async Task<string> AnchorAsync(EpcisEvent epcisEvent, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // ── ペイロード構築 ────────────────────────────────────────────────
        var payload = new
        {
            idempotency_key = epcisEvent.IdempotencyKey,
            chain_hash      = epcisEvent.ChainHash ?? epcisEvent.DataHash,
            data_hash       = epcisEvent.DataHash,
            parent_tx       = epcisEvent.ParentTx,
            event_time      = epcisEvent.EventTime.ToString("O"),
            record_time     = epcisEvent.RecordTime.ToString("O"),
            biz_step        = epcisEvent.BizStep,
            read_point      = epcisEvent.ReadPoint,
            source_cloud    = epcisEvent.SourceCloud,
            epc_list        = epcisEvent.EpcList,
            is_imputed      = epcisEvent.IsImputed,
            repair_model    = epcisEvent.RepairMetadata?.ModelHash
        };

        var content = RequestContent.Create(
            BinaryData.FromObjectAsJson(payload));

        _logger.LogInformation(
            "ACL: エントリ追記 key={Key}, parentTx={Parent}",
            epcisEvent.IdempotencyKey,
            epcisEvent.ParentTx?[..16] ?? "none");

        try
        {
            // WaitUntil.Completed = ACL がコミットを確認するまでポーリング
            var operation = await _ledgerClient!.PostLedgerEntryAsync(
                WaitUntil.Completed,
                content,
                _options.CollectionId,
                ct);

            // TransactionId を取得 (例: "2.15" — サブレジャー番号.シーケンス番号)
            var txId = operation.Value.TransactionId;

            // ── べき等性ストアに記録 ──────────────────────────────────────
            await _idempotencyStore.RecordAsync(epcisEvent.IdempotencyKey, txId, "Azure", ct);

            _logger.LogInformation("ACL: コミット完了 TransactionId={TxId}", txId);
            return txId;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // ACL の Conflict (べき等性重複) — 正常扱い
            _logger.LogInformation("ACL: 重複エントリ (409) key={Key}", epcisEvent.IdempotencyKey);
            throw new IdempotencyException(epcisEvent.IdempotencyKey, "Azure");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACL: 書き込み失敗 key={Key}", epcisEvent.IdempotencyKey);
            throw;
        }
    }

    /// <summary>べき等性ストアで既存エントリを確認する。</summary>
    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
        => await _idempotencyStore.ExistsAsync(idempotencyKey, ct);
}

// ════════════════════════════════════════════════════════════════════════════
// べき等性ストア (Azure Table Storage)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>べき等性ストアの抽象 (テスト差し替え可能)</summary>
public interface IAzureIdempotencyStore
{
    Task RecordAsync(string key, string txId, string cloud, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Azure Table Storage を使ったべき等性ストア実装。
/// NuGet: Azure.Data.Tables
/// </summary>
public sealed class TableStorageIdempotencyStore : IAzureIdempotencyStore
{
    private readonly Azure.Data.Tables.TableClient _tableClient;
    private readonly ILogger<TableStorageIdempotencyStore> _logger;

    public TableStorageIdempotencyStore(
        IOptions<AzureLedgerOptions> options,
        ILogger<TableStorageIdempotencyStore> logger)
    {
        _logger = logger;
        var credential = new DefaultAzureCredential();
        _tableClient = new Azure.Data.Tables.TableClient(
            new Uri(options.Value.TableStorageEndpoint),
            options.Value.IdempotencyTableName,
            credential);
    }

    public async Task RecordAsync(string key, string txId, string cloud, CancellationToken ct)
    {
        var entity = new Azure.Data.Tables.TableEntity("Traceability", key)
        {
            ["TxId"]       = txId,
            ["Cloud"]      = cloud,
            ["RecordedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };

        try
        {
            await _tableClient.AddEntityAsync(entity, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogDebug("Table Storage: 重複キー (競合) key={Key}", key);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<Azure.Data.Tables.TableEntity>(
                "Traceability", key, cancellationToken: ct);
            return response.Value != null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Table Storage: べき等性チェック失敗 key={Key}", key);
            return true; // フェイルオープン
        }
    }
}

/// <summary>テスト用インメモリべき等性ストア</summary>
public sealed class InMemoryIdempotencyStore : IAzureIdempotencyStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _store = new();

    public Task RecordAsync(string key, string txId, string cloud, CancellationToken ct = default)
    {
        _store.TryAdd(key, txId);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_store.ContainsKey(key));
}

// ── 設定 ─────────────────────────────────────────────────────────────────

/// <summary>Azure Confidential Ledger 本番設定</summary>
public sealed class AzureLedgerOptions
{
    public const string SectionName = "AzureLedger";

    /// <summary>ACL エンドポイント URL (Key Vault 経由で注入)</summary>
    public string LedgerEndpoint { get; set; } = string.Empty;

    /// <summary>コレクション ID (テナント分離)</summary>
    public string CollectionId { get; set; } = "traceability";

    /// <summary>Azure Table Storage エンドポイント</summary>
    public string TableStorageEndpoint { get; set; } = string.Empty;

    /// <summary>べき等性テーブル名</summary>
    public string IdempotencyTableName { get; set; } = "traceabilityidempotency";

    /// <summary>
    /// true: DefaultAzureCredential (ローカル・CI 環境)
    /// false: ManagedIdentityCredential (Functions 本番)
    /// </summary>
    public bool UseDefaultCredential { get; set; } = false;
}
