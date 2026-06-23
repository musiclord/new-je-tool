using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣行層分頁的共用兩段查詢讀取器(provider 中立)。兩 provider 的唯一差異是
/// LimitClause(SQLite LIMIT ↔ SQL Server OFFSET/FETCH),由呼叫端帶入 <see cref="ISqlDialect"/>;
/// 其餘 SQL 純 ANSI,故讀取邏輯共用一處(鏡射 <see cref="TagMatrixVoucherPageReader"/> 的範式,
/// 鍵改 entry_id、列集改命中傳票之所有行)。
///
/// 查詢 1(命中傳票之所有行 keyset 頁):FROM target_gl_entry g WHERE document_number IN(命中傳票集
/// = 命中行所屬傳票 DISTINCT document_number、排除 NULL)、游標展開布林式 `entry_id > @cursor`、
/// ORDER BY entry_id、LimitClause。列集**含該傳票內未命中任何情境的行**(只要與命中行同傳票即列出)。
/// reader 末欄取 entry_id 供編游標與對齊位置。NextCursor = 本頁筆數 == pageSize ? Encode(末鍵) : null。
///
/// 查詢 2(本頁各行命中位置,鍵範圍 (@lo, @hi]):FROM result_filter_run WHERE `entry_id <= @hi`
/// (@hi=本頁末 entry_id),非首頁再加 `entry_id > @lo`(@lo=本頁游標)。鍵範圍與查詢 1 完全一致,
/// 故每行的命中位置不漏不溢。空頁→不跑查詢 2,回空 dict、空 EntryIds。非命中行不在 dict(handler 補 [])。
/// </summary>
internal static class TagMatrixRowPageReader
{
    private const string PageSqlHead =
        "SELECT g.document_number, g.line_item, g.post_date, g.approval_date, g.created_by, g.approved_by, " +
        "       g.account_code, g.account_name, g.amount_scaled, g.document_description, g.entry_id " +
        "FROM target_gl_entry g " +
        "WHERE g.document_number IN ( " +
        "    SELECT DISTINCT g2.document_number FROM result_filter_run r " +
        "    JOIN target_gl_entry g2 ON g2.entry_id = r.entry_id " +
        "    WHERE g2.document_number IS NOT NULL) ";

    private const string PageSqlTail =
        "ORDER BY g.entry_id ";

    public static async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> ReadAsync(
        DbConnection connection, ISqlDialect dialect, PageRequest request, CancellationToken cancellationToken)
    {
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        long? cursorEntryId = hasCursor ? long.Parse(cursorKey) : null;

        // 查詢 1:本頁命中傳票之所有行(含非命中行)。
        var rows = new List<RowTagRow>();
        var entryIds = new List<long>();
        long? lastEntryId = null;
        await using (var command = connection.CreateCommand())
        {
            var keyset = cursorEntryId is not null ? "AND g.entry_id > @cursor " : string.Empty;
            command.CommandText =
                PageSqlHead + keyset + PageSqlTail + dialect.LimitClause("@pageSize") + ";";
            if (cursorEntryId is not null)
            {
                command.Parameters.Add(Param(command, "@cursor", cursorEntryId.Value));
            }

            command.Parameters.Add(Param(command, "@pageSize", request.ClampedPageSize));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new RowTagRow(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
                var entryId = reader.GetInt64(10);
                entryIds.Add(entryId);
                lastEntryId = entryId; // 末欄(rows 已升冪,末列即本頁末鍵)
            }
        }

        var next = rows.Count == request.ClampedPageSize && lastEntryId is not null
            ? PageCursor.Encode(lastEntryId.Value.ToString())
            : null;

        // 空頁:無行 → 無位置。不跑查詢 2(@hi 無意義)。
        if (lastEntryId is null)
        {
            return (
                new PageResult<RowTagRow>(rows, next),
                entryIds,
                new Dictionary<long, IReadOnlyList<int>>());
        }

        // 查詢 2:本頁各行命中位置(鍵範圍 (@lo, @hi],與查詢 1 同範圍)。
        var positions = new Dictionary<long, List<int>>();
        await using (var command = connection.CreateCommand())
        {
            var lowBound = cursorEntryId is not null ? "AND entry_id > @lo " : string.Empty;
            command.CommandText =
                "SELECT entry_id, scenario_position FROM result_filter_run " +
                "WHERE entry_id <= @hi " +
                lowBound +
                "ORDER BY entry_id, scenario_position;";
            if (cursorEntryId is not null)
            {
                command.Parameters.Add(Param(command, "@lo", cursorEntryId.Value));
            }

            command.Parameters.Add(Param(command, "@hi", lastEntryId.Value));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entryId = reader.GetInt64(0);
                var pos = reader.GetInt32(1);
                if (!positions.TryGetValue(entryId, out var list))
                {
                    list = [];
                    positions[entryId] = list;
                }

                list.Add(pos); // ORDER BY → 已有序;同 (entry_id,position) 在 result_filter_run 唯一
            }
        }

        var positionsByEntry = positions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);

        return (new PageResult<RowTagRow>(rows, next), entryIds, positionsByEntry);
    }

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }
}
