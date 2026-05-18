namespace UndeuxSales.Infrastructure;

/// <summary>
/// <c>sales_weekly</c> ファクトテーブルの列構成と、取込（COPY / UPSERT）に用いる
/// SQL を組み立てるためのメタデータ。DataLoader と API の取込処理で共有する。
/// </summary>
public static class SalesWeeklySchema
{
    /// <summary>ファクトテーブル名。</summary>
    public const string TableName = "sales_weekly";

    /// <summary>COPY 先のステージング（一時）テーブル名。</summary>
    public const string StagingTableName = "staging_sales_weekly";

    /// <summary>
    /// 取込対象の業務列（代理キー id・import_batch_id・ingested_at を除く全列）。
    /// COPY とステージングDDLはこの順序・構成に従う。
    /// </summary>
    public static readonly string[] DataColumns =
    {
        "import_date", "customer_code", "gyotai_code", "chohyo_kubun_name",
        "department", "hinban_code", "tanpin_code", "hinmei", "shohin_kigou",
        "color", "size", "tanawari1", "tanawari2",
        "toshu_uriage_count1", "toshu_uriage_count2", "toshu_uriage_count3",
        "toshu_uriage_count4", "toshu_uriage_count5", "toshu_uriage_count6",
        "toshu_uriage_count7",
        "uriage_count_zenshu", "uriage_count_2shumae", "uriage_count_3shumae",
        "uriage_count_4shumae",
        "zaikosu", "ruikei_uriage_count", "ruikei_nohin_count", "hatchu_count",
        "donyu_date", "zainiti", "genka", "baika", "kisetsu", "sakizuke_count",
        "source_created_at", "source_updated_at",
    };

    /// <summary>業務キー列（UPSERT の競合判定に用いる UNIQUE 制約の構成列）。</summary>
    public static readonly string[] KeyColumns =
    {
        "import_date", "customer_code", "gyotai_code",
        "hinban_code", "tanpin_code", "shohin_kigou", "donyu_date",
    };

    /// <summary>ステージング一時テーブルを作成するDDL。</summary>
    public static string StagingTableDdl() => $"""
        CREATE TEMP TABLE {StagingTableName} (
            import_date          date NOT NULL,
            customer_code        text NOT NULL,
            gyotai_code          text NOT NULL,
            chohyo_kubun_name    text NOT NULL,
            department           text NOT NULL,
            hinban_code          text NOT NULL,
            tanpin_code          text NOT NULL,
            hinmei               text NOT NULL,
            shohin_kigou         text NOT NULL,
            color                text NOT NULL,
            size                 text NOT NULL,
            tanawari1            text,
            tanawari2            text,
            toshu_uriage_count1  integer NOT NULL,
            toshu_uriage_count2  integer NOT NULL,
            toshu_uriage_count3  integer NOT NULL,
            toshu_uriage_count4  integer NOT NULL,
            toshu_uriage_count5  integer NOT NULL,
            toshu_uriage_count6  integer NOT NULL,
            toshu_uriage_count7  integer NOT NULL,
            uriage_count_zenshu  integer NOT NULL,
            uriage_count_2shumae integer NOT NULL,
            uriage_count_3shumae integer NOT NULL,
            uriage_count_4shumae integer NOT NULL,
            zaikosu              integer NOT NULL,
            ruikei_uriage_count  integer NOT NULL,
            ruikei_nohin_count   integer NOT NULL,
            hatchu_count         numeric(10,1) NOT NULL,
            donyu_date           text NOT NULL,
            zainiti              integer NOT NULL,
            genka                integer NOT NULL,
            baika                integer NOT NULL,
            kisetsu              text NOT NULL,
            sakizuke_count       integer NOT NULL,
            source_created_at    timestamp,
            source_updated_at    timestamp
        ) ON COMMIT DROP;
        """;

    /// <summary>COPY コマンド（ステージングへのバイナリ取込）。</summary>
    public static string CopyCommand()
        => $"COPY {StagingTableName} ({string.Join(", ", DataColumns)}) FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// ステージングからファクトテーブルへの UPSERT 文を組み立てる。
    /// 業務キーが既存ならば測定値・属性値を更新し、import_batch_id・ingested_at を更新する。
    /// </summary>
    public static string UpsertFromStagingSql()
    {
        var insertColumns = string.Join(", ", DataColumns);
        var keySet = new HashSet<string>(KeyColumns);

        var updateAssignments = DataColumns
            .Where(c => !keySet.Contains(c))
            .Select(c => $"{c} = EXCLUDED.{c}")
            .Append("import_batch_id = EXCLUDED.import_batch_id")
            .Append("ingested_at = now()");

        return $"""
            INSERT INTO {TableName} (import_batch_id, {insertColumns})
            SELECT @batchId, {insertColumns}
            FROM {StagingTableName}
            ON CONFLICT ({string.Join(", ", KeyColumns)})
            DO UPDATE SET
                {string.Join(",\n                ", updateAssignments)};
            """;
    }
}
