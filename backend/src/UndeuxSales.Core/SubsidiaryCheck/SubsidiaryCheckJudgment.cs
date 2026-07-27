namespace UndeuxSales.Core.SubsidiaryCheck;

/// <summary>
/// 指摘の severity 集計から総合判定（judgment）を導出する純粋関数。
/// fail &gt; 0 → fail（要修正） / warn &gt; 0 → warn（要確認） / それ以外 → pass（合格）。
/// </summary>
public static class SubsidiaryCheckJudgment
{
    /// <summary>指摘から fail / warn の件数を集計する（pass は件数に含めない）。</summary>
    public static (int FailCount, int WarnCount) Count(IEnumerable<SubsidiaryCheckFinding> findings)
    {
        var fail = 0;
        var warn = 0;
        foreach (var finding in findings)
        {
            switch (finding.Severity)
            {
                case SubsidiaryCheckSeverity.Fail:
                    fail++;
                    break;
                case SubsidiaryCheckSeverity.Warn:
                    warn++;
                    break;
            }
        }

        return (fail, warn);
    }

    /// <summary>fail / warn 件数から総合判定を導出する。</summary>
    public static string Decide(int failCount, int warnCount) =>
        failCount > 0 ? SubsidiaryCheckSeverity.Fail
        : warnCount > 0 ? SubsidiaryCheckSeverity.Warn
        : SubsidiaryCheckSeverity.Pass;
}
