using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Traceability.Common.Utils;

/// <summary>
/// 単位換算ユーティリティ。
/// Bushel ↔ KG 変換は小麦の公定換算係数 (27.2155 kg/bushel) を使用。
/// </summary>
public static class UnitConverter
{
    // USDA 公定換算係数: 1 bushel of wheat = 60 lb = 27.2155 kg
    private const double KgPerBushelWheat = 27.2155;
    private const double LbPerKg = 2.20462;
    private const double FahrenheitOffset = 32.0;
    private const double FahrenheitScale = 9.0 / 5.0;

    /// <summary>ブッシェル (小麦) をキログラムに変換</summary>
    public static double BushelToKg(double bushels) => bushels * KgPerBushelWheat;

    /// <summary>キログラムをブッシェル (小麦) に変換</summary>
    public static double KgToBushel(double kg) => kg / KgPerBushelWheat;

    /// <summary>ポンドをキログラムに変換</summary>
    public static double LbToKg(double lb) => lb / LbPerKg;

    /// <summary>キログラムをポンドに変換</summary>
    public static double KgToLb(double kg) => kg * LbPerKg;

    /// <summary>華氏を摂氏に変換</summary>
    public static double FahrenheitToCelsius(double f) => (f - FahrenheitOffset) / FahrenheitScale;

    /// <summary>摂氏を華氏に変換</summary>
    public static double CelsiusToFahrenheit(double c) => c * FahrenheitScale + FahrenheitOffset;
}

/// <summary>
/// ハッシュユーティリティ。
/// オフチェーンデータのフィンガープリントとクロスチェーンハッシュを生成する。
/// </summary>
public static class HashUtil
{
    /// <summary>
    /// オブジェクトを JSON シリアライズして SHA-256 ハッシュを算出する。
    /// </summary>
    public static string ComputeSha256<T>(T obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return ComputeSha256String(json);
    }

    /// <summary>文字列の SHA-256 ハッシュを算出する。</summary>
    public static string ComputeSha256String(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// クロスチェーンファイナルハッシュを算出する。
    /// Hash_final = SHA256(dataHash + parentTxId)
    /// parentTxId が null の場合は dataHash をそのまま返す（起点チェーン）。
    /// </summary>
    public static string ComputeChainHash(string dataHash, string? parentTxId)
    {
        if (string.IsNullOrWhiteSpace(parentTxId))
            return dataHash;

        return ComputeSha256String(dataHash + parentTxId);
    }

    /// <summary>
    /// べき等性キーを生成する。
    /// LotNumber + BizStep + EventTime の組み合わせで衝突を防ぐ。
    /// </summary>
    public static string ComputeIdempotencyKey(string lotNumber, string bizStep, DateTimeOffset eventTime)
    {
        var input = $"{lotNumber}:{bizStep}:{eventTime:O}";
        return ComputeSha256String(input)[..16]; // 先頭16文字で十分
    }
}

/// <summary>
/// GS1 識別子ビルダー。
/// sgtin / sgln 形式の文字列を生成する。
/// </summary>
public static class Gs1Builder
{
    /// <summary>
    /// SGTIN (Serialised Global Trade Item Number) を生成する。
    /// 形式: urn:epc:id:sgtin:{companyPrefix}.{itemRef}.{serial}
    /// </summary>
    public static string BuildSgtin(string companyPrefix, string itemRef, string serial)
        => $"urn:epc:id:sgtin:{companyPrefix}.{itemRef}.{serial}";

    /// <summary>
    /// SGLN (Serialised Global Location Number) を生成する。
    /// 形式: urn:epc:id:sgln:{companyPrefix}.{locationRef}.{extension}
    /// </summary>
    public static string BuildSgln(string companyPrefix, string locationRef, string extension = "0")
        => $"urn:epc:id:sgln:{companyPrefix}.{locationRef}.{extension}";
}
