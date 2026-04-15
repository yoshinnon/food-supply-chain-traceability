using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AutoMapper;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Traceability.AI;
using Traceability.Blockchain;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;
using Traceability.Mapping.Profiles;

namespace Traceability.Azure.Function;

/// <summary>
/// Azure Functions エントリポイント。
/// IoT Hub → Service Bus → Function のフローで農地センサーデータを受信し、
/// AI修復 → EPCIS変換 → BC アンカーリングを実行する。
/// AWS Lambda (TraceabilityFunction.cs) と対称的な構造を持つ。
/// </summary>
public sealed class TraceabilityFunction
{
    private readonly IMapper _mapper;
    private readonly IAiRepairService _aiService;
    private readonly CrossChainOrchestrator _orchestrator;
    private readonly ILogger<TraceabilityFunction> _logger;

    // DI コンテナから注入 (Isolated Worker モデル)
    public TraceabilityFunction(
        IMapper mapper,
        IAiRepairService aiService,
        CrossChainOrchestrator orchestrator,
        ILogger<TraceabilityFunction> logger)
    {
        _mapper       = mapper;
        _aiService    = aiService;
        _orchestrator = orchestrator;
        _logger       = logger;
    }

    /// <summary>
    /// Service Bus キュー (IoT Hub → Service Bus) トリガー。
    /// Dead Letter は Service Bus の組み込み機能で自動処理される。
    /// </summary>
    [Function("TraceabilityProcessor")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            queueName: "%ServiceBusQueueName%",
            Connection = "ServiceBusConnection"
        )]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        _logger.LogInformation(
            "メッセージ受信: MessageId={Id}, EnqueuedAt={At}",
            message.MessageId,
            message.EnqueuedTime);

        try
        {
            await ProcessMessageAsync(message);
            // 正常完了: Service Bus にACKを返す
            await messageActions.CompleteMessageAsync(message);
        }
        catch (IdempotencyException ex)
        {
            _logger.LogInformation("べき等性スキップ: {Message}", ex.Message);
            await messageActions.CompleteMessageAsync(message); // DLQ に入れない
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "処理エラー: MessageId={Id}", message.MessageId);
            // NACK → Service Bus が MaxDeliveryCount 回リトライ後に DLQ へ転送
            await messageActions.AbandonMessageAsync(message);
            throw;
        }
    }

    private async Task ProcessMessageAsync(ServiceBusReceivedMessage message)
    {
        // ── Step 1: IoT メッセージを生スキーマへデシリアライズ ────────────
        var body = message.Body.ToString();
        var rawSchema = JsonSerializer.Deserialize<AzureRawSchema>(body)
            ?? throw new InvalidOperationException("メッセージのデシリアライズ失敗");

        // ── Step 2: EPCIS 変換 ────────────────────────────────────────────
        var epcisEvent = _mapper.Map<EpcisEvent>(rawSchema);

        // ── Step 3: AI 修復 ──────────────────────────────────────────────
        var quantityHistory = new List<double>
        {
            epcisEvent.QuantityList.Count > 0
                ? epcisEvent.QuantityList[0].Quantity
                : 0
        };
        epcisEvent = await _aiService.ApplyRepairToEventAsync(epcisEvent, quantityHistory);

        if (epcisEvent.IsImputed)
            _logger.LogWarning(
                "AI修復適用: {Original} → {Repaired}",
                epcisEvent.RepairMetadata?.OriginalValue,
                epcisEvent.RepairMetadata?.RepairedValue);

        // ── Step 4: BC アンカーリング (Azure が受領側 = ParentTx を受け取る) ─
        // 輸出側 (AWS) から ParentTx が渡されている場合はクロスチェーンリンクを確立
        // 純粋な日本国内イベントの場合は ParentTx なしで Azure Ledger のみに書き込む
        if (!string.IsNullOrWhiteSpace(epcisEvent.ParentTx))
        {
            // 輸入イベント: AWS TxID をクロスチェーンで受け取り済み
            var chainHash = HashUtil.ComputeChainHash(epcisEvent.DataHash, epcisEvent.ParentTx);
            epcisEvent = epcisEvent with { ChainHash = chainHash };
            _logger.LogInformation(
                "クロスチェーンリンク確立: ParentTx={Parent}",
                epcisEvent.ParentTx[..16]);
        }

        var (awsResult, azureResult) = await _orchestrator.AnchorExportEventAsync(epcisEvent);

        _logger.LogInformation(
            "BC アンカー完了: AWS TxId={AwsTx}, Azure TxId={AzureTx}, ChainHash={Hash}",
            awsResult.TxId, azureResult.TxId, azureResult.ChainHash);
    }

    /// <summary>
    /// IoT Hub ジオフェンスイベントを受信して輸入受領イベントを生成する。
    /// Azure IoT Hub → Event Grid → Function (HTTP トリガー)
    /// </summary>
    [Function("GeofenceReceiver")]
    public async Task<GeofenceResult> GeofenceHandlerAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "geofence")]
        GeofenceIotEvent iotEvent,
        FunctionContext context)
    {
        var log = context.GetLogger<TraceabilityFunction>();
        log.LogInformation(
            "ジオフェンス通過 (日本側): DeviceId={Id}, Fence={Fence}",
            iotEvent.DeviceId, iotEvent.FenceId);

        // 横浜港 入港ジオフェンス → 受領 (receiving) イベントを生成
        await Task.CompletedTask;
        return new GeofenceResult { Status = "accepted", LotNumber = iotEvent.LotNumber };
    }
}

