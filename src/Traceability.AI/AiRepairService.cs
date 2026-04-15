using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;

namespace Traceability.AI;

/// <summary>
/// AI データ修復サービス。
/// ONNX モデルが存在する場合はそれを使用し、
/// 存在しない場合は移動平均による線形補間にフォールバックする。
/// 修復過程は RepairMetadata として透過的に記録する。
/// </summary>
public sealed class AiRepairService : IAiRepairService
{
    private readonly AiRepairOptions _options;
    private readonly IOnnxInferenceEngine? _onnxEngine;
    private readonly ILogger<AiRepairService> _logger;

    public AiRepairService(
        IOptions<AiRepairOptions> options,
        ILogger<AiRepairService> logger,
        IOnnxInferenceEngine? onnxEngine = null)
    {
        _options = options.Value;
        _logger = logger;
        _onnxEngine = onnxEngine;
    }

    /// <summary>
    /// センサー値のリストを受け取り、異常値を検知・修復した結果を返す。
    /// </summary>
    /// <param name="values">過去 N 件を含む時系列センサー値 (最新値が末尾)</param>
    /// <returns>修復結果。is_anomaly=false の場合は値をそのまま返す。</returns>
    public async Task<RepairResult> RepairAsync(IReadOnlyList<double> values)
    {
        if (values == null || values.Count == 0)
            throw new ArgumentException("値リストが空です", nameof(values));

        var current = values[^1];
        var history = values.Count > 1 ? values.SkipLast(1).ToList() : new List<double> { current };

        var (movingAvg, stdDev) = ComputeStats(history, _options.WindowSize);
        var threshold = _options.ThresholdSigma * stdDev;
        var isAnomaly = Math.Abs(current - movingAvg) > threshold && stdDev > 0;

        if (!isAnomaly)
        {
            return new RepairResult
            {
                IsAnomaly = false,
                OriginalValue = current,
                RepairedValue = current,
                MovingAvg = movingAvg,
                ModelHash = string.Empty
            };
        }

        _logger.LogWarning(
            "異常値検知: value={Value:F3}, avg={Avg:F3}, sigma={Sigma:F1}, threshold={Threshold:F3}",
            current, movingAvg, _options.ThresholdSigma, threshold);

        double repairedValue;
        string modelHash;

        if (_onnxEngine != null)
        {
            // ONNX モデルによる推論
            (repairedValue, modelHash) = await _onnxEngine.PredictAsync(history, current);
        }
        else
        {
            // スタブ: 移動平均による線形補間
            repairedValue = movingAvg;
            modelHash = "stub:moving-average-fallback";
            _logger.LogInformation("ONNX エンジン未接続: 移動平均フォールバックを使用 → {Repaired:F3}", repairedValue);
        }

        return new RepairResult
        {
            IsAnomaly = true,
            OriginalValue = current,
            RepairedValue = Math.Round(repairedValue, 4),
            MovingAvg = movingAvg,
            StdDev = stdDev,
            ModelHash = modelHash
        };
    }

    /// <summary>
    /// EpcisEvent の QuantityList に修復を適用し、RepairMetadata を付与して返す。
    /// </summary>
    public async Task<EpcisEvent> ApplyRepairToEventAsync(
        EpcisEvent ev,
        IReadOnlyList<double> quantityHistory)
    {
        var result = await RepairAsync(quantityHistory);

        if (!result.IsAnomaly)
            return ev with { IsImputed = false };

        var repairedQuantity = new QuantityElement
        {
            EpcClass = ev.QuantityList.Count > 0 ? ev.QuantityList[0].EpcClass : string.Empty,
            Quantity = result.RepairedValue,
            Uom = ev.QuantityList.Count > 0 ? ev.QuantityList[0].Uom : "KGM"
        };

        var metadata = new RepairMetadata
        {
            OriginalValue = result.OriginalValue,
            RepairedValue = result.RepairedValue,
            ModelHash = result.ModelHash,
            WindowSize = _options.WindowSize,
            MovingAvg = result.MovingAvg,
            ThresholdSigma = _options.ThresholdSigma,
            RepairedAt = DateTimeOffset.UtcNow
        };

        return ev with
        {
            IsImputed = true,
            QuantityList = new[] { repairedQuantity },
            RepairMetadata = metadata
        };
    }

    // ── 統計計算 ───────────────────────────────────────────────────────

    private static (double avg, double stdDev) ComputeStats(IReadOnlyList<double> values, int windowSize)
    {
        var window = values.TakeLast(windowSize).ToList();
        if (window.Count == 0) return (0, 0);

        var avg = window.Average();
        var variance = window.Select(v => Math.Pow(v - avg, 2)).Average();
        return (avg, Math.Sqrt(variance));
    }
}

// ── インターフェース・モデル ──────────────────────────────────────────────

public interface IAiRepairService
{
    Task<RepairResult> RepairAsync(IReadOnlyList<double> values);
    Task<EpcisEvent> ApplyRepairToEventAsync(EpcisEvent ev, IReadOnlyList<double> quantityHistory);
}

/// <summary>
/// ONNX 推論エンジンの抽象。
/// 本番では Microsoft.ML.OnnxRuntime を実装して DI に登録する。
/// </summary>
public interface IOnnxInferenceEngine
{
    /// <summary>
    /// 過去値と現在値から修復値を推論する。
    /// </summary>
    /// <returns>(repairedValue, modelHash)</returns>
    Task<(double repairedValue, string modelHash)> PredictAsync(
        IReadOnlyList<double> history, double current);

    /// <summary>モデルのロード時 SHA-256 ハッシュ (クラウド間で一致を検証する)</summary>
    string ModelHash { get; }
}

/// <summary>
/// ONNX エンジンのスタブ実装。
/// テスト・開発環境でモデルなしに動作させるために使用する。
/// </summary>
public sealed class StubOnnxEngine : IOnnxInferenceEngine
{
    private readonly ILogger<StubOnnxEngine> _logger;
    public string ModelHash => "stub:00000000000000000000000000000000";

    public StubOnnxEngine(ILogger<StubOnnxEngine> logger)
    {
        _logger = logger;
    }

    public Task<(double repairedValue, string modelHash)> PredictAsync(
        IReadOnlyList<double> history, double current)
    {
        _logger.LogDebug("StubOnnxEngine.PredictAsync called (moving average)");
        var avg = history.Count > 0 ? history.Average() : current;
        return Task.FromResult((avg, ModelHash));
    }
}

/// <summary>修復処理の結果</summary>
public sealed record RepairResult
{
    public bool IsAnomaly { get; init; }
    public double OriginalValue { get; init; }
    public double RepairedValue { get; init; }
    public double MovingAvg { get; init; }
    public double StdDev { get; init; }
    public string ModelHash { get; init; } = string.Empty;
}

/// <summary>AI 修復エンジンの設定</summary>
public sealed class AiRepairOptions
{
    public const string SectionName = "AiRepair";

    /// <summary>移動平均の窓サイズ N</summary>
    public int WindowSize { get; set; } = 10;

    /// <summary>異常判定閾値 (標準偏差の倍数σ)</summary>
    public double ThresholdSigma { get; set; } = 3.0;
}
