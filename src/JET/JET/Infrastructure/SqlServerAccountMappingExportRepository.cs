using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 科目配對全列匯出(SQL Server,鏡像 <see cref="SqliteAccountMappingExportRepository"/>;匯出底稿 sheet 15)。
/// 自 <c>target_account_mapping</c> 取每一列,not-in-tb 旗標以 <see cref="ValidationSql.CompletenessDiffCte"/>
/// 的 not_in_tb 為單一事實來源(GL 有 TB 無 = 1)。科目數有界,全載入、不分頁。排序鍵 account_code ASC。
/// not_in_tb 旗標 SQL Server 用 GetInt32(CASE WHEN 回 int)。
/// </summary>
public sealed class SqlServerAccountMappingExportRepository(SqlServerProjectDatabase database)
    : IAccountMappingExportRepository
{
    public async Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            ValidationSql.CompletenessDiffCte +
            """

            SELECT m.account_code,
                   m.account_name,
                   m.standardized_category,
                   CASE WHEN m.account_code IN (SELECT account_code FROM diff WHERE not_in_tb = 1)
                        THEN 1 ELSE 0 END AS not_in_tb
            FROM target_account_mapping m
            ORDER BY m.account_code;
            """;

        var rows = new List<AccountMappingExportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountMappingExportRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0));
        }

        return rows;
    }
}
