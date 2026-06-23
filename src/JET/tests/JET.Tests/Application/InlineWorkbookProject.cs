using System.Text.Json;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

/// <summary>
/// 測試內自含的小型 GL 工作簿 → 正式 file-based 管線（create → import → flag 模式 commit）。
/// 欄名→mapping key 為固定慣例（與 DemoDataFactory 同名詞彙）；
/// 「借方旗標」存在時自動補 dcDebitCode="1"。測試逐列宣告資料，無 mystery guest。
/// </summary>
internal sealed class InlineGlWorkbookBuilder
{
    private static readonly IReadOnlyDictionary<string, string> ColumnToKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["傳票號碼"] = "docNum",
            ["傳票項次"] = "lineID",
            ["傳票日期"] = "postDate",
            ["核准日期"] = "docDate",
            ["科目代號"] = "accNum",
            ["科目名稱"] = "accName",
            ["摘要"] = "description",
            ["建立人員"] = "createBy",
            ["人工傳票"] = "manual",
            ["金額"] = "amount",
            ["借方旗標"] = "dcField"
        };

    private readonly List<object?[]> _rows = [];
    private string[] _columns = [];

    public InlineGlWorkbookBuilder WithColumns(params string[] columns)
    {
        _columns = columns;
        return this;
    }

    public InlineGlWorkbookBuilder AddRow(params object?[] cells)
    {
        _rows.Add(cells);
        return this;
    }

    internal string WriteWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            for (var c = 0; c < _columns.Length; c++)
            {
                sheet.Cell(1, c + 1).SetValue(_columns[c]);
            }

            for (var r = 0; r < _rows.Count; r++)
            {
                for (var c = 0; c < _rows[r].Length; c++)
                {
                    var cell = sheet.Cell(r + 2, c + 1);
                    switch (_rows[r][c])
                    {
                        case null:
                            break;
                        case int i:
                            cell.SetValue(i);
                            break;
                        case long l:
                            cell.SetValue(l);
                            break;
                        case decimal d:
                            cell.SetValue(d);
                            break;
                        case double dbl:
                            cell.SetValue(dbl);
                            break;
                        default:
                            cell.SetValue(_rows[r][c]!.ToString());
                            break;
                    }
                }
            }
        });
    }

    internal Dictionary<string, string> BuildFlagModeMapping()
    {
        var mapping = _columns
            .Where(ColumnToKey.ContainsKey)
            .ToDictionary(c => ColumnToKey[c], c => c, StringComparer.Ordinal);

        if (mapping.ContainsKey("dcField"))
        {
            mapping["dcDebitCode"] = "1";
        }

        return mapping;
    }
}

/// <summary>
/// 測試內自含的小型 TB 工作簿（科目代號／科目名稱／變動金額），以 direct 變動模式 commit。
/// 與 GL 並行宣告，供完整性測試（part(a) 控制總數、Not-in-TB）造對齊或刻意不齊的小母體。
/// 欄名固定:科目代號→accNum、科目名稱→accName、變動金額→amount（單一帶號變動欄）。
/// </summary>
internal sealed class InlineTbWorkbookBuilder
{
    private const string AccCodeColumn = "科目代號";
    private const string AccNameColumn = "科目名稱";
    private const string AmountColumn = "變動金額";

    private readonly List<(string? Code, string? Name, object Amount)> _rows = [];

    public InlineTbWorkbookBuilder AddRow(string? accountCode, string? accountName, object changeAmount)
    {
        _rows.Add((accountCode, accountName, changeAmount));
        return this;
    }

    internal bool HasRows => _rows.Count > 0;

    internal string WriteWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).SetValue(AccCodeColumn);
            sheet.Cell(1, 2).SetValue(AccNameColumn);
            sheet.Cell(1, 3).SetValue(AmountColumn);

            for (var r = 0; r < _rows.Count; r++)
            {
                var (code, name, amount) = _rows[r];
                if (code is not null) { sheet.Cell(r + 2, 1).SetValue(code); }
                if (name is not null) { sheet.Cell(r + 2, 2).SetValue(name); }
                switch (amount)
                {
                    case int i: sheet.Cell(r + 2, 3).SetValue(i); break;
                    case long l: sheet.Cell(r + 2, 3).SetValue(l); break;
                    case decimal d: sheet.Cell(r + 2, 3).SetValue(d); break;
                    case double dbl: sheet.Cell(r + 2, 3).SetValue(dbl); break;
                    default: sheet.Cell(r + 2, 3).SetValue(amount.ToString()); break;
                }
            }
        });
    }

    internal static Dictionary<string, string> BuildDirectModeMapping() =>
        new(StringComparer.Ordinal)
        {
            ["accNum"] = AccCodeColumn,
            ["accName"] = AccNameColumn,
            ["amount"] = AmountColumn
        };
}

internal static class InlineWorkbookProject
{
    public static async Task<string> SetupAsync(
        HandlerTestHost host,
        Action<InlineGlWorkbookBuilder> configure,
        string? lastPeriodStart = null,
        IReadOnlyList<string>? holidays = null,
        IReadOnlyList<string>? makeupDays = null,
        string databaseProvider = "sqlite",
        Action<InlineTbWorkbookBuilder>? configureTb = null)
    {
        var builder = new InlineGlWorkbookBuilder();
        configure(builder);

        var tbBuilder = new InlineTbWorkbookBuilder();
        configureTb?.Invoke(tbBuilder);

        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "INLINE-2025-001",
            entityName = "測試自含案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            lastPeriodStart,
            databaseProvider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;

        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath,
                fileName = "inline-gl.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }

        if (tbBuilder.HasRows)
        {
            var tbPath = tbBuilder.WriteWorkbook();
            try
            {
                await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
                {
                    filePath = tbPath,
                    fileName = "inline-tb.xlsx"
                }));
            }
            finally
            {
                TestWorkbookBuilder.Delete(tbPath);
            }
        }

        if (holidays is { Count: > 0 })
        {
            await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = holidays }));
        }

        if (makeupDays is { Count: > 0 })
        {
            await host.DispatchAsync("import.makeupDay", JsonSerializer.Serialize(new { dates = makeupDays }));
        }

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag"
        }));

        if (tbBuilder.HasRows)
        {
            await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
            {
                mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
                changeMode = "direct"
            }));
        }

        return projectId;
    }
}
