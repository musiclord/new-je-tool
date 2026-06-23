using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server 的每專案資料庫連線工廠與 schema 初始化(guide §13;對應 SQLite 的
/// <see cref="JetProjectDatabase"/>)。隔離模型:共用 instance、每專案一個資料庫
/// <c>JET_{projectId}</c>(忠實對應 SQLite 每專案一個 jet.db 檔),資料表不帶 project_id 欄。
/// 連線字串(base)指向 instance;每專案 DB 名以 InitialCatalog 切換。
/// SQL Server Express(開發)與 Standard/Enterprise(生產)共用本實作,差異僅在連線字串。
/// schema 為 forward-only,新庫直接建到目前版本(SQL Server 無 legacy 專案,不需 migration 鏈)。
/// </summary>
public sealed class SqlServerProjectDatabase(SqlServerConnectionOptions options)
    : IProjectDatabaseInitializer, IProjectDatabaseDeleter
{
    // 目前 schema 版本(對齊 SQLite 的第 5 版形狀)。
    private const string SchemaVersion = "5";

    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        var databaseName = DatabaseName(projectId);

        // 1) 連 master 建庫(CREATE DATABASE 不可在交易內;DB_ID 檢查使其冪等)。
        await using (var master = new SqlConnection(BuildConnectionString("master")))
        {
            await master.OpenAsync(cancellationToken);
            await using var create = master.CreateCommand();
            // databaseName 已正規化為 JET_ + 英數底線,安全內嵌為識別字(CREATE DATABASE 不接受參數化庫名)。
            create.CommandText =
                $"IF DB_ID(N'{databaseName}') IS NULL EXEC('CREATE DATABASE [{databaseName}]');";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) 連專案庫跑冪等 schema DDL。
        await using var connection = new SqlConnection(BuildConnectionString(databaseName));
        await connection.OpenAsync(cancellationToken);
        await using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        await schema.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// DROP 該專案的 <c>JET_{projectId}</c> 庫(先 SET SINGLE_USER WITH ROLLBACK IMMEDIATE 斷開所有連線)。
    /// DB_ID 守使其冪等(庫不存在則 no-op)。連線未設定時沿用 BuildConnectionString 的 sql_server_not_configured 例外。
    /// </summary>
    public async Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        var databaseName = DatabaseName(projectId);

        await using var master = new SqlConnection(BuildConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using var drop = master.CreateCommand();
        // databaseName 已正規化為 JET_ + 英數底線,安全內嵌為識別字(DROP DATABASE 不接受參數化庫名)。
        drop.CommandText =
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>回傳指向該專案 <c>JET_{projectId}</c> 庫的連線(未開啟)。</summary>
    public SqlConnection CreateConnection(string projectId)
    {
        return new SqlConnection(BuildConnectionString(DatabaseName(projectId)));
    }

    /// <summary>JET_{淨化後 projectId} 庫是否已存在(DB_ID)。供 project.create 攔截「不同案件名稱淨化後撞同庫名」。</summary>
    public async Task<bool> DatabaseExistsAsync(string projectId, CancellationToken cancellationToken)
    {
        var databaseName = DatabaseName(projectId);
        await using var master = new SqlConnection(BuildConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"SELECT CASE WHEN DB_ID(N'{databaseName}') IS NULL THEN 0 ELSE 1 END;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private string BuildConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            throw new JetActionException(
                "sql_server_not_configured",
                "未設定 SQL Server 連線(環境變數 JET_SQLSERVER_CONNECTION)。選用 sqlServer provider 的專案需要它。");
        }

        return new SqlConnectionStringBuilder(options.BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }

    internal static string DatabaseName(string projectId)
    {
        // projectId 來自 Guid "N"(32 hex)。防禦性只保留英數與底線,杜絕 CREATE DATABASE 識別字注入。
        var safe = new string(projectId.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (safe.Length == 0)
        {
            throw new JetActionException(
                "invalid_project_id",
                $"專案代號 '{projectId}' 無法對應為合法的 SQL Server 資料庫名。");
        }

        return $"JET_{safe}";
    }

    /// <summary>
    /// SQL Server 版 schema(對齊 SQLite SchemaSql 的第 3 版形狀,型別映射見 plan)。
    /// 冪等:每張表以 OBJECT_ID 守、每個索引以 sys.indexes 守。保留字 key/value 以方括號包。
    /// </summary>
    private const string SchemaSql =
        """
        IF OBJECT_ID(N'dbo.schema_info','U') IS NULL
            CREATE TABLE dbo.schema_info ([key] NVARCHAR(450) PRIMARY KEY, [value] NVARCHAR(MAX) NOT NULL);
        IF NOT EXISTS (SELECT 1 FROM dbo.schema_info WHERE [key] = 'schema_version')
            INSERT INTO dbo.schema_info ([key], [value]) VALUES ('schema_version', '5');

        IF OBJECT_ID(N'dbo.import_batch','U') IS NULL
            CREATE TABLE dbo.import_batch (
                batch_id         NVARCHAR(64) PRIMARY KEY,
                dataset_kind     NVARCHAR(20) NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
                source_file_path NVARCHAR(MAX) NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                imported_utc     NVARCHAR(40) NOT NULL,
                row_count        INT NOT NULL DEFAULT 0,
                columns_json     NVARCHAR(MAX) NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_import_batch_kind' AND object_id = OBJECT_ID(N'dbo.import_batch'))
            CREATE INDEX ix_import_batch_kind ON dbo.import_batch (dataset_kind, imported_utc);

        IF OBJECT_ID(N'dbo.import_batch_source','U') IS NULL
            CREATE TABLE dbo.import_batch_source (
                batch_id         NVARCHAR(64) NOT NULL,
                source_no        INT NOT NULL,
                source_file_path NVARCHAR(MAX) NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                sheet_name       NVARCHAR(400) NULL,
                encoding         NVARCHAR(400) NULL,
                delimiter        NVARCHAR(400) NULL,
                row_count        INT NOT NULL DEFAULT 0,
                imported_utc     NVARCHAR(40) NOT NULL,
                PRIMARY KEY (batch_id, source_no)
            );

        IF OBJECT_ID(N'dbo.staging_gl_raw_row','U') IS NULL
            CREATE TABLE dbo.staging_gl_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'dbo.staging_tb_raw_row','U') IS NULL
            CREATE TABLE dbo.staging_tb_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'dbo.config_field_mapping','U') IS NULL
            CREATE TABLE dbo.config_field_mapping (
                dataset_kind    NVARCHAR(20) PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
                mapping_json    NVARCHAR(MAX) NOT NULL,
                mode_name       NVARCHAR(40) NOT NULL,
                source_batch_id NVARCHAR(64) NOT NULL,
                committed_utc   NVARCHAR(40) NOT NULL
            );

        IF OBJECT_ID(N'dbo.staging_calendar_raw_day','U') IS NULL
            CREATE TABLE dbo.staging_calendar_raw_day (
                day_type NVARCHAR(10) NOT NULL CHECK (day_type IN ('holiday','makeup')),
                date     NVARCHAR(32) NOT NULL,
                day_name NVARCHAR(256) NULL,
                PRIMARY KEY (day_type, date)
            );
        IF COL_LENGTH('dbo.staging_calendar_raw_day','day_name') IS NULL
            ALTER TABLE dbo.staging_calendar_raw_day ADD day_name NVARCHAR(256) NULL;
        UPDATE dbo.schema_info SET [value] = '5' WHERE [key] = 'schema_version';

        IF OBJECT_ID(N'dbo.target_gl_entry','U') IS NULL
            CREATE TABLE dbo.target_gl_entry (
                entry_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) NOT NULL,
                source_row_number     BIGINT NOT NULL,
                document_number       NVARCHAR(450) NULL,
                line_item             NVARCHAR(400) NULL,
                post_date             NVARCHAR(32) NULL,
                approval_date         NVARCHAR(32) NULL,
                voucher_date          NVARCHAR(32) NULL,
                account_code          NVARCHAR(450) NULL,
                account_name          NVARCHAR(400) NULL,
                document_description  NVARCHAR(MAX) NULL,
                source_module         NVARCHAR(400) NULL,
                created_by            NVARCHAR(400) NULL,
                approved_by           NVARCHAR(400) NULL,
                is_manual             INT NULL,
                amount_scaled         BIGINT NOT NULL,
                debit_amount_scaled   BIGINT NOT NULL,
                credit_amount_scaled  BIGINT NOT NULL,
                dr_cr                 NVARCHAR(10) NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT'))
            );
        IF COL_LENGTH('dbo.target_gl_entry','voucher_date') IS NULL
            ALTER TABLE dbo.target_gl_entry ADD voucher_date NVARCHAR(32) NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_doc' AND object_id = OBJECT_ID(N'dbo.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_doc ON dbo.target_gl_entry (document_number);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_account' AND object_id = OBJECT_ID(N'dbo.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_account ON dbo.target_gl_entry (account_code);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_post_date' AND object_id = OBJECT_ID(N'dbo.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_post_date ON dbo.target_gl_entry (post_date);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_approval_date' AND object_id = OBJECT_ID(N'dbo.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_approval_date ON dbo.target_gl_entry (approval_date);

        IF OBJECT_ID(N'dbo.target_tb_balance','U') IS NULL
            CREATE TABLE dbo.target_tb_balance (
                balance_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) NOT NULL,
                source_row_number     BIGINT NOT NULL,
                account_code          NVARCHAR(450) NULL,
                account_name          NVARCHAR(400) NULL,
                change_amount_scaled  BIGINT NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_tb_balance_account' AND object_id = OBJECT_ID(N'dbo.target_tb_balance'))
            CREATE INDEX ix_target_tb_balance_account ON dbo.target_tb_balance (account_code);

        IF OBJECT_ID(N'dbo.result_rule_run','U') IS NULL
            CREATE TABLE dbo.result_rule_run (
                run_id        NVARCHAR(64) PRIMARY KEY,
                run_kind      NVARCHAR(20) NOT NULL CHECK (run_kind IN ('validate','prescreen')),
                generated_utc NVARCHAR(40) NOT NULL,
                summary_json  NVARCHAR(MAX) NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_result_rule_run_kind' AND object_id = OBJECT_ID(N'dbo.result_rule_run'))
            CREATE INDEX ix_result_rule_run_kind ON dbo.result_rule_run (run_kind, generated_utc);

        IF OBJECT_ID(N'dbo.result_inf_sampling_test_sample','U') IS NULL
            CREATE TABLE dbo.result_inf_sampling_test_sample (
                run_id          NVARCHAR(64) NOT NULL,
                entry_id        BIGINT NOT NULL,
                document_number NVARCHAR(450) NULL,
                line_item       NVARCHAR(400) NULL,
                PRIMARY KEY (run_id, entry_id)
            );

        IF OBJECT_ID(N'dbo.config_filter_scenario','U') IS NULL
            CREATE TABLE dbo.config_filter_scenario (
                position        INT PRIMARY KEY,
                name            NVARCHAR(400) NOT NULL,
                rationale       NVARCHAR(MAX) NOT NULL,
                definition_json NVARCHAR(MAX) NOT NULL,
                saved_utc       NVARCHAR(40) NOT NULL
            );

        IF OBJECT_ID(N'dbo.staging_account_mapping_raw_row','U') IS NULL
            CREATE TABLE dbo.staging_account_mapping_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'dbo.target_account_mapping','U') IS NULL
            CREATE TABLE dbo.target_account_mapping (
                mapping_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) NOT NULL,
                source_row_number     INT NOT NULL,
                account_code          NVARCHAR(450) NOT NULL,
                account_name          NVARCHAR(400) NULL,
                standardized_category NVARCHAR(40) NOT NULL
                    CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others'))
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_account_mapping_code' AND object_id = OBJECT_ID(N'dbo.target_account_mapping'))
            CREATE UNIQUE INDEX ix_target_account_mapping_code ON dbo.target_account_mapping (account_code);

        IF OBJECT_ID(N'dbo.staging_authorized_preparer_raw_row','U') IS NULL
            CREATE TABLE dbo.staging_authorized_preparer_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'dbo.target_authorized_preparer','U') IS NULL
            CREATE TABLE dbo.target_authorized_preparer (
                name NVARCHAR(450) NOT NULL PRIMARY KEY
            );

        IF OBJECT_ID(N'dbo.app_message_log','U') IS NULL
            CREATE TABLE dbo.app_message_log (
                message_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
                occurred_utc NVARCHAR(40) NOT NULL,
                level        NVARCHAR(10) NOT NULL CHECK (level IN ('info','warn')),
                text         NVARCHAR(MAX) NOT NULL
            );

        IF OBJECT_ID(N'dbo.gl_control_total','U') IS NULL
            CREATE TABLE dbo.gl_control_total (
                singleton        INT PRIMARY KEY CHECK (singleton = 1),
                source_row_count BIGINT NOT NULL,
                target_row_count BIGINT NOT NULL,
                target_debit_scaled  BIGINT NOT NULL,
                target_credit_scaled BIGINT NOT NULL
            );

        IF OBJECT_ID(N'dbo.result_filter_run', N'U') IS NULL
            CREATE TABLE dbo.result_filter_run (
                scenario_position INT NOT NULL,
                entry_id          BIGINT NOT NULL,
                PRIMARY KEY (scenario_position, entry_id)
            );
        -- D2 tag 矩陣即時 pivot 的輔助索引(對齊 SQLite idx_result_filter_run_entry):以 entry_id 為前導鍵,
        -- 讓 EXISTS / entry_id 鍵範圍存取走索引(PK 前導鍵為 scenario_position,不利這兩種存取)。
        -- sys.indexes 守欄、加法,不升 schema 版本。
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_result_filter_run_entry' AND object_id = OBJECT_ID(N'dbo.result_filter_run'))
            CREATE INDEX idx_result_filter_run_entry ON dbo.result_filter_run (entry_id, scenario_position);
        """;
}

/// <summary>
/// SQL Server 連線設定。BaseConnectionString 指向 instance(InitialCatalog 由 provider
/// 依專案覆寫);來源為環境變數 JET_SQLSERVER_CONNECTION(見 AppCompositionRoot)。
/// 為 null/空白時代表未設定,選用 sqlServer 的專案會在連線時得到明確錯誤。
/// </summary>
public sealed record SqlServerConnectionOptions(string? BaseConnectionString);
