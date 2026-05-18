namespace UndeuxSales.Core.Models;

/// <summary>
/// 売上参照ファイルの1レコード。<c>sales_weekly</c> テーブルの業務カラムに対応する。
/// 初期DBダンプ取込・週次CSV取込・DB書込の共通モデル。
/// </summary>
public sealed class SalesRecord
{
    /// <summary>取込日（月曜日）。</summary>
    public DateOnly ImportDate { get; set; }

    public string CustomerCode { get; set; } = string.Empty;
    public string GyotaiCode { get; set; } = string.Empty;
    public string ChohyoKubunName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string HinbanCode { get; set; } = string.Empty;
    public string TanpinCode { get; set; } = string.Empty;
    public string Hinmei { get; set; } = string.Empty;
    public string ShohinKigou { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string? Tanawari1 { get; set; }
    public string? Tanawari2 { get; set; }

    /// <summary>当週売上数量（月）= 取込日-7 の売上数量。</summary>
    public int ToshuUriageCount1 { get; set; }
    public int ToshuUriageCount2 { get; set; }
    public int ToshuUriageCount3 { get; set; }
    public int ToshuUriageCount4 { get; set; }
    public int ToshuUriageCount5 { get; set; }
    public int ToshuUriageCount6 { get; set; }

    /// <summary>当週売上数量（日）= 取込日-1 の売上数量。</summary>
    public int ToshuUriageCount7 { get; set; }

    public int UriageCountZenshu { get; set; }
    public int UriageCount2Shumae { get; set; }
    public int UriageCount3Shumae { get; set; }
    public int UriageCount4Shumae { get; set; }

    public int Zaikosu { get; set; }
    public int RuikeiUriageCount { get; set; }
    public int RuikeiNohinCount { get; set; }
    public decimal HatchuCount { get; set; }

    public string DonyuDate { get; set; } = string.Empty;
    public int Zainiti { get; set; }
    public int Genka { get; set; }
    public int Baika { get; set; }
    public string Kisetsu { get; set; } = string.Empty;
    public int SakizukeCount { get; set; }

    /// <summary>元データの created_at（取込前の値を保持。タイムゾーン情報なし）。</summary>
    public DateTime? SourceCreatedAt { get; set; }
    public DateTime? SourceUpdatedAt { get; set; }

    /// <summary>当週（月〜日）の売上数量合計。</summary>
    public int WeekTotalQuantity =>
        ToshuUriageCount1 + ToshuUriageCount2 + ToshuUriageCount3 + ToshuUriageCount4
        + ToshuUriageCount5 + ToshuUriageCount6 + ToshuUriageCount7;

    /// <summary>日次インデックス(1=月..7=日)で当週売上数量を取得する。</summary>
    public int DailyQuantity(int dayIndex) => dayIndex switch
    {
        1 => ToshuUriageCount1,
        2 => ToshuUriageCount2,
        3 => ToshuUriageCount3,
        4 => ToshuUriageCount4,
        5 => ToshuUriageCount5,
        6 => ToshuUriageCount6,
        7 => ToshuUriageCount7,
        _ => throw new ArgumentOutOfRangeException(nameof(dayIndex), dayIndex,
            "dayIndex は 1〜7 で指定してください。"),
    };
}
