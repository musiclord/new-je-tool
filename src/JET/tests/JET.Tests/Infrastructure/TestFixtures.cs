using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

namespace JET.Tests.Infrastructure;

internal static class TestWorkbookBuilder
{
    public static string WriteWorkbook(Action<IXLWorksheet> build)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-fixture-{Guid.NewGuid():N}.xlsx");

        using var workbook = new XLWorkbook();
        build(workbook.AddWorksheet("Sheet1"));
        workbook.SaveAs(path);

        return path;
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>temp 專案根目錄；Dispose 時清空 SQLite 連線池再遞迴刪除。</summary>
internal sealed class TempProjectRoot : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jet-projects-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
