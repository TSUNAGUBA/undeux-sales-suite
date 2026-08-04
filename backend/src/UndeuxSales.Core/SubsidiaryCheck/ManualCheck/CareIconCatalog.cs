namespace UndeuxSales.Core.SubsidiaryCheck.ManualCheck;

/// <summary>
/// 洗濯表示（取扱い絵表示）アイコンの単一 SoT。
/// <para>
/// 原本 SVG は外部依存のない自己完結のインライン SVG として本カタログが保持する
/// （ライセンス・CSP・外部取得の不確実性を避ける・原則3/原則4）。
/// JIS L0001 / ISO 3758 の分類（洗濯・漂白・タンブル乾燥・自然乾燥・アイロン・商業クリーニング）に
/// 沿った代表記号を収録する。形状は識別可能性を優先した簡略図であり、法定の原本と差異がある場合は
/// 原本が正（あくまで人手検品の補助）。
/// </para>
/// <para>
/// アイコンの <c>Code</c> は入力（選択）・AI 抽出・突合の突き合わせキーであり、変更禁止（追加のみ）。
/// SVG は <c>currentColor</c> でストロークするためライト/ダーク両テーマに追従する。
/// </para>
/// </summary>
public static class CareIconCatalog
{
    /// <summary>分類コード。</summary>
    public static class Category
    {
        public const string Wash = "wash";
        public const string Bleach = "bleach";
        public const string Tumble = "tumble";
        public const string NaturalDry = "dry";
        public const string Iron = "iron";
        public const string Professional = "professional";
    }

    public const string Source =
        "洗濯表示アイコン（JIS L0001 / ISO 3758 準拠の簡略図。原本 SVG はコード内蔵で自己完結）。"
        + "識別補助用であり、法定表示の原本と差異がある場合は原本が正。";

    private static readonly IReadOnlyList<CareIcon> Catalog = BuildCatalog();

    /// <summary>全アイコン（選択 UI・突合の選択肢）。</summary>
    public static IReadOnlyList<CareIcon> All => Catalog;

