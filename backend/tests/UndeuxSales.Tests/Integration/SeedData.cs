using UndeuxSales.Core.Models;

namespace UndeuxSales.Tests.Integration;

/// <summary>統合テスト用のシードデータ。2週分・2部門・2品番の既知データを生成する。</summary>
internal static class SeedData
{
    /// <summary>シードレコード（売上数量合計 = 28、週数 = 2、部門数 = 2）。</summary>
    public static IReadOnlyList<SalesRecord> Records() => new[]
    {
        Make("2026-05-04", "01", "100", "0001", "S100", [1, 1, 1, 1, 1, 1, 1], 1000, 400, 50, 20, 30),
        Make("2026-05-04", "01", "100", "0002", "S100", [2, 0, 0, 0, 0, 0, 0], 2000, 800, 10, 5, 8),
        Make("2026-05-04", "02", "200", "0001", "S200", [0, 0, 0, 3, 0, 0, 0], 1500, 600, 30, 12, 20),
        Make("2026-05-11", "01", "100", "0001", "S100", [1, 2, 3, 0, 0, 0, 0], 1000, 400, 44, 26, 30),
        Make("2026-05-11", "01", "100", "0002", "S100", [0, 0, 0, 0, 0, 0, 0], 2000, 800, 10, 5, 8),
        Make("2026-05-11", "02", "200", "0001", "S200", [5, 5, 0, 0, 0, 0, 0], 1500, 600, 20, 22, 20),
    };

    private static SalesRecord Make(
        string importDate,
        string department,
        string hinban,
        string tanpin,
        string shohinKigou,
        int[] dailyCounts,
        int baika,
        int genka,
        int zaikosu,
        int ruikeiUriage,
        int ruikeiNohin) => new()
    {
        ImportDate = DateOnly.Parse(importDate),
        CustomerCode = "C001",
        GyotaiCode = "G1",
        ChohyoKubunName = "売発注",
        Department = department,
        HinbanCode = hinban,
        TanpinCode = tanpin,
        Hinmei = $"商品{hinban}",
        ShohinKigou = shohinKigou,
        Color = "標準色",
        Size = "M",
        ToshuUriageCount1 = dailyCounts[0],
        ToshuUriageCount2 = dailyCounts[1],
        ToshuUriageCount3 = dailyCounts[2],
        ToshuUriageCount4 = dailyCounts[3],
        ToshuUriageCount5 = dailyCounts[4],
        ToshuUriageCount6 = dailyCounts[5],
        ToshuUriageCount7 = dailyCounts[6],
        Zaikosu = zaikosu,
        RuikeiUriageCount = ruikeiUriage,
        RuikeiNohinCount = ruikeiNohin,
        HatchuCount = 1.0m,
        DonyuDate = "20240101",
        Zainiti = 30,
        Genka = genka,
        Baika = baika,
        Kisetsu = "通季",
    };
}
