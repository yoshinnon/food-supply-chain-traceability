using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Traceability.AI;

/// <summary>
/// ONNX Runtime を使った本番推論エンジン。
///
/// モデル仕様 (想定):
///   Input  : "input"  shape=[1, window_size]  float32  — 過去 N 件の正規化済み時系列
///   Output : "output" shape=[1, 1]            float32  — 修復後の正規化済み値
///
/// 起動時にモデルファイルの SHA-256 を計算し、
/// 環境変数 ONNX_MODEL_HASH と照合して両クラウドでの同一性を保証する。
/// </summary>
public sealed class OnnxInferenceEngine : IOnnxInferenceEngine, IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxEngineOptions _options;
    private readonly ILogger<OnnxInferenceEngine> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public string ModelHash { get; }

    // ── コンストラクタ ────────────────────────────────────────────────────

    public OnnxInferenceEngine(
        IOptions<OnnxEngineOptions> options,
        ILogger<OnnxInferenceEngine> logger)
    {
        _options = options.Value;
        _logger = logger;

        // モデルファイルを読み込み
        if (!File.Exists(_options.ModelPath))
            throw new FileNotFoundException(
                $"ONNX モデルファイルが見つかりません: {_options.ModelPath}");

        var modelBytes = File.ReadAllBytes(_options.ModelPath);

        // ── モデルハッシュ検証 ────────────────────────────────────────────
        ModelHash = ComputeFileHash(modelBytes);
        ValidateModelHash(ModelHash, _options.ExpectedModelHash);

        // ── セッション初期化 ───────────────────────────────────────────────
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = _options.NumThreads
        };

        // CPU EP を明示的に有効化 (Lambda/Functions はGPU不要)
        sessionOptions.AppendExecutionProvider_CPU(0);

        _session = new InferenceSession(modelBytes, sessionOptions);

        _logger.LogInformation(
            "ONNX モデル読み込み完了: path={Path}, hash={Hash}, inputs={Inputs}",
            _options.ModelPath,
            ModelHash[..16] + "...",
            string.Join(", ", _session.InputNames));
    }

    // ── 推論 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 過去の時系列値と現在値から修復値を推論する。
    /// </summary>
    /// <param name="history">過去値 (新しい順に並んでいる必要はない — 時系列順)</param>
    /// <param name="current">異常と判定された現在値 (参照用、入力には含めない)</param>
    /// <returns>(修復後の実スケール値, モデルハッシュ)</returns>
    public async Task<(double repairedValue, string modelHash)> PredictAsync(
        IReadOnlyList<double> history,
        double current)
    {
        await _sessionLock.WaitAsync();
        try
        {
            // ── 前処理: ウィンドウ切り出し & 正規化 ─────────────────────
            var window = PrepareWindow(history, _options.WindowSize);
            var (mean, std) = ComputeMeanStd(window);
            var normalized = window.Select(v => std > 0 ? (float)((v - mean) / std) : 0f).ToArray();

            // ── 推論 ──────────────────────────────────────────────────────
            var inputTensor = new DenseTensor<float>(normalized, new[] { 1, _options.WindowSize });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_options.InputNodeName, inputTensor)
            };

            using var results = _session.Run(inputs);
            var outputTensor = results.First(r => r.Name == _options.OutputNodeName)
                .AsTensor<float>();

            var normalizedOutput = outputTensor[0, 0];

            // ── 後処理: 逆正規化 ─────────────────────────────────────────
            var repairedValue = std > 0
                ? normalizedOutput * std + mean
                : mean;

            // 物理的に負の重量は不正 → クリッピング
            repairedValue = Math.Max(repairedValue, 0f);

            _logger.LogDebug(
                "ONNX 推論: current={Current:F3} → repaired={Repaired:F3} (mean={Mean:F3}, std={Std:F3})",
                current, repairedValue, mean, std);

            return (Math.Round(repairedValue, 4), ModelHash);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // ── ヘルパー ──────────────────────────────────────────────────────────

    /// <summary>
    /// 履歴から WindowSize 分のウィンドウを作る。
    /// 履歴が足りない場合は末尾値でパディングする。
    /// </summary>
    private static float[] PrepareWindow(IReadOnlyList<double> history, int windowSize)
    {
        var result = new float[windowSize];
        var src = history.TakeLast(windowSize).Select(v => (float)v).ToArray();
        var offset = windowSize - src.Length;

        // 先頭をパディング (末尾値で埋める)
        var padValue = src.Length > 0 ? src[0] : 0f;
        for (var i = 0; i < offset; i++) result[i] = padValue;
        src.CopyTo(result, offset);
        return result;
    }

    private static (double mean, double std) ComputeMeanStd(float[] values)
    {
        if (values.Length == 0) return (0, 0);
        var mean = values.Average();
        var variance = values.Select(v => Math.Pow(v - mean, 2)).Average();
        return (mean, Math.Sqrt(variance));
    }

    private static string ComputeFileHash(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void ValidateModelHash(string actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogWarning(
                "ONNX_MODEL_HASH が未設定です。モデル同一性検証をスキップします。" +
                "本番環境では必ず設定してください。actual_hash={Hash}", actual);
            return;
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ONNX モデルハッシュ不一致。" +
                $"期待値={expected[..16]}... 実際={actual[..16]}..." +
                $"\nAWS と Azure で異なるモデルがロードされています。デプロイを確認してください。");
        }

        _logger.LogInformation("ONNX モデルハッシュ検証 OK: {Hash}", actual[..16] + "...");
    }

    public void Dispose()
    {
        _session.Dispose();
        _sessionLock.Dispose();
    }
}

