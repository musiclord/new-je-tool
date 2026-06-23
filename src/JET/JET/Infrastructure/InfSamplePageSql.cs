namespace JET.Infrastructure;

/// <summary>
/// infSamplePage 共用 SQL 片段(雙 provider 同一份;ANSI,無方言差異)。
/// </summary>
internal static class InfSamplePageSql
{
    /// <summary>
    /// 限定 result_inf_sampling_test_sample 的列只取最近一次 validate run 落地的樣本。
    /// 樣本表以 (run_id, entry_id) 跨 run 累積(validate.run 不清表),故須挑最新 run_id:
    /// run_id ∈ result_rule_run 內 run_kind='validate' 且 generated_utc 最大者。
    /// generated_utc 為 ISO-8601 round-trip 字串,字典序即時間序;ANSI 子查詢,SQLite/SQL Server 共通。
    ///
    /// 平手保護:外層用 <c>MAX(rr.run_id)</c> 而非裸 <c>rr.run_id</c>——若兩次 validate 落在完全相同的
    /// generated_utc(同 100ns tick,極罕見但測試/快速連點可能),內層會回多個 run_id,使
    /// 「= (純量子查詢)」在 SQL Server 報 "subquery returned more than 1 value"、SQLite 任取一筆。
    /// 改取 MAX(run_id) 後恆為單一確定值(run_id 為 32-hex,字典序確定),消除 fan-out;單 run 時 MAX 即原值。
    /// </summary>
    public const string LatestRunFilter =
        "s.run_id = (SELECT MAX(rr.run_id) FROM result_rule_run rr " +
        "            WHERE rr.run_kind = 'validate' " +
        "              AND rr.generated_utc = (SELECT MAX(generated_utc) FROM result_rule_run WHERE run_kind = 'validate'))";
}
