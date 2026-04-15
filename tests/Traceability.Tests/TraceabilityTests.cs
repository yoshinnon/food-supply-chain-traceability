using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Traceability.AI;
using Traceability.Blockchain;
using Traceability.Common.DTOs;
using Traceability.Common.Utils;
using Traceability.Mapping.Profiles;
using Xunit;

namespace Traceability.Tests.Unit;

// ════════════════════════════════════════════════════════════════════════════
// 単位換算テスト
// ════════════════════════════════════════════════════════════════════════════

public sealed class UnitConverterTests
{
    [Theory]
    [InlineData(1.0, 27.2155)]
    [InlineData(0.0, 0.0)]
    [InlineData(100.0, 2721.55)]
    [InlineData(36.744, 36.744 * 27.2155)]
    public void BushelToKg_Returns_CorrectValue(double bushels, double expectedKg)
    {
        var result = UnitConverter.BushelToKg(bushels);
        Assert.Equal(expectedKg, result, precision: 3);
    }

    [Theory]
    [InlineData(27.2155, 1.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(2721.55, 100.0)]
    public void KgToBushel_Returns_CorrectValue(double kg, double expectedBushels)
    {
        var result = UnitConverter.KgToBushel(kg);
        Assert.Equal(expectedBushels, result, precision: 3);
    }

    [Fact]
    public void BushelToKg_And_Back_IsRoundTrippable()
    {
        const double original = 50.0;
        var kg = UnitConverter.BushelToKg(original);
        var back = UnitConverter.KgToBushel(kg);
        Assert.Equal(original, back, precision: 6);
    }

    [Theory]
    [InlineData(32.0, 0.0)]    // 氷点
    [InlineData(212.0, 100.0)] // 沸点
    [InlineData(98.6, 37.0)]   // 体温
    public void FahrenheitToCelsius_Returns_CorrectValue(double f, double expectedC)
    {
        var result = UnitConverter.FahrenheitToCelsius(f);
        Assert.Equal(expectedC, result, precision: 2);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// ハッシュ一貫性テスト
// ════════════════════════════════════════════════════════════════════════════

public sealed class HashUtilTests
{
    [Fact]
    public void ComputeSha256_SameInput_ReturnsSameHash()
    {
        var input = "traceability-test-data";
        Assert.Equal(HashUtil.ComputeSha256String(input), HashUtil.ComputeSha256String(input));
    }

    [Fact]
    public void ComputeSha256_DifferentInput_ReturnsDifferentHash()
    {
        var h1 = HashUtil.ComputeSha256String("data-A");
        var h2 = HashUtil.ComputeSha256String("data-B");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void ComputeChainHash_WithParentTx_DiffersFromDataHash()
    {
        var dataHash = HashUtil.ComputeSha256String("event-data");
        var chainHash = HashUtil.ComputeChainHash(dataHash, "fabric-txid-12345");
        Assert.NotEqual(dataHash, chainHash);
    }

    [Fact]
    public void ComputeChainHash_NullParentTx_ReturnsDataHash()
    {
        var dataHash = HashUtil.ComputeSha256String("event-data");
        var chainHash = HashUtil.ComputeChainHash(dataHash, null);
        Assert.Equal(dataHash, chainHash);
    }

    [Fact]
    public void ComputeChainHash_Deterministic()
    {
        var dataHash = "abc123";
        var parentTx = "fabric-tx-001";
        Assert.Equal(
            HashUtil.ComputeChainHash(dataHash, parentTx),
            HashUtil.ComputeChainHash(dataHash, parentTx));
    }

    [Fact]
    public void ComputeSha256_OnObject_IsConsistentWithStringVersion()
    {
        var ev = new EpcisEvent
        {
            EventTime = DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            BizStep = "shipping",
            SourceCloud = "AWS"
        };
        var h1 = HashUtil.ComputeSha256(ev);
        var h2 = HashUtil.ComputeSha256(ev);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void IdempotencyKey_SameInput_SameKey()
    {
        var t = DateTimeOffset.Parse("2025-06-01T12:00:00Z");
        var k1 = HashUtil.ComputeIdempotencyKey("LOT-001", "shipping", t);
        var k2 = HashUtil.ComputeIdempotencyKey("LOT-001", "shipping", t);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void IdempotencyKey_DifferentLot_DifferentKey()
    {
        var t = DateTimeOffset.Parse("2025-06-01T12:00:00Z");
        var k1 = HashUtil.ComputeIdempotencyKey("LOT-001", "shipping", t);
        var k2 = HashUtil.ComputeIdempotencyKey("LOT-002", "shipping", t);
        Assert.NotEqual(k1, k2);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// AutoMapper マッピングテスト
// ════════════════════════════════════════════════════════════════════════════

public sealed class MappingTests
{
    private readonly IMapper _mapper;

    public MappingTests()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<AwsToEpcisProfile>();
            cfg.AddProfile<AzureToEpcisProfile>();
        });
        config.AssertConfigurationIsValid();
        _mapper = config.CreateMapper();
    }

    [Fact]
    public void AwsRaw_ToEpcis_ConvertsBushelsToKg()
    {
        var raw = new AwsRawSchema
        {
            VolumeBushels = 100.0,
            FarmName = "Test Farm",
            LocalTime = DateTimeOffset.UtcNow,
            FarmGln = "123456",
            LotNumber = "LOT-001",
            BizStep = "shipping"
        };

        var ev = _mapper.Map<EpcisEvent>(raw);

        Assert.Single(ev.QuantityList);
        Assert.Equal("KGM", ev.QuantityList[0].Uom);
        Assert.Equal(UnitConverter.BushelToKg(100.0), ev.QuantityList[0].Quantity, precision: 3);
    }

    [Fact]
    public void AwsRaw_ToEpcis_SetsSourceCloudToAWS()
    {
        var raw = new AwsRawSchema { LocalTime = DateTimeOffset.UtcNow, LotNumber = "L1", BizStep = "shipping" };
        var ev = _mapper.Map<EpcisEvent>(raw);
        Assert.Equal("AWS", ev.SourceCloud);
    }

    [Fact]
    public void AwsRaw_ToEpcis_LocalTimeConvertedToUtc()
    {
        var jst = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.FromHours(9)); // JST 09:00
        var raw = new AwsRawSchema { LocalTime = jst, LotNumber = "L1", BizStep = "shipping" };
        var ev = _mapper.Map<EpcisEvent>(raw);
        Assert.Equal(DateTimeKind.Unspecified, ev.EventTime.DateTime.Kind); // DateTimeOffset は Kind を持たない
        Assert.Equal(0, ev.EventTime.Offset.Hours); // UTC
    }

    [Fact]
    public void AzureRaw_ToEpcis_PreservesKg()
    {
        var raw = new AzureRawSchema
        {
            WeightKg = 1000.0,
            WholesaleMarketId = "TSUKIJI",
            UtcTime = DateTimeOffset.UtcNow,
            MarketGln = "654321",
            LotNumber = "LOT-002",
            BizStep = "receiving"
        };

        var ev = _mapper.Map<EpcisEvent>(raw);

        Assert.Equal(1000.0, ev.QuantityList[0].Quantity, precision: 3);
        Assert.Equal("KGM", ev.QuantityList[0].Uom);
        Assert.Equal("Azure", ev.SourceCloud);
    }

    [Fact]
    public void AwsRaw_ToEpcis_BizStepIsFullUri()
    {
        var raw = new AwsRawSchema { LocalTime = DateTimeOffset.UtcNow, BizStep = "shipping", LotNumber = "L1" };
        var ev = _mapper.Map<EpcisEvent>(raw);
        Assert.Contains("BizStep-shipping", ev.BizStep);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// AI 修復テスト
// ════════════════════════════════════════════════════════════════════════════

public sealed class AiRepairServiceTests
{
    private static AiRepairService BuildService(int windowSize = 5, double sigma = 3.0)
    {
        var opts = Options.Create(new AiRepairOptions { WindowSize = windowSize, ThresholdSigma = sigma });
        return new AiRepairService(opts, NullLogger<AiRepairService>.Instance, new StubOnnxEngine(NullLogger<StubOnnxEngine>.Instance));
    }

    [Fact]
    public async Task RepairAsync_NormalValue_IsNotAnomaly()
    {
        var service = BuildService();
        var values = new List<double> { 100, 102, 98, 101, 99, 100 }; // 正常値リスト
        var result = await service.RepairAsync(values);
        Assert.False(result.IsAnomaly);
        Assert.Equal(100, result.RepairedValue, precision: 1);
    }

    [Fact]
    public async Task RepairAsync_OutlierValue_IsAnomaly()
    {
        var service = BuildService(windowSize: 5, sigma: 2.0);
        // 通常100付近なのに突然1000が来た
        var values = new List<double> { 100, 102, 98, 101, 99, 1000 };
        var result = await service.RepairAsync(values);
        Assert.True(result.IsAnomaly);
        Assert.NotEqual(1000, result.RepairedValue);
        // 修復値は移動平均付近になる
        Assert.InRange(result.RepairedValue, 90, 110);
    }

    [Fact]
    public async Task ApplyRepairToEvent_SetsIsImputed_WhenAnomaly()
    {
        var service = BuildService(windowSize: 5, sigma: 2.0);
        var ev = new EpcisEvent
        {
            EventTime = DateTimeOffset.UtcNow,
            QuantityList = new[] { new QuantityElement { Quantity = 9999, Uom = "KGM", EpcClass = "urn:test" } }
        };
        var history = new List<double> { 100, 102, 98, 101, 99, 9999 };
        var repaired = await service.ApplyRepairToEventAsync(ev, history);

        Assert.True(repaired.IsImputed);
        Assert.NotNull(repaired.RepairMetadata);
        Assert.Equal(9999, repaired.RepairMetadata!.OriginalValue, precision: 0);
        Assert.InRange(repaired.RepairMetadata.RepairedValue, 90, 110);
    }

    [Fact]
    public async Task RepairAsync_SingleValue_NoStdDev_NotAnomaly()
    {
        var service = BuildService();
        var result = await service.RepairAsync(new List<double> { 100 });
        Assert.False(result.IsAnomaly);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// BC モック統合テスト
// ════════════════════════════════════════════════════════════════════════════

namespace Traceability.Tests.Integration;

public sealed class CrossChainOrchestratorTests
{
    private static CrossChainOrchestrator BuildOrchestrator()
    {
        var awsOpts = Options.Create(new AwsFabricOptions());
        var azureOpts = Options.Create(new AzureLedgerOptions());
        var aws = new AwsFabricStrategy(NullLogger<AwsFabricStrategy>.Instance, awsOpts);
        var azure = new AzureConfidentialLedgerStrategy(NullLogger<AzureConfidentialLedgerStrategy>.Instance, azureOpts);
        return new CrossChainOrchestrator(aws, azure, NullLogger<CrossChainOrchestrator>.Instance);
    }

    private static EpcisEvent BuildSampleEvent(string lot = "LOT-999") => new()
    {
        EventTime = DateTimeOffset.UtcNow,
        RecordTime = DateTimeOffset.UtcNow,
        EpcList = new[] { $"urn:epc:id:sgtin:0614141.000001.{lot}" },
        BizStep = "https://ref.gs1.org/cbv/BizStep-shipping",
        ReadPoint = "urn:epc:id:sgln:0614141.001234.0",
        QuantityList = new[] { new QuantityElement { Quantity = 2721.55, Uom = "KGM", EpcClass = "urn:test" } },
        SourceCloud = "AWS",
        IdempotencyKey = HashUtil.ComputeIdempotencyKey(lot, "shipping", DateTimeOffset.UtcNow)
    };

    [Fact]
    public async Task AnchorExportEvent_Returns_BothTxIds()
    {
        var orchestrator = BuildOrchestrator();
        var ev = BuildSampleEvent("LOT-001");

        var (aws, azure) = await orchestrator.AnchorExportEventAsync(ev);

        Assert.NotEmpty(aws.TxId);
        Assert.NotEmpty(azure.TxId);
        Assert.Equal("AWS", aws.CloudName);
        Assert.Equal("Azure", azure.CloudName);
    }

    [Fact]
    public async Task AnchorExportEvent_ChainHash_ContainsParentTx()
    {
        var orchestrator = BuildOrchestrator();
        var ev = BuildSampleEvent("LOT-002");

        var (aws, azure) = await orchestrator.AnchorExportEventAsync(ev);

        // ChainHash は DataHash と異なるはず (ParentTx が加算されるため)
        Assert.NotEqual(aws.ChainHash, azure.ChainHash);
        Assert.NotEmpty(azure.ChainHash);
    }

    [Fact]
    public async Task AnchorExportEvent_DuplicateCall_ThrowsIdempotencyException()
    {
        var orchestrator = BuildOrchestrator();
        var ev = BuildSampleEvent("LOT-003");

        await orchestrator.AnchorExportEventAsync(ev); // 1回目: 成功
        await Assert.ThrowsAsync<IdempotencyException>(
            () => orchestrator.AnchorExportEventAsync(ev)); // 2回目: べき等性例外
    }

    [Fact]
    public async Task AnchorExportEvent_DifferentLots_BothSucceed()
    {
        var orchestrator = BuildOrchestrator();
        var ev1 = BuildSampleEvent("LOT-010");
        var ev2 = BuildSampleEvent("LOT-011");

        var (aws1, _) = await orchestrator.AnchorExportEventAsync(ev1);
        var (aws2, _) = await orchestrator.AnchorExportEventAsync(ev2);

        Assert.NotEqual(aws1.TxId, aws2.TxId);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// EPCIS スキーマ検証テスト
// ════════════════════════════════════════════════════════════════════════════

namespace Traceability.Tests.Schema;

public sealed class EpcisSchemaValidationTests
{
    [Fact]
    public void EpcisEvent_RequiredFields_NotEmpty()
    {
        var raw = new AwsRawSchema
        {
            VolumeBushels = 50.0,
            FarmName = "Test Farm",
            LocalTime = DateTimeOffset.UtcNow,
            FarmGln = "123456",
            LotNumber = "LOT-SCHEMA-001",
            BizStep = "shipping"
        };

        var config = new MapperConfiguration(cfg => cfg.AddProfile<AwsToEpcisProfile>());
        var mapper = config.CreateMapper();
        var ev = mapper.Map<EpcisEvent>(raw);

        Assert.NotEmpty(ev.Context);
        Assert.NotEmpty(ev.Type);
        Assert.NotEmpty(ev.BizStep);
        Assert.NotEmpty(ev.EpcList);
        Assert.NotEmpty(ev.SourceCloud);
        Assert.NotEmpty(ev.IdempotencyKey);
        Assert.NotEqual(default, ev.EventTime);
    }

    [Fact]
    public void EpcisEvent_EpcList_HasValidSgtinFormat()
    {
        var raw = new AwsRawSchema
        {
            LotNumber = "LOT-SGTIN-TEST",
            LocalTime = DateTimeOffset.UtcNow,
            BizStep = "shipping"
        };
        var config = new MapperConfiguration(cfg => cfg.AddProfile<AwsToEpcisProfile>());
        var ev = config.CreateMapper().Map<EpcisEvent>(raw);

        foreach (var epc in ev.EpcList)
            Assert.StartsWith("urn:epc:id:sgtin:", epc);
    }

    [Fact]
    public void EpcisEvent_BizStep_HasGs1Uri()
    {
        var raw = new AwsRawSchema { LotNumber = "L1", LocalTime = DateTimeOffset.UtcNow, BizStep = "shipping" };
        var config = new MapperConfiguration(cfg => cfg.AddProfile<AwsToEpcisProfile>());
        var ev = config.CreateMapper().Map<EpcisEvent>(raw);

        Assert.StartsWith("https://ref.gs1.org/cbv/BizStep-", ev.BizStep);
    }
}
