using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Grpc.Core;
using Grpc.Net.Client;
using Hyperledger.Fabric.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;

namespace Traceability.Blockchain;

/// <summary>
/// AWS Managed Blockchain (Hyperledger Fabric 2.2) への本番書き込み実装。
///
/// 使用 SDK: hyperledger/fabric-gateway (C# クライアント)
/// Nuget: Hyperledger.Fabric.SDK.Gateway
///
/// べき等性ストア: Amazon DynamoDB
///   テーブル名: traceability-idempotency-{env}
///   パーティションキー: idempotency_key (String)
///   TTL 属性: expires_at (Number / Unix 秒) — 90日で自動削除
///
/// リトライ戦略:
///   Polly を使い指数バックオフ (最大3回) でリトライ。
///   MVCC_READ_CONFLICT (楽観的ロック競合) は即リトライ。
///   その他の Fabric エラーは DLQ へ転送。
/// </summary>
public sealed class FabricGatewayStrategy : IBlockchainStrategy, IAsyncDisposable
{
    public string CloudName => "AWS";

    private readonly AwsFabricOptions _options;
    private readonly ILogger<FabricGatewayStrategy> _logger;
    private readonly IAmazonDynamoDB _dynamoDb;

    // Fabric Gateway はスレッドセーフ — シングルトンとして保持
    private GrpcChannel? _channel;
    private Gateway? _gateway;
    private Network? _network;
    private Contract? _contract;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    // ── コンストラクタ ────────────────────────────────────────────────────

    public FabricGatewayStrategy(
        IOptions<AwsFabricOptions> options,
        ILogger<FabricGatewayStrategy> logger,
        IAmazonDynamoDB dynamoDb)
    {
        _options = options.Value;
        _logger = logger;
        _dynamoDb = dynamoDb;
    }

