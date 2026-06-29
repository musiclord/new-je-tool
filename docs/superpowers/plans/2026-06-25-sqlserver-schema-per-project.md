# SQL Server 2022 schema-per-project 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 SQL Server provider 從 DB-per-project 改為單一資料庫內 schema-per-project，跑在測試環境 SQL Server 2022，並把連線設定改為 appsettings 分層 + 健康檢查。

**Architecture:** 23+ 個 `SqlServer*` repo 的裸表名 SQL 透過單一命令工廠（方案 C）綁定到 `prj_xxx` schema；schema 名由 projectId 確定性衍生（純函式）；建立/刪除專案用 CREATE/DROP SCHEMA 取代 CREATE/DROP DATABASE；單庫內一張 `dbo.project_schema_map` 供反查。

**Tech Stack:** .NET / C#、`Microsoft.Data.SqlClient`、`Microsoft.Extensions.Configuration`、xUnit、原生 ADO.NET（事實表禁用 EF Core）。

**對應 spec:** `docs/superpowers/specs/2026-06-25-sqlserver-schema-per-project-design.md`

## Global Constraints

- 依賴方向 `Infrastructure → Domain`；provider 分支只在 Infrastructure，不上 Application/前端（AGENTS.md）。
- 所有 GL/TB 規則一律 parameterized set-based SQL；不得把完整列集載入 Application 記憶體。
- 規則名稱用 `docs/jet-guide.md` §4 登錄表名（`completeness_test`…）；`W1~W10` / `V/R/A` 流水代號已退役，**不得**出現於表名、UI、wire、新文件。
- 事實表讀寫走原生 SQL（Dapper/ADO.NET），**不得**用 EF Core。
- schema 名只能由系統衍生，禁止使用者輸入；任何拼進 SQL 的 schema 名必須先過白名單 `^prj_[a-z0-9]*_[0-9a-f]{8}$` 並以方括號包裹。
- provider 為 per-project、寫在 `project.json`、不可變；**不**引入全域 DI 切換。
- SQLite provider、資料夾/registry 身分模型、dbo 鎖/稽核/權限、AD、migration runner、歸檔、多人並發 **不在本計畫範圍**。
- **版本控制：agent 全程不 commit、不 push、不提議 commit**（AGENTS.md / CLAUDE.md）。每個任務以「驗收點」收尾；commit 由使用者驗證後自行執行。需要隔離複審時用 `git add -A && git write-tree`（不落 commit）。
- 連線字串元素：`Encrypt=true` 一律加密；`Application Name` 帶上；錯誤訊息**不得含密碼**。

---

### Task 1: `SqlServerProjectSchema` 純函式（schema 名衍生與白名單）

**Files:**
- Create: `src/JET/JET/Infrastructure/SqlServerProjectSchema.cs`
- Test: `src/JET/tests/JET.Tests/Infrastructure/SqlServerProjectSchemaTests.cs`

**Interfaces:**
- Produces:
  - `static string SqlServerProjectSchema.For(string projectId)` → 回傳 `prj_<sanitized>_<8hex>`
  - `static bool SqlServerProjectSchema.IsValid(string schemaName)` → 白名單檢查

- [ ] **Step 1: 寫失敗測試**

```csharp
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlServerProjectSchemaTests
{
    [Fact]
    public void For_IsDeterministic()
        => Assert.Equal(SqlServerProjectSchema.For("ACME-FY2025"), SqlServerProjectSchema.For("ACME-FY2025"));

    [Fact]
    public void For_MatchesWhitelist()
    {
        foreach (var id in new[] { "ACME-FY2025", "光寶2025", "合併案", "A_B (1)" })
            Assert.Matches("^prj_[a-z0-9]*_[0-9a-f]{8}$", SqlServerProjectSchema.For(id));
    }

    [Fact]
    public void For_NoCollision_WhenSanitizedEqual()
        => Assert.NotEqual(SqlServerProjectSchema.For("A-B"), SqlServerProjectSchema.For("AB"));

    [Fact]
    public void IsValid_RejectsInjection()
    {
        Assert.False(SqlServerProjectSchema.IsValid("prj_x]; DROP TABLE t;--"));
        Assert.False(SqlServerProjectSchema.IsValid("dbo"));
        Assert.True(SqlServerProjectSchema.IsValid(SqlServerProjectSchema.For("光寶2025")));
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --filter SqlServerProjectSchemaTests`
Expected: FAIL（型別不存在）

