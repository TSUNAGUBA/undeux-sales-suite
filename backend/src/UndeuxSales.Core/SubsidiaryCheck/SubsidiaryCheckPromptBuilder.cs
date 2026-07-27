using System.Text;

namespace UndeuxSales.Core.SubsidiaryCheck;

/// <summary>
/// 副資材チェックの AI プロンプトを構築する純粋ロジック。
/// <para>
/// system: 検品専門家ロール＋ルール（<see cref="SubsidiaryCheckRuleCatalog"/> 由来）＋JSON 出力契約。
/// user: 「指示書画像（正）」ラベル付き画像 → 「タグ画像（検査対象）」ラベル付き画像 →
/// 商品ラベル・付属情報テキスト（あれば）→ チェック指示、の順に構成する。
/// </para>
/// </summary>
public static class SubsidiaryCheckPromptBuilder
{
    /// <summary>
    /// 推奨最大出力トークン数。findings JSON（3カテゴリ×複数指摘）を収めるのに十分な値で、
    /// 実際の呼出時は AiOptions.MaxOutputTokens を上限としてこの値まで使用する。
    /// </summary>
    public const int RecommendedMaxTokens = 4096;

    /// <summary>指示書画像のラベル接頭辞。</summary>
    public const string InstructionImageLabel = "指示書画像（正）";

    /// <summary>タグ画像のラベル接頭辞。</summary>
    public const string TagImageLabel = "タグ画像（検査対象）";

    /// <summary>system プロンプト（検品専門家ロール＋ルール＋出力契約）を構築する。</summary>
    public static string BuildSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "あなたはアパレル小売（しまむらグループ）向け副資材（下げ札・TAG・品質表示）の検品専門家です。"
            + "メーカーの出荷前チェックとして、指示書画像（正）とタグ画像（検査対象）を突き合わせ、"
            + "以下のルールに照らして検品してください。");
        builder.AppendLine();
        builder.AppendLine("# 検品ルール");
        builder.AppendLine(SubsidiaryCheckRuleCatalog.BuildPromptRules());
        builder.AppendLine();
        builder.AppendLine("# 出力契約");
        builder.AppendLine("応答は次の JSON オブジェクトのみを返すこと。説明文・前置き・コードフェンスは一切禁止。");
        builder.AppendLine("""
            { "findings": [ { "category": "layout|order|content", "severity": "fail|warn|pass",
                "title": "簡潔な見出し", "detail": "何がどう相違/一致しているか",
                "suggestion": "修正提案(なければ null)", "evidence": "根拠(指示書のどの記載と比較したか)" } ] }
            """);
        builder.AppendLine("- 3カテゴリ（layout=レイアウト / order=順番 / content=内容）それぞれについて最低1件"
            + "（問題がなければ severity=pass の確認結果）を返すこと");
        builder.AppendLine("- fail=指示書・規定との明確な相違 / warn=画像から確証が持てず人の目での確認が必要 / "
            + "pass=確認して問題なし");
        builder.AppendLine("- 画像が不鮮明で判読できない項目は warn として「目視確認してください」と返すこと");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// user メッセージ末尾のテキスト（商品ラベル・付属情報・チェック指示）を構築する。
    /// 画像ブロックの後ろに置くことを前提とする。
    /// </summary>
    /// <param name="product">商品マスタ情報（未選択の場合 null）。</param>
    /// <param name="productLabel">表示用の商品ラベル（品番等。なければ空）。</param>
    public static string BuildUserPrompt(SubsidiaryCheckProductInfo? product, string? productLabel)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(productLabel))
        {
            builder.Append("商品ラベル: ").AppendLine(productLabel.Trim());
        }

        if (product is not null)
        {
            builder.AppendLine("商品マスタ情報:");
            builder.Append("- 商品名: ").AppendLine(product.ProductName);
            builder.Append("- 商品記号: ").AppendLine(product.ProductSign);
            builder.Append("- 品番: ").AppendLine(product.ProductTypeCrd);
            if (!string.IsNullOrWhiteSpace(product.Brand))
            {
                builder.Append("- ブランド: ").AppendLine(product.Brand);
            }

            if (product.Attachment is { } attachment)
            {
                builder.AppendLine("付属情報（商品マスタ登録値。タグの記載内容と一致していること）:");
                AppendIfPresent(builder, "組成・混率", attachment.Composition);
                AppendIfPresent(builder, "原産国", attachment.OriginCountry);
                AppendIfPresent(builder, "洗濯絵表示", attachment.CareLabels);
                AppendIfPresent(builder, "色落ち表示", attachment.ColorFastnessNote);
                AppendIfPresent(builder, "表示の順序", attachment.DisplayOrder);
                AppendIfPresent(builder, "注意事項", attachment.QualityNotes);
            }
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine("上記の指示書画像（正）を基準として、タグ画像（検査対象）を次の3カテゴリで比較・検品し、"
            + "出力契約の JSON のみを返してください:");
        builder.AppendLine("1. layout（レイアウト）: タグの構成・配置が指示書・規定どおりか（表面の並び: 商品名→アイコン→サイズ表記 等）");
        builder.AppendLine("2. order（順番）: 表示の順序が指示書どおりか（品番→サイズ→混率→洗濯表示→色落ち表示等→原産国表示→製造者）、"
            + "アイコンが優先順位順か");
        builder.Append("3. content（内容）: 品番・サイズ・混率・原産国・洗濯絵表示等の記載内容が指示書・付属情報と一致するか、"
            + "禁止用語がないか、必須デメリット表記があるか");
        return builder.ToString();
    }

    /// <summary>
    /// 画像の直前に置くラベルテキスト（例:「指示書画像（正） 1/3」）を構築する。
    /// </summary>
    /// <param name="kind"><see cref="SubsidiaryCheckImageKind"/> の値。</param>
    /// <param name="index">同一種別内の 1 始まり連番。</param>
    /// <param name="total">同一種別の合計枚数。</param>
    public static string BuildImageLabel(string kind, int index, int total)
    {
        var prefix = kind == SubsidiaryCheckImageKind.Instruction ? InstructionImageLabel : TagImageLabel;
        return $"{prefix} {index}/{total}";
    }

    private static void AppendIfPresent(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append("- ").Append(label).Append(": ").AppendLine(value.Trim());
        }
    }
}
