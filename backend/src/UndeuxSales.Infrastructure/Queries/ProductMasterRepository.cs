using Dapper;
using Npgsql;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 商品マスタ（m_product / m_product_sku）の参照リポジトリ。
/// 一覧（カード型UI用の集計済みサマリ）と詳細（SKU+画像）を提供する。
/// </summary>
public sealed class ProductMasterRepository
{
    private const int DefaultPageSize = 24;
    private const int MaxPageSize = 200;
    // m_product_sku が大きくなった環境でも 30 秒のデフォルトで打ち切られないよう、
    // 参照クエリは明示的に余裕のあるタイムアウトを与える（分析系の許容範囲）。
    private const int QueryCommandTimeoutSeconds = 120;

    private readonly IDbConnectionFactory _connectionFactory;

    public ProductMasterRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>商品マスタの検索フィルタ選択肢（業態・部門・ブランド・担当者）を返す。</summary>
    public async Task<MasterFilterOptions> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        // 業態は business_type マスタを SoT として返す（short_name 付き）。
        var businessTypes = await MasterRepository.QueryBusinessTypesAsync(connection, cancellationToken);

        var divisions = (await connection.QueryAsync<DivisionRow>(new CommandDefinition("""
            SELECT division_cd::text AS code,
                   MAX(division_name) AS name
            FROM m_product
            GROUP BY division_cd
            ORDER BY division_cd;
            """, cancellationToken: cancellationToken)))
            .Select(d => new CodeName(d.Code, d.Name))
            .ToList();

