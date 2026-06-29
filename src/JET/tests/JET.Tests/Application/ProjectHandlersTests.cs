using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectHandlersTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31",
          "lastPeriodStart": "2024-12-31"
        }
        """;

    [Fact]
    public async Task Create_WritesFolderJsonDbAndReturnsProjectId()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreatePayload);

        Assert.True(data.GetProperty("ok").GetBoolean());
        var projectId = data.GetProperty("projectId").GetString();
        Assert.NotNull(projectId);
        Assert.Matches("^[0-9a-f]{32}$", projectId);

        var projectDir = Path.Combine(host.ProjectsRoot, projectId);
        Assert.True(File.Exists(Path.Combine(projectDir, "project.json")));
        Assert.True(File.Exists(Path.Combine(projectDir, "jet.db")));

        // session 已設定：dev.db.overview 不需先 load 即可使用
        var overview = await host.DispatchAsync("dev.db.overview");
        Assert.True(overview.GetProperty("tables").GetArrayLength() >= 7);
    }

    [Fact]
    public async Task Create_MissingEntityName_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create",
                """{ "projectCode": "X", "operatorId": "op", "periodStart": "2024-01-01", "periodEnd": "2024-12-31" }"""));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("entityName", ex.Message);
    }

    [Fact]
    public async Task Create_BadDateFormat_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create",
                """
                { "projectCode": "X", "entityName": "E", "operatorId": "op",
                  "periodStart": "2024/01/01", "periodEnd": "2024-12-31" }
                """));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("periodStart", ex.Message);
    }

    // ---- 案件名稱(caseName)→ projectId/資料夾名(2026-06-22);選填,未提供回退 GUID ----

    private const string CreateWithCaseName =
        """
        {
          "caseName": "2025年度甲公司查核",
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31"
        }
        """;

    [Fact]
    public async Task Create_WithCaseName_UsesItAsProjectIdAndFolderName()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreateWithCaseName);

        Assert.Equal("2025年度甲公司查核", data.GetProperty("projectId").GetString());
        Assert.True(File.Exists(
            Path.Combine(host.ProjectsRoot, "2025年度甲公司查核", "project.json")));
    }

    [Fact]
    public async Task Create_WithoutCaseName_FallsBackToGuidProjectId()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreatePayload);

        Assert.Matches("^[0-9a-f]{32}$", data.GetProperty("projectId").GetString());
    }

    [Fact]
    public async Task Create_WithIllegalCaseName_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create", CreateWithCaseName.Replace("2025年度甲公司查核", "壞/名稱")));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    [Fact]
    public async Task Create_DuplicateCaseName_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreateWithCaseName);
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.create", CreateWithCaseName));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("已存在", ex.Message);
    }

    /// <summary>
    /// schema-per-project 修掉了舊 DB-name 淨化碰撞 bug：schema = For(projectId) =
    /// prj_ + sanitize + hash8(projectId)，兩個不同案名 → 不同 hash8 → 不同 schema（碰撞機率極低 ~2⁻³²）。
    /// 故兩個「在舊單庫淨化模型下會撞同 JET_ 庫名」的案名（僅差一個空白），在新模型下兩案皆建立成功、
    /// 且落在不同 schema。oracle：SqlServerProjectSchema.For 衍生規格（兩 schema 必不相等）+ 兩 project.json 皆落地。
    /// </summary>
    [SqlServerFact]
    public async Task Create_SqlServer_PreviouslyCollidingCaseNames_BothSucceedInDistinctSchemas()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        static string PayloadFor(string caseName) =>
            $$"""
            { "caseName": "{{caseName}}", "projectCode": "P", "entityName": "E", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "sqlServer" }
            """;

        // 唯一 tag → 每輪不同的 schema（免跨輪殘留汙染）；兩名僅差一個空白，舊模型淨化後相同 → 舊會碰撞。
        var tag = Guid.NewGuid().ToString("N")[..8];
        var nameA = $"案 {tag}";
        var nameB = $"案{tag}";

        // 新模型規格：兩不同案名（即 projectId）→ 不同 hash8 → 不同 schema（碰撞機率極低 ~2⁻³²）。
        Assert.NotEqual(SqlServerProjectSchema.For(nameA), SqlServerProjectSchema.For(nameB));

        try
        {
            var createdA = await host.DispatchAsync("project.create", PayloadFor(nameA));
            var createdB = await host.DispatchAsync("project.create", PayloadFor(nameB));

            // 兩案皆建立成功（不再有第二案被擋的碰撞）。
            Assert.True(createdA.GetProperty("ok").GetBoolean());
            Assert.True(createdB.GetProperty("ok").GetBoolean());

            // 兩 project.json 皆落地、各自獨立。
            Assert.True(File.Exists(Path.Combine(host.ProjectsRoot, nameA, "project.json")));
            Assert.True(File.Exists(Path.Combine(host.ProjectsRoot, nameB, "project.json")));
        }
        finally
        {
            // schema-per-project：清掉兩案在共用單庫（測試 host 預設 JET_DEV）留下的 prj_xxx schema。
            var db = new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString));
            await db.DeleteAsync(nameA, CancellationToken.None);
            await db.DeleteAsync(nameB, CancellationToken.None);
        }
    }

    [Fact]
    public async Task List_ReturnsCreatedProjectsNewestFirst()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreatePayload);
        await host.DispatchAsync(
            "project.create",
            CreatePayload.Replace("ENG-2024-001", "ENG-2024-002"));

        var data = await host.DispatchAsync("project.list");
        var projects = data.GetProperty("projects");

        Assert.Equal(2, projects.GetArrayLength());
        Assert.Equal("ENG-2024-002", projects[0].GetProperty("projectCode").GetString());
    }

    [Fact]
    public async Task Create_RecordsSqliteDatabaseProvider()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");

        // manifest「資料庫 provider 歸屬」：本地專案固定 sqlite。
        Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
    }

    [Fact]
    public async Task Load_LegacyProjectJsonWithoutProvider_DefaultsToSqlite()
    {
        using var host = new HandlerTestHost();

        // 舊版 project.json（databaseProvider 欄位出現前的形狀）→ 讀取時一律正規化為 sqlite。
        var projectId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projectDir = Path.Combine(host.ProjectsRoot, projectId);
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "project.json"), $$"""
            {
              "projectId": "{{projectId}}",
              "projectCode": "LEGACY-001",
              "entityName": "舊版專案",
              "operatorId": "op",
              "periodStart": "2025-01-01",
              "periodEnd": "2025-12-31",
              "lastAccountingPeriodDate": null,
              "moneyScale": 10000,
              "roundingMode": "AwayFromZero",
              "createdUtc": "2026-06-01T00:00:00+00:00",
              "currentStep": 1,
              "schemaVersion": 1
            }
            """);

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");

        Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
    }

    [Fact]
    public async Task Load_UnknownOrTraversalId_ThrowsProjectNotFound()
    {
        using var host = new HandlerTestHost();

        var unknown = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{new string('0', 32)}}" }"""));
        Assert.Equal(JetErrorCodes.ProjectNotFound, unknown.Code);

        var traversal = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", """{ "projectId": "..\\..\\evil" }"""));
        Assert.Equal(JetErrorCodes.ProjectNotFound, traversal.Code);
    }

    [Fact]
    public async Task Delete_RemovesFolderAndDatabase_AndDropsFromList()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;
        var projectDir = Path.Combine(host.ProjectsRoot, projectId);
        Assert.True(File.Exists(Path.Combine(projectDir, "jet.db")));

        var deleted = await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");

        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.Equal(projectId, deleted.GetProperty("projectId").GetString());
        // 資料夾(含 jet.db)整個移除,且不再出現在清單。
        Assert.False(Directory.Exists(projectDir));
        var list = await host.DispatchAsync("project.list");
        Assert.Equal(0, list.GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task Delete_UnknownProjectId_ThrowsProjectNotFound()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.delete", $$"""{ "projectId": "{{new string('0', 32)}}" }"""));

        Assert.Equal(JetErrorCodes.ProjectNotFound, ex.Code);
    }

    [Fact]
    public async Task List_AfterCreate_IncludesProviderAndNullLastOpened()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreatePayload);

        var project = (await host.DispatchAsync("project.list")).GetProperty("projects")[0];

        Assert.Equal("sqlite", project.GetProperty("databaseProvider").GetString());
        // 從未開啟過 → lastOpenedUtc 為 null(前端據此 fallback 顯示建立時間)。
        Assert.Equal(JsonValueKind.Null, project.GetProperty("lastOpenedUtc").ValueKind);
    }

    [Fact]
    public async Task Load_StampsLastOpenedUtc_VisibleInList()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");

        var project = (await host.DispatchAsync("project.list")).GetProperty("projects")[0];
        // 載入後 lastOpenedUtc 應被戳記為可解析的時間戳(非 null)。
        Assert.Equal(JsonValueKind.String, project.GetProperty("lastOpenedUtc").ValueKind);
        Assert.True(DateTimeOffset.TryParse(project.GetProperty("lastOpenedUtc").GetString(), out _));
    }

    [SqlServerFact]
    public async Task Delete_SqlServerProject_DropsDatabaseAndFolder()
    {
        // 連線閘控:無 LocalDB/Express 即跳過(對齊 ProviderParityJourneyTests)。
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        var created = await host.DispatchAsync(
            "project.create",
            CreatePayload.Insert(CreatePayload.LastIndexOf('}'), """, "databaseProvider": "sqlServer" """));
        var projectId = created.GetProperty("projectId").GetString()!;

        try
        {
            Assert.True(await SchemaExistsAsync(connectionString, projectId)); // 建案已建 schema

            await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");

            // schema 被 DROP、資料夾移除。
            Assert.False(await SchemaExistsAsync(connectionString, projectId));
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, projectId)));
        }
        finally
        {
            // 測試失敗時(delete 未成功)兜底清理,避免殘留 JET_ 暫時庫。
            await TempSqlServerProject.DropDatabaseAsync(connectionString, projectId);
        }
    }

    // ---- A/B/C 端到端流程驗收（驅動 GUI 按鈕背後的同一組 project.* actions） ----

    /// <summary>A：SQLite 建立→載入(currentStep=1,前端據此自動進入匯入)→在清單→刪除→移除。</summary>
    [Fact]
    public async Task Flow_Sqlite_CreateLoadListDelete()
    {
        using var host = new HandlerTestHost();

        // 建立（GUI 預設選項即 sqlite）。
        var created = await host.DispatchAsync("project.create", CreatePayload);
        Assert.True(created.GetProperty("ok").GetBoolean());
        var projectId = created.GetProperty("projectId").GetString()!;
        Assert.True(File.Exists(Path.Combine(host.ProjectsRoot, projectId, "jet.db")));

        // 載入：currentStep=1 → 前端 applyLoadedProject 映射到「匯入」步驟（自動前進，不停在建立畫面）。
        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Equal(1, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
        Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());

        // 在清單、provider 標籤正確。
        var before = await host.DispatchAsync("project.list");
        var row = SingleProject(before, projectId);
        Assert.Equal("sqlite", row.GetProperty("databaseProvider").GetString());

        // 刪除 → 立即從清單移除、資料夾消失。
        await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, projectId)));
        Assert.False(ProjectInList(await host.DispatchAsync("project.list"), projectId));
    }

    /// <summary>B：SQL Server 建立(建 schema)→載入(currentStep=1)→在清單→刪除(DROP SCHEMA + 移除)。</summary>
    [SqlServerFact]
    public async Task Flow_SqlServer_CreateLoadListDelete_DropsDatabase()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        var created = await host.DispatchAsync(
            "project.create",
            CreatePayload.Insert(CreatePayload.LastIndexOf('}'), """, "databaseProvider": "sqlServer" """));
        var projectId = created.GetProperty("projectId").GetString()!;

        try
        {
            // 建立即在共用單庫建該專案 schema（連線已設定 → EnsureCreated 成功，不再卡在建立步驟）。
            Assert.True(created.GetProperty("ok").GetBoolean());
            Assert.True(await SchemaExistsAsync(connectionString, projectId));

            var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
            Assert.Equal(1, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
            Assert.Equal("sqlServer", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());

            var row = SingleProject(await host.DispatchAsync("project.list"), projectId);
            Assert.Equal("sqlServer", row.GetProperty("databaseProvider").GetString());

            // 刪除 → 該專案 schema 從共用單庫消失、清單移除、資料夾消失。
            await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");
            Assert.False(await SchemaExistsAsync(connectionString, projectId));
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, projectId)));
            Assert.False(ProjectInList(await host.DispatchAsync("project.list"), projectId));
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, projectId);
        }
    }

    /// <summary>C：SQLite 與 SQL Server 專案並存時，清單各自顯示正確 provider 標籤。</summary>
    [SqlServerFact]
    public async Task Flow_Mixed_ListShowsBothProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        var sqlite = await host.DispatchAsync(
            "project.create", CreatePayload.Replace("ENG-2024-001", "SQLITE-001"));
        var sqliteId = sqlite.GetProperty("projectId").GetString()!;

        var sqlServer = await host.DispatchAsync(
            "project.create",
            CreatePayload.Replace("ENG-2024-001", "SQLSRV-001")
                .Insert(CreatePayload.Replace("ENG-2024-001", "SQLSRV-001").LastIndexOf('}'),
                    """, "databaseProvider": "sqlServer" """));
        var sqlServerId = sqlServer.GetProperty("projectId").GetString()!;

        try
        {
            var list = await host.DispatchAsync("project.list");
            Assert.Equal("sqlite", SingleProject(list, sqliteId).GetProperty("databaseProvider").GetString());
            Assert.Equal("sqlServer", SingleProject(list, sqlServerId).GetProperty("databaseProvider").GetString());
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, sqlServerId);
        }
    }


    [Fact]
    public async Task Create_UnsupportedDatabaseProvider_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create",
                CreatePayload.Insert(CreatePayload.LastIndexOf('}'), """, "databaseProvider": "postgres" """)));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("未支援的 databaseProvider 'postgres'", ex.Message);
    }

    private static JsonElement SingleProject(JsonElement listResponse, string projectId)
    {
        var match = listResponse.GetProperty("projects").EnumerateArray()
            .Where(p => p.GetProperty("projectId").GetString() == projectId)
            .ToList();
        Assert.Single(match);
        return match[0];
    }

    private static bool ProjectInList(JsonElement listResponse, string projectId)
    {
        return listResponse.GetProperty("projects").EnumerateArray()
            .Any(p => p.GetProperty("projectId").GetString() == projectId);
    }

    // schema-per-project 模型下,共用單庫即 AppCompositionRoot 的安全預設 JET_DEV(測試 host 未設 Sql:Database)。
    private const string SingleDatabaseName = "JET_DEV";

    /// <summary>
    /// 該專案衍生的 schema 是否存在於共用單庫(SCHEMA_ID;schema 名以參數綁定)。
    /// schema-per-project 模型:建案建 schema、刪案刪 schema,故「存在性」改以 schema 維度斷言(取代 per-DB 的 DB_ID)。
    /// </summary>
    private static async Task<bool> SchemaExistsAsync(string baseConnectionString, string projectId)
    {
        var schema = SqlServerProjectSchema.For(projectId);
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDatabaseName }.ConnectionString);
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
        query.Parameters.AddWithValue("@s", schema);
        return Convert.ToInt32(await query.ExecuteScalarAsync()) == 1;
    }
}
