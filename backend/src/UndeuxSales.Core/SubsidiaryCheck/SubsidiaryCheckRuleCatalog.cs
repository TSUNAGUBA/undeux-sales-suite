using System.Text;

namespace UndeuxSales.Core.SubsidiaryCheck;

/// <summary>
/// 副資材（下げ札・TAG）チェックルールの単一 SoT。
/// <para>
/// ルールブック表示（<see cref="BuildRuleBook"/>）と AI プロンプト（<see cref="BuildPromptRules"/>）の
/// 両方を本カタログから生成し、画面表示とチェック基準の乖離を防ぐ（1カタログ→2出力）。
/// 出典: しまむらグループ「アイコンの管理と運用」（406章）・アイコン付記用語注意事項一覧・
/// 副資材依頼フォーマット、および un.deux 指示書の表示順序規定（転記。原本と差異がある場合は原本が正）。
/// </para>
/// </summary>
public static class SubsidiaryCheckRuleCatalog
{
    /// <summary>出典の総括文（ルールブック応答に含める）。</summary>
    public const string SourceSummary =
        "出典: しまむらグループ「アイコンの管理と運用」マニュアル（406章）、アイコン付記用語注意事項一覧、"
        + "副資材依頼フォーマット、および un.deux 指示書の表示順序規定。原本と差異がある場合は原本が正。";

