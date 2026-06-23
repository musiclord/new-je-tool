using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using Xunit.Abstractions;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 三顧案件真實資料 gated 煙霧測試（jet-testing skill §3「mystery-guest 唯一豁免」：
/// 明確標註的 RealDataSmokeTests，以環境變數／絕對路徑引用真實檔，檔案不存在時條件跳過，
/// 且**不把任何客戶資料內容寫入測試碼**——欄位配對只引用結構性欄名（標頭字面值），
/// 母體數字一律由 JET 後端實際輸出，不在測試碼硬編 oracle 期望值）。
///
/// 來源：JET_SANGU_DIR 環境變數指向的資料夾（缺省時退回開發者本機文件夾的固定路徑），
/// 內含 JE.xlsx（GL，17.5MB、單工作表「工作表1」、32 欄含重複標頭兩組）與 TB.xlsx
/// （試算表，8 欄、col2 標頭含內部空白）。兩檔皆機敏客戶資料，不入 repo。
///
/// 本測試以**正式 action 序列**驅動 production 後端（即 GUI bridge 呼叫的同一批 handler）：
/// project.create → import.gl.fromFile → import.tb.fromFile → mapping.commit.gl →
/// mapping.commit.tb → validate.run，產出 JET 的資料驗證輸出（不驅動 GUI）。
/// 量測值與 validate.run 回應以 ITestOutputHelper 印成 JSON，供 harness 擷取分析。
/// </summary>
public sealed class SanguRealDataSmokeTests(ITestOutputHelper output)
{
    /// <summary>
    /// 解析三顧案件資料夾：優先 JET_SANGU_DIR 環境變數（CI／他機可覆寫），缺省退回
    /// 開發者本機文件夾固定路徑。兩個來源都不存在或缺檔時回 null → 測試靜默跳過。
    /// 路徑字面值是「機器環境」而非「客戶資料內容」，不違反 §3 不入庫客戶資料的約束。
    /// </summary>
    private static string? FindSanguDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("JET_SANGU_DIR"),
            @"c:\Users\rich2\Documents\JET Test Case\Case-三顧",
        };

        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            if (File.Exists(Path.Combine(dir, "JE.xlsx")) && File.Exists(Path.Combine(dir, "TB.xlsx")))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// 全程 journey（jet-testing skill §3 journey 豁免：分階段斷言屬同一行為鏈，各階段附註解）。
    /// 這條路徑就是 GUI bridge 呼叫的同一批 handler；目的是讓 production 後端對三顧真實資料
    /// 跑出 validate.run，並把實際輸出（不硬斷言 oracle）以 JSON 印到測試輸出供 harness 擷取。
    /// 管線中途若 handler 報錯，例外會讓本測試失敗並帶出錯誤碼（不靜默吞）。
    /// </summary>
    [Fact]
    public async Task FullPipeline_SanguRealData_ImportMapProjectAndValidate()
    {
        var dir = FindSanguDirectory();
        if (dir is null)
        {
            output.WriteLine("SKIPPED：未設 JET_SANGU_DIR 且本機固定路徑無 JE.xlsx/TB.xlsx。");
            return; // gated 跳過：缺真實檔即不執行（非失敗）
        }

        var glPath = Path.Combine(dir, "JE.xlsx");
        var tbPath = Path.Combine(dir, "TB.xlsx");

        using var host = new HandlerTestHost();

        // 階段 1：建立 sqlite 暫存專案（TempProjectRoot；Dispose 時清連線池並遞迴刪除）。
        // 會計期間取自舊工具留痕（傳票日期全在 2025；非客戶機密內容）。
        var created = await host.DispatchAsync(
            "project.create",
            """
            {
              "projectCode": "SANGU-SMOKE",
              "entityName": "三顧真實資料煙霧測試",
              "operatorId": "smoke",
              "periodStart": "2025-01-01",
              "periodEnd": "2025-12-31",
              "lastPeriodStart": "2025-12-31"
            }
            """);
        var projectId = created.GetProperty("projectId").GetString()!;

        // 階段 2：GL 串流匯入（單工作表，replace）。payload 不帶 rows；rowCount/columns 由後端回。
        var glImport = await host.DispatchAsync(
            "import.gl.fromFile",
            JsonSerializer.Serialize(new { filePath = glPath, sheetName = "工作表1", mode = "replace" }));
        var glStagingRowCount = glImport.GetProperty("rowCount").GetInt32();
        var glColumns = glImport.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetString()!).ToList();
        output.WriteLine($"GL staging rowCount={glStagingRowCount}; columns={glColumns.Count}");

        // 階段 3：TB 串流匯入（單工作表，replace）。
        var tbImport = await host.DispatchAsync(
            "import.tb.fromFile",
            JsonSerializer.Serialize(new { filePath = tbPath, sheetName = "工作表1", mode = "replace" }));
        var tbStagingRowCount = tbImport.GetProperty("rowCount").GetInt32();
        output.WriteLine($"TB staging rowCount={tbStagingRowCount}");

        // 階段 4：GL DualAmount 配對 → 全母體投影。**採 GUI 最自然順序：先 commit GL、後 commit TB。**
        // 2026-06-22 稽核前，後續的 TB commit 會走 RuleRunResultReset 連帶清掉 GL 落地的 gl_control_total，
        // 使 validate.run 的 completeness.partA 變全 null；失效範圍收斂後（RuleRunResultReset 不再清
        // gl_control_total，只屬 GL 投影），此自然順序下 part(a) 控制總數仍存活到 validate.run。本煙霧測試
        // 即以此順序在真實 12 萬列資料上端到端驗證該修正（單元守門見 SqliteRepositoryTests.TbProjection_DoesNotClearGlControlTotal）。
        // 配對只引用結構性欄名（標頭字面值，scope:data 結論）：
        //   description → 摘要_2（重複標頭去重產物；真正有內容的摘要是第二個。第一個「摘要」整欄空，
        //                 若配到會全列誤命中 blank_description——這是 _2 字尾存在的理由）。
        //   debit/credit → 本幣借方金額/本幣貸方金額（**列層**金額，非「本幣借/貸方總金額」總額欄）。
        var glCommit = await host.DispatchAsync(
            "mapping.commit.gl",
            """
            {
              "mapping": {
                "docNum": "傳票編號",
                "postDate": "傳票日期",
                "docDate": "確認日期",
                "accNum": "會計科目",
                "accName": "科目名稱",
                "description": "摘要_2",
                "createBy": "登錄人員",
                "debitAmount": "本幣借方金額",
                "creditAmount": "本幣貸方金額"
              },
              "amountMode": "dual"
            }
            """);
        var glProjectedRowCount = glCommit.GetProperty("projectedRowCount").GetInt32();
        output.WriteLine($"GL projectedRowCount={glProjectedRowCount}");

        // 階段 5：TB DebitCredit 配對（本案無單一變動金額欄，只能用 本期借方−本期貸方）→ 投影。
        // accName 取含內部空白的字面值「項        目」（normalizer 只 trim 前後、保留內部空白）。
        var tbCommit = await host.DispatchAsync(
            "mapping.commit.tb",
            """
            {
              "mapping": {
                "accNum": "科目編號",
                "accName": "項        目",
                "debitAmt": "本期借方金額",
                "creditAmt": "本期貸方金額"
              },
              "changeMode": "debitCredit"
            }
            """);
        var tbProjectedRowCount = tbCommit.GetProperty("projectedRowCount").GetInt32();
        output.WriteLine($"TB projectedRowCount={tbProjectedRowCount}");

        // 階段 6：執行資料驗證（GUI bridge 同一 handler）。完整 response 印成 JSON。
        var validate = await host.DispatchAsync("validate.run");
        var validateJson = JsonSerializer.Serialize(validate, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        output.WriteLine("=== validate.run RESPONSE (JET 實際輸出) ===");
        output.WriteLine(validateJson);

        // 階段 7：直接讀 DB 取 scaled BIGINT 總額與 MoneyScale 保真佐證（不經 MoneyScaling）。
        // 這些是「JET 實際 DB 狀態」的觀察值，仍非 oracle 硬斷言。
        var dbObservations = await ReadDbObservationsAsync(host.ProjectsRoot, projectId);
        output.WriteLine("=== DB OBSERVATIONS ===");
        output.WriteLine(JsonSerializer.Serialize(dbObservations, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        // 結構化機讀區塊：harness 以這兩個 marker 之間的單行 JSON 擷取所有需要回填 schema 的數據。
        // 不硬斷言 oracle 數字——只把 JET 的實際輸出彙整成可解析載荷。
        var machine = BuildMachineReadable(
            projectId,
            glStagingRowCount, tbStagingRowCount,
            glProjectedRowCount, tbProjectedRowCount,
            glColumns,
            validate,
            dbObservations);
        output.WriteLine("===JET_SMOKE_JSON_BEGIN===");
        output.WriteLine(JsonSerializer.Serialize(machine, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        output.WriteLine("===JET_SMOKE_JSON_END===");

        // 唯一的「弱」結構斷言：管線確實走到底、產出了 result run，且 staging→target 列數守恆。
        // 這不是 oracle 比對，只是「煙霧測試確有點燃」的存在性把關（值＋身分由上方 JSON 留證）。
        Assert.Equal(glStagingRowCount, glProjectedRowCount);
        Assert.Equal(tbStagingRowCount, tbProjectedRowCount);
        Assert.True(
            validate.GetProperty("resultRef").GetProperty("runId").GetString()!.Length == 32,
            "validate.run 應回 32-hex runId（已落地 result_rule_run）");
    }

    /// <summary>
    /// 直接對專案 sqlite 連線讀 scaled 總額與 MoneyScale 保真佐證。沿用 PbcRealDataSmokeTests
    /// 的連線取法（JetProjectDatabase + JetProjectFolder）。回傳供測試輸出與機讀 JSON 兩用。
    /// </summary>
    private static async Task<DbObservations> ReadDbObservationsAsync(string projectsRoot, string projectId)
    {
        var database = new JetProjectDatabase(new JetProjectFolder(projectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        long targetDebitScaled, targetCreditScaled, targetNetScaled;
        long targetGlRows;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT COALESCE(SUM(debit_amount_scaled), 0),
                       COALESCE(SUM(credit_amount_scaled), 0),
                       COALESCE(SUM(amount_scaled), 0),
                       COUNT(*)
                FROM target_gl_entry;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            targetDebitScaled = reader.GetInt64(0);
            targetCreditScaled = reader.GetInt64(1);
            targetNetScaled = reader.GetInt64(2);
            targetGlRows = reader.GetInt64(3);
        }

        // gl_control_total（投影時落地的控制總數，part(a) 的來源）。
        long ctSourceRows = -1, ctTargetRows = -1, ctDebitScaled = -1, ctCreditScaled = -1;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT source_row_count, target_row_count, target_debit_scaled, target_credit_scaled
                FROM gl_control_total WHERE singleton = 1;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                ctSourceRows = reader.GetInt64(0);
                ctTargetRows = reader.GetInt64(1);
                ctDebitScaled = reader.GetInt64(2);
                ctCreditScaled = reader.GetInt64(3);
            }
        }

        long targetTbRows;
        long tbChangeScaled;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COUNT(*), COALESCE(SUM(change_amount_scaled), 0) FROM target_tb_balance;";
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            targetTbRows = reader.GetInt64(0);
            tbChangeScaled = reader.GetInt64(1);
        }

        // MoneyScale 保真佐證：scaled 值是否整除 MoneyScale（10000）。
        // 三顧 GL/TB 金額皆整數元，×10000 後末四位應全為 0 → 縮放無取整事件。
        long glNonExactScale, tbNonExactScale;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM target_gl_entry WHERE amount_scaled % 10000 <> 0;";
            glNonExactScale = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM target_tb_balance WHERE change_amount_scaled % 10000 <> 0;";
            tbNonExactScale = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        // INF 抽樣落地筆數（最近一次 validate.run）。
        long infSampleRows;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM result_inf_sampling_test_sample;";
            infSampleRows = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        return new DbObservations(
            targetGlRows, targetDebitScaled, targetCreditScaled, targetNetScaled,
            ctSourceRows, ctTargetRows, ctDebitScaled, ctCreditScaled,
            targetTbRows, tbChangeScaled,
            glNonExactScale, tbNonExactScale, infSampleRows);
    }

    private sealed record DbObservations(
        long TargetGlRows,
        long TargetDebitScaled,
        long TargetCreditScaled,
        long TargetNetScaled,
        long ControlSourceRows,
        long ControlTargetRows,
        long ControlDebitScaled,
        long ControlCreditScaled,
        long TargetTbRows,
        long TbChangeScaled,
        long GlNonExactScaleRows,
        long TbNonExactScaleRows,
        long InfSampleRows);

    /// <summary>
    /// 把 validate.run 回應與 DB 觀察值彙整成單一機讀物件（harness 擷取後回填 StructuredOutput）。
    /// 全數取自 JET 實際輸出，無任何硬編 oracle。
    /// </summary>
    private static object BuildMachineReadable(
        string projectId,
        int glStagingRows, int tbStagingRows,
        int glProjectedRows, int tbProjectedRows,
        IReadOnlyList<string> glColumns,
        JsonElement validate,
        DbObservations db)
    {
        var stats = validate.GetProperty("stats");
        var completeness = validate.GetProperty("completenessTest");
        var partA = completeness.GetProperty("partA");
        var docBalance = validate.GetProperty("docBalanceTest");
        var inf = validate.GetProperty("infSamplingTest");
        var nullRecords = validate.GetProperty("nullRecordsTest");

        // 完整性差異明細（有界 ≤50，衍生顯示樣本）逐筆取出 → mismatchDetail。
        var diffAccounts = completeness.GetProperty("diffAccounts").EnumerateArray()
            .Select(a => new
            {
                accountCode = a.GetProperty("accountCode").GetString(),
                accountName = a.GetProperty("accountName").GetString(),
                tbAmount = NumOrNull(a, "tbAmount"),
                glAmount = NumOrNull(a, "glAmount"),
                diff = NumOrNull(a, "diff"),
                notInTb = a.GetProperty("notInTb").GetBoolean(),
            })
            .ToList();

        return new
        {
            projectId,
            staging = new { glStagingRows, tbStagingRows },
            projected = new { glProjectedRows, tbProjectedRows },
            glBatchColumnCount = glColumns.Count,
            stats = new
            {
                glRowCount = stats.GetProperty("glRowCount").GetInt64(),
                voucherCount = stats.GetProperty("voucherCount").GetInt64(),
                totalDebit = stats.GetProperty("totalDebit").GetDecimal(),
                totalCredit = stats.GetProperty("totalCredit").GetDecimal(),
                net = stats.GetProperty("net").GetDecimal(),
            },
            completeness = new
            {
                status = completeness.GetProperty("status").GetString(),
                naReason = StrOrNull(completeness, "naReason"),
                diffAccountCount = completeness.GetProperty("diffAccountCount").GetInt64(),
                partA = new
                {
                    // na 形狀（尚未投影或控制總數被失效）時各鍵齊備、值為 null/false。
                    sourceRowCount = IntOrNull(partA, "sourceRowCount"),
                    targetRowCount = IntOrNull(partA, "targetRowCount"),
                    totalDebit = NumOrNull(partA, "totalDebit"),
                    totalCredit = NumOrNull(partA, "totalCredit"),
                    rowCountMatch = partA.GetProperty("rowCountMatch").GetBoolean(),
                    amountMatch = partA.GetProperty("amountMatch").GetBoolean(),
                },
                diffAccounts,
            },
            docBalance = new
            {
                status = docBalance.GetProperty("status").GetString(),
                unbalancedDocumentCount = docBalance.GetProperty("unbalancedDocumentCount").GetInt64(),
            },
            infSampling = new
            {
                status = inf.GetProperty("status").GetString(),
                sampleSize = inf.GetProperty("sampleSize").GetInt32(),
                seed = inf.GetProperty("seed").GetInt64(),
                persistedSampleRows = db.InfSampleRows,
            },
            nullRecords = new
            {
                status = nullRecords.GetProperty("status").GetString(),
                nullAccountCount = nullRecords.GetProperty("nullAccountCount").GetInt64(),
                nullDocumentCount = nullRecords.GetProperty("nullDocumentCount").GetInt64(),
                nullDescriptionCount = nullRecords.GetProperty("nullDescriptionCount").GetInt64(),
                outOfRangeDateCount = nullRecords.GetProperty("outOfRangeDateCount").GetInt64(),
            },
            db = new
            {
                targetGlRows = db.TargetGlRows,
                targetDebitScaled = db.TargetDebitScaled,
                targetCreditScaled = db.TargetCreditScaled,
                targetNetScaled = db.TargetNetScaled,
                controlSourceRows = db.ControlSourceRows,
                controlTargetRows = db.ControlTargetRows,
                controlDebitScaled = db.ControlDebitScaled,
                controlCreditScaled = db.ControlCreditScaled,
                targetTbRows = db.TargetTbRows,
                tbChangeScaled = db.TbChangeScaled,
                glNonExactScaleRows = db.GlNonExactScaleRows,
                tbNonExactScaleRows = db.TbNonExactScaleRows,
                moneyScale = 10000,
            },
        };
    }

    // na 形狀的 partA / 明細欄位可能是 JSON null；以下 helper 容忍 null 不丟例外。
    private static long? IntOrNull(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static decimal? NumOrNull(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static string? StrOrNull(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