    // ── 初期化 (遅延: Lambda コールドスタートの最適化) ───────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _logger.LogInformation(
                "Fabric Gateway 初期化: endpoint={Endpoint}, msp={Msp}, channel={Channel}",
                _options.NodeEndpoint, _options.MspId, _options.ChannelName);

            // ── gRPC チャネル (TLS) ────────────────────────────────────────
            var channelOptions = new GrpcChannelOptions
            {
                Credentials = BuildTlsCredentials()
            };
            _channel = GrpcChannel.ForAddress(_options.NodeEndpoint, channelOptions);

            // ── Identity & Signer ─────────────────────────────────────────
            // 証明書と秘密鍵は Secrets Manager から取得済みを想定
            // (起動時に環境変数 FABRIC_CERT_PEM / FABRIC_KEY_PEM に展開)
            var certPem = Environment.GetEnvironmentVariable("FABRIC_CERT_PEM")
                ?? throw new InvalidOperationException("環境変数 FABRIC_CERT_PEM が未設定です");
            var keyPem = Environment.GetEnvironmentVariable("FABRIC_KEY_PEM")
                ?? throw new InvalidOperationException("環境変数 FABRIC_KEY_PEM が未設定です");

            var identity = Identities.NewX509Identity(_options.MspId, certPem);
            var signer = Signers.NewPrivateKeySigner(keyPem);

            // ── Gateway 接続 ──────────────────────────────────────────────
            _gateway = Gateway.NewBuilder()
                .Identity(identity)
                .Signer(signer)
                .Connection(_channel)
                .Build();

            _network = _gateway.GetNetwork(_options.ChannelName);
            _contract = _network.GetContract(_options.ChaincodeName);

            _initialized = true;
            _logger.LogInformation("Fabric Gateway 初期化完了");
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ── IBlockchainStrategy 実装 ──────────────────────────────────────────

    /// <summary>
    /// EPCIS イベントを Hyperledger Fabric に記録する。
    /// Chaincode 関数: CreateEpcisEvent(idempotencyKey, epcisEventJson)
    /// </summary>
    public async Task<string> AnchorAsync(EpcisEvent epcisEvent, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var eventJson = JsonSerializer.Serialize(epcisEvent, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        _logger.LogInformation(
            "Fabric: トランザクション送信 key={Key}, chainHash={Hash}",
            epcisEvent.IdempotencyKey, epcisEvent.ChainHash?[..16]);

        try
        {
            // ── トランザクション送信 (Endorsement + Order + Commit) ────────
            var result = await _contract!
                .NewTransaction("CreateEpcisEvent")
                .AddArguments(epcisEvent.IdempotencyKey, eventJson)
                .Submit(ct);

            var txId = Convert.ToHexString(result).ToLowerInvariant();

            // ── DynamoDB べき等性ストアに記録 ─────────────────────────────
            await RecordIdempotencyAsync(epcisEvent.IdempotencyKey, txId, ct);

            _logger.LogInformation("Fabric: コミット完了 TxId={TxId}", txId[..16] + "...");
            return txId;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted
            && ex.Message.Contains("MVCC_READ_CONFLICT"))
        {
            // 楽観的ロック競合 — 呼び出し元でリトライ
            _logger.LogWarning("Fabric: MVCC_READ_CONFLICT 競合 key={Key}", epcisEvent.IdempotencyKey);
            throw new FabricMvccConflictException(epcisEvent.IdempotencyKey, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fabric: トランザクション失敗 key={Key}", epcisEvent.IdempotencyKey);
            throw;
        }
    }

    /// <summary>DynamoDB でべき等性キーの存在を確認する。</summary>
    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
    {
        var request = new GetItemRequest
        {
            TableName = _options.IdempotencyTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["idempotency_key"] = new AttributeValue { S = idempotencyKey }
            },
            ProjectionExpression = "idempotency_key"
        };

        try
        {
            var response = await _dynamoDb.GetItemAsync(request, ct);
            return response.Item.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DynamoDB: べき等性チェック失敗 key={Key}", idempotencyKey);
            // フェイルオープン: チェック失敗時は重複書き込みを防ぐため存在ありとして扱う
            return true;
        }
    }

    // ── ヘルパー ──────────────────────────────────────────────────────────

    private async Task RecordIdempotencyAsync(string key, string txId, CancellationToken ct)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds();

        var request = new PutItemRequest
        {
            TableName = _options.IdempotencyTableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["idempotency_key"] = new AttributeValue { S = key },
                ["tx_id"]          = new AttributeValue { S = txId },
                ["recorded_at"]    = new AttributeValue { S = DateTimeOffset.UtcNow.ToString("O") },
                ["cloud"]          = new AttributeValue { S = "AWS" },
                ["expires_at"]     = new AttributeValue { N = expiresAt.ToString() }
            },
            // すでに存在する場合は上書きしない (二重登録防止)
            ConditionExpression = "attribute_not_exists(idempotency_key)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // 競合: 別プロセスが先に書いた — 無視してよい
            _logger.LogDebug("DynamoDB: 重複キー (競合) key={Key}", key);
        }
    }

    private ChannelCredentials BuildTlsCredentials()
    {
        // TLS CA 証明書 (AWS Managed Blockchain のルート CA)
        var tlsCert = Environment.GetEnvironmentVariable("FABRIC_TLS_CA_CERT_PEM");
        if (string.IsNullOrWhiteSpace(tlsCert))
        {
            _logger.LogWarning("FABRIC_TLS_CA_CERT_PEM 未設定: TLS 検証なしで接続します (開発環境のみ)");
            return ChannelCredentials.Insecure;
        }
        return new SslCredentials(tlsCert);
    }

    public async ValueTask DisposeAsync()
    {
        _gateway?.Dispose();
        if (_channel != null)
        {
            await _channel.ShutdownAsync();
            _channel.Dispose();
        }
        _initLock.Dispose();
    }
}

// ── 拡張オプション ────────────────────────────────────────────────────────

/// <summary>AWS Fabric 本番接続設定</summary>
public sealed class AwsFabricOptions
{
    public const string SectionName = "AwsFabric";

    /// <summary>Fabric ピアノードの gRPC エンドポイント (grpcs://xxx.amazonaws.com:30001)</summary>
    public string NodeEndpoint { get; set; } = string.Empty;

    /// <summary>チャネル名</summary>
    public string ChannelName { get; set; } = "traceability-channel";

    /// <summary>Chaincode (スマートコントラクト) 名</summary>
    public string ChaincodeName { get; set; } = "epcis-chaincode";

    /// <summary>MSP ID (組織識別子)</summary>
    public string MspId { get; set; } = string.Empty;

    /// <summary>DynamoDB べき等性テーブル名</summary>
    public string IdempotencyTableName { get; set; } = "traceability-idempotency-prod";
}

// ── 例外 ─────────────────────────────────────────────────────────────────

public sealed class FabricMvccConflictException : Exception
{
    public string IdempotencyKey { get; }
    public FabricMvccConflictException(string key, Exception inner)
        : base($"Fabric MVCC 競合: key={key}", inner)
    {
        IdempotencyKey = key;
    }
}