    private static readonly IReadOnlyList<SubsidiaryRuleSection> Sections = new[]
    {
        new SubsidiaryRuleSection(
            "display-order",
            "表示の順序",
            "タグ・品質表示の記載は次の順序に従う。指示書・付属情報の表示順序指定がある場合はそちらを優先して照合する。",
            new[]
            {
                new SubsidiaryRuleItem(
                    "標準の表示順序",
                    "品番 → サイズ → 混率 → 洗濯表示 → 色落ち表示等 → 原産国表示 → 製造者 の順に記載する。",
                    "un.deux 指示書"),
            }),
        new SubsidiaryRuleSection(
            "icon-priority",
            "アイコンの優先順位（7分類）",
            "アイコンは優先順位の昇順に左から記載する。優先順位が同じ場合は強調したいアイコンを左から記載する。"
            + "優先順位と異なる順番は商品部担当執行役員の承認が必要。靴アイコン・ヘビロテシリーズは優先順位表の対象外。"
            + "アイコン順番は WEBEDI 通りではなく、しまむら品管承認の順番に従う。",
            new[]
            {
                new SubsidiaryRuleItem("優先1: 温感、涼感、ドライ", "例: WARM、接触冷感、DRY", "アイコン順番26.6 §4-3"),
                new SubsidiaryRuleItem("優先2: 素材", "例: 綿100%、綿混、本革", "アイコン順番26.6 §4-3"),
                new SubsidiaryRuleItem("優先3: 素材形状", "例: 起毛、裏起毛、裏ボア", "アイコン順番26.6 §4-3"),
                new SubsidiaryRuleItem(
                    "優先4: 快適・その他の機能",
                    "快適（暖かさ・爽やかさ）: MOIST、吸湿放湿、遮熱 / 快適（衛生・清潔）: 制菌、防汚、防ダニ / "
                    + "その他の機能: UV対策、UPF50+、静電防止",
                    "アイコン順番26.6 §4-3"),
                new SubsidiaryRuleItem("優先5: 素材風合い", "例: STRETCH、ソフトタッチ、低反発", "アイコン順番26.6 §4-3"),
                new SubsidiaryRuleItem("優先6: 仕様", "例: サイズ調整、YKKファスナー使用、滑り止め加工", "アイコン順番26.6 §4-3"),
                new SubsidiaryRuleItem("優先7: 取り扱い", "例: 乾燥機OK、EASY CARE、洗える", "アイコン順番26.6 §4-3"),
            }),
        new SubsidiaryRuleSection(
            "layout",
            "レイアウト規定（表面並び・寸法要点）",
            "タグの構成・配置・寸法の規定。指示書のレイアウトとの一致も確認する。",
            new[]
            {
                new SubsidiaryRuleItem(
                    "表面の並び",
                    "上から 商品名 → アイコン → サイズ表記 の順（入れ替え不可）。",
                    "副資材依頼フォーマット"),
                new SubsidiaryRuleItem(
                    "タグサイズ・穴",
                    "スリッパは基本 85mm × 45mm（吊るした時に踵からはみ出さない）。穴は直径3mm、端から穴までの距離5mm。",
                    "副資材依頼フォーマット"),
                new SubsidiaryRuleItem(
                    "アイコン・文字・シールの寸法",
                    "アイコンサイズは最小8mm。文字サイズは基本8pt（約3mm）・最低5.5pt（約1.5〜2mm）。"
                    + "紙マークは最小6mm（任意）。証紙シールは12mmで統一。",
                    "副資材依頼フォーマット"),
                new SubsidiaryRuleItem(
                    "フォント・原産国表記",
                    "裏面説明文は小塚ゴシックProで統一。原産国表示は英語で統一する（例: MADE IN CHINA）。",
                    "副資材依頼フォーマット"),
                new SubsidiaryRuleItem(
                    "業態別サイズ表記",
                    "しまむらTAGはサイズ表記あり。アベイル・シャンブル・バースデイTAGはサイズ表記なし。"
                    + "福袋・PISは全業態サイズ表記あり。",
                    "副資材依頼フォーマット"),
                new SubsidiaryRuleItem(
                    "裏面の必須表示",
                    "社名・住所・TEL、輸入元・販売元、原産国表示、洗濯絵表示、組成表示、品番（スリッパ以外）、"
                    + "副資材取得番号。キャラクター商品はコピーライト・年度表記。",
                    "副資材依頼フォーマット"),
            }),
        new SubsidiaryRuleSection(
            "demerit",
            "必須デメリット表記（打ち消し表示）",
            "アイコンを入れた場合は必ず裏面に付記用語を入れる。以下の機能を謳う場合は対応するデメリット表記が必須。",
            new[]
            {
                new SubsidiaryRuleItem("はっ水", "「※防水ではありません」を必ず表示する。", "アイコン順番26.6 §4-4"),
                new SubsidiaryRuleItem("防水", "「※完全防水ではありません」を必ず表示する。", "アイコン順番26.6 §4-4"),
                new SubsidiaryRuleItem(
                    "防滑",
                    "「※濡れたマンホールの上やタイルの上など路面状況によっては滑ります」を必ず表示する。",
                    "アイコン順番26.6 §4-4"),
                new SubsidiaryRuleItem("防音", "「※完全防音ではありません」を必ず表示する。", "アイコン順番26.6 §4-4"),
                new SubsidiaryRuleItem(
                    "透けにくい",
                    "「※使用環境によって機能が低下する恐れがあります」を必ず表示する。",
                    "アイコン順番26.6 §4-4"),
                new SubsidiaryRuleItem(
                    "持続冷感",
                    "「触れた瞬間のひんやり感が長持ちします。（当社基準）」を必須表示する。",
                    "アイコン順番26.6 §4-4"),
                new SubsidiaryRuleItem(
                    "温度調節",
                    "「※使用環境によって効果が異なります」を必ず表示する。",
                    "アイコン順番26.6 §4-4"),
            }),
        new SubsidiaryRuleSection(
            "forbidden",
            "禁止・要注意表現（4分類）",
            "以下の用語・表現が記載されていないか確認する。",
            new[]
            {
                new SubsidiaryRuleItem(
                    "断定的用語（使用禁止）",
                    "「完全」「完ぺき」「パーフェクト」等の全く欠ける所が無い意味の用語、"
                    + "「抜群」「画期的」「理想的」等の優位性を意味する用語、"
                    + "「安全」「安心」等の安全性を強調する用語は使用しない。",
                    "副資材依頼フォーマット・付記用語注意事項一覧"),
                new SubsidiaryRuleItem(
                    "客観的根拠の提示が必要な用語",
                    "「最高」「最大」「最小」「最高級」「超」「極上」等の最大級、"
                    + "「世界一」「日本一」「第一位」「当社だけ」等の第一位、"
                    + "「新製品」「新発売」「ニュー」等の斬新性を意味する用語は根拠なしに使用しない。",
                    "副資材依頼フォーマット・付記用語注意事項一覧"),
                new SubsidiaryRuleItem(
                    "個別の断定表現（禁止）",
                    "「滑らない」（防滑・滑り止め加工）、「透けない」（透けにくい）、"
                    + "「臭いがしない」（室内干し臭軽減）、跡が「付かない」（吊り干し・シワ系）等の断定表現は使用しない。",
                    "アイコン順番26.6 §5"),
                new SubsidiaryRuleItem(
                    "素材名の表記",
                    "「竹」単体表記はNG（「竹素材」ならOK）。「い草」単体表記はNG（他社商標。「い草素材」ならOK）。"
                    + "竹・い草等の天然素材は「【取り扱い上のご注意】天然素材のため、ささくれ等がある場合があります"
                    + "ので取り扱いにはご注意ください。」を必ず入れる。",
                    "付記用語注意事項一覧"),
            }),
    };

    /// <summary>画面表示用のルールブックを生成する。</summary>
    public static SubsidiaryRuleBook BuildRuleBook() => new(Sections, SourceSummary);

    /// <summary>AI プロンプト（system）に埋め込むルールテキストを生成する。</summary>
    public static string BuildPromptRules()
    {
        var builder = new StringBuilder();
        foreach (var section in Sections)
        {
            builder.Append("## ").AppendLine(section.Title);
            if (!string.IsNullOrEmpty(section.Description))
            {
                builder.AppendLine(section.Description);
            }

            foreach (var item in section.Items)
            {
                builder.Append("- ").Append(item.Name).Append(": ").AppendLine(item.Description);
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
