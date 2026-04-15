using System;
using System.Collections.Generic;
using AutoMapper;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;

namespace Traceability.Mapping.Profiles;

/// <summary>
/// AWS (米国) 生スキーマ ↔ EPCIS 2.0 の双方向マッピング定義。
/// 単位換算 (Bushel→KG)、タイムゾーン統一 (LocalTime→UTC) を透過的に処理する。
/// </summary>
public sealed class AwsToEpcisProfile : Profile
{
    // 米国農業会社のデモ用 GS1 プレフィックス
    private const string UsCompanyPrefix = "0614141";
    private const string WheatItemRef = "000001";

    public AwsToEpcisProfile()
    {
        // ─── AWS Raw → EPCIS ─────────────────────────────────────────────
        CreateMap<AwsRawSchema, EpcisEvent>()
            .ForMember(dst => dst.EventTime,
                opt => opt.MapFrom(src => src.LocalTime.ToUniversalTime()))
            .ForMember(dst => dst.RecordTime,
                opt => opt.MapFrom(_ => DateTimeOffset.UtcNow))
            .ForMember(dst => dst.EpcList,
                opt => opt.MapFrom(src => BuildEpcList(src)))
            .ForMember(dst => dst.BizStep,
                opt => opt.MapFrom(src => NormalizeBizStep(src.BizStep)))
            .ForMember(dst => dst.ReadPoint,
                opt => opt.MapFrom(src =>
                    Gs1Builder.BuildSgln(UsCompanyPrefix, src.FarmGln.PadRight(6, '0')[..6])))
            .ForMember(dst => dst.BizLocation,
                opt => opt.MapFrom(src =>
                    Gs1Builder.BuildSgln(UsCompanyPrefix, src.FarmGln.PadRight(6, '0')[..6])))
            .ForMember(dst => dst.QuantityList,
                opt => opt.MapFrom(src => BuildQuantityList(src)))
            .ForMember(dst => dst.SourceCloud,
                opt => opt.MapFrom(_ => "AWS"))
            .ForMember(dst => dst.IdempotencyKey,
                opt => opt.MapFrom(src =>
                    HashUtil.ComputeIdempotencyKey(src.LotNumber, src.BizStep, src.LocalTime.ToUniversalTime())))
            // DataHash / ChainHash は後段の BlockchainClient で設定するため省略
            .ForMember(dst => dst.DataHash, opt => opt.Ignore())
            .ForMember(dst => dst.ChainHash, opt => opt.Ignore())
            .ForMember(dst => dst.ParentTx, opt => opt.Ignore())
            .ForMember(dst => dst.IsImputed, opt => opt.Ignore())
            .ForMember(dst => dst.RepairMetadata, opt => opt.Ignore())
            .ForMember(dst => dst.Context, opt => opt.Ignore())
            .ForMember(dst => dst.Type, opt => opt.Ignore());

        // ─── EPCIS → AWS Raw (逆マッピング、監査・再確認用) ────────────────
        CreateMap<EpcisEvent, AwsRawSchema>()
            .ForMember(dst => dst.LocalTime,
                opt => opt.MapFrom(src => src.EventTime)) // UTC のまま返す
            .ForMember(dst => dst.VolumeBushels,
                opt => opt.MapFrom(src => ExtractBushels(src)))
            .ForMember(dst => dst.FarmName,
                opt => opt.MapFrom(src => src.ReadPoint))
            .ForMember(dst => dst.FarmGln,
                opt => opt.MapFrom(src => ExtractGln(src.ReadPoint)))
            .ForMember(dst => dst.LotNumber,
                opt => opt.MapFrom(src => ExtractLot(src.EpcList)))
            .ForMember(dst => dst.BizStep,
                opt => opt.MapFrom(src => src.BizStep))
            .ForMember(dst => dst.SensorTemperatureF, opt => opt.Ignore())
            .ForMember(dst => dst.SensorHumidityPct, opt => opt.Ignore())
            .ForMember(dst => dst.CropType, opt => opt.Ignore());
    }

    // ── ヘルパー ──────────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildEpcList(AwsRawSchema src)
        => new[]
        {
            Gs1Builder.BuildSgtin(UsCompanyPrefix, WheatItemRef, src.LotNumber)
        };

    private static IReadOnlyList<QuantityElement> BuildQuantityList(AwsRawSchema src)
        => new[]
        {
            new QuantityElement
            {
                EpcClass = $"urn:epc:class:lgtin:{UsCompanyPrefix}.{WheatItemRef}",
                // Bushel → KG 変換（EPCIS は SI 単位を推奨）
                Quantity = Math.Round(UnitConverter.BushelToKg(src.VolumeBushels), 3),
                Uom = "KGM"
            }
        };

    private static string NormalizeBizStep(string raw) =>
        raw.ToLowerInvariant() switch
        {
            "shipping" => "https://ref.gs1.org/cbv/BizStep-shipping",
            "receiving" => "https://ref.gs1.org/cbv/BizStep-receiving",
            "harvesting" => "https://ref.gs1.org/cbv/BizStep-harvesting",
            "inspecting" => "https://ref.gs1.org/cbv/BizStep-inspecting",
            _ => $"https://ref.gs1.org/cbv/BizStep-{raw.ToLowerInvariant()}"
        };

    private static double ExtractBushels(EpcisEvent ev)
    {
        if (ev.QuantityList.Count == 0) return 0;
        return UnitConverter.KgToBushel(ev.QuantityList[0].Quantity);
    }

    private static string ExtractGln(string sgln)
    {
        // urn:epc:id:sgln:XXXXXX.YYYYYY.0 → YYYYYY
        var parts = sgln.Split('.');
        return parts.Length >= 2 ? parts[^2] : sgln;
    }

    private static string ExtractLot(IReadOnlyList<string> epcList)
    {
        if (epcList.Count == 0) return string.Empty;
        var parts = epcList[0].Split('.');
        return parts.Length >= 1 ? parts[^1] : string.Empty;
    }
}
