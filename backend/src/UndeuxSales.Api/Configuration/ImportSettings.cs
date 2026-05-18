namespace UndeuxSales.Api.Configuration;

/// <summary>週次CSV取込の設定。</summary>
public sealed class ImportSettings
{
    /// <summary>appsettings.json 上のセクション名。</summary>
    public const string SectionName = "Import";

    /// <summary>取込CSVファイルサイズの上限（バイト）。既定 50MB。</summary>
    public long MaxFileSizeBytes { get; set; } = 52_428_800;
}
