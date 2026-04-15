using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Traceability.Common.DTOs;

/// <summary>
/// EPCIS 2.0 準拠のオブジェクトイベント。
/// オンチェーンに記録する「唯一の真実」となるデータ構造。
/// </summary>
public sealed record EpcisEvent
{
    /// <summary>EPCIS 2.0 context URI</summary>
    [JsonPropertyName("@context")]
    public string Context { get; init; } = "https://ref.gs1.org/standards/epcis/epcis-context.jsonld";

    /// <summary>イベント種別 (ObjectEvent 固定)</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "ObjectEvent";

    /// <summary>イベント発生時刻 (UTC)</summary>
    [JsonPropertyName("eventTime")]
    public DateTimeOffset EventTime { get; init; }

    /// <summary>イベント記録時刻 (UTC)</summary>
    [JsonPropertyName("recordTime")]
    public DateTimeOffset RecordTime { get; init; }

    /// <summary>GS1識別子リスト (sgtin形式)</summary>
    [JsonPropertyName("epcList")]
    public IReadOnlyList<string> EpcList { get; init; } = Array.Empty<string>();

    /// <summary>ビジネス工程識別子 (shipping / receiving / harvesting 等)</summary>
    [JsonPropertyName("bizStep")]
    public string BizStep { get; init; } = string.Empty;

    /// <summary>場所識別子 (sgln形式)</summary>
    [JsonPropertyName("readPoint")]
    public string ReadPoint { get; init; } = string.Empty;

    /// <summary>ビジネス場所識別子 (sgln形式)</summary>
    [JsonPropertyName("bizLocation")]
    public string BizLocation { get; init; } = string.Empty;

    /// <summary>数量リスト (バルク農産物向け)</summary>
    [JsonPropertyName("quantityList")]
    public IReadOnlyList<QuantityElement> QuantityList { get; init; } = Array.Empty<QuantityElement>();

    /// <summary>AIによる修復が行われたか否か</summary>
    [JsonPropertyName("is_imputed")]
    public bool IsImputed { get; init; }

    /// <summary>オフチェーンデータの SHA-256 ハッシュ (Hex 文字列)</summary>
    [JsonPropertyName("data_hash")]
    public string DataHash { get; init; } = string.Empty;

    /// <summary>
    /// クロスチェーン親ポインタ。
    /// 輸入側が輸出側 BC の TxID をここに格納することで
    /// 「事実の連続性」を数学的に証明する。
    /// </summary>
    [JsonPropertyName("parentTx")]
    public string? ParentTx { get; init; }

    /// <summary>
    /// ParentTx を含むファイナルハッシュ。
    /// Hash_final = SHA256(DataHash + ParentTxID)
    /// </summary>
    [JsonPropertyName("chain_hash")]
    public string? ChainHash { get; init; }

    /// <summary>AI修復メタデータ (修復があった場合のみ)</summary>
    [JsonPropertyName("repair_metadata")]
    public RepairMetadata? RepairMetadata { get; init; }

    /// <summary>発行元クラウド識別子 ("AWS" | "Azure")</summary>
    [JsonPropertyName("source_cloud")]
    public string SourceCloud { get; init; } = string.Empty;

    /// <summary>べき等性キー: 同一データの二重書き込みを防ぐ</summary>
    [JsonPropertyName("idempotency_key")]
    public string IdempotencyKey { get; init; } = string.Empty;
}

/// <summary>EPCIS 2.0 QuantityElement</summary>
public sealed record QuantityElement
{
    [JsonPropertyName("epcClass")]
    public string EpcClass { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public double Quantity { get; init; }

    /// <summary>SI 単位 (KGM = kg, LTR = litre 等 UN/CEFACT コード)</summary>
    [JsonPropertyName("uom")]
    public string Uom { get; init; } = "KGM";
}

/// <summary>AI修復の透明性を担保するメタデータ</summary>
public sealed record RepairMetadata
{
    /// <summary>修復前の生センサー値</summary>
    [JsonPropertyName("original_value")]
    public double OriginalValue { get; init; }

    /// <summary>ONNXモデルが算出した修復後の値</summary>
    [JsonPropertyName("repaired_value")]
    public double RepairedValue { get; init; }

    /// <summary>使用した ONNX モデルの SHA-256 ハッシュ (両クラウドで同一を保証)</summary>
    [JsonPropertyName("model_hash")]
    public string ModelHash { get; init; } = string.Empty;

    /// <summary>異常検知に使用した移動平均の窓サイズ N</summary>
    [JsonPropertyName("window_size")]
    public int WindowSize { get; init; }

    /// <summary>移動平均値</summary>
    [JsonPropertyName("moving_avg")]
    public double MovingAvg { get; init; }

    /// <summary>検知閾値 (標準偏差の倍数)</summary>
    [JsonPropertyName("threshold_sigma")]
    public double ThresholdSigma { get; init; }

    [JsonPropertyName("repaired_at")]
    public DateTimeOffset RepairedAt { get; init; }
}