// ── 設定 ─────────────────────────────────────────────────────────────────

/// <summary>ONNX エンジン設定</summary>
public sealed class OnnxEngineOptions
{
    public const string SectionName = "OnnxEngine";

    /// <summary>ONNX モデルファイルのパス (コンテナ内 or Lambda レイヤー)</summary>
    public string ModelPath { get; set; } = "/opt/models/repair-model.onnx";

    /// <summary>
    /// 期待するモデルの SHA-256 ハッシュ。
    /// AWS / Azure 両環境で同一値を設定することで、
    /// 異なるモデルが混入していないことを起動時に保証する。
    /// 環境変数 ONNX_MODEL_HASH から注入するのが推奨。
    /// </summary>
    public string? ExpectedModelHash { get; set; }

    /// <summary>入力ノード名 (モデル仕様に合わせて変更)</summary>
    public string InputNodeName { get; set; } = "input";

    /// <summary>出力ノード名 (モデル仕様に合わせて変更)</summary>
    public string OutputNodeName { get; set; } = "output";

    /// <summary>推論ウィンドウサイズ (AiRepairOptions.WindowSize と揃える)</summary>
    public int WindowSize { get; set; } = 10;

    /// <summary>ONNX Runtime 並列スレッド数</summary>
    public int NumThreads { get; set; } = 1;
}

// ── DI 登録ヘルパー ────────────────────────────────────────────────────────

public static class OnnxServiceExtensions
{
    /// <summary>
    /// ONNX エンジンと AI 修復サービスを DI に登録する。
    /// モデルファイルが存在する場合は本番エンジン、
    /// 存在しない場合はスタブエンジンを自動選択する。
    /// </summary>
    public static IServiceCollection AddAiRepairServices(
        this IServiceCollection services,
        Action<OnnxEngineOptions>? configureOnnx = null,
        Action<AiRepairOptions>? configureRepair = null)
    {
        // AiRepair 設定
        if (configureRepair != null)
            services.Configure(configureRepair);
        else
            services.Configure<AiRepairOptions>(_ => { });

        // ONNX 設定
        if (configureOnnx != null)
            services.Configure(configureOnnx);
        else
            services.Configure<OnnxEngineOptions>(_ => { });

        // モデルファイルの有無で自動切り替え
        services.AddSingleton<IOnnxInferenceEngine>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OnnxEngineOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<OnnxInferenceEngine>>();
            var stubLogger = sp.GetRequiredService<ILogger<StubOnnxEngine>>();

            if (File.Exists(opts.ModelPath))
            {
                logger.LogInformation("本番 ONNX エンジンを使用: {Path}", opts.ModelPath);
                return new OnnxInferenceEngine(
                    sp.GetRequiredService<IOptions<OnnxEngineOptions>>(), logger);
            }

            logger.LogWarning(
                "ONNX モデルファイルが見つかりません ({Path})。スタブエンジンを使用します。",
                opts.ModelPath);
            return new StubOnnxEngine(stubLogger);
        });

        services.AddSingleton<IAiRepairService, AiRepairService>();
        return services;
    }
}

// IServiceCollection を using なしで使うための最小宣言
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
