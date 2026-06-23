using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// 規則結果(result_rule_run / result_inf_sampling_test_sample / result_filter_run)失效的單一事實來源。
/// 這些是衍生資料:任何改寫其上游(GL/TB target、科目配對、行事曆、授權清單)的交易,必須在
/// 「同一交易」內呼叫本方法清除舊結果,確保不出現「資料已換、舊結果還在」的中間態——
/// 上游若 rollback,清除亦一併回退。清空後 project.load 的 latestRuns 自然回 null,要求重跑。
/// (DELETE 為 ANSI 共通,SQLite 與 SQL Server 路徑共用同一不變量。)
///
/// 注意:part(a) 控制總數 <c>gl_control_total</c> **不**在此清除範圍。它的上游只有 GL target,
/// 由 GL 投影(<see cref="SqliteGlRepository"/>/<see cref="SqlServerGlRepository"/>)在同一交易內隨
/// target 一起 upsert 覆寫,與 target_gl_entry 恆一致。若併入本共用清除,TB 投影、科目配對／行事曆／
/// 授權清單匯入等與 GL 無關的寫入會把它連帶刪掉,使完整性 part(a) 在常見的「先 commit GL、後 commit TB」
/// 順序下變成全 null(控制總數核對形同沒跑)——2026-06-22 三顧稽核發現的失效範圍過廣,已收斂。
/// </summary>
internal static class RuleRunResultReset
{
    public static async Task ClearWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM result_rule_run;
            DELETE FROM result_inf_sampling_test_sample;
            DELETE FROM result_filter_run;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
