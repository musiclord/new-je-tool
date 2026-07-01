using ClosedXML.Excel;
using JET.Application;

namespace JET.Infrastructure;

/// <summary>
/// Demo xlsx 寫出器(dev fixture;guide §1.5.5 僅禁大型底稿)。demo 為確定性單例,
/// 故每種檔以 process 靜態 Lazy 記憶化:整個行程只寫一次、跨 test class 重用同檔
/// (唯讀重用安全;Lazy 序列化單次寫入,化解並行寫入互鎖)。檔名為契約值。
/// 科目代號等代碼欄寫文字 cell、日期寫 DateTime、旗標寫 1/0,驗證 import reader 型別正規化。
/// </summary>
public sealed class DemoWorkbookWriter : IDemoFileWriter
{
    private static readonly Lazy<DemoExportedFile> GlFile = new(() => WriteGlCore(DemoDataFactory.Create()));
    private static readonly Lazy<DemoExportedFile> TbFile = new(() => WriteTbCore(DemoDataFactory.Create()));
    private static readonly Lazy<DemoExportedFile> AccountMappingFile = new(() => WriteAccountMappingCore(DemoDataFactory.Create()));
    private static readonly Lazy<DemoExportedFile> AuthorizedPreparerFile = new(() => WriteAuthorizedPreparerCore(DemoDataFactory.Create()));

    public Task<DemoExportedFile> WriteGlAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(GlFile.Value);

    public Task<DemoExportedFile> WriteTbAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(TbFile.Value);

    public Task<DemoExportedFile> WriteAccountMappingAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(AccountMappingFile.Value);

    public Task<DemoExportedFile> WriteAuthorizedPreparerAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(AuthorizedPreparerFile.Value);

    private static DemoExportedFile WriteGlCore(DemoProjectData data)
    {
        var filePath = GetExportPath(data.GlFileName);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("GL");
        WriteHeader(worksheet, data.GlColumns);

        var rowNumber = 1;
        foreach (var row in data.GlRows)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = row.PostDate.ToDateTime(TimeOnly.MinValue);
            worksheet.Cell(rowNumber, 2).Value = row.VoucherNumber;
            worksheet.Cell(rowNumber, 3).Value = row.LineNumber;
            worksheet.Cell(rowNumber, 4).Value = row.AccountCode;
            worksheet.Cell(rowNumber, 5).Value = row.AccountName;
            worksheet.Cell(rowNumber, 6).Value = row.Description;
            worksheet.Cell(rowNumber, 7).Value = row.Amount;
            worksheet.Cell(rowNumber, 8).Value = row.IsDebit ? 1 : 0;
            worksheet.Cell(rowNumber, 9).Value = row.CreatedBy;
            worksheet.Cell(rowNumber, 10).Value = row.ApprovalDate.ToDateTime(TimeOnly.MinValue);
            worksheet.Cell(rowNumber, 11).Value = row.IsManual ? 1 : 0;
            worksheet.Cell(rowNumber, 12).Value = row.VoucherDate.ToDateTime(TimeOnly.MinValue);
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.GlFileName);
    }

    private static DemoExportedFile WriteTbCore(DemoProjectData data)
    {
        var filePath = GetExportPath(data.TbFileName);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("TB");
        WriteHeader(worksheet, data.TbColumns);

        var rowNumber = 1;
        foreach (var row in data.TbRows)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = row.AccountCode;
            worksheet.Cell(rowNumber, 2).Value = row.AccountName;
            worksheet.Cell(rowNumber, 3).Value = row.OpeningBalance;
            worksheet.Cell(rowNumber, 4).Value = row.DebitTotal;
            worksheet.Cell(rowNumber, 5).Value = row.CreditTotal;
            worksheet.Cell(rowNumber, 6).Value = row.ClosingBalance;
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.TbFileName);
    }

    private static DemoExportedFile WriteAccountMappingCore(DemoProjectData data)
    {
        var filePath = GetExportPath(data.AccountMappingFileName);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("AccountMapping");
        WriteHeader(worksheet, data.AccountMappingColumns);

        var rowNumber = 1;
        foreach (var row in data.AccountMappingRows)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = row.AccountCode;
            worksheet.Cell(rowNumber, 2).Value = row.AccountName;
            worksheet.Cell(rowNumber, 3).Value = row.Category;
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.AccountMappingFileName);
    }

    private static DemoExportedFile WriteAuthorizedPreparerCore(DemoProjectData data)
    {
        var filePath = GetExportPath(data.AuthorizedPreparerFileName);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("AuthorizedPreparer");
        WriteHeader(worksheet, data.AuthorizedPreparerColumns);

        var rowNumber = 1;
        foreach (var name in data.AuthorizedPreparers)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = name;
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.AuthorizedPreparerFileName);
    }

    private static void WriteHeader(IXLWorksheet worksheet, IReadOnlyList<string> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            worksheet.Cell(1, i + 1).Value = columns[i];
        }
    }

    private static string GetExportPath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "jet-demo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }
}