/// <summary>HTTP トリガーのレスポンス</summary>
public sealed record GeofenceResult
{
    public string Status { get; init; } = string.Empty;
    public string LotNumber { get; init; } = string.Empty;
}

/// <summary>IoT Hub から届くジオフェンスイベント形式 (Lambda 側と共通)</summary>
public sealed record GeofenceIotEvent
{
    public string DeviceId { get; init; } = string.Empty;
    public string FenceId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string LotNumber { get; init; } = string.Empty;
}

// ════════════════════════════════════════════════════════════════════════════
// DI / ホスト設定 (Program.cs 相当)
// ════════════════════════════════════════════════════════════════════════════

public static class FunctionHostBuilder
{
    public static IHostBuilder ConfigureTraceabilityServices(this IHostBuilder builder)
    {
        return builder.ConfigureFunctionsWorkerDefaults()
            .ConfigureServices((ctx, services) =>
            {
                var cfg = ctx.Configuration;

                services.AddLogging(b => b.AddConsole());
                services.AddAutoMapper(
                    typeof(AwsToEpcisProfile),
                    typeof(AzureToEpcisProfile));

                // AI 修復サービス
                services.AddAiRepairServices(
                    configureOnnx: opts =>
                    {
                        opts.ModelPath         = cfg["OnnxEngine:ModelPath"]
                                                 ?? "/home/site/wwwroot/models/repair_model.onnx";
                        opts.ExpectedModelHash = cfg["ONNX_MODEL_HASH"];
                        opts.WindowSize        = 10;
                    },
                    configureRepair: opts =>
                    {
                        opts.WindowSize      = 10;
                        opts.ThresholdSigma  = 3.0;
                    });

                // BC クライアント (Azure 側は ACL + Fabric 両方に書く)
                services.Configure<AzureLedgerOptions>(cfg.GetSection(AzureLedgerOptions.SectionName));
                services.Configure<AwsFabricOptions>(cfg.GetSection(AwsFabricOptions.SectionName));
                services.AddSingleton<IAzureIdempotencyStore, TableStorageIdempotencyStore>();
                services.AddSingleton<IBlockchainStrategy, AzureConfidentialLedgerStrategy>();
                services.AddSingleton<CrossChainOrchestrator>();
            });
    }
}
