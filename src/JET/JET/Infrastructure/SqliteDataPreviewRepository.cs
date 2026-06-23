using System.Globalization;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 使用者資料預覽（manifest query.dataPreview）：有界唯讀查詢，絕不回完整母體。
/// 統計（COUNT / MIN / MAX）一律 set-based SQL；金額顯示值由 C# decimal 自 scaled 換算（精確）。
/// </summary>
public sealed class SqliteDataPreviewRepository(JetProjectDatabase database) : IDataPreviewRepository
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>glEntries 的固定欄位集（與 filter.preview 的 previewRows 同欄位，manifest 細節段）。</summary>
    private static readonly string[] GlEntryColumns =
        ["documentNumber", "lineItem", "postDate", "accountCode", "accountName", "documentDescription", "amount", "drCr"];

    private static readonly string[] TbBalanceColumns = ["accountCode", "accountName", "changeAmount"];

    private static readonly string[] AccountMappingColumns = ["accountCode", "accountName", "standardizedCategory"];

    private static readonly string[] AuthorizedPreparerColumns = ["preparerName"];

    public async Task<DataPreviewResult> GetPreviewAsync(
        string projectId,
        DataPreviewDataset dataset,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return dataset switch
        {
            DataPreviewDataset.GlStaging => await StagingPreviewAsync(connection, "gl", "staging_gl_raw_row", limit, cancellationToken),
            DataPreviewDataset.TbStaging => await StagingPreviewAsync(connection, "tb", "staging_tb_raw_row", limit, cancellationToken),
            DataPreviewDataset.GlEntries => await GlEntriesPreviewAsync(connection, moneyScale, limit, cancellationToken),
            DataPreviewDataset.TbBalances => await TbBalancesPreviewAsync(connection, moneyScale, limit, cancellationToken),
            DataPreviewDataset.AccountMappings => await AccountMappingsPreviewAsync(connection, limit, cancellationToken),
            DataPreviewDataset.AuthorizedPreparers => await AuthorizedPreparersPreviewAsync(connection, limit, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null)
        };
    }

    /// <summary>已匯入的科目配對表（依科目代號排序；無統計）。</summary>
    private static async Task<DataPreviewResult> AccountMappingsPreviewAsync(
        SqliteConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM target_account_mapping;";
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT account_code, account_name, standardized_category
            FROM target_account_mapping
            ORDER BY account_code
            LIMIT {limit};
            """;

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
            [
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2)
            ]);
        }

        return new DataPreviewResult(AccountMappingColumns, rows, totalCount, null);
    }

    /// <summary>已匯入的授權編製人員清單（單欄姓名，依姓名排序；無統計）。</summary>
    private static async Task<DataPreviewResult> AuthorizedPreparersPreviewAsync(
        SqliteConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM target_authorized_preparer;";
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT name
            FROM target_authorized_preparer
            ORDER BY name
            LIMIT {limit};
            """;

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add([reader.GetString(0)]);
        }

        return new DataPreviewResult(AuthorizedPreparerColumns, rows, totalCount, null);
    }

    /// <summary>
    /// 來源原貌：columns = 該 dataset 最新批次的正規化欄名（與欄位配對下拉一字不差），
    /// rows = staging row_json 依批次排序鍵取前 N 列（cell 缺漏 → null）。
    /// </summary>
    private static async Task<DataPreviewResult> StagingPreviewAsync(
        SqliteConnection connection,
        string kindName,
        string stagingTable,
        int limit,
        CancellationToken cancellationToken)
    {
        string? batchId = null;
        List<string> columns = [];

        await using (var findBatch = connection.CreateCommand())
        {
            findBatch.CommandText =
                """
                SELECT batch_id, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """;
            findBatch.Parameters.AddWithValue("@kind", kindName);

            await using var reader = await findBatch.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                batchId = reader.GetString(0);
                columns = JsonSerializer.Deserialize<List<string>>(reader.GetString(1), JsonOptions) ?? [];
            }
        }

        if (batchId is null)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM {stagingTable} WHERE batch_id = @batchId;";
            count.Parameters.AddWithValue("@batchId", batchId);
            totalCount = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                $"""
                SELECT row_json
                FROM {stagingTable}
                WHERE batch_id = @batchId
                ORDER BY row_number
                LIMIT @limit;
                """;
            select.Parameters.AddWithValue("@batchId", batchId);
            select.Parameters.AddWithValue("@limit", limit);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0), JsonOptions)
                    ?? [];
                rows.Add(columns.Select(c => values.TryGetValue(c, out var v) ? v : null).ToList());
            }
        }

        return new DataPreviewResult(columns, rows, totalCount, null);
    }

    private static async Task<DataPreviewResult> GlEntriesPreviewAsync(
        SqliteConnection connection,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken)
    {
        long totalCount;
        GlEntriesPreviewStats? stats = null;

        // 總數與概況統計一次取得（set-based；空表時 MIN/MAX 為 NULL → stats 維持 null）
        await using (var statsQuery = connection.CreateCommand())
        {
            statsQuery.CommandText =
                """
                SELECT COUNT(*),
                       COUNT(DISTINCT document_number),
                       MIN(ABS(amount_scaled)),
                       MAX(ABS(amount_scaled)),
                       MIN(post_date),
                       MAX(post_date)
                FROM target_gl_entry;
                """;

            await using var reader = await statsQuery.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalCount = reader.GetInt64(0);

            if (totalCount > 0)
            {
                stats = new GlEntriesPreviewStats(
                    ToDisplayAmount(reader.GetInt64(2), moneyScale),
                    ToDisplayAmount(reader.GetInt64(3), moneyScale),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(1));
            }
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT document_number, line_item, post_date, account_code, account_name,
                       document_description, amount_scaled, dr_cr
                FROM target_gl_entry
                ORDER BY entry_id
                LIMIT @limit;
                """;
            select.Parameters.AddWithValue("@limit", limit);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(
                [
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    ToDisplayAmount(reader.GetInt64(6), moneyScale).ToString(CultureInfo.InvariantCulture),
                    reader.GetString(7)
                ]);
            }
        }

        return new DataPreviewResult(GlEntryColumns, rows, totalCount, stats);
    }

    private static async Task<DataPreviewResult> TbBalancesPreviewAsync(
        SqliteConnection connection,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken)
    {
        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM target_tb_balance;";
            totalCount = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT account_code, account_name, change_amount_scaled
                FROM target_tb_balance
                ORDER BY balance_id
                LIMIT @limit;
                """;
            select.Parameters.AddWithValue("@limit", limit);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(
                [
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    ToDisplayAmount(reader.GetInt64(2), moneyScale).ToString(CultureInfo.InvariantCulture)
                ]);
            }
        }

        return new DataPreviewResult(TbBalanceColumns, rows, totalCount, null);
    }

    private static decimal ToDisplayAmount(long scaled, int moneyScale)
    {
        return (decimal)scaled / moneyScale;
    }
}
