using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣傳票層分頁的共用兩段查詢讀取器(provider 中立)。兩 provider 的唯一差異是
/// LimitClause(SQLite LIMIT ↔ SQL Server OFFSET/FETCH),由呼叫端帶入 <see cref="ISqlDialect"/>;
/// 其餘 SQL 純 ANSI,故讀取邏輯共用一處(Linus 好品味:消除兩 repo 的逐字重複)。
///
/// 查詢 1(命中傳票 keyset 頁):GROUP BY document_number、EXISTS(result_filter_run) 篩命中、
/// 排除 NULL document_number、聚合 MIN(post_date)/MIN(created_by)/COALESCE(SUM(debit_amount_scaled),0)、
/// 游標展開布林式 `document_number > @cursor`、ORDER BY document_number、LimitClause。reader 末欄取
/// document_number 供編游標。NextCursor = 本頁筆數 == pageSize ? Encode(末鍵) : null。
///
/// 查詢 2(本頁傳票命中位置,鍵範圍 (@lo, @hi]):DISTINCT (document_number, scenario_position)、
/// 排除 NULL document_number、`document_number <= @hi`(@hi=本頁末鍵),非首頁再加 `document_number > @lo`
/// (@lo=本頁游標)。鍵範圍與查詢 1 完全一致,故每傳票位置不漏不溢。空頁→不跑查詢 2,回空 dict。
/// </summary>
internal static class TagMatrixVoucherPageReader
{
    private const string PageSqlHead =
        "SELECT g.document_number, MIN(g.post_date), MIN(g.created_by), " +
        "       COALESCE(SUM(g.debit_amount_scaled), 0) " +
        "FROM target_gl_entry g " +
        "WHERE g.document_number IS NOT NULL " +
        "  AND EXISTS (SELECT 1 FROM result_filter_run r WHERE r.entry_id = g.entry_id) ";

    private const string PageSqlTail =
        "GROUP BY g.document_number " +
        "ORDER BY g.document_number ";

    public static async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> ReadAsync(
        DbConnection connection, ISqlDialect dialect, PageRequest request, CancellationToken cancellationToken)
    {
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);

        // 查詢 1:本頁命中傳票 + 聚合。
        var rows = new List<VoucherTagRow>();
        string? lastDoc = null;
        await using (var command = connection.CreateCommand())
        {
            var keyset = hasCursor ? "AND g.document_number > @cursor " : string.Empty;
            command.CommandText =
                PageSqlHead + keyset + PageSqlTail + dialect.LimitClause("@pageSize") + ";";
            if (hasCursor)
            {
                command.Parameters.Add(Param(command, "@cursor", cursorKey));
            }

            command.Parameters.Add(Param(command, "@pageSize", request.ClampedPageSize));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var doc = reader.GetString(0);
                rows.Add(new VoucherTagRow(
                    doc,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3)));
                lastDoc = doc; // 末欄(rows 已升冪,末列即本頁末鍵)
            }
        }

        var next = rows.Count == request.ClampedPageSize && lastDoc is not null
            ? PageCursor.Encode(lastDoc)
            : null;

        // 空頁:無傳票 → 無位置。不跑查詢 2(@hi 無意義)。
        if (lastDoc is null)
        {
            return (
                new PageResult<VoucherTagRow>(rows, next),
                new Dictionary<string, IReadOnlyList<int>>());
        }

        // 查詢 2:本頁傳票命中位置(鍵範圍 (@lo, @hi],與查詢 1 同範圍)。
        var positions = new Dictionary<string, List<int>>();
        await using (var command = connection.CreateCommand())
        {
            var lowBound = hasCursor ? "AND g.document_number > @lo " : string.Empty;
            command.CommandText =
                "SELECT DISTINCT g.document_number, r.scenario_position " +
                "FROM result_filter_run r " +
                "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
                "WHERE g.document_number IS NOT NULL " +
                lowBound +
                "AND g.document_number <= @hi " +
                "ORDER BY g.document_number, r.scenario_position;";
            if (hasCursor)
            {
                command.Parameters.Add(Param(command, "@lo", cursorKey));
            }

            command.Parameters.Add(Param(command, "@hi", lastDoc));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var doc = reader.GetString(0);
                var pos = reader.GetInt32(1);
                if (!positions.TryGetValue(doc, out var list))
                {
                    list = [];
                    positions[doc] = list;
                }

                list.Add(pos); // ORDER BY + DISTINCT → 已有序去重
            }
        }

        var positionsByDoc = positions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);

        return (new PageResult<VoucherTagRow>(rows, next), positionsByDoc);
    }

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }
}