    private static readonly IReadOnlyDictionary<string, CareIcon> ByCode =
        Catalog.ToDictionary(icon => icon.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>コードが既知のアイコンか（比較は大文字小文字非依存）。</summary>
    public static bool IsKnownCode(string code) => ByCode.ContainsKey(code.Trim());

    /// <summary>コードからアイコンを引く（未知なら null。比較は大文字小文字非依存）。</summary>
    public static CareIcon? Find(string code) =>
        ByCode.TryGetValue(code.Trim(), out var icon) ? icon : null;

    /// <summary>コードから日本語名称を引く（未知ならコードそのものを返す）。</summary>
    public static string LabelOf(string code) => Find(code)?.Label ?? code.Trim();

    /// <summary>画面表示・AI 応答用のレスポンスを構築する。</summary>
    public static CareIconCatalogResponse BuildResponse() => new(Catalog, Source);

    private static IReadOnlyList<CareIcon> BuildCatalog() => new List<CareIcon>
    {
        // ---- 洗濯（washtub） ----
        Wash("wash_95", "液温95℃限度・洗濯機（洗濯可）", Tub("95")),
        Wash("wash_40", "液温40℃限度・洗濯機（洗濯可）", Tub("40")),
        Wash("wash_30", "液温30℃限度・洗濯機（弱め）", Tub("30")),
        Wash("wash_hand", "手洗い（液温40℃限度）", Tub(null, HandMark)),
        Wash("wash_none", "家庭での洗濯禁止", Tub(null, Prohibit)),

        // ---- 漂白（triangle） ----
        Bleach("bleach_any", "塩素系・酸素系漂白可", Triangle()),
        Bleach("bleach_oxygen", "酸素系漂白のみ可（塩素系不可）", Triangle(OxygenStripes)),
        Bleach("bleach_none", "漂白禁止", Triangle(Prohibit)),

        // ---- タンブル乾燥（square + circle） ----
        Tumble("tumble_high", "タンブル乾燥可（高温）", TumbleBox(TwoDots)),
        Tumble("tumble_low", "タンブル乾燥可（低温）", TumbleBox(OneDot)),
        Tumble("tumble_none", "タンブル乾燥禁止", TumbleBox(Prohibit)),

        // ---- 自然乾燥（square） ----
        NaturalDry("dry_line", "つり干し", DryBox(VerticalLine)),
        NaturalDry("dry_flat", "平干し", DryBox(HorizontalLine)),
        NaturalDry("dry_drip", "ぬれつり干し", DryBox(TwoVerticalLines)),
        NaturalDry("dry_shade", "日陰でつり干し", DryBox(VerticalLine + ShadeCorner)),

        // ---- アイロン（iron） ----
        Iron("iron_high", "高温アイロン可（200℃限度）", IronShape(ThreeDots)),
        Iron("iron_mid", "中温アイロン可（150℃限度）", IronShape(TwoDots)),
        Iron("iron_low", "低温アイロン可（110℃限度・スチームなし）", IronShape(OneDot)),
        Iron("iron_none", "アイロン禁止", IronShape(Prohibit)),

        // ---- 商業クリーニング（circle） ----
        Professional("pro_p", "ドライクリーニング可（パークロロエチレン系）", Circle("P")),
        Professional("pro_f", "ドライクリーニング可（石油系）", Circle("F")),
        Professional("pro_none", "ドライクリーニング禁止", Circle(null, Prohibit)),
    };

    // ------------------------------------------------------------
    // アイコン組立ヘルパー（分類ごとのファクトリと共通オーバーレイ）
    // ------------------------------------------------------------

    private static CareIcon Wash(string code, string label, string body) =>
        new(code, Category.Wash, label, Wrap(body));

    private static CareIcon Bleach(string code, string label, string body) =>
        new(code, Category.Bleach, label, Wrap(body));

    private static CareIcon Tumble(string code, string label, string body) =>
        new(code, Category.Tumble, label, Wrap(body));

    private static CareIcon NaturalDry(string code, string label, string body) =>
        new(code, Category.NaturalDry, label, Wrap(body));

    private static CareIcon Iron(string code, string label, string body) =>
        new(code, Category.Iron, label, Wrap(body));

    private static CareIcon Professional(string code, string label, string body) =>
        new(code, Category.Professional, label, Wrap(body));

    /// <summary>SVG のルート要素で本体を包む（currentColor でテーマ追従・外部依存なし）。</summary>
    private static string Wrap(string body) =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48' fill='none' "
        + "stroke='currentColor' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round' "
        + "role='img' aria-hidden='true'>" + body + "</svg>";

    // ---- 分類ベース形状 ----

    /// <summary>洗濯桶（水波つき台形）。中央に温度数字、または overlay を重ねる。</summary>
    private static string Tub(string? temperature, string overlay = "")
    {
        var body = "<path d='M5 20 L10 39 L38 39 L43 20 Z'/>"
                   + "<path d='M8 20 q3.5 -5 7 0 t7 0 t7 0 t7 0'/>";
        if (!string.IsNullOrEmpty(temperature))
        {
            body += Number(temperature);
        }

        return body + overlay;
    }

    /// <summary>漂白の三角形。</summary>
    private static string Triangle(string overlay = "") =>
        "<path d='M24 8 L42 39 L6 39 Z'/>" + overlay;

    /// <summary>タンブル乾燥の四角＋円。</summary>
    private static string TumbleBox(string overlay = "") =>
        Square() + "<circle cx='24' cy='24' r='9'/>" + overlay;

    /// <summary>自然乾燥の四角。</summary>
    private static string DryBox(string overlay = "") => Square() + overlay;

    /// <summary>アイロン形状。</summary>
    private static string IronShape(string overlay = "") =>
        "<path d='M8 33 L40 33'/>"
        + "<path d='M11 33 L15 21 L34 21 L37 33 Z'/>"
        + "<path d='M15 21 q9 -7 18 -1'/>"
        + overlay;

    /// <summary>商業クリーニングの円。中央に記号文字、または overlay。</summary>
    private static string Circle(string? letter, string overlay = "")
    {
        var body = "<circle cx='24' cy='24' r='16'/>";
        if (!string.IsNullOrEmpty(letter))
        {
            body += Letter(letter);
        }

        return body + overlay;
    }

    private static string Square() => "<rect x='8' y='8' width='32' height='32' rx='1.5'/>";

    // ---- 共通オーバーレイ ----

    /// <summary>禁止（×）。塗り記号ではなく2本の対角線で示す。</summary>
    private const string Prohibit = "<path d='M9 9 L39 39'/><path d='M39 9 L9 39'/>";

    private const string OneDot = "<circle cx='24' cy='26' r='1.6' fill='currentColor' stroke='none'/>";

    private const string TwoDots =
        "<circle cx='20' cy='26' r='1.6' fill='currentColor' stroke='none'/>"
        + "<circle cx='28' cy='26' r='1.6' fill='currentColor' stroke='none'/>";

    private const string ThreeDots =
        "<circle cx='18' cy='26' r='1.6' fill='currentColor' stroke='none'/>"
        + "<circle cx='24' cy='26' r='1.6' fill='currentColor' stroke='none'/>"
        + "<circle cx='30' cy='26' r='1.6' fill='currentColor' stroke='none'/>";

    private const string OxygenStripes = "<path d='M18 15 L13 27'/><path d='M26 15 L21 27'/>";

    private const string VerticalLine = "<path d='M24 12 L24 36'/>";

    private const string TwoVerticalLines = "<path d='M21 12 L21 36'/><path d='M27 12 L27 36'/>";

    private const string HorizontalLine = "<path d='M12 24 L36 24'/>";

    private const string ShadeCorner =
        "<path d='M8 8 L22 8 L8 22 Z' fill='currentColor' stroke='none'/>";

    private const string HandMark =
        "<path d='M15 32 L15 26 q0 -3 3 -3 l9 0 q4 0 4 4 l0 5' stroke-width='2'/>"
        + "<path d='M15 32 L33 32'/>";

    /// <summary>中央の温度数字（洗濯桶の中に描く）。</summary>
    private static string Number(string text) =>
        "<text x='24' y='34.5' font-size='13' font-family='sans-serif' font-weight='600' "
        + "text-anchor='middle' fill='currentColor' stroke='none'>" + text + "</text>";

    /// <summary>中央の記号文字（商業クリーニングの P / F）。</summary>
    private static string Letter(string text) =>
        "<text x='24' y='30' font-size='17' font-family='sans-serif' font-weight='600' "
        + "text-anchor='middle' fill='currentColor' stroke='none'>" + text + "</text>";
}