- [ ] **Step 3: 實作**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JET.Infrastructure;

/// <summary>
/// 由 projectId 確定性衍生 SQL Server schema 名（schema-per-project）。純函式、零儲存。
/// 格式 prj_<sanitized>_<8hex>；hex 尾使不同 projectId 淨化後相同時碰撞機率極低（8 hex ≈ 2⁻³²），最終由 schema 存在性檢查 backstop。
/// schema 名只能由此衍生、不接受使用者輸入；拼進 SQL 前一律過 <see cref="IsValid"/>。
/// </summary>
public static partial class SqlServerProjectSchema
{
    private const int SanitizedMax = 40;

    [GeneratedRegex("^prj_[a-z0-9]*_[0-9a-f]{8}$")]
    private static partial Regex Whitelist();

    public static string For(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var sanitized = new string(projectId.Where(char.IsLetterOrDigit)
            .Where(c => c < 128) // 只留 ASCII 英數；中文等非 ASCII 交給 hex 尾
            .Select(char.ToLowerInvariant).ToArray());
        if (sanitized.Length > SanitizedMax)
            sanitized = sanitized[..SanitizedMax];

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectId));
        var hex8 = Convert.ToHexString(hash).ToLowerInvariant()[..8]; // .NET 5+ 安全寫法

        return $"prj_{sanitized}_{hex8}";
    }

    public static bool IsValid(string schemaName)
        => !string.IsNullOrEmpty(schemaName) && Whitelist().IsMatch(schemaName);
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --filter SqlServerProjectSchemaTests`
Expected: PASS

- [ ] **Step 5: 驗收點（不 commit）** — 標記完成；commit 留給使用者。

---

### Task 2: 命令工廠 `SqlServerProjectDatabase.CreateCommand`（方案 C 收斂點）

**Files:**
- Modify: `src/JET/JET/Infrastructure/SqlServerProjectDatabase.cs`
- Test: `src/JET/tests/JET.Tests/Infrastructure/SqlServerCommandFactoryTests.cs`

**Interfaces:**
- Consumes: `SqlServerProjectSchema.For` / `IsValid`（Task 1）
- Produces: `SqlCommand SqlServerProjectDatabase.CreateCommand(SqlConnection connection, string projectId, string sqlWithTokens)`
  — 把所有 `{s}` 替換為 `[prj_xxx]`（過白名單），設好 `Connection`+`CommandText` 回傳；呼叫端自行加 `Parameters` / 設 `Transaction`。

- [ ] **Step 1: 寫失敗測試**

```csharp
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlServerCommandFactoryTests
{
    private static SqlServerProjectDatabase NewDb()
        => new(new SqlServerConnectionOptions("Server=localhost;Database=JET_DEV;Integrated Security=True;"));

    [Fact]
    public void CreateCommand_SubstitutesSchemaToken()
    {
        using var conn = new SqlConnection();
        var schema = SqlServerProjectSchema.For("ACME-FY2025");
        using var cmd = NewDb().CreateCommand(conn, "ACME-FY2025", "SELECT * FROM {s}.target_gl_entry");
        Assert.Equal($"SELECT * FROM [{schema}].target_gl_entry", cmd.CommandText);
        Assert.DoesNotContain("{s}", cmd.CommandText);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --filter SqlServerCommandFactoryTests`
Expected: FAIL（方法不存在）

- [ ] **Step 3: 實作（加進 `SqlServerProjectDatabase`）**

```csharp
/// <summary>
/// 方案 C 收斂點：把 SQL 中的哨兵 {s} 全部替換為該專案 schema 的方括號識別字。
/// schema 名先過白名單，杜絕識別字注入（schema 名不可參數化）。
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
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --filter SqlServerCommandFactoryTests`
Expected: PASS

- [ ] **Step 5: 驗收點（不 commit）**

---

### Task 3: 單庫 + EnsureSingleDatabase + `dbo.project_schema_map` + DDL 改 `{s}.` + 建立流程

**Files:**
- Modify: `src/JET/JET/Infrastructure/SqlServerProjectDatabase.cs`

**Interfaces:**
- Consumes: `CreateCommand`（Task 2）、`SqlServerProjectSchema.For`
- Produces:
  - `EnsureCreatedAsync(projectId, ct)` 改為：確保單庫 → 確保 `dbo.project_schema_map` → `CREATE SCHEMA` → 在 schema 建所有表（DDL `{s}.`）→ upsert map。單一 transaction（DDL 段）。
  - `CreateConnection(projectId)` 的 `InitialCatalog` ＝ 單庫（`options` 帶 `Database`）。

- [ ] **Step 1: 改 `BuildConnectionString` 指向單庫**

把 `DatabaseName(projectId)`（`JET_{projectId}`）退役；`CreateConnection`/schema DDL 都連單庫。單庫名取自連線字串既有的 `InitialCatalog`（由 B 的設定提供，dev＝`JET_DEV`）。

```csharp
public SqlConnection CreateConnection(string projectId)
    => new(options.BaseConnectionString); // InitialCatalog 已是單庫；projectId 只用於 schema
```

- [ ] **Step 2: EnsureCreatedAsync 改為 schema 建立**

```csharp
public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
{
    var schema = SqlServerProjectSchema.For(projectId);

    await EnsureSingleDatabaseAndMapAsync(cancellationToken);

    await using var connection = CreateConnection(projectId);
    await connection.OpenAsync(cancellationToken);

    // schema 已存在則整段 no-op（冪等）。
    await using (var exists = connection.CreateCommand())
    {
        exists.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
        exists.Parameters.AddWithValue("@s", schema);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 1)
            return;
    }

    await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
    await using (var create = connection.CreateCommand())
    {
        create.Transaction = tx;
        // CREATE SCHEMA 必須是批次首句 → 用 EXEC 包；schema 已過 For() 衍生且即將 IsValid 檢查。
        create.CommandText = $"EXEC('CREATE SCHEMA [{schema}]');";
        await create.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var ddl = CreateCommand(connection, projectId, SchemaSql)) // SchemaSql 改用 {s}.
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

private async Task EnsureSingleDatabaseAndMapAsync(CancellationToken cancellationToken)
{
    var dbName = new SqlConnectionStringBuilder(options.BaseConnectionString).InitialCatalog;
    await using (var master = new SqlConnection(BuildConnectionString("master")))
    {
        await master.OpenAsync(cancellationToken);
        await using var create = master.CreateCommand();
        // dbName 來自設定、非使用者輸入；仍以括號內嵌（CREATE DATABASE 不接受參數）。
        create.CommandText = $"IF DB_ID(N'{dbName}') IS NULL EXEC('CREATE DATABASE [{dbName}]');";
        await create.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var conn = CreateConnection("__map__placeholder__"); // 連單庫
    await conn.OpenAsync(cancellationToken);
    await using var map = conn.CreateCommand();
    map.CommandText =
        """
        IF OBJECT_ID(N'dbo.project_schema_map','U') IS NULL
            CREATE TABLE dbo.project_schema_map (
                schema_name NVARCHAR(64) PRIMARY KEY,
                project_id  NVARCHAR(100) NOT NULL,
                case_name   NVARCHAR(200) NULL
            );
        """;
    await map.ExecuteNonQueryAsync(cancellationToken);
}
```

> 註：`CreateConnection("__map__placeholder__")` 僅借其連單庫的連線字串，不算 schema；若覺不雅，改寫一個 `CreateSingleDbConnection()` 私有方法回傳同一連線字串。實作時擇一，行為等價。

- [ ] **Step 3: 把 `SchemaSql` 內所有 `dbo.` 改成 `{s}.`**

`SqlServerProjectDatabase.SchemaSql` 內每一處 `dbo.<table>`、`UPDATE dbo.schema_info`、`OBJECT_ID(N'dbo.<table>')`、`sys.indexes ... OBJECT_ID(N'dbo.<table>')` 全部改 `{s}.` 與 `OBJECT_ID(N'{s}.<table>')`。索引存在性檢查的 `object_id = OBJECT_ID(N'{s}.<table>')` 同步改。表內容（欄位、型別、CHECK、IDENTITY、索引）一字不動，維持第 5 版形狀與 SQLite 等價。

- [ ] **Step 4: 建置**

Run: `dotnet build src/JET/JET.slnx --no-restore --nologo`
Expected: 成功（無 `dbo.` 殘留於 SchemaSql）

- [ ] **Step 5: 驗收點（不 commit）**

---

### Task 4: 刪除流程（drop tables → DROP SCHEMA → delete map）

**Files:**
- Modify: `src/JET/JET/Infrastructure/SqlServerProjectDatabase.cs`

**Interfaces:**
- Produces: `DeleteAsync(projectId, ct)` 改為 drop 該 schema 全表 → `DROP SCHEMA` → 刪 map 列。

- [ ] **Step 1: 改寫 `DeleteAsync`**

```csharp
public async Task DeleteAsync(string projectId, CancellationToken cancellationToken)
{
    var schema = SqlServerProjectSchema.For(projectId);
    if (!SqlServerProjectSchema.IsValid(schema)) return;

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
        DELETE FROM dbo.project_schema_map WHERE schema_name = @s;
        """;
    cmd.Parameters.AddWithValue("@s", schema);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
}
```

- [ ] **Step 2: 建置**

Run: `dotnet build src/JET/JET.slnx --no-restore --nologo`
Expected: 成功

- [ ] **Step 3: 驗收點（不 commit）**

---

### Task 5: 移除 `dbo.project_schema_map` 未使用的 `case_name` 欄

**背景（2026-06-25 修訂）**：身分模型**維持 2026-06-22 現況**（`projectId` ＝ 案件名稱，已可讀；`projectCode` ＝ 案件編號；`entityName` ＝ 客戶名稱）。早期草案要加 `ProjectDocument.caseName` 顯示欄並寫 `map.case_name`，經查證後判定為設計誤解（projectId 本身已是可讀名稱，反查由 `map.project_id` 直接達成）。因此**不**動 `ProjectDocument`／`ProjectCreateHandler`，並移除 Task 3 預留、恆為 NULL、無 populator 的 `case_name` 欄（YAGNI）。

**Files:**
- Modify: `src/JET/JET/Infrastructure/SqlServerProjectDatabase.cs`（`EnsureSingleDatabaseAndMapAsync` 的 map 建表 DDL）

**Interfaces:**
- Produces: `dbo.project_schema_map` 定稿為兩欄 `(schema_name PK, project_id)`。map INSERT（Task 3）本就只寫這兩欄，無需改。

- [ ] **Step 1: 把 map 建表 DDL 從三欄改為兩欄**

```sql
IF OBJECT_ID(N'dbo.project_schema_map','U') IS NULL
    CREATE TABLE dbo.project_schema_map (
        schema_name NVARCHAR(64) PRIMARY KEY,
        project_id  NVARCHAR(100) NOT NULL
    );
```
（移除 `case_name NVARCHAR(200) NULL` 一行。INSERT 既已是 `(schema_name, project_id)`，不動。）

- [ ] **Step 2: 建置**

Run: `dotnet build src/JET/JET.slnx --no-restore --nologo`
Expected: 成功、0 警告 0 錯誤。

- [ ] **Step 3: 驗收點（不 commit）**

---

### Task 6: Repo 掃描——裸表名 → `{s}.` 並改走命令工廠（機械式）

**Files（全部 Modify；逐檔處理，每檔改完即建置）：**
```
SqlServerAccountMappingExportRepository.cs   SqlServerAccountMappingRepository.cs
SqlServerAuthorizedPreparerRepository.cs     SqlServerCalendarExportRepository.cs
SqlServerCalendarStore.cs                    SqlServerCompletenessAccountPageRepository.cs
SqlServerCompletenessDiffPageRepository.cs   SqlServerCreatorSummaryExportRepository.cs
SqlServerDevDatabaseInspector.cs             SqlServerDocBalancePageRepository.cs
SqlServerFilterHitsPageRepository.cs         SqlServerFilterRunMaterializer.cs
SqlServerFilterRunRepository.cs              SqlServerFilterScenarioStore.cs
SqlServerInfSamplePageRepository.cs          SqlServerMappingStateStore.cs
SqlServerMessageLogStore.cs                  SqlServerNullRecordsPageRepository.cs
SqlServerRuleRunStore.cs                     SqlServerTagMatrixRowPageRepository.cs
SqlServerTagMatrixScenariosRepository.cs     SqlServerTagMatrixVoucherPageRepository.cs
SqlServerTbRepository.cs                      SqlServerValidationRunRepository.cs
SqlServerPrescreenRunRepository.cs           SqlServerDataPreviewRepository.cs
```
（`SqlServerGlRepository.cs`、`SqlServerImportRepository.cs` 在 Task 7 單獨處理，因含 `dbo.` 與 BulkCopy。`SqlServerProjectDatabase.cs` 已於 Task 2–4 處理。`SqlServerDevDatabaseInspector` 若查 `sys.*`/`INFORMATION_SCHEMA` 來列表，需改成過濾該專案 schema——見 Step 3。）

**機械式轉換規則（每檔）：**
1. 每條 SQL 內的裸表名（`FROM/INTO/UPDATE/JOIN/EXISTS(... )` 後的 `target_gl_entry`、`staging_*`、`import_batch*`、`config_*`、`result_*`、`target_*`、`app_message_log`、`gl_control_total` 等）前綴 `{s}.`。
2. 該 SQL 所屬的 `connection.CreateCommand()` 改為 `database.CreateCommand(connection, projectId, sql)`；`Parameters` / `Transaction` 設定保持原樣。
3. 不動：欄名、參數名、`@p` 佔位、TOP/ORDER BY、商業邏輯。

**代表範例（`SqlServerMessageLogStore.AppendAsync`）：**

before:
```csharp
await using var command = connection.CreateCommand();
command.CommandText =
    """
    INSERT INTO app_message_log (occurred_utc, level, text) VALUES (@utc, @level, @text);
    DELETE FROM app_message_log
    WHERE message_id <= (SELECT MAX(message_id) FROM app_message_log) - @retained;
    """;
```
after:
```csharp
await using var command = database.CreateCommand(connection, projectId,
    """
    INSERT INTO {s}.app_message_log (occurred_utc, level, text) VALUES (@utc, @level, @text);
    DELETE FROM {s}.app_message_log
    WHERE message_id <= (SELECT MAX(message_id) FROM {s}.app_message_log) - @retained;
    """);
```

- [ ] **Step 1: 逐檔套用上述規則**（每改 3–5 檔就 `dotnet build` 一次，及早抓編譯錯）
- [ ] **Step 2: DevDatabaseInspector 特例**：若它用 `sys.tables`/`INFORMATION_SCHEMA.TABLES`/`sys.objects` 列出資料表，把過濾條件加上 `WHERE schema_name = SqlServerProjectSchema.For(projectId)`（dev 檢視只看該專案 schema）。
- [ ] **Step 3: 完整性 grep 閘門（證明掃乾淨）**

Run（PowerShell / Grep 工具）：搜尋殘留裸表名與未收斂命令——
- 在上列檔案中 grep `CreateCommand\(\)`（不帶參數的）→ 應為 0（全改走工廠；唯讀單庫 map/exists 等非專案表查詢除外，需人工確認）。
- grep `FROM (import_batch|staging_|target_|result_|config_|app_message_log|gl_control_total)`（不含 `{s}.` 前綴）→ 應為 0。

- [ ] **Step 4: 建置**

Run: `dotnet build src/JET/JET.slnx --no-restore --nologo`
Expected: 成功

- [ ] **Step 5: 驗收點（不 commit）**

---

### Task 7: `SqlServerGlRepository` 的 `dbo.` 兩處 + `SqlServerImportRepository` BulkCopy 目的表

**Files:**
- Modify: `src/JET/JET/Infrastructure/SqlServerGlRepository.cs`
- Modify: `src/JET/JET/Infrastructure/SqlServerImportRepository.cs`

**Interfaces:**
- Consumes: `CreateCommand`（Task 2）、`SqlServerProjectSchema.For`（Task 1）

- [ ] **Step 1: GlRepository**：把現有 2 處 `dbo.` 與所有裸表名按 Task 6 規則改 `{s}.` + 走工廠（`DELETE FROM target_gl_entry` → `DELETE FROM {s}.target_gl_entry` 經工廠）。
- [ ] **Step 2: ImportRepository 一般 SQL**：按 Task 6 規則改 `{s}.` + 走工廠。
- [ ] **Step 3: ImportRepository 的 `SqlBulkCopy`**：`DestinationTableName = $"dbo.{stagingTable}"` 改為：

```csharp
var schema = SqlServerProjectSchema.For(projectId);
// DestinationTableName 接受方括號識別字；schema 已過 For() 衍生。
bulkCopy.DestinationTableName = $"[{schema}].[{stagingTable}]";
```

- [ ] **Step 4: 建置 + 對該兩檔重跑 grep 閘門**（`dbo.` 殘留應為 0；裸表名應為 0）

Run: `dotnet build src/JET/JET.slnx --no-restore --nologo`
Expected: 成功

- [ ] **Step 5: 驗收點（不 commit）**

---

### Task 8: 連線設定抽象層（B，中度）——appsettings + `SqlConnectionStringBuilder` + 來源優先序

**Files:**
- Create: `src/JET/JET/appsettings.json`
- Create: `src/JET/JET/appsettings.Development.json`
- Create: `src/JET/JET/Infrastructure/SqlConnectionStringFactory.cs`
- Modify: `src/JET/JET/JET.csproj`（appsettings 設為 `CopyToOutputDirectory`）
- Modify: `src/JET/JET/AppCompositionRoot.cs`（接 IConfiguration、來源優先序）
- Test: `src/JET/tests/JET.Tests/Infrastructure/SqlConnectionStringFactoryTests.cs`

**Interfaces:**
- Produces: `static string SqlConnectionStringFactory.Build(IConfiguration config, string? envOverride)`
  — `envOverride`（`JET_SQLSERVER_CONNECTION`）若非空 → 直接回傳；否則由 `Sql:*` 經 `SqlConnectionStringBuilder` 組合（含 `Encrypt`、`Application Name`、`Connect Timeout`）。

- [ ] **Step 1: 寫失敗測試**

```csharp
using JET.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlConnectionStringFactoryTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void EnvOverride_Wins()
    {
        var cs = SqlConnectionStringFactory.Build(Config(new()), "Server=x;Database=y;Integrated Security=True;");
        Assert.Contains("Server=x", cs);
    }

    [Fact]
    public void Composes_From_Sql_Section()
    {
        var cs = SqlConnectionStringFactory.Build(Config(new()
        {
            ["Sql:Server"] = "localhost",
            ["Sql:Database"] = "JET_DEV",
            ["Sql:IntegratedSecurity"] = "true",
            ["Sql:Encrypt"] = "true",
            ["Sql:TrustServerCertificate"] = "true",
            ["Sql:ApplicationName"] = "JET-Dev",
            ["Sql:ConnectTimeoutSeconds"] = "5",
        }), envOverride: null);

        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        Assert.Equal("localhost", b.DataSource);
        Assert.Equal("JET_DEV", b.InitialCatalog);
        Assert.True(b.IntegratedSecurity);
        Assert.True(b.Encrypt);
        Assert.Equal("JET-Dev", b.ApplicationName);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗** — Expected: FAIL
- [ ] **Step 3: 實作 `SqlConnectionStringFactory`**

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace JET.Infrastructure;

public static class SqlConnectionStringFactory
{
    public static string Build(IConfiguration config, string? envOverride)
    {
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;

        var s = config.GetSection("Sql");
        var b = new SqlConnectionStringBuilder
        {
            DataSource = s["Server"] ?? "",
            InitialCatalog = s["Database"] ?? "",
            IntegratedSecurity = bool.TryParse(s["IntegratedSecurity"], out var ig) && ig,
            Encrypt = !bool.TryParse(s["Encrypt"], out var en) || en, // 缺省 true
            TrustServerCertificate = bool.TryParse(s["TrustServerCertificate"], out var tsc) && tsc,
            ApplicationName = s["ApplicationName"] ?? "JET",
            ConnectTimeout = int.TryParse(s["ConnectTimeoutSeconds"], out var t) ? t : 30,
        };
        return b.ConnectionString;
    }
}
```

- [ ] **Step 4: 建 appsettings**

`appsettings.json`:
```json
{ "Sql": { "Encrypt": true, "ConnectTimeoutSeconds": 30, "ApplicationName": "JET" } }
```
`appsettings.Development.json`:
```json
{ "Sql": { "Server": "localhost", "Database": "JET_DEV", "IntegratedSecurity": true, "TrustServerCertificate": true, "ConnectTimeoutSeconds": 5, "ApplicationName": "JET-Dev" } }
```
`JET.csproj` 加：
```xml
<ItemGroup>
  <None Update="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 5: AppCompositionRoot 接 IConfiguration**：建 `IConfiguration`（`AddJsonFile("appsettings.json", optional:true)` + `AddJsonFile($"appsettings.{env}.json", optional:true)`，env 取 `JET_ENVIRONMENT` 缺省 `Production`）；以 `SqlConnectionStringFactory.Build(config, Environment.GetEnvironmentVariable("JET_SQLSERVER_CONNECTION"))` 取得連線字串餵 `SqlServerConnectionOptions`。保留既有 `sqlServerConnectionString` 參數作測試覆寫優先。
- [ ] **Step 6: 跑測試確認通過** — Expected: PASS
- [ ] **Step 7: 建置** — `dotnet build src/JET/JET.slnx --no-restore --nologo`，Expected: 成功
- [ ] **Step 8: 驗收點（不 commit）**

---

### Task 9: 啟動健康檢查（非阻斷）

**Files:**
- Create: `src/JET/JET/Infrastructure/SqlServerHealthCheck.cs`
- Modify: `src/JET/JET/AppCompositionRoot.cs` 或 `Program.cs`（啟動時呼叫一次）
- Test: `src/JET/tests/JET.Tests/Infrastructure/SqlServerHealthCheckTests.cs`

**Interfaces:**
- Produces: `Task<HealthResult> SqlServerHealthCheck.ProbeAsync(string connectionString, CancellationToken ct)`
  — 成功回 `@@VERSION`/`DB_NAME()`/`SUSER_SNAME()`；失敗回含「伺服器+資料庫+認證模式、**不含密碼**」的訊息。不丟例外給呼叫端阻斷 app。

- [ ] **Step 1: 寫失敗測試**（給壞 server 名 → 回失敗且訊息不含任何 `Password=`／密碼字樣；含目標 server/db）

```csharp
[Fact]
public async Task Probe_BadServer_ReturnsRedactedFailure()
{
    var r = await SqlServerHealthCheck.ProbeAsync(
        "Server=nonexistent.invalid;Database=JET_DEV;Integrated Security=True;Connect Timeout=2;", default);
    Assert.False(r.Ok);
    Assert.Contains("JET_DEV", r.Message);
    Assert.DoesNotContain("Password", r.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: 跑測試確認失敗** — Expected: FAIL
- [ ] **Step 3: 實作**（`SqlConnectionStringBuilder` 取 server/db/auth 拼訊息，永不輸出 `Password`/`User ID`）
- [ ] **Step 4: 啟動接點**：app 啟動時若連線字串可得，呼叫 `ProbeAsync`，結果寫進啟動日誌（沿用 `RingBufferLoggerProvider`/NDJSON）。失敗**不**中止啟動（純 SQLite 專案不受影響）。
- [ ] **Step 5: 跑測試確認通過** — Expected: PASS
- [ ] **Step 6: 驗收點（不 commit）**

---

### Task 10: 測試輔助 `TempSqlServerProject` 改為 schema 建/清

**Files:**
- Modify: `src/JET/tests/JET.Tests/Infrastructure/`（`TempSqlServerProject` 所在檔，連同 `SqlServerFact` 探測）

**Interfaces:**
- Consumes: 新的 `SqlServerProjectDatabase`（schema 模型）
- Produces: 測試起手建 `prj_*` schema、結束 `DROP SCHEMA` + 刪 map 列；探測仍走 `JET_SQLSERVER_CONNECTION`（缺省 LocalDB）。

- [ ] **Step 1: 把 temp 專案的建立/清理從「建/刪 DB」改為「建/刪 schema」**（呼叫新 `EnsureCreatedAsync`/`DeleteAsync`；`ProbeConnectionStringAsync` 維持，但確保探測連到的是單庫並能 `CREATE SCHEMA`）。
- [ ] **Step 2: 確保測試隔離**：每個測試用唯一 projectId → 唯一 schema；`Dispose`/finalizer 一律 `DeleteAsync` 清掉，避免殘留 `prj_*`。
- [ ] **Step 3: 建置** — `dotnet build src/JET/JET.slnx --no-restore --nologo`，Expected: 成功
- [ ] **Step 4: 驗收點（不 commit）**

---

### Task 11: Provider 等價套件全綠（驗證任務）

**Files:** 無新增；執行既有 + 新增測試。

- [ ] **Step 1: 設定本機連線**：`JET_SQLSERVER_CONNECTION` 指向測試環境 SQL Server 2022 的**單庫**（例 `Server=localhost;Database=JET_DEV;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;`）。
- [ ] **Step 2: 跑全套測試**

Run: `dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --nologo`
Expected: 全綠；`SqlServerFact`/`SqlServerTheory` 不再 skip（偵測到 2022），provider 等價測試（SQLite vs SQL Server 同 fixture 同結果）通過。

- [ ] **Step 3: 手動冒煙（依 jet-dev-loop skill）**：跑 app，建一個 sqlServer provider 專案 → import → mapping → validate → prescreen → filter → export 全流程；於 SSMS 確認單庫內出現 `prj_xxx` schema 與所有表、`dbo.project_schema_map` 有對應列；刪除專案後 schema 與 map 列消失、單庫保留。
- [ ] **Step 4: 故意打錯伺服器名**，確認啟動日誌清楚指出「以哪個身分連到哪個伺服器哪個資料庫被拒」、不含密碼，且純 SQLite 專案仍可運作。
- [ ] **Step 5: 驗收點（不 commit）** — 全部完成；交付使用者驗證後由其決定 commit。

---

## Self-Review（計畫對 spec 的覆蓋自查）

- **§2 身分模型** → Task 5（caseName + map）。
- **§3 schema 命名** → Task 1。
- **§4 Token 收斂點** → Task 2（工廠）、Task 6/7（套用）。
- **§5 建立/刪除** → Task 3（建）、Task 4（刪）。
- **§6 連線設定 B** → Task 8（appsettings/工廠/優先序）、Task 9（健康檢查）。
- **§7 測試** → Task 1/2/8/9 單元測試、Task 10 harness、Task 11 等價+冒煙。
- **§8 檔案影響圖** → Task 3/4（ProjectDatabase）、Task 6（28 repo）、Task 7（Gl/Import）、Task 8（CompositionRoot/appsettings/ProjectDocument）。
- **§9 風險** → 注入(Task 2 白名單)、測試相容(Task 8 優先序)、DROP SCHEMA 清表(Task 4)、純函式解析(Task 1/2)、健康檢查非阻斷(Task 9)。
- **型別一致性**：`SqlServerProjectSchema.For/IsValid`、`SqlServerProjectDatabase.CreateCommand(conn,projectId,sql)`、`SqlConnectionStringFactory.Build(config,envOverride)`、`SqlServerHealthCheck.ProbeAsync` 在各任務簽名一致。
- **未知精度**：Task 6 檔案清單已逐一列出；`SqlServerDevDatabaseInspector`/`ProjectDocument` 既有結構需於實作時對齊現檔（已標註）。
