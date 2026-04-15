using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Traceability.Common.DTOs;

namespace Traceability.Blockchain;

/// <summary>
/// Hyperledger Fabric へのトランザクション送信に対する
/// Polly ベースのリトライポリシー。
///
/// ポリシー設計:
///   MVCC_READ_CONFLICT : 楽観的ロック競合 → 即リトライ (最大5回、指数バックオフ)
///   一時的ネットワーク障害 : 最大3回、指数バックオフ (1s→2s→4s)
///   IdempotencyException : リトライしない (正常ケース)
///   その他の例外        : リトライしない → DLQ へ転送
/// </summary>
public sealed class FabricRetryPolicy
{
    private readonly AsyncRetryPolicy<string> _mvccPolicy;
    private readonly AsyncRetryPolicy<string> _networkPolicy;
    private readonly ILogger<FabricRetryPolicy> _logger;

    public FabricRetryPolicy(ILogger<FabricRetryPolicy> logger)
    {
        _logger = logger;

        // MVCC 競合ポリシー: 指数バックオフ (100ms → 200ms → 400ms → 800ms → 1600ms)
        _mvccPolicy = Policy<string>
            .Handle<FabricMvccConflictException>()
            .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, timespan, attempt, _) =>
                    _logger.LogWarning(
                        "Fabric MVCC リトライ {Attempt}/5: key={Key}, 待機={Wait}ms",
                        attempt,
                        (outcome.Exception as FabricMvccConflictException)?.IdempotencyKey,
                        timespan.TotalMilliseconds));

        // ネットワーク障害ポリシー: 指数バックオフ (1s → 2s → 4s)
        _networkPolicy = Policy<string>
            .Handle<Exception>(ex =>
                ex is not FabricMvccConflictException
                && ex is not IdempotencyException
                && IsTransient(ex))
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)),
                onRetry: (outcome, timespan, attempt, _) =>
                    _logger.LogWarning(
                        "Fabric ネットワークリトライ {Attempt}/3: {Error}, 待機={Wait}s",
                        attempt,
                        outcome.Exception.Message,
                        timespan.TotalSeconds));
    }

    /// <summary>
    /// リトライポリシーを適用して BC 書き込みを実行する。
    /// MVCC 競合 → ネットワーク障害 の順に適用する (ラップ構造)。
    /// </summary>
    public async Task<string> ExecuteAsync(
        Func<CancellationToken, Task<string>> operation,
        CancellationToken ct = default)
    {
        // 外側: ネットワーク障害ポリシー
        // 内側: MVCC 競合ポリシー
        return await _networkPolicy.ExecuteAsync(
            async (ctx, token) => await _mvccPolicy.ExecuteAsync(
                innerToken => operation(innerToken), token),
            new Context(),
            ct);
    }

    private static bool IsTransient(Exception ex)
    {
        // gRPC の一時的エラーコードを判定
        if (ex is Grpc.Core.RpcException rpc)
        {
            return rpc.StatusCode is
                Grpc.Core.StatusCode.Unavailable or
                Grpc.Core.StatusCode.DeadlineExceeded or
                Grpc.Core.StatusCode.ResourceExhausted;
        }
        // タイムアウト系
        return ex is TimeoutException or OperationCanceledException;
    }
}

/// <summary>
/// FabricRetryPolicy を組み込んだ FabricGatewayStrategy デコレーター。
/// DI で FabricGatewayStrategy の代わりに登録することでリトライを透過的に追加する。
/// </summary>
public sealed class RetryableFabricStrategy : IBlockchainStrategy
{
    public string CloudName => "AWS";

    private readonly FabricGatewayStrategy _inner;
    private readonly FabricRetryPolicy _retryPolicy;

    public RetryableFabricStrategy(
        FabricGatewayStrategy inner,
        FabricRetryPolicy retryPolicy)
    {
        _inner = inner;
        _retryPolicy = retryPolicy;
    }

    public Task<string> AnchorAsync(EpcisEvent epcisEvent, CancellationToken ct = default)
        => _retryPolicy.ExecuteAsync(
            token => _inner.AnchorAsync(epcisEvent, token), ct);

    public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
        => _inner.ExistsAsync(idempotencyKey, ct);
}
