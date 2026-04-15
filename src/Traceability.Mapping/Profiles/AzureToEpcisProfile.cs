using System;
using System.Collections.Generic;
using AutoMapper;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;

namespace Traceability.Mapping.Profiles;

/// <summary>
/// Azure (日本) 生スキーマ ↔ EPCIS 2.0 の双方向マッピング定義。
/// Azure 側はすでに KG / UTC で保持しているため単位換算は不要。
/// </summary>
public sealed class AzureToEpcisProfile : Profile
{
    private const string JpCompanyPrefix = "4912345";
    private const string WheatItemRef = "000001";

    public AzureToEpcisProfile()
    {
        // ─── Azure Raw → EPCIS ────────────────────────────────────────────
        CreateMap<AzureRawSchema, EpcisEvent>()
            .ForMember(dst => dst.EventTime,
                opt => opt.MapFrom(src => src.UtcTime))
            .ForMember(dst => dst.RecordTime,
                opt => opt.MapFrom(_ => DateTimeOffset.UtcNow))
            .ForMember(dst => dst.EpcList,
                opt => opt.MapFrom(src => BuildEpcList(src)))
            .ForMember(dst => dst.BizStep,
                opt => opt.MapFrom(src => NormalizeBizStep(src.BizStep)))
            .ForMember(dst => dst.ReadPoint,
                opt => opt.MapFrom(src =>
                    Gs1Builder.BuildSgln(JpCompanyPrefix, src.MarketGln.PadRight(6, '0')[..6])))
            .ForMember(dst => dst.BizLocation,
                opt => opt.MapFrom(src =>
                    Gs1Builder.BuildSgln(JpCompanyPrefix, src.MarketGln.PadRight(6, '0')[..6])))
            .ForMember(dst => dst.QuantityList,
                opt => opt.MapFrom(src => BuildQuantityList(src)))
            .ForMember(dst => dst.SourceCloud,
                opt => opt.MapFrom(_ => "Azure"))
            .ForMember(dst => dst.IdempotencyKey,
                opt => opt.MapFrom(src =>
                    HashUtil.ComputeIdempotencyKey(src.LotNumber, src.BizStep, src.UtcTime)))
            .ForMember(dst => dst.DataHash, opt => opt.Ignore())
            .ForMember(dst => dst.ChainHash, opt => opt.Ignore())
            .ForMember(dst => dst.ParentTx, opt => opt.Ignore())
            .ForMember(dst => dst.IsImputed, opt => opt.Ignore())
            .ForMember(dst => dst.RepairMetadata, opt => opt.Ignore())
            .ForMember(dst => dst.Context, opt => opt.Ignore())
            .ForMember(dst => dst.Type, opt => opt.Ignore());

        // ─── EPCIS → Azure Raw (逆マッピング) ────────────────────────────
        CreateMap<EpcisEvent, AzureRawSchema>()
            .ForMember(dst => dst.UtcTime,
                opt => opt.MapFrom(src => src.EventTime))
            .ForMember(dst => dst.WeightKg,
                opt => opt.MapFrom(src => ExtractKg(src)))
            .ForMember(dst => dst.WholesaleMarketId,
                opt => opt.MapFrom(src => src.BizLocation))
            .ForMember(dst => dst.MarketGln,
                opt => opt.MapFrom(src => ExtractGln(src.BizLocation)))
            .ForMember(dst => dst.LotNumber,
                opt => opt.MapFrom(src => ExtractLot(src.EpcList)))
            .ForMember(dst => dst.BizStep,
                opt => opt.MapFrom(src => src.BizStep))
            .ForMember(dst => dst.SensorTemperatureC, opt => opt.Ignore())
            .ForMember(dst => dst.SensorHumidityPct, opt => opt.Ignore())
            .ForMember(dst => dst.CropType, opt => opt.Ignore());
    }

    private static IReadOnlyList<string> BuildEpcList(AzureRawSchema src)
        => new[]
        {
            Gs1Builder.BuildSgtin(JpCompanyPrefix, WheatItemRef, src.LotNumber)
        };

    private static IReadOnlyList<QuantityElement> BuildQuantityList(AzureRawSchema src)
        => new[]
        {
            new QuantityElement
            {
                EpcClass = $"urn:epc:class:lgtin:{JpCompanyPrefix}.{WheatItemRef}",
                Quantity = Math.Round(src.WeightKg, 3),
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

    private static double ExtractKg(EpcisEvent ev)
    {
        if (ev.QuantityList.Count == 0) return 0;
        return ev.QuantityList[0].Uom == "KGM"
            ? ev.QuantityList[0].Quantity
            : UnitConverter.LbToKg(ev.QuantityList[0].Quantity);
    }

    private static string ExtractGln(string sgln)
    {
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
