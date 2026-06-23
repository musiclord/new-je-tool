using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SqlServerDataPreviewRepository 的 SQL Server 預覽測試。oracle：小型固定資料集手算，鎖定公開回傳欄位、列值、總筆數。
/// </summary>
public sealed class SqlServerDataPreviewRepositoryTests
{
    [SqlServerFact]
    public async Task GetPreviewAsync_GlStaging_ReturnsLatestBatchColumnsRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlStaging,
            moneyScale: 10_000,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(["doc", "amount", "missing"], result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["JV-001", "100", null], row);
        Assert.Null(result.Stats);
    }


    [SqlServerFact]
    public async Task GetPreviewAsync_TbStaging_ReturnsLatestBatchColumnsRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.TbStaging,
            moneyScale: 10_000,
            limit: 2,
            CancellationToken.None);

        Assert.Equal(["account", "change"], result.Columns);
        Assert.Equal(1, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["1101", "500"], row);
        Assert.Null(result.Stats);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_GlEntries_ReturnsScaledRowsAndStats()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlEntries,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(
            ["documentNumber", "lineItem", "postDate", "accountCode", "accountName", "documentDescription", "amount", "drCr"],
            result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["JV-100", "1", "2024-01-01", "1101", "現金", "借方", "1234.56", "DEBIT"], row);
        Assert.NotNull(result.Stats);
        Assert.Equal(25m, result.Stats.AmountAbsMin);
        Assert.Equal(1234.56m, result.Stats.AmountAbsMax);
        Assert.Equal("2024-01-01", result.Stats.PostDateMin);
        Assert.Equal("2024-01-31", result.Stats.PostDateMax);
        Assert.Equal(2, result.Stats.VoucherCount);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_TbBalances_ReturnsScaledRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.TbBalances,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(["accountCode", "accountName", "changeAmount"], result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["1101", "現金", "-34.5"], row);
        Assert.Null(result.Stats);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_AccountMappings_ReturnsOrderedRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.AccountMappings,
            moneyScale: 10_000,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(["accountCode", "accountName", "standardizedCategory"], result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["1101", "現金", "Cash"], row);
        Assert.Null(result.Stats);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_UnknownDataset_ThrowsArgumentOutOfRangeException()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.GetPreviewAsync(
            project.ProjectId,
            (DataPreviewDataset)999,
            moneyScale: 10_000,
            limit: 1,
            CancellationToken.None));

        Assert.Equal("dataset", exception.ParamName);
        Assert.Equal((DataPreviewDataset)999, exception.ActualValue);
    }

    private static async Task SeedPreviewDataAsync(SqlServerProjectDatabase database, string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES ('gl-old', 'gl', 'C:\old.csv', 'old.csv', '2024-01-01T00:00:00Z', 1, N'["old"]');
            INSERT INTO import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES ('gl-new', 'gl', 'C:\new.csv', 'new.csv', '2024-01-02T00:00:00Z', 2, N'["doc","amount","missing"]');
            INSERT INTO staging_gl_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES ('gl-new', 2, 1, 2, N'{"doc":"JV-001","amount":"100"}');
            INSERT INTO staging_gl_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES ('gl-new', 3, 1, 3, N'{"doc":"JV-002","amount":"200"}');

            INSERT INTO import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES ('tb-new', 'tb', 'C:\tb.csv', 'tb.csv', '2024-01-03T00:00:00Z', 1, N'["account","change"]');
            INSERT INTO staging_tb_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES ('tb-new', 2, 1, 2, N'{"account":"1101","change":"500"}');

            INSERT INTO target_gl_entry (
                batch_id, source_row_number, document_number, line_item, post_date, account_code, account_name,
                document_description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES
                ('gl-target', 2, 'JV-100', '1', '2024-01-01', '1101', N'現金', N'借方', 123456, 123456, 0, 'DEBIT'),
                ('gl-target', 3, 'JV-200', '1', '2024-01-31', '4101', N'銷貨', N'貸方', -2500, 0, 2500, 'CREDIT');

            INSERT INTO target_tb_balance (batch_id, source_row_number, account_code, account_name, change_amount_scaled)
            VALUES
                ('tb-target', 2, '1101', N'現金', -3450),
                ('tb-target', 3, '4101', N'銷貨收入', 7650);

            INSERT INTO target_account_mapping (batch_id, source_row_number, account_code, account_name, standardized_category)
            VALUES
                ('mapping-target', 2, '1101', N'現金', 'Cash'),
                ('mapping-target', 3, '4101', N'銷貨收入', 'Revenue');
            """);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}
