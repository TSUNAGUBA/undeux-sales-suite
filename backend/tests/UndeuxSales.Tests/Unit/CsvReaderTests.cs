using UndeuxSales.Core.Parsing;

namespace UndeuxSales.Tests.Unit;

public sealed class CsvReaderTests
{
    [Fact]
    public void ReadRows_SimpleRows_SplitsFields()
    {
        using var reader = new StringReader("a,b,c\n1,2,3\n");

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b", "c" }, rows[0]);
        Assert.Equal(new[] { "1", "2", "3" }, rows[1]);
    }

    [Fact]
    public void ReadRows_QuotedFieldWithComma_KeepsCommaInField()
    {
        using var reader = new StringReader("\"a,b\",c\n");

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Equal(new[] { "a,b", "c" }, rows[0]);
    }

    [Fact]
    public void ReadRows_EscapedQuote_Unescapes()
    {
        using var reader = new StringReader("\"say \"\"hi\"\"\",x\n");

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Equal(new[] { "say \"hi\"", "x" }, rows[0]);
    }

    [Fact]
    public void ReadRows_CrlfLineEndings_AreHandled()
    {
        using var reader = new StringReader("a,b\r\n1,2\r\n");

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "1", "2" }, rows[1]);
    }

    [Fact]
    public void ReadRows_BlankLines_AreSkipped()
    {
        using var reader = new StringReader("a,b\n\n1,2\n\n");

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ReadRows_LastRowWithoutNewline_IsIncluded()
    {
        using var reader = new StringReader("a,b\n1,2");

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "1", "2" }, rows[1]);
    }

    [Fact]
    public void ReadRows_QuotedFieldWithNewline_KeepsNewline()
    {
        using var reader = new StringReader("\"line1\nline2\",x\n");

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Single(rows);
        Assert.Equal("line1\nline2", rows[0][0]);
    }

    [Fact]
    public void ReadRows_EmptyInput_ReturnsNoRows()
    {
        using var reader = new StringReader(string.Empty);

        var rows = CsvReader.ReadRows(reader).ToList();

        Assert.Empty(rows);
    }
}
