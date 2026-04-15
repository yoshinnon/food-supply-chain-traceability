using System;
using System.Text.Json.Serialization;

namespace Traceability.Common.DTOs;

/// <summary>
/// AWS (米国) 側の生DBスキーマ。
/// 単位: ブッシェル / ローカル時刻 / 農場名
/// </summary>
public sealed record AwsRawSchema
{
    [JsonPropertyName("Volume_Bushels")]
    public double VolumeBushels { get; init; }

    [JsonPropertyName("Farm_Name")]
    public string FarmName { get; init; } = string.Empty;

    /// <summary>農場のローカル時刻 (タイムゾーン付き)</summary>
    [JsonPropertyName("Local_Time")]
    public DateTimeOffset LocalTime { get; init; }

    [JsonPropertyName("Crop_Type")]
    public string CropType { get; init; } = "Wheat";

    [JsonPropertyName("Farm_GLN")]
    public string FarmGln { get; init; } = string.Empty;

    [JsonPropertyName("Lot_Number")]
    public string LotNumber { get; init; } = string.Empty;

    [JsonPropertyName("Sensor_Temperature_F")]
    public double? SensorTemperatureF { get; init; }

    [JsonPropertyName("Sensor_Humidity_Pct")]
    public double? SensorHumidityPct { get; init; }

    [JsonPropertyName("Biz_Step")]
    public string BizStep { get; init; } = "shipping";
}

/// <summary>
/// Azure (日本) 側の生DBスキーマ。
/// 単位: キログラム / UTC / 卸売市場ID
/// </summary>
public sealed record AzureRawSchema
{
    [JsonPropertyName("Weight_KG")]
    public double WeightKg { get; init; }

    [JsonPropertyName("Wholesale_Market_ID")]
    public string WholesaleMarketId { get; init; } = string.Empty;

    /// <summary>UTC 時刻</summary>
    [JsonPropertyName("UTC_Time")]
    public DateTimeOffset UtcTime { get; init; }

    [JsonPropertyName("Crop_Type")]
    public string CropType { get; init; } = "Wheat";

    [JsonPropertyName("Market_GLN")]
    public string MarketGln { get; init; } = string.Empty;

    [JsonPropertyName("Lot_Number")]
    public string LotNumber { get; init; } = string.Empty;

    [JsonPropertyName("Sensor_Temperature_C")]
    public double? SensorTemperatureC { get; init; }

    [JsonPropertyName("Sensor_Humidity_Pct")]
    public double? SensorHumidityPct { get; init; }

    [JsonPropertyName("Biz_Step")]
    public string BizStep { get; init; } = "receiving";
}
