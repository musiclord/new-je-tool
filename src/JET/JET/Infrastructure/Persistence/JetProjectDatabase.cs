using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 每專案 jet.db 的連線工廠與 schema 初始化。
/// 每專案一個 DB 檔，資料表不帶 project_id 欄（檔案即 scope）。
/// </summary>
public sealed class JetProjectDatabase(JetProjectFolder folder) : IProjectDatabaseInitializer, IProjectDatabaseDeleter
{
    /// <summary>
    /// schema 第 3 版（規則命名更名 + 科目配對，2026-06-11；多來源批次見 guide §3.1.4）。
    /// 新資料庫直接以此建立；第 1/2 版資料庫由 <see cref="EnsureCreatedAsync"/> 的遷移段逐版升級。
    /// staging 的 row_number = 批次內單調遞增排序鍵（INF 抽樣基礎），
    /// source_row_number = 來源檔內實際列號（錯誤定位），source_no 對應 import_batch_source。
    /// </summary>
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS schema_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '5')
            ON CONFLICT(key) DO NOTHING;

        CREATE TABLE IF NOT EXISTS import_batch (
            batch_id         TEXT PRIMARY KEY,
            dataset_kind     TEXT NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            imported_utc     TEXT NOT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            columns_json     TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_import_batch_kind
            ON import_batch (dataset_kind, imported_utc);

        CREATE TABLE IF NOT EXISTS import_batch_source (
            batch_id         TEXT NOT NULL,
            source_no        INTEGER NOT NULL,
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            sheet_name       TEXT NULL,
            encoding         TEXT NULL,
            delimiter        TEXT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            imported_utc     TEXT NOT NULL,
            PRIMARY KEY (batch_id, source_no)
        );

        CREATE TABLE IF NOT EXISTS staging_gl_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS staging_tb_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS config_field_mapping (
            dataset_kind    TEXT PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
            mapping_json    TEXT NOT NULL,
            mode_name       TEXT NOT NULL,
            source_batch_id TEXT NOT NULL,
            committed_utc   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS staging_calendar_raw_day (
            day_type TEXT NOT NULL CHECK (day_type IN ('holiday','makeup')),
            date     TEXT NOT NULL,
            day_name TEXT NULL,
            PRIMARY KEY (day_type, date)
        );

        CREATE TABLE IF NOT EXISTS target_gl_entry (
            entry_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            document_number       TEXT NULL,
            line_item             TEXT NULL,
            post_date             TEXT NULL,
            approval_date         TEXT NULL,
            voucher_date          TEXT NULL,
            account_code          TEXT NULL,
            account_name          TEXT NULL,
            document_description  TEXT NULL,
            source_module         TEXT NULL,
            created_by            TEXT NULL,
            approved_by           TEXT NULL,
            is_manual             INTEGER NULL,
            amount_scaled         INTEGER NOT NULL,
            debit_amount_scaled   INTEGER NOT NULL,
            credit_amount_scaled  INTEGER NOT NULL,
            dr_cr                 TEXT NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT'))
        );
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_doc
            ON target_gl_entry (document_number);
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_account
            ON target_gl_entry (account_code);

        CREATE TABLE IF NOT EXISTS target_tb_balance (
            balance_id            INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            account_code          TEXT NULL,
            account_name          TEXT NULL,
            change_amount_scaled  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_target_tb_balance_account
            ON target_tb_balance (account_code);

        CREATE TABLE IF NOT EXISTS result_rule_run (
            run_id        TEXT PRIMARY KEY,
            run_kind      TEXT NOT NULL CHECK (run_kind IN ('validate','prescreen')),
            generated_utc TEXT NOT NULL,
            summary_json  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_result_rule_run_kind
            ON result_rule_run (run_kind, generated_utc);

        CREATE TABLE IF NOT EXISTS result_inf_sampling_test_sample (
            run_id          TEXT NOT NULL,
            entry_id        INTEGER NOT NULL,
            document_number TEXT NULL,
            line_item       TEXT NULL,
            PRIMARY KEY (run_id, entry_id)
        );

        CREATE TABLE IF NOT EXISTS config_filter_scenario (
            position        INTEGER PRIMARY KEY,
            name            TEXT NOT NULL,
            rationale       TEXT NOT NULL,
            definition_json TEXT NOT NULL,
            saved_utc       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS staging_account_mapping_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS target_account_mapping (
            mapping_id            INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            account_code          TEXT NOT NULL,
            account_name          TEXT NULL,
            standardized_category TEXT NOT NULL
                CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others'))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_target_account_mapping_code
            ON target_account_mapping (account_code);

        -- 授權編製人員清單（C 子專案）。單欄姓名集合（name PK）+ 原始列暫存。
        -- 不入 import_batch dataset_kind 體系;加法建表(IF NOT EXISTS)不需 schema 升版,既有資料庫開啟時自動補上。
        CREATE TABLE IF NOT EXISTS staging_authorized_preparer_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );
        CREATE TABLE IF NOT EXISTS target_authorized_preparer (
            name TEXT NOT NULL PRIMARY KEY
        );

        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_post_date
            ON target_gl_entry (post_date);
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_approval_date
            ON target_gl_entry (approval_date);

        -- 前端「狀態與訊息」的持久化（manifest log.append / log.recent）。
        -- UX 輔助紀錄,非審計留痕;加法建表(IF NOT EXISTS)不需 schema 升版,既有資料庫開啟時自動補上。
        CREATE TABLE IF NOT EXISTS app_message_log (
            message_id   INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_utc TEXT NOT NULL,
            level        TEXT NOT NULL CHECK (level IN ('info','warn')),
            text         TEXT NOT NULL
        );

        -- 完整性 part(a) 控制總數（單列;投影時 upsert replace）。
        -- 投影 staging→target 時落地的來源列數、母體列數與借/貸總額,供 validate.run 對上 target 現值。
        -- 加法建表(IF NOT EXISTS)不需 schema 升版,既有資料庫開啟時自動補上（同 app_message_log 先例）。
        CREATE TABLE IF NOT EXISTS gl_control_total (
            singleton        INTEGER PRIMARY KEY CHECK (singleton = 1),
            source_row_count INTEGER NOT NULL,
            target_row_count INTEGER NOT NULL,
            target_debit_scaled  INTEGER NOT NULL,
            target_credit_scaled INTEGER NOT NULL
        );

        -- 進階篩選命中落地（plan 子專案 D1）：filter.commit 把每個已存情境的命中 entry_id
        -- 落地於此，供 query.filterHitsPage keyset 分頁回取（PK 覆蓋 seek）。衍生資料：
        -- 隨 RuleRunResultReset 失效集清除。加法建表（IF NOT EXISTS）不需 schema 升版。
        CREATE TABLE IF NOT EXISTS result_filter_run (
            scenario_position INTEGER NOT NULL,
            entry_id          INTEGER NOT NULL,
            PRIMARY KEY (scenario_position, entry_id)
        );
        -- D2 tag 矩陣即時 pivot 的輔助索引:以 entry_id 為前導鍵,讓
        -- 「EXISTS(result_filter_run WHERE entry_id = g.entry_id)」與「entry_id 鍵範圍取命中位置」走索引
        -- (PK 前導鍵為 scenario_position,不利這兩種存取)。加法、IF NOT EXISTS 不升 schema 版本。
        CREATE INDEX IF NOT EXISTS idx_result_filter_run_entry ON result_filter_run (entry_id, scenario_position);
        """;

    public string GetDatabasePath(string projectId) => folder.GetDatabasePath(projectId);

    /// <summary>SQLite:jet.db 檔是否存在(同名資料夾的唯一性由 IProjectStore.FindAsync 把關,此處為防禦縱深)。</summary>
    public Task<bool> DatabaseExistsAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult(File.Exists(folder.GetDatabasePath(projectId)));

    public SqliteConnection CreateConnection(string projectId)
    {
        var factory = new SqliteConnectionFactory(new SqliteOptions(folder.GetDatabasePath(projectId)));
        return factory.CreateConnection();
    }

    /// <summary>
    /// dev 檢視專用的獨立唯讀連線：Mode=ReadOnly、私有快取、不進連線池——
    /// 每次查詢都真實開磁碟檔，保證看到的是已持久化資料且零副作用。
    /// DB 檔不存在時開啟即失敗（不會建檔）。
    /// </summary>
    public SqliteConnection CreateReadOnlyConnection(string projectId)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = folder.GetDatabasePath(projectId),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };

        return new SqliteConnection(builder.ToString());
    }

    /// <summary>
    /// 第 1 版 → 第 2 版的加法遷移（單一 transaction、冪等：版本判斷 + 升版同交易）。
    /// 第 1 版的 row_number 就是來源列號，因此 source_row_number 直接回填 row_number、
    /// 既有批次補一筆來源序號 1 的紀錄——遷移後既有專案的 INF 抽樣排序鍵完全不變。
    /// 不重建表、不動 target；SQL 為 SQL Server 可直譯的加法語句。
    /// </summary>
    private const string MigrateV1ToV2Sql =
        """
        ALTER TABLE staging_gl_raw_row ADD COLUMN source_no INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE staging_gl_raw_row ADD COLUMN source_row_number INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE staging_tb_raw_row ADD COLUMN source_no INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE staging_tb_raw_row ADD COLUMN source_row_number INTEGER NOT NULL DEFAULT 0;
        UPDATE staging_gl_raw_row SET source_row_number = row_number;
        UPDATE staging_tb_raw_row SET source_row_number = row_number;
        INSERT INTO import_batch_source
            (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
        SELECT batch_id, 1, source_file_path, source_file_name, NULL, NULL, NULL, row_count, imported_utc
        FROM import_batch;
        UPDATE schema_info SET value = '2' WHERE key = 'schema_version';
        """;

    /// <summary>
    /// 第 2 版 → 第 3 版（規則命名更名 + 科目配對，2026-06-11）：
    /// 1. 舊鍵（v1–v4 / r1–r8 / descNullCount）儲存的規則執行摘要**清除不翻譯**——
    ///    衍生資料重跑即恢復且結果相同（INF 抽樣 seed 固定）；舊抽樣表一併卸除
    ///    （改建為 result_inf_sampling_test_sample，由基底 schema 建立）。
    /// 2. import_batch 的 dataset_kind CHECK 擴充 'account_mapping'：SQLite 不能
    ///    ALTER CHECK，以重建表搬資料完成（欄序不變）。
    /// 3. 篩選情境（使用者著作的組態）由 C# 段逐鍵翻譯保留，見
    ///    <see cref="TranslateScenarioKeysAsync"/>。
    /// </summary>
    private const string MigrateV2ToV3Sql =
        """
        DELETE FROM result_rule_run;
        DROP TABLE IF EXISTS result_validation_v3_sample;

        CREATE TABLE import_batch_v3 (
            batch_id         TEXT PRIMARY KEY,
            dataset_kind     TEXT NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            imported_utc     TEXT NOT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            columns_json     TEXT NOT NULL
        );
        INSERT INTO import_batch_v3
        SELECT batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json
        FROM import_batch;
        DROP TABLE import_batch;
        ALTER TABLE import_batch_v3 RENAME TO import_batch;
        CREATE INDEX IF NOT EXISTS ix_import_batch_kind
            ON import_batch (dataset_kind, imported_utc);

        UPDATE schema_info SET value = '3' WHERE key = 'schema_version';
        """;

    /// <summary>
    /// 第 3 版 → 第 4 版:假日/補班名稱欄(行事曆檔案匯入帶名稱)。加法、冪等。
    /// 註:基底 SchemaSql 對「表尚不存在」的庫(如 v1/v2 凍結快照)已直接建出帶 day_name 的表;
    /// SQLite 無 ADD COLUMN IF NOT EXISTS,故 ALTER 前以 pragma_table_info 守欄是否已存在,
    /// 避免鏈式升級時 duplicate column。版本回填一律執行(冪等)。
    /// </summary>
    private const string AddCalendarDayNameSql =
        "ALTER TABLE staging_calendar_raw_day ADD COLUMN day_name TEXT;";

    private const string BumpToV4Sql =
        "UPDATE schema_info SET value = '4' WHERE key = 'schema_version';";

    private const string CalendarDayNameExistsSql =
        "SELECT COUNT(*) FROM pragma_table_info('staging_calendar_raw_day') WHERE name = 'day_name';";

    /// <summary>
    /// 第 4 版 → 第 5 版:傳票日期欄(回溯過帳偵測 + 日期區間篩選)。加法、冪等。
    /// 註:基底 SchemaSql 對「表尚不存在」的庫已直接建出帶 voucher_date 的表;
    /// SQLite 無 ADD COLUMN IF NOT EXISTS,故 ALTER 前以 pragma_table_info 守欄是否已存在,
    /// 避免鏈式升級時 duplicate column。版本回填一律執行(冪等)。
    /// </summary>
    private const string VoucherDateExistsSql =
        "SELECT COUNT(*) FROM pragma_table_info('target_gl_entry') WHERE name = 'voucher_date';";

    private const string AddVoucherDateSql =
        "ALTER TABLE target_gl_entry ADD COLUMN voucher_date TEXT;";

    private const string BumpToV5Sql =
        "UPDATE schema_info SET value = '5' WHERE key = 'schema_version';";

    /// <summary>config_filter_scenario 內 prescreenKey 的舊鍵 → 新 wire key 對照（manifest Prescreen 章節）。</summary>
    private static readonly IReadOnlyDictionary<string, string> PrescreenKeyMigrationMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["r1"] = "postPeriodApproval",
            ["r2"] = "suspiciousKeywords",
            ["r3"] = "unexpectedAccountPair",
            ["r4"] = "trailingZeros",
            ["r7post"] = "weekendPosting",
            ["r7doc"] = "weekendApproval",
            ["r8post"] = "holidayPosting",
            ["r8doc"] = "holidayApproval",
            ["descNull"] = "blankDescription"
        };

    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // WAL 是資料庫檔案層的持久設定（冪等）：百萬列單交易寫入避免 rollback journal
        // 的雙倍寫放大，replace 模式的大量 DELETE 也因此變廉價（guide §3.1.5 規模調校）
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var version = await ReadVersionAsync(connection, cancellationToken);

        if (version == "1")
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = MigrateV1ToV2Sql;
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            version = "2";
        }

        if (version == "2")
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = MigrateV2ToV3Sql;
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }

            await TranslateScenarioKeysAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            version = "3";
        }

        if (version == "3")
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = CalendarDayNameExistsSql;
                var hasColumn = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
                if (!hasColumn)
                {
                    await using var addColumn = connection.CreateCommand();
                    addColumn.Transaction = transaction;
                    addColumn.CommandText = AddCalendarDayNameSql;
                    await addColumn.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = BumpToV4Sql;
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            version = "4";
        }

        if (version == "4")
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = VoucherDateExistsSql;
                var hasColumn = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
                if (!hasColumn)
                {
                    await using var addColumn = connection.CreateCommand();
                    addColumn.Transaction = transaction;
                    addColumn.CommandText = AddVoucherDateSql;
                    await addColumn.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = BumpToV5Sql;
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            version = "5";
        }
    }

    /// <summary>
    /// 刪除該專案的 jet.db（含 WAL/SHM 邊檔）。先 ClearAllPools 釋放連線池對檔案的鎖，
    /// 否則 Windows 上仍開啟的 handle 會讓後續資料夾刪除以「檔案使用中」失敗。
    /// 資料夾本身由 IProjectStore.DeleteAsync 刪除。
    /// </summary>
    public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();

        var databasePath = folder.GetDatabasePath(projectId);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task<string?> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var versionQuery = connection.CreateCommand();
        versionQuery.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        return (string?)await versionQuery.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// v2→v3 的篩選情境翻譯：definition_json 內每條 rule 的 prescreenKey 舊鍵改新鍵。
    /// 情境是使用者著作的組態，不可清除；列數 ≤ 5，在 C# 內以 JsonNode 改寫安全且可讀。
    /// 未知鍵保持原樣（讓後續驗證報錯，而非遷移時靜默吞掉）。
    /// </summary>
    private static async Task TranslateScenarioKeysAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<(long Position, string Json)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT position, definition_json FROM config_filter_scenario;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        foreach (var (position, json) in rows)
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject scenario
                || scenario["groups"] is not System.Text.Json.Nodes.JsonArray groups)
            {
                continue;
            }

            var changed = false;
            foreach (var group in groups)
            {
                if (group?["rules"] is not System.Text.Json.Nodes.JsonArray rules)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    var oldKey = rule?["prescreenKey"]?.GetValue<string>();
                    if (oldKey is not null && PrescreenKeyMigrationMap.TryGetValue(oldKey, out var newKey))
                    {
                        rule!["prescreenKey"] = newKey;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE config_filter_scenario SET definition_json = @json WHERE position = @position;";
            update.Parameters.AddWithValue("@json", scenario.ToJsonString(JetJsonStorage.Options));
            update.Parameters.AddWithValue("@position", position);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
