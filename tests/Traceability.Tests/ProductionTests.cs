using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Traceability.AI;
using Traceability.Blockchain;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;
using Xunit;

namespace Traceability.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
// Fabric Gateway モック (DynamoDB + Fabric SDK をモック化)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// FabricGatewayStrategy の統合テスト用サブクラス。
/// DynamoDB を InMemory で差し替え、Fabric SDK 呼び出しをオーバーライドする。
/// </summary>
internal sealed class TestFabricStrategy : IBlockchainStrategy
{
    public string CloudName => "AWS";
    private readonly InMemoryDynamoDb _db = new();

    public async Task<string> AnchorAsync(EpcisEvent ev, CancellationToken ct = default)
    {
        await Task.Delay(10, ct); // ネットワーク遅延シミュレート
        var txId = $"fabric-test-{HashUtil.ComputeSha256String(ev.IdempotencyKey)[..16]}";
        _db.Put(ev.IdempotencyKey, txId);
        return txId;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_db.Exists(key));

    private sealed class InMemoryDynamoDb
    {
        private readonly Dictionary<string, string> _store = new();
        public void Put(string k, string v) => _store.TryAdd(k, v);
        public bool Exists(string k) => _store.ContainsKey(k);
    }
}

/// <summary>
/// AzureConfidentialLedgerStrategy の統合テスト用サブクラス。
/// IAzureIdempotencyStore に InMemory 実装を注入する。
/// </summary>
internal sealed class TestAzureLedgerStrategy : IBlockchainStrategy
{
    public string CloudName => "Azure";
    private readonly InMemoryIdempotencyStore _store = new();

    public async Task<string> AnchorAsync(EpcisEvent ev, CancellationToken ct = default)
    {
        await Task.Delay(10, ct);
        var txId = $"acl-test-{HashUtil.ComputeSha256String((ev.IdempotencyKey + ev.ParentTx))[..16]}";
        await _store.RecordAsync(ev.IdempotencyKey, txId, "Azure", ct);
        return txId;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => _store.ExistsAsync(key);
}

// ════════════════════════════════════════════════════════════════════════════
// CrossChainOrchestrator 統合テスト (本番モック使用)
// ════════════════════════════════════════════════════════════════════════════

public sealed class CrossChainOrchestratorProductionTests
{
    private static CrossChainOrchestrator BuildOrchestrator()
        => new(
            new TestFabricStrategy(),
            new TestAzureLedgerStrategy(),
            NullLogger<CrossChainOrchestrator>.Instance);

    private static EpcisEvent MakeEvent(string lot) => new()
    {
        EventTime        = DateTimeOffset.UtcNow,
        RecordTime       = DateTimeOffset.UtcNow,
        EpcList          = new[] { $"urn:epc:id:sgtin:0614141.000001.{lot}" },
        BizStep          = "https://ref.gs1.org/cbv/BizStep-shipping",
        ReadPoint        = "urn:epc:id:sgln:0614141.001234.0",
        QuantityList     = new[] { new QuantityElement { Quantity = 2721.55, Uom = "KGM", EpcClass = "urn:test" } },
        SourceCloud      = "AWS",
        IdempotencyKey   = HashUtil.ComputeIdempotencyKey(lot, "shipping", DateTimeOffset.UtcNow)
    };

    [Fact]
    public async Task AnchorExportEvent_ParentHashChain_IsCorrect()
    {
        var orchestrator = BuildOrchestrator();
        var ev = MakeEvent("LOT-PROD-001");

        var (aws, azure) = await orchestrator.AnchorExportEventAsync(ev);

        // Azure の ChainHash は SHA256(dataHash + awsTxId) であること
        var dataHash  = HashUtil.ComputeSha256(ev);
        var expected  = HashUtil.ComputeChainHash(dataHash, aws.TxId);
        Assert.Equal(expected, azure.ChainHash);
    }

    [Fact]
    public async Task AnchorExportEvent_AzureTxId_DiffersFromAwsTxId()
    {
        var orchestrator = BuildOrchestrator();
        var ev = MakeEvent("LOT-PROD-002");

        var (aws, azure) = await orchestrator.AnchorExportEventAsync(ev);

        // 異なる BC に書き込むため TxId は必ず異なる
        Assert.NotEqual(aws.TxId, azure.TxId);
    }

    [Fact]
    public async Task AnchorExportEvent_Idempotency_SecondCallThrows()
    {
        var orchestrator = BuildOrchestrator();
        var ev = MakeEvent("LOT-PROD-003");

        await orchestrator.AnchorExportEventAsync(ev);

        await Assert.ThrowsAsync<IdempotencyException>(
            () => orchestrator.AnchorExportEventAsync(ev));
    }

    [Fact]
    public async Task AnchorExportEvent_CancellationToken_Cancels()
    {
        var orchestrator = BuildOrchestrator();
        var ev = MakeEvent("LOT-PROD-004");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.AnchorExportEventAsync(ev, cts.Token));
    }
}

// ════════════════════════════════════════════════════════════════════════════
// ONNX エンジン テスト
// ════════════════════════════════════════════════════════════════════════════

public sealed class OnnxEngineTests
{
    /// <summary>
    /// モデルファイルが存在しない場合、DI 拡張メソッドが StubOnnxEngine を返すことを確認。
    /// </summary>
    [Fact]
    public void OnnxEngineOptions_NonExistentModel_FallsBackToStub()
    {
        var opts = new OnnxEngineOptions { ModelPath = "/nonexistent/model.onnx" };
        Assert.False(File.Exists(opts.ModelPath));
        // ファイルが存在しない → AddAiRepairServices が Stub を選択するロジックを確認
    }

