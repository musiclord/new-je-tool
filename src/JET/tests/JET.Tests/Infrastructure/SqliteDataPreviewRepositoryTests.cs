using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SqliteDataPreviewRepository 的兩個正準目錄檢視（dateDimension / schemaOverview）對真 SQLite。
/// oracle：dateDimension 用固定假日／補班 fixture 鎖身分與值；schemaOverview 由 JetSchemaCatalog
/// 驅動，鎖「恰為 DataView+StructureOnly、且不含任一 Hidden」。
/// </summary>
public sealed class SqliteDataPreviewRepositoryTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempProjectRoot _root = new();
        private readonly JetProjectFolder _folder;

        public Fixture()
        {
            _folder = new JetProjectFolder(_root.Path);
            Database = new JetProjectDatabase(_folder);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_folder.GetProjectDirectory(ProjectId));
            Calendar = new SqliteCalendarStore(Database);
            Repository = new SqliteDataPreviewRepository(Database);
        }

        public string ProjectId { get; }

        public JetProjectDatabase Database { get; }

        public SqliteCalendarStore Calendar { get; }

        public SqliteDataPreviewRepository Repository { get; }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task GetPreviewAsync_DateDimension_ReturnsImportedHolidayAndMakeupDaysSortedByDate()
    {
        using var fixture = new Fixture();

        // 固定 fixture：2 假日（含一筆有名稱）+ 1 補班；刻意亂序匯入以驗 ORDER BY date。
        await fixture.Calendar.ReplaceDaysAsync(
            fixture.ProjectId,
            CalendarDayType.Holiday,
            [new CalendarDayEntry("2025-10-10", "國慶日"), new CalendarDayEntry("2025-01-01", null)],
            CancellationToken.None);
        await fixture.Calendar.ReplaceDaysAsync(
            fixture.ProjectId,
            CalendarDayType.Makeup,
            [new CalendarDayEntry("2025-02-08", "補班")],
            CancellationToken.None);

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.DateDimension,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(["date", "dayType", "dayName"], result.Columns);
        Assert.Equal(3, result.TotalCount);
        Assert.Null(result.Stats);

        // 依 date 升冪、逐列身分（date / day_type 原值 / day_name；缺名 → null）。
        Assert.Equal(["2025-01-01", "holiday", null], result.Rows[0]);
        Assert.Equal(["2025-02-08", "makeup", "補班"], result.Rows[1]);
        Assert.Equal(["2025-10-10", "holiday", "國慶日"], result.Rows[2]);
    }

    [Fact]
    public async Task GetPreviewAsync_DateDimension_EmptyReturnsZeroCountNotError()
    {
        using var fixture = new Fixture();
        await fixture.Database.EnsureCreatedAsync(fixture.ProjectId, CancellationToken.None);

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.DateDimension,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalCount);
        Assert.Null(result.Stats);
    }

    [Fact]
    public async Task GetPreviewAsync_SchemaOverview_ListsExactlyCatalogExposedEntries()
    {
        using var fixture = new Fixture();

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.SchemaOverview,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(["canonicalName", "physicalName", "layer", "audience", "browsable"], result.Columns);
        Assert.Null(result.Stats);

        // 列 ≡ catalog 的 DataView + StructureOnly（Hidden 排除）；身分以正準名鎖定。
        var expectedCanonical = JetSchemaCatalog.All
            .Where(e => e.Audience is SchemaAudience.DataView or SchemaAudience.StructureOnly)
            .Select(e => (string?)e.CanonicalName)
            .ToList();

        var actualCanonical = result.Rows.Select(r => r[0]).ToList();
        Assert.Equal(expectedCanonical, actualCanonical);
        Assert.Equal(expectedCanonical.Count, result.TotalCount);

        // 正向：曝光表確實在（含一張 DataView、一張 StructureOnly）。
        Assert.Contains("JE_PBC", actualCanonical);
        Assert.Contains("VALIDATION_OVERVIEW", actualCanonical);

        // 負向：Hidden 一張都不得出現（gl_control_total / app_message_log / schema_info / *_raw_row 暫存）。
        Assert.DoesNotContain("GL_CONTROL_TOTAL", actualCanonical);
        Assert.DoesNotContain("APP_MESSAGE_LOG", actualCanonical);
        Assert.DoesNotContain("SCHEMA_INFO", actualCanonical);
        Assert.DoesNotContain("ACCOUNT_MAPPING_PBC", actualCanonical);
        Assert.DoesNotContain("AUTHORIZED_PREPARER_PBC", actualCanonical);

        // 同時鎖實體名不外洩 Hidden、且 browsable 與 audience 一致。
        var physicalNames = result.Rows.Select(r => r[1]).ToList();
        Assert.DoesNotContain("gl_control_total", physicalNames);
        Assert.DoesNotContain("app_message_log", physicalNames);

        foreach (var row in result.Rows)
        {
            var audience = row[3];
            var browsable = row[4];
            Assert.Equal(audience == "DataView" ? "是" : "—", browsable);
        }
    }

    [Fact]
    public async Task GetPreviewAsync_SchemaOverview_RowForJeIsCanonicalMetadata()
    {
        using var fixture = new Fixture();

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.SchemaOverview,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        // JE 列：正準名 / 實體名 / 層 / 用途 / 可瀏覽（逐欄身分，對齊 catalog 宣告）。
        var je = result.Rows.Single(r => r[0] == "JE");
        Assert.Equal(["JE", "target_gl_entry", "Staging", "DataView", "是"], je);

        // StructureOnly 列：可瀏覽為「—」。
        var overview = result.Rows.Single(r => r[0] == "VALIDATION_OVERVIEW");
        Assert.Equal("—", overview[4]);
        Assert.Equal("StructureOnly", overview[3]);
    }
}