        var brands = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT brand
            FROM m_product
            WHERE brand IS NOT NULL AND brand <> ''
            ORDER BY brand;
            """, cancellationToken: cancellationToken))).ToList();

        var managers = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT manager
            FROM m_product
            WHERE manager IS NOT NULL AND manager <> ''
            ORDER BY manager;
            """, cancellationToken: cancellationToken))).ToList();

        return new MasterFilterOptions(businessTypes, divisions, brands, managers);
    }

    /// <summary>商品マスタの一覧（カード表示用に集計済み）をページングで取得する。</summary>
    public async Task<MasterProductPage> GetProductsAsync(
        ProductMasterFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        ProductMasterFilterSql.AddParameters(filter, parameters);
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);

        // SKU 統計と代表画像（最小 image_index）を 1 商品 1 行に集約する。
        var sql = $"""
            WITH sku_stats AS (
                SELECT product_id,
                       COUNT(DISTINCT unit_cd)                          AS sku_count,
                       COUNT(DISTINCT color_name)                       AS color_count,
                       COUNT(DISTINCT size_name)                        AS size_count,
                       MIN(sales_price) FILTER (WHERE sales_price > 0)  AS min_sales_price,
                       MAX(sales_price) FILTER (WHERE sales_price > 0)  AS max_sales_price
                FROM m_product_sku
                GROUP BY product_id
            )
            SELECT mp.product_id,
                   mp.business_category_cd,
                   mp.business_category_sign,
                   mp.division_cd,
                   mp.division_name,
                   mp.product_name,
                   mp.brand,
                   mp.product_sign,
                   mp.manager,
                   mp.product_type_crd,
                   COALESCE(ss.sku_count, 0)::int   AS sku_count,
                   COALESCE(ss.color_count, 0)::int AS color_count,
                   COALESCE(ss.size_count, 0)::int  AS size_count,
                   ss.min_sales_price,
                   ss.max_sales_price,
                   img.image_url                    AS primary_image_url,
                   (COUNT(*) OVER ())::int          AS total_count
            FROM m_product mp
            LEFT JOIN sku_stats ss ON ss.product_id = mp.product_id
            LEFT JOIN LATERAL (
                SELECT image_url
                FROM m_product_sku
                WHERE product_id = mp.product_id
                ORDER BY image_index, sku_item_id
                LIMIT 1
            ) AS img ON true
            {ProductMasterFilterSql.WhereClause(filter, "mp")}
            ORDER BY mp.business_category_cd, mp.product_sign, mp.product_type_crd, mp.product_id
            LIMIT @limit OFFSET @offset;
            """;

        var rawRows = (await connection.QueryAsync<MasterProductRawRow>(
            new CommandDefinition(sql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken))).ToList();

        // OFFSET overshoot で本体クエリが空集合の場合でも、実件数を別クエリで取得する
        // （window関数 COUNT(*) OVER () は LIMIT が空だと値を返さないため）。
        var totalCount = rawRows.Count > 0
            ? rawRows[0].TotalCount
            : await CountProductsAsync(connection, filter, parameters, cancellationToken);
        var items = rawRows.Select(ToSummary).ToList();

        return new MasterProductPage(items, totalCount, page, pageSize);
    }

    private static async Task<int> CountProductsAsync(
        NpgsqlConnection connection,
        ProductMasterFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(*)::int
            FROM m_product mp
            {ProductMasterFilterSql.WhereClause(filter, "mp")};
            """;
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken));
    }

    /// <summary>商品マスタの詳細（親 + SKU 一覧、SKU 内画像は IReadOnlyList で集約）を取得する。</summary>
    public async Task<MasterProductDetail?> GetProductDetailAsync(
        Guid productId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("productId", productId);

        var headSql = """
            WITH sku_stats AS (
                SELECT product_id,
                       COUNT(DISTINCT unit_cd)                          AS sku_count,
                       COUNT(DISTINCT color_name)                       AS color_count,
                       COUNT(DISTINCT size_name)                        AS size_count,
                       MIN(sales_price) FILTER (WHERE sales_price > 0)  AS min_sales_price,
                       MAX(sales_price) FILTER (WHERE sales_price > 0)  AS max_sales_price
                FROM m_product_sku
                WHERE product_id = @productId
                GROUP BY product_id
            )
            SELECT mp.product_id,
                   mp.business_category_cd,
                   mp.business_category_sign,
                   mp.division_cd,
                   mp.division_name,
                   mp.product_name,
                   mp.brand,
                   mp.product_sign,
                   mp.manager,
                   mp.product_type_crd,
                   COALESCE(ss.sku_count, 0)::int   AS sku_count,
                   COALESCE(ss.color_count, 0)::int AS color_count,
                   COALESCE(ss.size_count, 0)::int  AS size_count,
                   ss.min_sales_price,
                   ss.max_sales_price,
                   img.image_url                    AS primary_image_url,
                   0::int                           AS total_count
            FROM m_product mp
            LEFT JOIN sku_stats ss ON ss.product_id = mp.product_id
            LEFT JOIN LATERAL (
                SELECT image_url
                FROM m_product_sku
                WHERE product_id = mp.product_id
                ORDER BY image_index, sku_item_id
                LIMIT 1
            ) AS img ON true
            WHERE mp.product_id = @productId;
            """;

        var head = await connection.QuerySingleOrDefaultAsync<MasterProductRawRow>(
            new CommandDefinition(headSql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken));
        if (head is null)
        {
            return null;
        }

        const string skuSql = """
            SELECT sku_item_id,
                   unit_cd,
                   color_name,
                   size_name,
                   sales_price,
                   cost_price,
                   image_id,
                   image_index,
                   image_file_name,
                   image_url
            FROM m_product_sku
            WHERE product_id = @productId
            ORDER BY color_name, size_name, unit_cd, image_index, sku_item_id;
            """;

        var skuImageRows = (await connection.QueryAsync<MasterProductSkuRawRow>(
            new CommandDefinition(skuSql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken))).ToList();

        // 同一 SKU（unit_cd × color × size）に複数の画像（image_index）が紐づくケースを集約する。
        var skus = skuImageRows
            .GroupBy(r => (r.UnitCd, r.ColorName, r.SizeName))
            .Select(g =>
            {
                var first = g.OrderBy(r => r.ImageIndex).ThenBy(r => r.SkuItemId).First();
                var images = g
                    .OrderBy(r => r.ImageIndex)
                    .ThenBy(r => r.SkuItemId)
                    .Select(r => new MasterProductSkuImage(
                        r.ImageId, r.ImageIndex, r.ImageFileName, r.ImageUrl))
                    .ToList();
                return new MasterProductSku(
                    first.SkuItemId,
                    first.UnitCd,
                    first.ColorName,
                    first.SizeName,
                    first.SalesPrice,
                    first.CostPrice,
                    images);
            })
            .ToList();

        return new MasterProductDetail(ToSummary(head), skus);
    }

    private static MasterProductSummary ToSummary(MasterProductRawRow row) => new(
        row.ProductId,
        row.BusinessCategoryCd,
        row.BusinessCategorySign,
        row.DivisionCd,
        row.DivisionName,
        row.ProductName,
        row.Brand,
        row.ProductSign,
        row.Manager,
        row.ProductTypeCrd,
        row.SkuCount,
        row.ColorCount,
        row.SizeCount,
        row.MinSalesPrice,
        row.MaxSalesPrice,
        row.PrimaryImageUrl);

    private sealed record DivisionRow(string Code, string? Name);

    private sealed record MasterProductRawRow(
        Guid ProductId,
        string BusinessCategoryCd,
        string BusinessCategorySign,
        int DivisionCd,
        string DivisionName,
        string ProductName,
        string? Brand,
        string ProductSign,
        string? Manager,
        string ProductTypeCrd,
        int SkuCount,
        int ColorCount,
        int SizeCount,
        int? MinSalesPrice,
        int? MaxSalesPrice,
        string? PrimaryImageUrl,
        int TotalCount);

    private sealed record MasterProductSkuRawRow(
        Guid SkuItemId,
        string UnitCd,
        string ColorName,
        string SizeName,
        int SalesPrice,
        int CostPrice,
        Guid ImageId,
        int ImageIndex,
        string? ImageFileName,
        string ImageUrl);
}
