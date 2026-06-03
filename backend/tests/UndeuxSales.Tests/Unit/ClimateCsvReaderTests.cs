using UndeuxSales.Core.Parsing;

namespace UndeuxSales.Tests.Unit;

public sealed class ClimateCsvReaderTests
{
    private const string Header = "年月日,曜日,平均気温(℃),最高気温(℃),最低気温(℃),エリア";

    [Fact]
    public void ReadRecords_SkipsHeader_AndMapsAreas()
    {
        using var reader = new StringReader(
            Header + "\n"
            + "2024年1月1日,月,9.2,12.5,4.4,東京\n"
            + "2024年1月1日,月,-3.2,0.1,-6,札幌\n"
            + "2024年1月1日,月,17.3,19.8,14.6,那覇\n");

        var records = ClimateCsvReader.ReadRecords(reader).ToList();

        Assert.Equal(3, records.Count);
        Assert.Equal("standard", records[0].AreaCode);
        Assert.Equal(new DateOnly(2024, 1, 1), records[0].Date);
        Assert.Equal(9.2, records[0].TempAvg);
        Assert.Equal(12.5, records[0].TempMax);
        Assert.Equal(4.4, records[0].TempMin);
        Assert.Equal("cold", records[1].AreaCode);
        Assert.Equal(-3.2, records[1].TempAvg);
        Assert.Equal("warm", records[2].AreaCode);
    }

    [Fact]
    public void ReadRecords_IntegerTemperature_ParsesAsDouble()
    {
        // "11"（小数点なし）も double として解釈できること。
        using var reader = new StringReader(
            Header + "\n2024年1月3日,水,7.4,11,2.8,東京\n");

        var record = Assert.Single(ClimateCsvReader.ReadRecords(reader).ToList());

        Assert.Equal(11.0, record.TempMax);
    }

    [Fact]
    public void ReadRecords_UnknownArea_IsSkipped()
    {
        // 既知3地点以外（大阪）は前方互換のため無視する。
        using var reader = new StringReader(
            Header + "\n"
            + "2024年1月1日,月,9.2,12.5,4.4,東京\n"
            + "2024年1月1日,月,8.0,11.0,5.0,大阪\n");

        var records = ClimateCsvReader.ReadRecords(reader).ToList();

        Assert.Single(records);
        Assert.Equal("standard", records[0].AreaCode);
    }

    [Fact]
    public void ReadRecords_NoHeader_ParsesFirstRow()
    {
        // ヘッダが無い場合でも、東京の有効行はデータ行として解釈する。
        using var reader = new StringReader("2024年1月1日,月,9.2,12.5,4.4,東京\n");

        var record = Assert.Single(ClimateCsvReader.ReadRecords(reader).ToList());

        Assert.Equal(new DateOnly(2024, 1, 1), record.Date);
    }

    [Fact]
    public void ReadRecords_InvalidDate_Throws()
    {
        using var reader = new StringReader(
            Header + "\n2024-01-01,月,9.2,12.5,4.4,東京\n");

        Assert.Throws<FormatException>(
            () => ClimateCsvReader.ReadRecords(reader).ToList());
    }

    [Fact]
    public void ReadRecords_InvalidTemperature_Throws()
    {
        using var reader = new StringReader(
            Header + "\n2024年1月1日,月,abc,12.5,4.4,東京\n");

        Assert.Throws<FormatException>(
            () => ClimateCsvReader.ReadRecords(reader).ToList());
    }
}
