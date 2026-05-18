using UndeuxSales.Core.Parsing;

namespace UndeuxSales.Tests.Unit;

public sealed class SqlTupleScannerTests
{
    [Fact]
    public void ScanTuples_SimpleTuple_ParsesFields()
    {
        var tuples = SqlTupleScanner.ScanTuples("(1,'abc',2)").ToList();

        Assert.Single(tuples);
        Assert.Equal(new string?[] { "1", "abc", "2" }, tuples[0]);
    }

    [Fact]
    public void ScanTuples_NullLiteral_ParsesAsNull()
    {
        var tuples = SqlTupleScanner.ScanTuples("(1,NULL,'x')").ToList();

        Assert.Equal(new string?[] { "1", null, "x" }, tuples[0]);
    }

    [Fact]
    public void ScanTuples_MultipleTuples_ParsesEach()
    {
        var tuples = SqlTupleScanner.ScanTuples("(1,'a'),(2,'b'),(3,'c')").ToList();

        Assert.Equal(3, tuples.Count);
        Assert.Equal(new string?[] { "2", "b" }, tuples[1]);
    }

    [Fact]
    public void ScanTuples_BackslashEscapedQuote_Unescapes()
    {
        var tuples = SqlTupleScanner.ScanTuples(@"('it\'s ok')").ToList();

        Assert.Equal("it's ok", tuples[0][0]);
    }

    [Fact]
    public void ScanTuples_DoubledQuote_Unescapes()
    {
        var tuples = SqlTupleScanner.ScanTuples("('it''s ok')").ToList();

        Assert.Equal("it's ok", tuples[0][0]);
    }

    [Fact]
    public void ScanTuples_BackslashEscapeSequences_Unescapes()
    {
        var tuples = SqlTupleScanner.ScanTuples(@"('line1\nline2\ttab')").ToList();

        Assert.Equal("line1\nline2\ttab", tuples[0][0]);
    }

    [Fact]
    public void ScanTuples_DelimiterCharsInsideString_AreNotTreatedAsDelimiters()
    {
        var tuples = SqlTupleScanner.ScanTuples("('a,b)c','d')").ToList();

        Assert.Equal(new string?[] { "a,b)c", "d" }, tuples[0]);
    }

    [Fact]
    public void ScanTuples_JapaneseText_Preserved()
    {
        var tuples = SqlTupleScanner.ScanTuples("('日本語ｶﾅ','通季')").ToList();

        Assert.Equal(new string?[] { "日本語ｶﾅ", "通季" }, tuples[0]);
    }

    [Fact]
    public void ScanTuples_NegativeAndDecimalNumbers_PreservedAsText()
    {
        var tuples = SqlTupleScanner.ScanTuples("(-2,0.0,1480)").ToList();

        Assert.Equal(new string?[] { "-2", "0.0", "1480" }, tuples[0]);
    }

    [Fact]
    public void ScanTuples_StartOffsetAndTrailingSemicolon_AreHandled()
    {
        const string text = "VALUES (1,'a'),(2,'b');";

        var tuples = SqlTupleScanner.ScanTuples(text, "VALUES ".Length).ToList();

        Assert.Equal(2, tuples.Count);
    }

    [Fact]
    public void ScanTuples_EmptyString_ParsesAsEmpty()
    {
        var tuples = SqlTupleScanner.ScanTuples("('',1)").ToList();

        Assert.Equal(new string?[] { string.Empty, "1" }, tuples[0]);
    }

    [Fact]
    public void ScanTuples_UnterminatedString_Throws()
    {
        Assert.Throws<FormatException>(
            () => SqlTupleScanner.ScanTuples("(1,'unterminated)").ToList());
    }
}