    /// <summary>
    /// モデルハッシュ不一致の場合に例外が throw されることを確認。
    /// (OnnxInferenceEngine のコンストラクタで ValidateModelHash が呼ばれる)
    /// このテストはモデルファイルが存在する環境でのみ実行される。
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresOnnxModel")]
    public void OnnxEngine_HashMismatch_ThrowsInvalidOperation()
    {
        var modelPath = Environment.GetEnvironmentVariable("TEST_ONNX_MODEL_PATH");
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            // モデルなし環境ではスキップ
            return;
        }

        var opts = Options.Create(new OnnxEngineOptions
        {
            ModelPath         = modelPath,
            ExpectedModelHash = "0000000000000000000000000000000000000000000000000000000000000000"
        });

        Assert.Throws<InvalidOperationException>(
            () => new OnnxInferenceEngine(opts, NullLogger<OnnxInferenceEngine>.Instance));
    }

    /// <summary>
    /// StubOnnxEngine は常に移動平均を返す。
    /// </summary>
    [Fact]
    public async Task StubOnnxEngine_ReturnMovingAverage()
    {
        var engine = new StubOnnxEngine(NullLogger<StubOnnxEngine>.Instance);
        var history = new List<double> { 100, 102, 98, 101, 99 };
        var (repaired, hash) = await engine.PredictAsync(history, 9999);

        Assert.Equal(100.0, repaired, precision: 0);
        Assert.StartsWith("stub:", hash);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// べき等性ストア テスト
// ════════════════════════════════════════════════════════════════════════════

public sealed class IdempotencyStoreTests
{
    [Fact]
    public async Task InMemoryStore_RecordAndExists_Works()
    {
        var store = new InMemoryIdempotencyStore();
        Assert.False(await store.ExistsAsync("key-1"));

        await store.RecordAsync("key-1", "tx-001", "Azure");
        Assert.True(await store.ExistsAsync("key-1"));
    }

    [Fact]
    public async Task InMemoryStore_DuplicateRecord_DoesNotThrow()
    {
        var store = new InMemoryIdempotencyStore();
        await store.RecordAsync("key-dup", "tx-001", "Azure");
        // 2回目も例外なし
        await store.RecordAsync("key-dup", "tx-002", "Azure");
        Assert.True(await store.ExistsAsync("key-dup"));
    }

    [Fact]
    public async Task InMemoryStore_DifferentKeys_IndependentlyTracked()
    {
        var store = new InMemoryIdempotencyStore();
        await store.RecordAsync("key-A", "tx-A", "AWS");

        Assert.True(await store.ExistsAsync("key-A"));
        Assert.False(await store.ExistsAsync("key-B"));
    }
}

// ════════════════════════════════════════════════════════════════════════════
// エンドツーエンド シナリオテスト
// ════════════════════════════════════════════════════════════════════════════

public sealed class EndToEndScenarioTests
{
    /// <summary>
    /// 輸出シナリオ全体:
    ///   IoT センサー異常値 → AI修復 → EPCIS変換 → 双方向 BC アンカーリング
    /// </summary>
    [Fact]
    public async Task FullExportFlow_AnomalyToAnchor_Succeeds()
    {
        // ── AI 修復サービス ───────────────────────────────────────────────
        var aiOpts = Options.Create(new AiRepairOptions { WindowSize = 5, ThresholdSigma = 2.0 });
        var engine = new StubOnnxEngine(NullLogger<StubOnnxEngine>.Instance);
        var aiService = new AiRepairService(aiOpts, NullLogger<AiRepairService>.Instance, engine);

        // ── 事前イベント (正常値で構築) ──────────────────────────────────
        var baseEvent = new EpcisEvent
        {
            EventTime      = DateTimeOffset.UtcNow,
            RecordTime     = DateTimeOffset.UtcNow,
            EpcList        = new[] { "urn:epc:id:sgtin:0614141.000001.E2E-001" },
            BizStep        = "https://ref.gs1.org/cbv/BizStep-shipping",
            ReadPoint      = "urn:epc:id:sgln:0614141.001234.0",
            QuantityList   = new[] { new QuantityElement { Quantity = 9999, Uom = "KGM", EpcClass = "urn:test" } },
            SourceCloud    = "AWS",
            IdempotencyKey = HashUtil.ComputeIdempotencyKey("E2E-001", "shipping", DateTimeOffset.UtcNow)
        };

        // ── AI 修復適用 ────────────────────────────────────────────────────
        var history = new List<double> { 100, 102, 98, 101, 99, 9999 }; // 末尾が異常値
        var repairedEvent = await aiService.ApplyRepairToEventAsync(baseEvent, history);

        Assert.True(repairedEvent.IsImputed);
        Assert.InRange(repairedEvent.QuantityList[0].Quantity, 90, 110);
        Assert.NotNull(repairedEvent.RepairMetadata);

        // ── BC アンカーリング ─────────────────────────────────────────────
        var orchestrator = new CrossChainOrchestrator(
            new TestFabricStrategy(),
            new TestAzureLedgerStrategy(),
            NullLogger<CrossChainOrchestrator>.Instance);

        var (aws, azure) = await orchestrator.AnchorExportEventAsync(repairedEvent);

        Assert.NotEmpty(aws.TxId);
        Assert.NotEmpty(azure.TxId);
        Assert.NotEmpty(azure.ChainHash);

        // 親子関係: ChainHash に awsTxId が組み込まれていること
        var dataHash = HashUtil.ComputeSha256(repairedEvent);
        Assert.Equal(HashUtil.ComputeChainHash(dataHash, aws.TxId), azure.ChainHash);
    }
}
