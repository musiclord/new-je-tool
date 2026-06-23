using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class JsonFileProjectStoreTests
{
    private static ProjectDocument NewDocument(string? id = null, DateTimeOffset? createdUtc = null) => new(
        id ?? Guid.NewGuid().ToString("N"),
        "ENG-2024-001",
        "範例股份有限公司",
        "auditor01",
        "2024-01-01",
        "2024-12-31",
        "2024-12-31",
        ProjectDocument.DefaultMoneyScale,
        ProjectDocument.DefaultRoundingMode,
        createdUtc ?? DateTimeOffset.UtcNow,
        CurrentStep: 1,
        ProjectDocument.CurrentSchemaVersion);

    [Fact]
    public async Task CreateListFindRoundTrip()
    {
        using var root = new TempProjectRoot();
        var store = new JsonFileProjectStore(new JetProjectFolder(root.Path));

        var older = NewDocument(createdUtc: DateTimeOffset.UtcNow.AddHours(-1));
        var newer = NewDocument(createdUtc: DateTimeOffset.UtcNow);

        await store.CreateAsync(older, CancellationToken.None);
        await store.CreateAsync(newer, CancellationToken.None);

        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Equal(2, listed.Count);
        Assert.Equal(newer.ProjectId, listed[0].ProjectId); // createdUtc desc

        var found = await store.FindAsync(older.ProjectId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(older.ProjectCode, found.ProjectCode);
        Assert.Equal(older.EntityName, found.EntityName);
        Assert.Equal(older.MoneyScale, found.MoneyScale);
        Assert.Equal(older.RoundingMode, found.RoundingMode);
        Assert.Equal(older.PeriodStart, found.PeriodStart);
        Assert.Equal(older.LastAccountingPeriodDate, found.LastAccountingPeriodDate);
    }

    [Fact]
    public async Task CorruptJson_SkippedByList_NullFromFind()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);

        var good = NewDocument();
        await store.CreateAsync(good, CancellationToken.None);

        var corruptId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(corruptId));
        await File.WriteAllTextAsync(folder.GetProjectJsonPath(corruptId), "{ not valid json");

        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Single(listed);
        Assert.Equal(good.ProjectId, listed[0].ProjectId);

        Assert.Null(await store.FindAsync(corruptId, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidProjectId_FindReturnsNull()
    {
        using var root = new TempProjectRoot();
        var store = new JsonFileProjectStore(new JetProjectFolder(root.Path));

        Assert.Null(await store.FindAsync(@"..\..\evil", CancellationToken.None));
        Assert.Null(await store.FindAsync("not-a-guid", CancellationToken.None));
    }
}
