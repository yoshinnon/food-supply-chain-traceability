using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using AutoMapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Traceability.AI;
using Traceability.Blockchain;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;
using Traceability.Mapping.Profiles;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Traceability.AWS.Lambda;

/// <summary>
/// AWS Lambda エントリポイント。
/// IoT Core → SQS → Lambda のフローで農地センサーデータを受信し、
/// AI修復 → EPCIS変換 → BC アンカーリングを実行する。
/// </summary>
public sealed class TraceabilityFunction
{
    private readonly IServiceProvider _services;

    public TraceabilityFunction()
    {
        _services = BuildServiceProvider();
    }

    /// <summary>DI コンテナ組み立て (Lambda コールドスタート時に一度だけ実行)</summary>
    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddConsole());
        services.AddAutoMapper(typeof(AwsToEpcisProfile), typeof(AzureToEpcisProfile));

        // AI 修復サービス
        services.Configure<AiRepairOptions>(opts =>
        {
            opts.WindowSize = 10;
            opts.ThresholdSigma = 3.0;
        });
        services.AddSingleton<IOnnxInferenceEngine, StubOnnxEngine>();
        services.AddSingleton<IAiRepairService, AiRepairService>();

        // BC クライアント
        services.Configure<AwsFabricOptions>(opts => { /* appsettings or env vars */ });
        services.Configure<AzureLedgerOptions>(opts => { /* appsettings or env vars */ });
        services.AddSingleton<IBlockchainStrategy, AwsFabricStrategy>();
        services.AddSingleton<CrossChainOrchestrator>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// SQS メッセージ (IoT Core から転送) を処理するメインハンドラ。
    /// </summary>
    public async Task FunctionHandlerAsync(SQSEvent sqsEvent, ILambdaContext context)
    {
        var logger = _services.GetRequiredService<ILogger<TraceabilityFunction>>();
        var mapper = _services.GetRequiredService<IMapper>();
        var aiService = _services.GetRequiredService<IAiRepairService>();
        var orchestrator = _services.GetRequiredService<CrossChainOrchestrator>();

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                await ProcessRecordAsync(record, mapper, aiService, orchestrator, logger);
            }
            catch (IdempotencyException ex)
            {
                logger.LogInformation("べき等性スキップ: {Message}", ex.Message);
                // 正常終了扱い (DLQ に入れない)
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "レコード処理エラー: MessageId={Id}", record.MessageId);
                // 例外を再スローして SQS に NACK → DLQ へ
                throw;
            }
        }
    }

    private static async Task ProcessRecordAsync(
        SQSEvent.SQSMessage record,
        IMapper mapper,
        IAiRepairService aiService,
        CrossChainOrchestrator orchestrator,
        ILogger logger)
    {
        logger.LogInformation("処理開始: MessageId={Id}", record.MessageId);

        // ── Step 1: IoT メッセージを生スキーマへデシリアライズ ────────────
        var rawSchema = JsonSerializer.Deserialize<AwsRawSchema>(record.Body)
            ?? throw new InvalidOperationException("メッセージのデシリアライズに失敗しました");

        // ── Step 2: EPCIS 変換 ────────────────────────────────────────────
        var epcisEvent = mapper.Map<EpcisEvent>(rawSchema);

        // ── Step 3: AI 修復 (センサー異常値チェック) ──────────────────────
        // 本来は過去 N 件を RDS から取得するが、デモでは単一値を渡す
        var quantityHistory = new List<double>
        {
            epcisEvent.QuantityList.Count > 0 ? epcisEvent.QuantityList[0].Quantity : 0
        };
        epcisEvent = await aiService.ApplyRepairToEventAsync(epcisEvent, quantityHistory);

        if (epcisEvent.IsImputed)
            logger.LogWarning("AI修復適用: {Original} → {Repaired}",
                epcisEvent.RepairMetadata?.OriginalValue,
                epcisEvent.RepairMetadata?.RepairedValue);

        // ── Step 4: クロスチェーン BC アンカーリング ─────────────────────
        var (awsResult, azureResult) = await orchestrator.AnchorExportEventAsync(epcisEvent);

        logger.LogInformation(
            "BC アンカー完了: AWS TxId={AwsTx}, Azure TxId={AzureTx}, ChainHash={Hash}",
            awsResult.TxId, azureResult.TxId, azureResult.ChainHash);
    }
}

/// <summary>Geofence イベント (IoT Core ルール → Lambda 直呼び出し) ハンドラ</summary>
public sealed class GeofenceEventFunction
{
    /// <summary>
    /// ジオフェンスイベントを輸出 EPCIS イベントに変換してキューに積む。
    /// コンテナが港の出口を超えた際に IoT Core ルールが呼び出す。
    /// </summary>
    public async Task FunctionHandlerAsync(GeofenceIotEvent iotEvent, ILambdaContext context)
    {
        context.Logger.LogInformation(
            $"Geofence イベント受信: DeviceId={iotEvent.DeviceId}, " +
            $"Fence={iotEvent.FenceId}, Lat={iotEvent.Latitude}, Lng={iotEvent.Longitude}");

        // ジオフェンス通過 → 輸出イベント生成ロジック
        // 本番では SQS へ export イベントを Enqueue する
        await Task.CompletedTask;
        context.Logger.LogInformation("輸出イベント生成完了");
    }
}

/// <summary>IoT Core から届くジオフェンスイベント形式</summary>
public sealed record GeofenceIotEvent
{
    public string DeviceId { get; init; } = string.Empty;
    public string FenceId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty; // "enter" | "exit"
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string LotNumber { get; init; } = string.Empty;
}
