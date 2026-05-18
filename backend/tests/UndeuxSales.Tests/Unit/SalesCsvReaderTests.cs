using UndeuxSales.Core;
using UndeuxSales.Core.Parsing;

namespace UndeuxSales.Tests.Unit;

public sealed class SalesCsvReaderTests
{
    private static readonly string Header = string.Join(",", SalesCsvReader.RequiredColumns);

    // RequiredColumns と同順の有効なデータ行（32列）。
    private const string ValidRow =
        "2026-05-11,5687,01,売発注,55,558,0005,商品A,CE8224,ﾈｲﾋﾞｰ,M,"
        + "1,2,3,4,5,6,7,10,8,6,4,20,50,60,1.0,20180101,30,1480,2900,通季,5";

    private static StringReader Csv(params string[] dataRows)
        => new(Header + "\n" + string.Join("\n", dataRows) + "\n");

    private static string ReplaceCell(string row, int index, string value)
    {
        var cells = row.Split(',');
        cells[index] = value;
        return string.Join(",", cells);
    }

    [Fact]
    public void Parse_ValidRow_ReturnsRecordWithMappedFields()
    {
        using var reader = Csv(ValidRow);

        var result = SalesCsvReader.Parse(reader);

        Assert.False(result.HasErrors);
        var record = Assert.Single(result.Records);
        Assert.Equal(new DateOnly(2026, 5, 11), record.ImportDate);
        Assert.Equal("5687", record.CustomerCode);
        Assert.Equal("商品A", record.Hinmei);
        Assert.Equal(28, record.WeekTotalQuantity); // 1+2+3+4+5+6+7
        Assert.Equal(20, record.Zaikosu);
        Assert.Equal(1.0m, record.HatchuCount);
        Assert.Equal(2900, record.Baika);
        Assert.Equal("通季", record.Kisetsu);
    }

    [Fact]
    public void Parse_MissingRequiredColumn_ThrowsFormatInvalid()
    {
        var brokenHeader = string.Join(
            ",", SalesCsvReader.RequiredColumns.Where(c => c != "baika"));
        using var reader = new StringReader(brokenHeader + "\n" + ValidRow + "\n");

        var ex = Assert.Throws<AppException>(() => SalesCsvReader.Parse(reader));

        Assert.Equal(ErrorCodes.ImportFormatInvalid.Code, ex.Error.Code);
    }

    [Fact]
    public void Parse_InvalidNumber_CollectsRowError()
    {
        using var reader = Csv(ReplaceCell(ValidRow, 28, "abc")); // genka

        var result = SalesCsvReader.Parse(reader);

        Assert.Empty(result.Records);
        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.RowNumber);
        Assert.Contains("genka", error.Message);
    }

    [Fact]
    public void Parse_NonMondayImportDate_CollectsRowError()
    {
        using var reader = Csv(ReplaceCell(ValidRow, 0, "2026-05-12")); // 火曜日

        var result = SalesCsvReader.Parse(reader);

        Assert.Empty(result.Records);
        var error = Assert.Single(result.Errors);
        Assert.Contains("月曜日", error.Message);
    }

    [Fact]
    public void Parse_EmptyKeyColumn_CollectsRowError()
    {
        using var reader = Csv(ReplaceCell(ValidRow, 1, string.Empty)); // customer_code

        var result = SalesCsvReader.Parse(reader);

        Assert.Empty(result.Records);
        var error = Assert.Single(result.Errors);
        Assert.Contains("customer_code", error.Message);
    }

    [Fact]
    public void Parse_ValidAndInvalidRows_SeparatesThem()
    {
        using var reader = Csv(ValidRow, ReplaceCell(ValidRow, 0, "2026-05-12"), ValidRow);

        var result = SalesCsvReader.Parse(reader);

        Assert.Equal(2, result.Records.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Parse_EmptyFile_ThrowsFileEmpty()
    {
        using var reader = new StringReader(string.Empty);

        var ex = Assert.Throws<AppException>(() => SalesCsvReader.Parse(reader));

        Assert.Equal(ErrorCodes.ImportFileEmpty.Code, ex.Error.Code);
    }

    [Fact]
    public void Parse_HeaderOnly_ThrowsFileEmpty()
    {
        using var reader = new StringReader(Header + "\n");

        var ex = Assert.Throws<AppException>(() => SalesCsvReader.Parse(reader));

        Assert.Equal(ErrorCodes.ImportFileEmpty.Code, ex.Error.Code);
    }

    [Fact]
    public void Parse_OptionalTanawariColumn_IsMapped()
    {
        using var reader = new StringReader(
            Header + ",tanawari1\n" + ValidRow + ",棚A\n");

        var result = SalesCsvReader.Parse(reader);

        var record = Assert.Single(result.Records);
        Assert.Equal("棚A", record.Tanawari1);
    }
}
