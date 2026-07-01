using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server 的每專案 schema 連線工廠與 schema 初始化(guide §13;對應 SQLite 的
/// <see cref="JetProjectDatabase"/>)。隔離模型:共用 instance、單一資料庫,每專案一個
/// <c>prj_xxx</c> schema(由 <see cref="SqlServerProjectSchema.For"/> 衍生),事實表建在該 schema 內、不帶 project_id 欄;
/// 跨專案反查表 <c>dbo.project_schema_map</c> 記錄 schema → 專案。
/// 連線字串(base)的 InitialCatalog 即為該單一資料庫;不再依專案切換庫名。
/// 刪除走 drop 該 schema 全表 → <c>DROP SCHEMA</c> → 刪 map 列。
/// 目標引擎為 SQL Server 2022(開發用 Developer、生產用 Standard/Enterprise,共用本實作、差異僅在連線字串);
/// SQL Server Express(含 LocalDB,EngineEdition=4)已淘汰——單庫模型撞其 10GB 上限,偵測到即以
/// sql_server_express_unsupported 擋下(見 EnsureSingleDatabaseAndMapAsync)。
/// schema 為 forward-only,新 schema 直接建到目前版本(SQL Server 無 legacy 專案,不需 migration 鏈)。
/// </summary>
public sealed class SqlServerProjectDatabase(SqlServerConnectionOptions options)
    : IProjectDatabaseInitializer, IProjectDatabaseDeleter
{
    // 目前 schema 版本(對齊 SQLite 的第 5 版形狀)。
    private const string SchemaVersion = "5";

    // Express 淘汰守衛：首次連上單庫時檢查引擎版別一次,非 Express 即快取放行(避免每次操作重查)。
    private bool _engineVerified;

    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(projectId);

        // 1) 確保單庫存在 + 反查表 dbo.project_schema_map 存在。
        await EnsureSingleDatabaseAndMapAsync(cancellationToken);

        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 2) schema 已存在則整段 no-op(冪等)。
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
            exists.Parameters.AddWithValue("@s", schema);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                return;
            }
        }

        // 3) CREATE SCHEMA → 在 schema 內建表(DDL {s}.) → insert map,三步同一交易。
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = tx;
            // schema 名由 For() 衍生(白名單格式),非使用者輸入;CREATE SCHEMA 識別字不可參數化。
            create.CommandText = $"EXEC('CREATE SCHEMA [{schema}]');";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var ddl = CreateCommand(connection, projectId, SchemaSql))
        {
            ddl.Transaction = tx;
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var map = connection.CreateCommand())
        {
            map.Transaction = tx;
            map.CommandText =
                "INSERT INTO dbo.project_schema_map (schema_name, project_id) VALUES (@s, @p);";
            map.Parameters.AddWithValue("@s", schema);
            map.Parameters.AddWithValue("@p", projectId);
            await map.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 啟動就緒化：確保單庫存在（不存在則以設定登入建立）與跨專案反查表就位，並驗證引擎非 Express。
    /// 冪等；供 composition 在 app 開啟時背景呼叫（「開啟即測試連線＋建庫」）。失敗由呼叫端當非致命處理
    /// （伺服器暫不可達、或登入尚未生效如剛切混合模式未重啟時，稍後建案會再試）。
    /// </summary>
    public Task EnsureDatabaseReadyAsync(CancellationToken cancellationToken) =>
        EnsureSingleDatabaseAndMapAsync(cancellationToken);

    /// <summary>
    /// 確保單庫存在(連 master、DB_ID 守冪等),並確保跨專案反查表 dbo.project_schema_map 存在。
    /// 單庫名取自 <see cref="SqlServerConnectionOptions.SingleDatabaseName"/>(顯式設定值、非使用者輸入、
    /// 不從連線字串 InitialCatalog 猜測)。空白即視為未設定 → 明確錯誤,杜絕 <c>CREATE DATABASE []</c>。
    /// </summary>
    private async Task EnsureSingleDatabaseAndMapAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SingleDatabaseName))
        {
            throw new JetActionException(
                "sql_server_not_configured",
                "SQL Server 單一資料庫名未設定(SqlServerConnectionOptions.SingleDatabaseName)。選用 sqlServer provider 的專案需要它。");
        }

        var dbName = options.SingleDatabaseName;
        await using (var master = new SqlConnection(BuildConnectionString("master")))
        {
            await master.OpenAsync(cancellationToken);

            // Express 已淘汰(EngineEdition=4,含 LocalDB):單庫模型下所有專案共用一個資料庫,會撞 Express 的
            // 10 GB 上限。偵測到即擋下、不建庫,回明確錯誤碼引導改用 SQL Server 2022。首次檢查後快取放行。
            if (!_engineVerified)
            {
                await using var caps = master.CreateCommand();
                caps.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);";
                const int expressEngineEdition = 4;
                if (Convert.ToInt32(await caps.ExecuteScalarAsync(cancellationToken)) == expressEngineEdition)
                {
                    throw new JetActionException(
                        "sql_server_express_unsupported",
                        "偵測到 SQL Server Express：單庫模型下所有專案共用一個資料庫，會撞 Express 的 10 GB 上限。" +
                        "Express 已淘汰，請改用 SQL Server 2022（Developer／Standard）。");
                }

                _engineVerified = true;
            }

            await using var create = master.CreateCommand();
            // dbName 來自設定、非使用者輸入;以括號內嵌(CREATE DATABASE 不接受參數化庫名)。
            create.CommandText = $"IF DB_ID(N'{dbName}') IS NULL EXEC('CREATE DATABASE [{dbName}]');";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var conn = CreateSingleDbConnection();
        await conn.OpenAsync(cancellationToken);
        await using var map = conn.CreateCommand();
        map.CommandText =
            """
            IF OBJECT_ID(N'dbo.project_schema_map','U') IS NULL
                CREATE TABLE dbo.project_schema_map (
                    schema_name NVARCHAR(64) PRIMARY KEY,
                    project_id  NVARCHAR(100) NOT NULL
                );
            """;
        await map.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 單一資料庫(<see cref="SqlServerConnectionOptions.SingleDatabaseName"/>)目前是否已存在。
    /// 連 master 以 <c>DB_ID</c> 判定,<b>不</b>直接開 <c>InitialCatalog=單庫</c> 的連線——後者在「庫尚未建立」
    /// 時會以「無法開啟登入所要求的資料庫」失敗。供 <see cref="DeleteAsync"/>/<see cref="DatabaseExistsAsync"/>
    /// 在切換伺服器或全新環境(單庫還沒建)時先行守門,避免誤把「庫不存在」當成錯誤。
    /// </summary>
    private async Task<bool> SingleDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        await using var master = new SqlConnection(BuildConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using var cmd = master.CreateCommand();
        cmd.CommandText = "SELECT DB_ID(@db);";
        cmd.Parameters.AddWithValue("@db", options.SingleDatabaseName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    /// <summary>
    /// 刪除該專案的 schema:drop 該 schema 內所有表 → <c>DROP SCHEMA</c>(SQL Server 要求 schema 清空才能 drop)→
    /// 刪 <c>dbo.project_schema_map</c> 對應列。SCHEMA_ID 守使前兩步冪等(schema 不存在則 no-op,但 map 列仍會清);
    /// schema 名不合法時直接 return(無從衍生則無從刪)。
    /// 單庫尚未建立(切換伺服器/全新環境)時整段 no-op:無庫即無 schema/map 可清,視為已刪除。
    /// 動態 drop 以 <c>QUOTENAME(@s)</c> 包裹識別字、走 <c>sp_executesql</c>,杜絕識別字注入。
    /// </summary>
    public async Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(projectId);
        if (!SqlServerProjectSchema.IsValid(schema)) return;

        // 單庫不存在 → 無 schema/map 可清。直接 return,避免開單庫連線時因庫不存在而登入失敗。
        if (!await SingleDatabaseExistsAsync(cancellationToken)) return;

        await using var conn = CreateConnection(projectId);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            IF SCHEMA_ID(@s) IS NOT NULL
            BEGIN
                DECLARE @drop NVARCHAR(MAX) = N'';
                SELECT @drop += 'DROP TABLE ' + QUOTENAME(@s) + '.' + QUOTENAME(t.name) + ';'
                FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(@s);
                EXEC sys.sp_executesql @drop;
                DECLARE @ds NVARCHAR(200) = N'DROP SCHEMA ' + QUOTENAME(@s) + N';';
                EXEC sys.sp_executesql @ds;
            END
            IF OBJECT_ID(N'dbo.project_schema_map','U') IS NOT NULL
                DELETE FROM dbo.project_schema_map WHERE schema_name = @s;
            """;
        cmd.Parameters.AddWithValue("@s", schema);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 回傳指向單庫的連線(未開啟)。InitialCatalog 已是單庫;projectId 僅用於衍生 schema、不影響庫選擇。
    /// schema-per-project 模型下,事實表存取由呼叫端透過 <see cref="CreateCommand"/> 的 {s} token 限定到專案 schema。
    /// </summary>
    public SqlConnection CreateConnection(string projectId)
    {
        return CreateSingleDbConnection();
    }

    /// <summary>
    /// 回傳指向單庫的連線(未開啟);InitialCatalog 顯式設為
    /// <see cref="SqlServerConnectionOptions.SingleDatabaseName"/>(不依賴 base 連線字串既有的 InitialCatalog)。
    /// </summary>
    private SqlConnection CreateSingleDbConnection()
    {
        return new SqlConnection(BuildConnectionString(options.SingleDatabaseName));
    }

    /// <summary>
    /// 方案 C 收斂點:把 SQL 中的哨兵 {s} 全部替換為該專案 schema 的方括號識別字。
    /// schema 名先過白名單,杜絕識別字注入(schema 名不可參數化)。
    /// 呼叫端自行 AddParameters 與設 Transaction。
    /// </summary>
    public SqlCommand CreateCommand(SqlConnection connection, string projectId, string sqlWithTokens)
    {
        var schema = SqlServerProjectSchema.For(projectId);
        if (!SqlServerProjectSchema.IsValid(schema))
        {
            throw new JET.Domain.JetActionException(
                "invalid_project_schema", $"專案 '{projectId}' 衍生出的 schema 名不合法。");
        }

        var command = connection.CreateCommand();
        command.CommandText = sqlWithTokens.Replace("{s}", $"[{schema}]");
        return command;
    }

    /// <summary>
    /// 該專案衍生的 schema 是否已存在(SCHEMA_ID)。供 project.create 攔截「不同案件名稱衍生後撞同 schema」。
    /// 簽名不變(IProjectDatabaseInitializer 介面);語意由「庫是否存在」改解讀為「schema 是否存在」。
    /// </summary>
    public async Task<bool> DatabaseExistsAsync(string projectId, CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(projectId);

        // 單庫尚未建立 → schema 必然不存在。先守門,避免在全新伺服器(單庫還沒建)上開單庫連線
        // 而登入失敗(此方法在 project.create 的碰撞攔截被呼叫、早於 EnsureCreatedAsync 建庫)。
        if (!await SingleDatabaseExistsAsync(cancellationToken)) return false;

        await using var connection = CreateSingleDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.AddWithValue("@s", schema);
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

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new JetActionException(
                "sql_server_not_configured",
                "SQL Server 單一資料庫名未設定(SqlServerConnectionOptions.SingleDatabaseName)。選用 sqlServer provider 的專案需要它。");
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
        IF OBJECT_ID(N'{s}.schema_info','U') IS NULL
            CREATE TABLE {s}.schema_info ([key] NVARCHAR(450) PRIMARY KEY, [value] NVARCHAR(MAX) NOT NULL);
        IF NOT EXISTS (SELECT 1 FROM {s}.schema_info WHERE [key] = 'schema_version')
            INSERT INTO {s}.schema_info ([key], [value]) VALUES ('schema_version', '5');

        IF OBJECT_ID(N'{s}.import_batch','U') IS NULL
            CREATE TABLE {s}.import_batch (
                batch_id         NVARCHAR(64) PRIMARY KEY,
                dataset_kind     NVARCHAR(20) NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
                source_file_path NVARCHAR(MAX) NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                imported_utc     NVARCHAR(40) NOT NULL,
                row_count        INT NOT NULL DEFAULT 0,
                columns_json     NVARCHAR(MAX) NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_import_batch_kind' AND object_id = OBJECT_ID(N'{s}.import_batch'))
            CREATE INDEX ix_import_batch_kind ON {s}.import_batch (dataset_kind, imported_utc);

        IF OBJECT_ID(N'{s}.import_batch_source','U') IS NULL
            CREATE TABLE {s}.import_batch_source (
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

        IF OBJECT_ID(N'{s}.staging_gl_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_gl_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.staging_tb_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_tb_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.config_field_mapping','U') IS NULL
            CREATE TABLE {s}.config_field_mapping (
                dataset_kind    NVARCHAR(20) PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
                mapping_json    NVARCHAR(MAX) NOT NULL,
                mode_name       NVARCHAR(40) NOT NULL,
                source_batch_id NVARCHAR(64) NOT NULL,
                committed_utc   NVARCHAR(40) NOT NULL
            );

        IF OBJECT_ID(N'{s}.staging_calendar_raw_day','U') IS NULL
            CREATE TABLE {s}.staging_calendar_raw_day (
                day_type NVARCHAR(10) NOT NULL CHECK (day_type IN ('holiday','makeup')),
                date     NVARCHAR(32) NOT NULL,
                day_name NVARCHAR(256) NULL,
                PRIMARY KEY (day_type, date)
            );
        IF COL_LENGTH('{s}.staging_calendar_raw_day','day_name') IS NULL
            ALTER TABLE {s}.staging_calendar_raw_day ADD day_name NVARCHAR(256) NULL;
        UPDATE {s}.schema_info SET [value] = '5' WHERE [key] = 'schema_version';

        IF OBJECT_ID(N'{s}.target_gl_entry','U') IS NULL
            CREATE TABLE {s}.target_gl_entry (
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
        IF COL_LENGTH('{s}.target_gl_entry','voucher_date') IS NULL
            ALTER TABLE {s}.target_gl_entry ADD voucher_date NVARCHAR(32) NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_doc' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_doc ON {s}.target_gl_entry (document_number);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_account' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_account ON {s}.target_gl_entry (account_code);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_post_date' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_post_date ON {s}.target_gl_entry (post_date);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_approval_date' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_approval_date ON {s}.target_gl_entry (approval_date);

        IF OBJECT_ID(N'{s}.target_tb_balance','U') IS NULL
            CREATE TABLE {s}.target_tb_balance (
                balance_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) NOT NULL,
                source_row_number     BIGINT NOT NULL,
                account_code          NVARCHAR(450) NULL,
                account_name          NVARCHAR(400) NULL,
                change_amount_scaled  BIGINT NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_tb_balance_account' AND object_id = OBJECT_ID(N'{s}.target_tb_balance'))
            CREATE INDEX ix_target_tb_balance_account ON {s}.target_tb_balance (account_code);

        IF OBJECT_ID(N'{s}.result_rule_run','U') IS NULL
            CREATE TABLE {s}.result_rule_run (
                run_id        NVARCHAR(64) PRIMARY KEY,
                run_kind      NVARCHAR(20) NOT NULL CHECK (run_kind IN ('validate','prescreen')),
                generated_utc NVARCHAR(40) NOT NULL,
                summary_json  NVARCHAR(MAX) NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_result_rule_run_kind' AND object_id = OBJECT_ID(N'{s}.result_rule_run'))
            CREATE INDEX ix_result_rule_run_kind ON {s}.result_rule_run (run_kind, generated_utc);

        IF OBJECT_ID(N'{s}.result_inf_sampling_test_sample','U') IS NULL
            CREATE TABLE {s}.result_inf_sampling_test_sample (
                run_id          NVARCHAR(64) NOT NULL,
                entry_id        BIGINT NOT NULL,
                document_number NVARCHAR(450) NULL,
                line_item       NVARCHAR(400) NULL,
                PRIMARY KEY (run_id, entry_id)
            );

        IF OBJECT_ID(N'{s}.config_filter_scenario','U') IS NULL
            CREATE TABLE {s}.config_filter_scenario (
                position        INT PRIMARY KEY,
                name            NVARCHAR(400) NOT NULL,
                rationale       NVARCHAR(MAX) NOT NULL,
                definition_json NVARCHAR(MAX) NOT NULL,
                saved_utc       NVARCHAR(40) NOT NULL
            );

        IF OBJECT_ID(N'{s}.staging_account_mapping_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_account_mapping_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.target_account_mapping','U') IS NULL
            CREATE TABLE {s}.target_account_mapping (
                mapping_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) NOT NULL,
                source_row_number     INT NOT NULL,
                account_code          NVARCHAR(450) NOT NULL,
                account_name          NVARCHAR(400) NULL,
                standardized_category NVARCHAR(40) NOT NULL
                    CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others'))
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_account_mapping_code' AND object_id = OBJECT_ID(N'{s}.target_account_mapping'))
            CREATE UNIQUE INDEX ix_target_account_mapping_code ON {s}.target_account_mapping (account_code);

        IF OBJECT_ID(N'{s}.staging_authorized_preparer_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_authorized_preparer_raw_row (
                batch_id          NVARCHAR(64) NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.target_authorized_preparer','U') IS NULL
            CREATE TABLE {s}.target_authorized_preparer (
                name NVARCHAR(450) NOT NULL PRIMARY KEY
            );

        IF OBJECT_ID(N'{s}.app_message_log','U') IS NULL
            CREATE TABLE {s}.app_message_log (
                message_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
                occurred_utc NVARCHAR(40) NOT NULL,
                level        NVARCHAR(10) NOT NULL CHECK (level IN ('info','warn')),
                text         NVARCHAR(MAX) NOT NULL
            );

        IF OBJECT_ID(N'{s}.gl_control_total','U') IS NULL
            CREATE TABLE {s}.gl_control_total (
                singleton        INT PRIMARY KEY CHECK (singleton = 1),
                source_row_count BIGINT NOT NULL,
                target_row_count BIGINT NOT NULL,
                target_debit_scaled  BIGINT NOT NULL,
                target_credit_scaled BIGINT NOT NULL
            );

        IF OBJECT_ID(N'{s}.result_filter_run', N'U') IS NULL
            CREATE TABLE {s}.result_filter_run (
                scenario_position INT NOT NULL,
                entry_id          BIGINT NOT NULL,
                PRIMARY KEY (scenario_position, entry_id)
            );
        -- D2 tag 矩陣即時 pivot 的輔助索引(對齊 SQLite idx_result_filter_run_entry):以 entry_id 為前導鍵,
        -- 讓 EXISTS / entry_id 鍵範圍存取走索引(PK 前導鍵為 scenario_position,不利這兩種存取)。
        -- sys.indexes 守欄、加法,不升 schema 版本。
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_result_filter_run_entry' AND object_id = OBJECT_ID(N'{s}.result_filter_run'))
            CREATE INDEX idx_result_filter_run_entry ON {s}.result_filter_run (entry_id, scenario_position);
        """;
}

/// <summary>
/// SQL Server 連線設定。BaseConnectionString 指向 instance(來源見 AppCompositionRoot:單一 appsettings.json 的
/// Sql:*、或環境變數 JET_SQLSERVER_CONNECTION 覆寫);為 null/空白時代表未設定,選用 sqlServer 的專案會在連線時得到明確錯誤。
/// SingleDatabaseName 是 schema-per-project 模型下「所有專案共用的那一個資料庫」名稱:顯式 value、
/// 不再從連線字串 InitialCatalog 隱性推斷(避免「連線字串沒帶 Database」時退化成 <c>CREATE DATABASE []</c>)。
/// 預設 JET_Test:僅供測試以 1 引數建構時落在隔離測試庫(jetapp 擁有);app 一律顯式帶入正式庫 JET(由 config)。
/// </summary>
public sealed record SqlServerConnectionOptions(string? BaseConnectionString, string SingleDatabaseName = "JET_Test");
