using UndeuxSales.Core.Parsing;

namespace UndeuxSales.Tests.Unit;

public sealed class MySqlDumpReaderTests
{
    // 実ダンプ先頭2行を模した INSERT 文（37列）。
    private const string SampleInsert =
        "INSERT INTO `sales` VALUES "
        + "(1,'2018-01-01','5687','01','売発注　','55','558','0005',"
        + "'UPﾃﾞﾆﾑHVYｽｷﾆｰ73','CE8224','ﾁﾕｳｺﾝﾑｼﾞ','58 -   -','UP','',"
        + "0,0,0,0,0,0,0,1,0,0,0,-2,2,0,0.0,'0',0,1480,2900,'通季',4500,"
        + "'2020-07-17 11:27:56','2023-03-09 08:00:07'),"
        + "(2,'2018-01-08','5687','01','売発注　','55','558','0008',"
        + "'UPﾃﾞﾆﾑHVYｽｷﾆｰ73','CE8224','ﾁﾕｳｺﾝﾑｼﾞ','61 -   -','UP',NULL,"
        + "3,0,0,0,0,0,2,0,0,0,0,7,9,1,1.5,'20180101',8,1480,2900,'通季',0,"
        + "NULL,NULL);";

    [Fact]
    public void ReadRecords_SampleInsert_ReturnsAllRows()
    {
        using var reader = new StringReader(SampleInsert);

        var records = MySqlDumpReader.ReadRecords(reader).ToList();

        Assert.Equal(2, records.Count);
    }

    [Fact]
    public void ReadRecords_MapsFirstRowFieldsCorrectly()
    {
        using var reader = new StringReader(SampleInsert);

        var record = MySqlDumpReader.ReadRecords(reader).First();

        Assert.Equal(new DateOnly(2018, 1, 1), record.ImportDate);
        Assert.Equal("5687", record.CustomerCode);
        Assert.Equal("01", record.GyotaiCode);
        Assert.Equal("売発注　", record.ChohyoKubunName);
        Assert.Equal("55", record.Department);
        Assert.Equal("558", record.HinbanCode);
        Assert.Equal("0005", record.TanpinCode);
        Assert.Equal("UPﾃﾞﾆﾑHVYｽｷﾆｰ73", record.Hinmei);
        Assert.Equal("CE8224", record.ShohinKigou);
        Assert.Equal("ﾁﾕｳｺﾝﾑｼﾞ", record.Color);
        Assert.Equal("58 -   -", record.Size);
        Assert.Equal("UP", record.Tanawari1);
        Assert.Equal(string.Empty, record.Tanawari2);
        Assert.Equal(1, record.UriageCountZenshu);
        Assert.Equal(-2, record.Zaikosu);
        Assert.Equal(2, record.RuikeiUriageCount);
        Assert.Equal(0m, record.HatchuCount);
        Assert.Equal("0", record.DonyuDate);
        Assert.Equal(1480, record.Genka);
        Assert.Equal(2900, record.Baika);
        Assert.Equal("通季", record.Kisetsu);
        Assert.Equal(4500, record.SakizukeCount);
        Assert.Equal(new DateTime(2020, 7, 17, 11, 27, 56), record.SourceCreatedAt);
        Assert.Equal(0, record.WeekTotalQuantity);
    }

    [Fact]
    public void ReadRecords_MapsNullableFields()
    {
        using var reader = new StringReader(SampleInsert);

        var record = MySqlDumpReader.ReadRecords(reader).Skip(1).First();

        Assert.Null(record.Tanawari2);
        Assert.Null(record.SourceCreatedAt);
        Assert.Null(record.SourceUpdatedAt);
        Assert.Equal(1.5m, record.HatchuCount);
        Assert.Equal(5, record.WeekTotalQuantity); // 3+0+0+0+0+0+2
    }

    [Fact]
    public void ReadRecords_IgnoresNonInsertLines()
    {
        const string dump = "-- comment line\n"
            + "/*!40101 SET NAMES utf8mb4 */;\n"
            + "DROP TABLE IF EXISTS `sales`;\n"
            + SampleInsert + "\n";
        using var reader = new StringReader(dump);

        var records = MySqlDumpReader.ReadRecords(reader).ToList();

        Assert.Equal(2, records.Count);
    }

    [Fact]
    public void MapToRecord_WrongFieldCount_Throws()
    {
        var tooFew = new string?[] { "1", "2", "3" };

        Assert.Throws<FormatException>(() => MySqlDumpReader.MapToRecord(tooFew));
    }
}
