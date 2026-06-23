using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 匯出底稿(WorkingPaper).xlsx 寫出器。<see cref="IWorkpaperWriter"/> 的 deep module 實作:
/// 對外只 <see cref="WriteAsync"/>,內部隱藏全部 OpenXML 細節與資料表 keyset 串流。
///
/// 為什麼 SAX(OpenXmlPartWriter)、不用 ClosedXML:
///   真實母體達百萬列(實務見過 ~140 萬列)。ClosedXML(及 OpenXML DOM)要把整個 worksheet
///   建成記憶體物件樹才落地,會 OutOfMemory;SAX 是 forward-only、逐元素串流寫,記憶體有界。
///   因此 ClosedXML 僅限 dev fixture(DemoWorkbookWriter),底稿寫出鐵律走 SAX。
///
/// 為什麼 inline string、不用 sharedStrings:
///   sharedStrings 需要一張全域字串表(理想上要先看完所有字串才能去重),與 forward-only 串流相斥;
///   inline string 讓每個 cell 自帶文字、一次寫完不回頭,正是串流情境該用的形式。
///
/// 元素順序的硬約束(ISO/IEC 29500 CT_Worksheet)由 <see cref="SheetWriter"/> 原語封裝:
///   worksheet 子序列為 …→ sheetData → … → mergeCells,即 mergeCells 必須在 sheetData 之後。
///   emitter 可任意順序呼叫 WriteRow / AddMerge;原語保證先串完整個 sheetData、關閉後才寫 mergeCells。
///
/// 資料表共用骨架(DRY 閾值=3,本 task step1 / step1-2 / step1-3 達標):
///   <see cref="WriteCommonHeader"/> 出 A1-A4(公司/期間/財報準備日/斜體說明,五表全有);
///   <see cref="EmitTableSheetAsync"/> 出「欄標列 + 逐列資料(列號內部累計)」;
///   <see cref="StreamRowsAsync"/> 把 keyset 分頁 repo 轉成不全載入的列序列。
///   step1-1(條件例外表)與 step1-3-1(條件表)結構不同,各自處理——條件由 orchestration guard 決定,非 god-switch。
///
/// step2/3/4/4-1(Task 4)高風險矩陣家族:
///   step2 可靠性逐頁 infSamplePage,借/貸兩欄直接用 DebitScaled/CreditScaled(brief 設計決策);
///   step3/4/4-1 的 C 欄集由情境決定——step4 用全 position、step4-1 只用「行層命中(rowHitCount>0)」的 position。
///   這是業務分支(對齊樣本的動態 schema):先以 scenarios 算出欄集 list(<see cref="TagColumnSet"/>),再逐列依該列
///   matchedPositions 是否含某 position 對映 Y——用 data structure 消特例,不寫「第幾欄特判」。
///   step4/4-1 逐頁串流,且每頁回的 matchedPositions 是「另一個 dict」(voucher 以 documentNumber、row 以 entry_id 對齊),
///   故另備 <see cref="StreamTaggedRowsAsync"/> 把「列 + 該列命中位置」配對成串流(沿用 keyset 不全載入)。
/// </summary>
public sealed class WorkpaperWriter(
    ICompletenessAccountPageRepository completenessAccounts,
    ICompletenessDiffPageRepository completenessDiffs,
    IDocBalancePageRepository docBalances,
    ICreatorSummaryExportRepository creatorSummaries,
    IInfSamplePageRepository infSamples,
    IFilterScenarioStore filterScenarios,
    ITagMatrixScenariosRepository tagMatrixCounts,
    ITagMatrixVoucherPageRepository tagMatrixVouchers,
    ITagMatrixRowPageRepository tagMatrixRows,
    IMappingStateStore mappingStates,
    ICalendarExportRepository calendarDays,
    IAccountMappingExportRepository accountMappings) : IWorkpaperWriter
{
    /// <summary>
    /// 匯出底稿全部工作表名(對齊樣本順序;OpenSheet 與 SelectedSheets 過濾共用,避免字面字串重複)。
    /// step1-1 / step1-3-1 仍是「條件表」(各自的 emitter guard 決定有無例外/差異列才出),
    /// SelectedSheets 過濾疊加其上:選了且條件滿足才出。
    /// </summary>
    private static class Sheets_
    {
        public const string Cover = "資料預先整理之說明";
        public const string Intro = "JE WorkingPaper說明";
        public const string Step1 = "step1 完整性測試";
        public const string Step11 = "step1-1 借貸不平測試";
        public const string Step12 = "step1-2 分錄編製人員說明";
        public const string Step13 = "step1-3 完整性測試之差異說明";
        public const string Step131 = "step1-3-1完整性差異調節";
        public const string Step2 = "step2 可靠性測試";
        public const string Step3 = "step3 高風險條件彙總";
        public const string Step4 = "step4 符合高風險條件傳票";
        public const string Step41 = "step4-1 符合高風險條件傳票明細";
        public const string Step5 = "step5 財務報表關帳後調整之分錄";
        public const string FieldInfo = "自動化工具-檔案欄位資訊";
        public const string CalendarInfo = "自動化工具-假期假日資訊";
        public const string AccountMapping = "自動化工具-科目配對資訊";
    }

    public async Task<ExportStats> WriteAsync(Stream output, WorkpaperContext context, CancellationToken cancellationToken)
    {
        var startOffset = output.CanSeek ? output.Length : 0L;
        var sheetStats = new List<SheetStat>();

        // SelectedSheets 過濾(orchestration guard,非 emitter 內特例):null=全部;否則只出名稱在集合內者。
        // 用 set 而非逐張 if 比對,讓「選了哪些」成為資料而非控制流(Linus 好品味:資料結構消特例)。
        var selected = context.SelectedSheets is null
            ? null
            : new HashSet<string>(context.SelectedSheets, StringComparer.Ordinal);
        bool Include(string sheet) => selected is null || selected.Contains(sheet);

        // 傳 Stream overload:package 不擁有此 stream,Dispose 時 flush 但不關閉它(caller 自管生命週期)
        using (var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            // 樣式表(恆小、DOM):各表 cell 以 StyleIndex 指進這裡的 cellXfs
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = WorkpaperStyles.Build();
            stylesPart.Stylesheet.Save();

            // Sheets 容器先建好;SAX forward-only 只能 prepend,故 Sheet 元素用 DOM 逐張 append
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            if (Include(Sheets_.Cover)) { EmitCoverSheet(document, sheets, sheetStats, context, cancellationToken); }
            if (Include(Sheets_.Intro)) { EmitIntroSheet(document, sheets, sheetStats, cancellationToken); }

            // step1 家族(封面後、step2 前)。step1-3-1 是否 emit 另取決於有無 diff≠0 科目(emitter 內條件 guard)。
            if (Include(Sheets_.Step1)) { await EmitStep1Async(document, sheets, sheetStats, context, cancellationToken); }
            if (Include(Sheets_.Step11)) { await EmitStep11Async(document, sheets, sheetStats, context, cancellationToken); }
            if (Include(Sheets_.Step12)) { await EmitStep12Async(document, sheets, sheetStats, context, cancellationToken); }
            if (Include(Sheets_.Step13)) { await EmitStep13Async(document, sheets, sheetStats, context, cancellationToken); }
            if (Include(Sheets_.Step131)) { await EmitStep131Async(document, sheets, sheetStats, context, cancellationToken); }

            // step2(可靠性)+ step3/4/4-1(高風險矩陣);step3/4/4-1 共用一次 scenarios 欄集計算。
            if (Include(Sheets_.Step2)) { await EmitStep2Async(document, sheets, sheetStats, context, cancellationToken); }

            // tagColumns 僅在 step3/4/4-1 至少一張被選時才需載入(否則省去兩次查詢)。
            if (Include(Sheets_.Step3) || Include(Sheets_.Step4) || Include(Sheets_.Step41))
            {
                var tagColumns = await TagColumnSet.LoadAsync(filterScenarios, tagMatrixCounts, context.ProjectId, cancellationToken);
                if (Include(Sheets_.Step3)) { EmitStep3(document, sheets, sheetStats, context, tagColumns, cancellationToken); }
                if (Include(Sheets_.Step4)) { await EmitStep4Async(document, sheets, sheetStats, context, tagColumns, cancellationToken); }
                if (Include(Sheets_.Step41)) { await EmitStep41Async(document, sheets, sheetStats, context, tagColumns, cancellationToken); }
            }

            if (Include(Sheets_.Step5)) { EmitStep5Sheet(document, sheets, sheetStats, cancellationToken); }

            // 三張「自動化工具」參考資料表(對齊樣本順序,接在 step5 後)。三表結構各異 = 各自 emitter,不抽 god 模板。
            if (Include(Sheets_.FieldInfo)) { await EmitFieldInfoSheetAsync(document, sheets, sheetStats, context, cancellationToken); }
            if (Include(Sheets_.CalendarInfo)) { await EmitCalendarInfoSheetAsync(document, sheets, sheetStats, context, cancellationToken); }
            if (Include(Sheets_.AccountMapping)) { await EmitAccountMappingSheetAsync(document, sheets, sheetStats, context, cancellationToken); }

            workbookPart.Workbook.Save();
        }

        var bytesWritten = (output.CanSeek ? output.Length : startOffset) - startOffset;
        return new ExportStats(bytesWritten, sheetStats);
    }

    // ================= 封面 / 固定文字 emitter(一次性、固定文字;非資料表)=================

    /// <summary>
    /// 「資料預先整理之說明」:公司名 / 測試期間 / 固定說明 / CAATs 文件檔名。
    /// A6 檔名的 yyyymmdd 取 PeriodEnd 去掉非數字字元(樣本 PeriodEnd 形式不一:20241231 或 2024/12/31)。
    /// </summary>
    private static void EmitCoverSheet(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Cover);

        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"測試資料期間 :  {context.PeriodStart} ~ {context.PeriodEnd}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(5, [sheet.TextCell(5, 1,
            "請於下方加入針對JE Testing Tool所需資料預先整理之說明底稿(CAATs Document)。", WorkpaperStyles.Plain)]);
        sheet.WriteFixedRow(6, [sheet.TextCell(6, 1,
            $"請詳：{context.CompanyName}_CAATS_JE_WP_{DigitsOnly(context.PeriodEnd)}.docx", WorkpaperStyles.Plain)]);

        stats.Add(sheet.CloseAndSummarize(cancellationToken));
    }

    /// <summary>「JE WorkingPaper說明」:A1「說明：」標籤 + B1 整段 boilerplate(合併 B1:O1)。</summary>
    private static void EmitIntroSheet(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Intro);

        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "說明：", WorkpaperStyles.Bold),
            sheet.TextCell(1, 2, IntroBoilerplate, WorkpaperStyles.BoldWrap)
        ]);
        sheet.AddMerge("B1:O1");

        stats.Add(sheet.CloseAndSummarize(cancellationToken));
    }

    /// <summary>「step5 財務報表關帳後調整之分錄」:A1 黃底橫幅(合併 A1:R1);其餘手填留空。</summary>
    private static void EmitStep5Sheet(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step5);

        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, Step5Banner, WorkpaperStyles.YellowBanner)]);
        sheet.AddMerge("A1:R1");

        stats.Add(sheet.CloseAndSummarize(cancellationToken));
    }

    // ================= 三張「自動化工具」參考資料表 emitter(Task 5;結構各異,各自處理)=================

    /// <summary>
    /// 「自動化工具-檔案欄位資訊」(Field Mapping Info):A1 版本標籤 + TB 段 + GL 段。
    /// 每段列出已配對的來源欄(配對前=來源欄名 / 型態=logical key 的 kind→中文 / 配對後=正準名;
    /// 長度/小數 JET 未精確追蹤故留空)。GL 段尾列出衍生旗標(K_R條件/K_情境三四/K_情境五/K_情境八,對齊樣本)。
    ///
    /// 列號採樣本的固定錨點(TB 標頭 3 / 資料自 4;GL 標題 18 / 標頭 19 / 資料自 20):
    /// 樣本是固定版面,JET 的已配對欄數有界(TB ≤5、GL ≤9 具名鍵),不會溢出此版面;固定錨點讓表貼齊樣本。
    /// 為什麼配對後可能留空:來源欄已配對但該 logical key 無事務所正準名(如 GL docDate/voucherDate/jeSource),
    /// 不臆造名稱(no silent assumption)——列仍出(有來源欄+型態),只是 E 欄空。
    /// </summary>
    private async Task EmitFieldInfoSheetAsync(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.FieldInfo);

        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, "V.2019", WorkpaperStyles.Bold)]);
        sheet.AddMerge("A1:E1");

        var tbMapping = await mappingStates.FindAsync(context.ProjectId, DatasetKind.Tb, cancellationToken);
        WriteFieldSection(
            sheet, titleRow: 2, "TB檔案配對前後欄位對照表",
            ResolveTbFields(tbMapping?.Mapping));

        var glMapping = await mappingStates.FindAsync(context.ProjectId, DatasetKind.Gl, cancellationToken);
        var glDataRows = WriteFieldSection(
            sheet, titleRow: 18, "GL檔案配對前後欄位對照表",
            ResolveGlFields(glMapping?.Mapping));

        // GL 段尾:衍生旗標逐列列出(對齊樣本;皆文字型態,無正準名/長度/小數)。
        var flagRow = (uint)(20 + glDataRows);
        foreach (var flag in DerivedFieldFlags)
        {
            sheet.WriteFixedRow(flagRow, [sheet.TextCell(flagRow, 1, flag, WorkpaperStyles.Default)]);
            flagRow++;
        }

        stats.Add(sheet.CloseAndSummarize(cancellationToken));
    }

    /// <summary>
    /// 寫一段欄位對照(標題列 + 欄標列 + 資料列),回資料列數。TB/GL 兩段結構相同故共用
    /// (業務分支只在「哪些欄、什麼 kind、有無正準名」,已由 <paramref name="fields"/> data structure 表達)。
    /// 標題列下一列為欄標、再下一列起為資料(對齊樣本:TB 標題 2/標頭 3/資料 4;GL 標題 18/標頭 19/資料 20)。
    /// </summary>
    private static int WriteFieldSection(
        SheetWriter sheet, uint titleRow, string title, IReadOnlyList<FieldMappingLine> fields)
    {
        sheet.WriteFixedRow(titleRow, [sheet.TextCell(titleRow, 1, title, WorkpaperStyles.Bold)]);

        var headerRow = titleRow + 1;
        sheet.WriteFixedRow(headerRow,
        [
            sheet.TextCell(headerRow, 1, "配對前欄位名稱", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 2, "欄位型態", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 3, "文字長度", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 4, "小數位數", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 5, "配對後欄位名稱", WorkpaperStyles.Bold)
        ]);

        var dataRow = headerRow + 1;
        foreach (var field in fields)
        {
            sheet.WriteFixedRow(dataRow,
            [
                sheet.TextCell(dataRow, 1, field.SourceColumn),
                sheet.TextCell(dataRow, 2, field.KindLabel),
                sheet.BlankCell(dataRow, 3), // 文字長度:JET 未精確追蹤 → 留空
                sheet.BlankCell(dataRow, 4), // 小數位數:同上
                sheet.TextCell(dataRow, 5, field.CanonicalName ?? string.Empty)
            ]);
            dataRow++;
        }

        return fields.Count;
    }

    /// <summary>GL 已配對欄 → 欄位對照列:依 <see cref="GlMappingKeys.All"/> 穩定順序,kind 取自 <see cref="GlFieldWhitelist"/>。</summary>
    private static IReadOnlyList<FieldMappingLine> ResolveGlFields(IReadOnlyDictionary<string, string>? mapping)
    {
        if (mapping is null)
        {
            return [];
        }

        var lines = new List<FieldMappingLine>();
        foreach (var key in GlMappingKeys.All)
        {
            // 只列「資料欄」:TryResolve 成功者(白名單欄);amount-mode 機制鍵(manual/dcField/dcDebitCode)無 kind,非檔案欄,略過。
            if (mapping.TryGetValue(key, out var source) && !string.IsNullOrWhiteSpace(source)
                && GlFieldWhitelist.TryResolve(key, out var column))
            {
                lines.Add(new FieldMappingLine(
                    source, KindLabel(column.Kind), GlCanonicalNames.Gl.GetValueOrDefault(key)));
            }
        }

        return lines;
    }

    /// <summary>TB 已配對欄 → 欄位對照列:依 <see cref="TbMappingKeys.All"/> 穩定順序,kind 由 <see cref="TbFieldKind"/> 推。</summary>
    private static IReadOnlyList<FieldMappingLine> ResolveTbFields(IReadOnlyDictionary<string, string>? mapping)
    {
        if (mapping is null)
        {
            return [];
        }

        var lines = new List<FieldMappingLine>();
        foreach (var key in TbMappingKeys.All)
        {
            if (mapping.TryGetValue(key, out var source) && !string.IsNullOrWhiteSpace(source))
            {
                lines.Add(new FieldMappingLine(
                    source, KindLabel(TbFieldKind(key)), GlCanonicalNames.Tb.GetValueOrDefault(key)));
            }
        }

        return lines;
    }

    /// <summary>TB logical key → 欄位 kind(TB 無白名單;accNum/accName 文字,金額類數字)。</summary>
    private static GlFieldKind TbFieldKind(string key) => key switch
    {
        TbMappingKeys.AccNum or TbMappingKeys.AccName => GlFieldKind.Text,
        _ => GlFieldKind.Amount // amount / debitAmt / creditAmt
    };

    /// <summary>GlFieldKind → 樣本中文型態標籤。</summary>
    private static string KindLabel(GlFieldKind kind) => kind switch
    {
        GlFieldKind.Text => "文字型態",
        GlFieldKind.Date => "日期型態",
        GlFieldKind.Amount => "數字型態",
        _ => string.Empty
    };

    /// <summary>欄位對照的一列:配對前來源欄 + 中文型態標籤 + 配對後正準名(無則 null)。</summary>
    private readonly record struct FieldMappingLine(string SourceColumn, string KindLabel, string? CanonicalName);

    /// <summary>
    /// 「自動化工具-假期假日資訊」:固定週末表 + 假日表(calendar store holiday)+ 補班段(makeup)。
    /// 週末表為資料化常數(<see cref="WeekendRows"/>:Mon-Fri=N、Sat/Sun=Y),非逐列特判——
    /// 「WORKDAY=Y」反指「視為非工作日(週末)」是樣本既定語意,週末固定七天故以常數陣列表達。
    /// 假日/補班逐日列出(日數有界):日期由 yyyy-MM-dd 轉樣本顯示形式 yyyy/MM/dd;假日 IS_HOLIDAY 一律 Y。
    /// </summary>
    private async Task EmitCalendarInfoSheetAsync(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.CalendarInfo);

        // 週末表(第 1 列標頭 + 7 列固定;資料化常數,非特判)。
        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "DAYOFWEEK", WorkpaperStyles.Bold),
            sheet.TextCell(1, 2, "WORKDAY", WorkpaperStyles.Bold)
        ]);
        var weekendRow = 2u;
        foreach (var (dayName, workday) in WeekendRows)
        {
            sheet.WriteFixedRow(weekendRow,
            [
                sheet.TextCell(weekendRow, 1, dayName),
                sheet.TextCell(weekendRow, 2, workday)
            ]);
            weekendRow++;
        }

        // 假日表(空一列後標頭;對齊樣本第 10 列標頭、第 11 列起資料)。
        var holidays = await calendarDays.FetchDaysAsync(context.ProjectId, CalendarDayType.Holiday, cancellationToken);
        var holidayHeaderRow = weekendRow + 1; // 7 列週末(2..8)+ 1 空列 → 標頭在第 10 列
        sheet.WriteFixedRow(holidayHeaderRow,
        [
            sheet.TextCell(holidayHeaderRow, 1, "DATE_OF_HOLIDAY", WorkpaperStyles.Bold),
            sheet.TextCell(holidayHeaderRow, 2, "HOLIDAY_NAME", WorkpaperStyles.Bold),
            sheet.TextCell(holidayHeaderRow, 3, "IS_HOLIDAY", WorkpaperStyles.Bold)
        ]);

        var rowIndex = holidayHeaderRow + 1;
        long dataRows = 0;
        foreach (var day in holidays)
        {
            sheet.WriteRow(rowIndex,
            [
                sheet.TextCell(rowIndex, 1, DisplayDate(day.Date)),
                sheet.TextCell(rowIndex, 2, day.Name ?? string.Empty),
                sheet.TextCell(rowIndex, 3, "Y") // 假日表內一律 Y(對齊樣本)
            ]);
            rowIndex++;
            dataRows++;
        }

        // 補班段(空一列後標頭 + 逐日)。
        var makeups = await calendarDays.FetchDaysAsync(context.ProjectId, CalendarDayType.Makeup, cancellationToken);
        var makeupHeaderRow = rowIndex + 1;
        sheet.WriteFixedRow(makeupHeaderRow,
        [
            sheet.TextCell(makeupHeaderRow, 1, "DATE_OF_MAKEUPDAY", WorkpaperStyles.Bold),
            sheet.TextCell(makeupHeaderRow, 2, "MAKEUPDAY_DESC", WorkpaperStyles.Bold)
        ]);

        rowIndex = makeupHeaderRow + 1;
        foreach (var day in makeups)
        {
            sheet.WriteRow(rowIndex,
            [
                sheet.TextCell(rowIndex, 1, DisplayDate(day.Date)),
                sheet.TextCell(rowIndex, 2, day.Name ?? string.Empty)
            ]);
            rowIndex++;
            dataRows++;
        }

        stats.Add(sheet.CloseAndSummarizeWith(dataRows, cancellationToken));
    }

    /// <summary>
    /// 「自動化工具-科目配對資訊」:GL_NUMBER / GL_NAME / STANDARDIZED_ACCOUNT_NAME + 每科目列。
    /// **Not-in-TB**(在 GL 有、TB 無的科目)GL_NAME 寫字面「Not in TB」而非 account_name——
    /// 為什麼用字面值:對齊樣本(福懋此欄即固定字串「Not in TB」),標示「該科目在 GL 出現卻未配對到 TB 餘額」,
    /// 供審計員一眼辨識完整性缺口。判定來源是完整性 not_in_tb 集合(repo 以 ValidationSql.CompletenessDiffCte 為單一事實來源),
    /// emitter 只依旗標渲染(data-structure 對映,非逐列特判)。
    /// </summary>
    private async Task EmitAccountMappingSheetAsync(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.AccountMapping);

        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "GL_NUMBER", WorkpaperStyles.Bold),
            sheet.TextCell(1, 2, "GL_NAME", WorkpaperStyles.Bold),
            sheet.TextCell(1, 3, "STANDARDIZED_ACCOUNT_NAME", WorkpaperStyles.Bold)
        ]);

        var mappings = await accountMappings.FetchAllAsync(context.ProjectId, cancellationToken);
        var rowIndex = 2u;
        foreach (var account in mappings)
        {
            // Not-in-TB → GL_NAME 字面「Not in TB」;否則 account_name(可能本就空)。
            var glName = account.NotInTb ? "Not in TB" : account.AccountName ?? string.Empty;
            sheet.WriteRow(rowIndex,
            [
                sheet.TextCell(rowIndex, 1, account.AccountCode),
                sheet.TextCell(rowIndex, 2, glName),
                sheet.TextCell(rowIndex, 3, account.Category)
            ]);
            rowIndex++;
        }

        stats.Add(sheet.CloseAndSummarizeWith(mappings.Count, cancellationToken));
    }

    // ================= step1 家族 emitter =================

    /// <summary>
    /// 「step1 完整性測試」:A1-A4 共同表頭 + Step1 程序固定文字 + 結論 + 第 19 列欄標 +
    /// completenessAccounts **全科目逐頁**(科目編號/名稱/TB 變動(A)/GL 彙總(C)/差異(B)-(A))。
    /// 差異欄用 repo 的 DiffScaled(= tb_s - gl_s),三張完整性表(step1/1-3/1-3-1)同一定義不漂移。
    /// </summary>
    private async Task EmitStep1Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step1);
        WriteCommonHeader(sheet, context, "此處底稿係記錄JE測試母體的完整性");

        sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(7, [sheet.TextCell(7, 1, "評估母體完整性：", WorkpaperStyles.Bold)]);
        WriteConclusionRow(sheet, 15, Step1Conclusion);
        sheet.WriteFixedRow(17, [sheet.TextCell(17, 2, Step1ListNote, WorkpaperStyles.Bold)]);

        var headerCells = HeaderCells(sheet, 19,
        [
            "試算表科目\n編號", "試算表科目\n名稱", "試算表科目\n變動金額\n(A)",
            "該科目於會計分錄之\n本期借貸金額彙總\n(C)", "差異數\n(B)-(A)"
        ]);

        var rows = await EmitTableSheetAsync(sheet, 19, headerCells, firstDataRow: 20,
            StreamRowsAsync(
                (cursor, ct) => completenessAccounts.GetPageAsync(
                    context.ProjectId, context.MoneyScale, new PageRequest(cursor, PageRequest.DefaultPageSize), ct),
                acc => row => CompletenessRowCells(sheet, row, acc, context.MoneyScale),
                cancellationToken),
            cancellationToken);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    /// <summary>
    /// 「step1-1 借貸不平測試」:A1-A4 共同表頭 + Step1-1 程序固定文字 + 結論;
    /// **docBalancePage 有列才 emit 例外表**(條件 guard:逐頁串流,有列才寫欄標 + 列,無列只留結論文字)。
    /// </summary>
    private async Task EmitStep11Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step11);
        WriteCommonHeader(sheet, context, "此處底稿係記錄JE測試母體的是否有借貸不平情形");

        sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-1", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(7, [sheet.TextCell(7, 1, "評估個別傳票是否借貸不平：", WorkpaperStyles.Bold)]);
        WriteConclusionRow(sheet, 12, Step11Conclusion);

        // 條件例外表:第 14 列欄標、資料自 15 列。逐頁串流;無不平傳票時整段不出(只剩上面結論)。
        const uint headerRow = 14;
        const uint firstDataRow = 15;
        var headerCells = HeaderCells(sheet, headerRow,
            ["傳票號碼", "借方金額", "貸方金額", "借貸差額"]);

        var rows = await EmitConditionalTableAsync(sheet, headerRow, headerCells, firstDataRow,
            StreamRowsAsync(
                (cursor, ct) => docBalances.GetPageAsync(
                    context.ProjectId, context.MoneyScale, new PageRequest(cursor, PageRequest.DefaultPageSize), ct),
                doc => row => UnbalancedRowCells(sheet, row, doc, context.MoneyScale),
                cancellationToken),
            cancellationToken);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    /// <summary>
    /// 「step1-2 分錄編製人員說明」:A1-A4 共同表頭 + Step1-2 程序固定文字 + 第 11 列欄標 +
    /// FetchAllAsync 全名單(B 編製人員/D 傳票數/E 金額彙總自動;C 自動或人工、F 部門、G 職稱、H 說明留空)。
    /// 全名單基數有界(不分頁),但仍走列序列骨架與其他資料表一致。
    /// </summary>
    private async Task EmitStep12Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step12);
        WriteCommonHeader(sheet, context, "此處底稿係記錄JE測試母體的是否有借貸不平情形");

        sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-2", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(7, [sheet.TextCell(7, 1, "評估是否有不適合的分錄編製人員：", WorkpaperStyles.Bold)]);

        var headerCells = HeaderCells(sheet, 11,
        [
            "編製人員\n(來自JE測試母體)", "屬於自動或人工\n(若原資料無此判別欄位則留白)",
            "經手之傳票數目\n(來自JE測試母體)", "經手之傳票金額彙總\n(來自JE測試母體)",
            "部門", "職稱或職務", "是否為適當編製人員之說明"
        ]);

        var creators = await creatorSummaries.FetchAllAsync(context.ProjectId, cancellationToken);
        var rows = await EmitTableSheetAsync(sheet, 11, headerCells, firstDataRow: 12,
            ToRowStream(creators, c => row => CreatorRowCells(sheet, row, c, context.MoneyScale)),
            cancellationToken);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    /// <summary>
    /// 「step1-3 完整性測試之差異說明」:A1-A4 共同表頭 + Step1-3 程序固定文字 + 結論 + 第 16 列欄標 +
    /// completenessDiffs **WHERE diff≠0**(B 科目編號/C 名稱/D 差異金額自動;E 原因、F 調節、G 調節後差異留空)。
    /// </summary>
    private async Task EmitStep13Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step13);
        WriteCommonHeader(sheet, context, "此處底稿係記錄JE測試母體於完整性測試有部分科目出現差異的回應與理由");

        sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, "Step 1-3", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(7, [sheet.TextCell(7, 1, "評估於Step1完整性測試中有部分科目出現差異的原因：", WorkpaperStyles.Bold)]);
        WriteConclusionRow(sheet, 12, Step13Conclusion);
        sheet.WriteFixedRow(14, [sheet.TextCell(14, 2, "出現差異之個別科目說明：", WorkpaperStyles.Bold)]);

        var headerCells = HeaderCells(sheet, 16,
        [
            "試算表科目編號", "試算表科目名稱", "於Step1之差異金額",
            "差異原因之說明", "說明如何進行調節以降低該差異", "調節後之差異金額或剩餘差異之說明"
        ]);

        var rows = await EmitTableSheetAsync(sheet, 16, headerCells, firstDataRow: 17,
            StreamRowsAsync(
                (cursor, ct) => completenessDiffs.GetPageAsync(
                    context.ProjectId, context.MoneyScale, new PageRequest(cursor, PageRequest.DefaultPageSize), ct),
                acc => row => DiffExplanationRowCells(sheet, row, acc, context.MoneyScale),
                cancellationToken),
            cancellationToken);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    /// <summary>
    /// 「step1-3-1完整性差異調節」(**條件表:有 diff≠0 科目才 emit**):A1-C1 欄標 + diff≠0 科目列
    /// (科目編號/名稱/差異金額)+ A4-A6 固定調節說明文字(前期損益金額手填留空)。
    /// guard:先取第一頁,空(無 diff≠0)則整張表不產生(連 sheet 都不註冊)。
    /// </summary>
    private async Task EmitStep131Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        var firstPage = await completenessDiffs.GetPageAsync(
            context.ProjectId, context.MoneyScale, new PageRequest(null, PageRequest.DefaultPageSize), cancellationToken);
        if (firstPage.Rows.Count == 0)
        {
            return; // 無差異科目 → 條件表不出現(佰鴻樣本即此情形)
        }

        using var sheet = OpenSheet(document, sheets, Sheets_.Step131);

        // A1-C1 欄標(此表無 A1-A4 共同表頭,欄標即首列)
        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "試算表科目編號", WorkpaperStyles.Bold),
            sheet.TextCell(1, 2, "試算表科目名稱", WorkpaperStyles.Bold),
            sheet.TextCell(1, 3, "於Step1之差異金額", WorkpaperStyles.Bold)
        ]);

        // 差異科目列自第 2 列;沿用已取的第一頁,再續頁串流(不重取首頁)。欄標已手寫故 writeHeader:false。
        var rows = await EmitTableSheetAsync(sheet, headerRow: 1, headerCells: [], firstDataRow: 2,
            StreamRowsFromAsync(
                firstPage,
                cursor => completenessDiffs.GetPageAsync(
                    context.ProjectId, context.MoneyScale, new PageRequest(cursor, PageRequest.DefaultPageSize), cancellationToken),
                acc => row => ReconciliationRowCells(sheet, row, acc, context.MoneyScale),
                cancellationToken),
            cancellationToken,
            writeHeader: false);

        // A4-A6 固定調節說明,接在資料列之後(資料列 2..(1+rows),空一列再起);前期損益金額為手填,文字保留樣本字面但金額處留白。
        var noteRow = (uint)(1 + rows + 2); // = 末資料列(1+rows) + 1 空列 + 1
        sheet.WriteFixedRow(noteRow, [sheet.TextCell(noteRow, 1, Step131Note1, WorkpaperStyles.Default)]);
        sheet.WriteFixedRow(noteRow + 1, [sheet.TextCell(noteRow + 1, 1, Step131Note2, WorkpaperStyles.Default)]);
        sheet.WriteFixedRow(noteRow + 2, [sheet.TextCell(noteRow + 2, 1, Step131Note3, WorkpaperStyles.Default)]);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    // ================= step2 / step3 / step4 / step4-1 emitter(高風險矩陣家族,Task 4)=================

    /// <summary>
    /// 「step2 可靠性測試」:表頭跨 49-52 列(含多處合併,逐字對齊福懋樣本);資料第 53 列起,逐頁 infSamplePage。
    /// 借/貸兩欄直接用 InfSampleRow.DebitScaled / CreditScaled(brief 設計決策;scaled→顯示)。
    /// A 樣本序號為列序 1..N(emitter 自累計);J 來源欄(infSamplePage 無 source_module)、H 核准日、T 說明手填留空。
    /// </summary>
    private async Task EmitStep2Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step2);
        WriteSubstantiveHeader(sheet, context, "此Report將提供測試JE母體攸關資料元素可靠性之樣本");

        // 測試說明 + 測試程序固定文字(逐字對齊樣本;列號/欄/合併皆照樣本)。
        WriteFixedLines(sheet, Step2HeaderLines);
        foreach (var range in Step2HeaderMerges)
        {
            sheet.AddMerge(range);
        }

        // 第 49-52 列欄標(多層合併);資料第 53 列起。
        WriteStep2ColumnHeaders(sheet);

        var rows = await EmitTableSheetAsync(sheet, headerRow: 52, headerCells: [], firstDataRow: 53,
            StreamRowsAsync(
                (cursor, ct) => infSamples.GetPageAsync(
                    context.ProjectId, context.MoneyScale, new PageRequest(cursor, PageRequest.DefaultPageSize), ct),
                sample => row => Step2SampleCells(sheet, row, sample, context.MoneyScale),
                cancellationToken),
            cancellationToken,
            writeHeader: false);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    /// <summary>
    /// 「step3 高風險條件彙總」:固定測試說明區塊 + C 表(欄標第 18 列;資料第 19 列起,依 position 升冪):
    /// B 代號 C{position} / C 條件描述=情境 name / D 選擇此條件原因=情境 rationale / E 符合條件之傳票數=voucherHitCount。
    /// 情境清單(name/rationale/position)取自 scenario store、傳票命中數取自 tagMatrix counts,於 TagColumnSet 合併。
    /// </summary>
    private void EmitStep3(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, TagColumnSet tagColumns, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step3);
        WriteSubstantiveHeader(sheet, context, "證實測試 - 會計分錄及其他調整", omitSubtitle: true);

        WriteFixedLines(sheet, Step3HeaderLines);
        foreach (var range in Step3HeaderMerges)
        {
            sheet.AddMerge(range);
        }

        // 第 18 列欄標(B 欄無欄標,直接是代號;C/D/E 有欄標)。
        sheet.WriteFixedRow(18,
        [
            sheet.TextCell(18, 3, "高風險範圍條件", WorkpaperStyles.Bold),
            sheet.TextCell(18, 4, "選擇此篩選條件的原因", WorkpaperStyles.Bold),
            sheet.TextCell(18, 5, "符合條件之傳票數目", WorkpaperStyles.Bold)
        ]);

        var rowIndex = 19u;
        long dataRows = 0;
        foreach (var scenario in tagColumns.Scenarios)
        {
            sheet.WriteRow(rowIndex,
            [
                sheet.TextCell(rowIndex, 2, $"C{scenario.Position}"),
                sheet.TextCell(rowIndex, 3, scenario.Name),
                sheet.TextCell(rowIndex, 4, scenario.Rationale),
                sheet.NumberCell(rowIndex, 5, scenario.VoucherHitCount)
            ]);
            rowIndex++;
            dataRows++;
        }

        stats.Add(sheet.CloseAndSummarizeWith(dataRows, cancellationToken));
    }

    /// <summary>
    /// 「step4 符合高風險條件傳票」:第 11 列欄標(A 編號/B 傳票號碼/C 總帳日期/D 編製者/E 傳票總金額)
    /// + 動態 C1..CN 欄(欄集 = 全 position 升冪);第 12 列固定說明;資料第 13 列起,逐頁 tagMatrixVoucherPage。
    /// 每列以該傳票 matchedPositions(PositionsByDoc 對齊)含某 position 標 'Y' 否則空;P-U 手填留空。
    /// </summary>
    private async Task EmitStep4Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, TagColumnSet tagColumns, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step4);
        WriteSubstantiveHeader(sheet, context, "證實測試 - 會計分錄及其他調整", omitSubtitle: true);

        WriteFixedLines(sheet, Step4HeaderLines);
        foreach (var range in Step4HeaderMerges)
        {
            sheet.AddMerge(range);
        }

        // 第 11 列欄標:固定 A-E + 動態 C{pos}(全 position)+ 查核程序 A/B/C + 手填影響/索引欄標。
        var headerCells = new List<Cell>
        {
            sheet.TextCell(11, 1, "編號", WorkpaperStyles.Bold),
            sheet.TextCell(11, 2, "傳票號碼", WorkpaperStyles.Bold),
            sheet.TextCell(11, 3, "總帳日期", WorkpaperStyles.Bold),
            sheet.TextCell(11, 4, "編製者", WorkpaperStyles.Bold),
            sheet.TextCell(11, 5, "傳票總金額", WorkpaperStyles.Bold)
        };
        tagColumns.AppendAllPositionHeaders(sheet, headerCells, 11, suffix: string.Empty);
        sheet.WriteFixedRow(11, headerCells);

        // 第 12 列固定說明(逐字樣本);資料第 13 列起。
        sheet.WriteFixedRow(12, [sheet.TextCell(12, 1, Step4SelectionNote, WorkpaperStyles.Bold)]);

        var ordinal = 0L;
        var rows = await EmitTableSheetAsync(sheet, headerRow: 11, headerCells: [], firstDataRow: 13,
            StreamTaggedVoucherRowsAsync(
                context, tagColumns, () => ++ordinal, sheet, cancellationToken),
            cancellationToken,
            writeHeader: false);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    /// <summary>
    /// 「step4-1 符合高風險條件傳票明細」:第 5 列欄標(A-I 固定 JE 欄)+ 動態 C*_TAG 欄
    /// (欄集 = 只 rowHitCount>0 的 position 升冪;標頭 C{position}_TAG);資料第 6 列起,逐頁 tagMatrixRowPage。
    /// 每行以該行 matchedPositions(PositionsByEntry 以 index 對齊)含某 position 標 'Y' 否則空。
    /// </summary>
    private async Task EmitStep41Async(
        SpreadsheetDocument document, Sheets sheets, List<SheetStat> stats,
        WorkpaperContext context, TagColumnSet tagColumns, CancellationToken cancellationToken)
    {
        using var sheet = OpenSheet(document, sheets, Sheets_.Step41);

        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"財務報表期間 :  {DigitsOnly(context.PeriodStart)} ~ {DigitsOnly(context.PeriodEnd)}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1, "符合高風險範圍條件之傳票明細", WorkpaperStyles.Bold)]);

        // 第 5 列欄標:固定 A-I + 動態 C{pos}_TAG(只 rowHitCount>0)。
        var headerCells = new List<Cell>();
        var fixedLabels = new[]
        {
            "傳票號碼_JE", "傳票文件項次_JE_S", "總帳日期_JE", "傳票建立人員_JE", "傳票核准人員_JE",
            "會計科目編號_JE", "會計科目名稱_JE", "傳票金額_JE", "傳票摘要_JE"
        };
        for (var i = 0; i < fixedLabels.Length; i++)
        {
            headerCells.Add(sheet.TextCell(5, (uint)(i + 1), fixedLabels[i], WorkpaperStyles.Bold));
        }

        tagColumns.AppendRowHitPositionHeaders(sheet, headerCells, 5);
        sheet.WriteFixedRow(5, headerCells);

        var rows = await EmitTableSheetAsync(sheet, headerRow: 5, headerCells: [], firstDataRow: 6,
            StreamTaggedRowDetailRowsAsync(context, tagColumns, sheet, cancellationToken),
            cancellationToken,
            writeHeader: false);

        stats.Add(sheet.CloseAndSummarizeWith(rows, cancellationToken));
    }

    // ---- step2/3/4 共用表頭 + 固定文字寫入 ----

    /// <summary>
    /// 證實測試表頭(step2/3/4 共用前三列):A1 公司名 / A2 財務報表期間 / A3 副標題。
    /// 與 step1 家族的 <see cref="WriteCommonHeader"/> 不同(那是「測試資料期間/財報準備開始日」),
    /// 故另立——這是兩組不同的固定表頭,非可統一的重複(對齊樣本各表逐字)。
    /// </summary>
    private static void WriteSubstantiveHeader(
        SheetWriter sheet, WorkpaperContext context, string subtitle, bool omitSubtitle = false)
    {
        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"財務報表期間 :  {DigitsOnly(context.PeriodStart)} ~ {DigitsOnly(context.PeriodEnd)}", WorkpaperStyles.Bold)]);
        if (!omitSubtitle)
        {
            sheet.WriteFixedRow(3, [sheet.TextCell(3, 1, subtitle, WorkpaperStyles.Bold)]);
        }
        else
        {
            sheet.WriteFixedRow(3, [sheet.TextCell(3, 1, "證實測試 - 會計分錄及其他調整", WorkpaperStyles.Bold)]);
        }
    }

    /// <summary>逐列寫固定文字(列號/欄/文字三元組);供 step2/3/4 表頭大段 boilerplate 共用。</summary>
    private static void WriteFixedLines(SheetWriter sheet, IReadOnlyList<(uint Row, uint Column, string Text)> lines)
    {
        foreach (var (row, column, text) in lines)
        {
            sheet.WriteFixedRow(row, [sheet.TextCell(row, column, text, WorkpaperStyles.Bold)]);
        }
    }

    /// <summary>step2 第 49-52 列多層欄標(逐字對齊樣本;合併範圍見 Step2HeaderMerges)。</summary>
    private static void WriteStep2ColumnHeaders(SheetWriter sheet)
    {
        sheet.WriteFixedRow(49,
        [
            sheet.TextCell(49, 1, "樣本編號", WorkpaperStyles.BoldWrap),
            sheet.TextCell(49, 3, "擬測試其正確性的攸關資料元素(即RDE，請詳上述說明1~3)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(49, 13, "測試結果(若左列預設的欄位屬於高風險條件的RDE，則應執行A~G測試程序，不得勾選N/A)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(49, 20, "說明詳細測試過程", WorkpaperStyles.BoldWrap)
        ]);
        sheet.WriteFixedRow(50,
        [
            sheet.TextCell(50, 3, "財務類型RDE(對應測試結果A)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(50, 7, "非財務類型RDE(對應測試結果B~G)", WorkpaperStyles.BoldWrap)
        ]);
        sheet.WriteFixedRow(51,
        [
            sheet.TextCell(51, 2, "傳票號碼\n(分錄編號)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 3, "會計科目編號", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 4, "會計科目名稱", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 5, "借方金額", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 6, "貸方金額", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 7, "總帳日期", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 8, "傳票核准日期\n(若有)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 9, "分錄編製人員\n(或過帳人員)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 10, "分錄來源或\n人工/自動分錄判斷(若有)", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 11, "分錄備註/說明", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 12, "傳票核准人員", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 14, "非財務類型", WorkpaperStyles.BoldWrap),
            sheet.TextCell(51, 20, "(例如：向誰詢問、觀察或檢視哪些內容或註記TickMark說明核至哪些相關文件。)", WorkpaperStyles.BoldWrap)
        ]);
        sheet.WriteFixedRow(52,
        [
            sheet.TextCell(52, 13, "A", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 14, "B", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 15, "C", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 16, "D", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 17, "E", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 18, "F", WorkpaperStyles.BoldWrap),
            sheet.TextCell(52, 19, "G", WorkpaperStyles.BoldWrap)
        ]);
    }

    /// <summary>
    /// step2 資料列:A 樣本序號(列序,emitter 給) / B 傳票號 / C 科目編號 / D 名稱 /
    /// **E 借方金額(DebitScaled→顯示)/ F 貸方金額(CreditScaled→顯示)** / G 總帳日 / H 核准日 /
    /// I 編製人員 / J 來源(空) / K 摘要 / L 核准人員;M-S 結果A-G、T 說明(手填留空)。
    /// </summary>
    private static IReadOnlyList<Cell> Step2SampleCells(
        SheetWriter sheet, uint row, InfSampleRow sample, int moneyScale)
    {
        var ordinal = (long)(row - 52); // 第 53 列=序號 1
        return
        [
            sheet.NumberCell(row, 1, ordinal),
            sheet.TextCell(row, 2, sample.DocumentNumber),
            sheet.TextCell(row, 3, sample.AccountCode),
            sheet.TextCell(row, 4, sample.AccountName),
            sheet.NumberCell(row, 5, Display(sample.DebitScaled, moneyScale)),
            sheet.NumberCell(row, 6, Display(sample.CreditScaled, moneyScale)),
            sheet.TextCell(row, 7, sample.PostDate),
            sheet.TextCell(row, 8, sample.ApprovalDate),
            sheet.TextCell(row, 9, sample.CreatedBy),
            sheet.BlankCell(row, 10), // J 來源:infSamplePage 無 source_module
            sheet.TextCell(row, 11, sample.Description),
            sheet.TextCell(row, 12, sample.ApprovedBy)
            // M-T 為手填(結果 A-G + 說明),不輸出 cell(留白)
        ];
    }

    // ---- step4/4-1 動態欄列串流(列 + 該列命中位置配對;沿用 keyset 不全載入)----

    /// <summary>
    /// step4 傳票層:逐頁 tagMatrixVoucherPage,把每列與其 matchedPositions(PositionsByDoc[doc] 對齊)
    /// 配成列工廠;每動態欄依該傳票是否含該 position 標 Y。編號由呼叫端 nextOrdinal 累計。
    /// </summary>
    private async IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> StreamTaggedVoucherRowsAsync(
        WorkpaperContext context, TagColumnSet tagColumns, Func<long> nextOrdinal, SheetWriter sheet,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var (page, positionsByDoc) = await tagMatrixVouchers.GetPageAsync(
                context.ProjectId, context.MoneyScale, new PageRequest(cursor, PageRequest.DefaultPageSize), cancellationToken);

            foreach (var voucher in page.Rows)
            {
                var matched = voucher.DocumentNumber is not null
                    && positionsByDoc.TryGetValue(voucher.DocumentNumber, out var p)
                    ? p
                    : (IReadOnlyList<int>)[];
                var ordinal = nextOrdinal();
                yield return row => Step4VoucherCells(sheet, row, ordinal, voucher, tagColumns, matched, context.MoneyScale);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    /// <summary>
    /// step4-1 行層:逐頁 tagMatrixRowPage,把每行(rows[i])與其 matchedPositions(PositionsByEntry[EntryIds[i]]
    /// 以 index 對齊)配成列工廠;每動態 C*_TAG 欄依該行是否含該 position 標 Y。
    /// </summary>
    private async IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> StreamTaggedRowDetailRowsAsync(
        WorkpaperContext context, TagColumnSet tagColumns, SheetWriter sheet,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var (page, entryIds, positionsByEntry) = await tagMatrixRows.GetPageAsync(
                context.ProjectId, context.MoneyScale, new PageRequest(cursor, PageRequest.DefaultPageSize), cancellationToken);

            for (var i = 0; i < page.Rows.Count; i++)
            {
                var detail = page.Rows[i];
                var matched = positionsByEntry.TryGetValue(entryIds[i], out var p) ? p : (IReadOnlyList<int>)[];
                yield return row => Step41RowCells(sheet, row, detail, tagColumns, matched, context.MoneyScale);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    /// <summary>step4 傳票列:固定 A-E + 動態 C{pos} 欄(matchedPositions 含該 position→Y);P-U 手填留空(不輸出)。</summary>
    private static IReadOnlyList<Cell> Step4VoucherCells(
        SheetWriter sheet, uint row, long ordinal, VoucherTagRow voucher,
        TagColumnSet tagColumns, IReadOnlyList<int> matched, int moneyScale)
    {
        var cells = new List<Cell>
        {
            sheet.NumberCell(row, 1, ordinal),
            sheet.TextCell(row, 2, voucher.DocumentNumber),
            sheet.TextCell(row, 3, voucher.PostDate),
            sheet.TextCell(row, 4, voucher.CreatedBy),
            sheet.NumberCell(row, 5, Display(voucher.VoucherTotalScaled, moneyScale))
        };
        tagColumns.AppendAllPositionMarks(sheet, cells, row, matched);
        return cells;
    }

    /// <summary>step4-1 行列:固定 A-I JE 欄 + 動態 C*_TAG 欄(matchedPositions 含該 position→Y)。</summary>
    private static IReadOnlyList<Cell> Step41RowCells(
        SheetWriter sheet, uint row, RowTagRow detail,
        TagColumnSet tagColumns, IReadOnlyList<int> matched, int moneyScale)
    {
        var cells = new List<Cell>
        {
            sheet.TextCell(row, 1, detail.DocumentNumber),
            sheet.TextCell(row, 2, detail.LineItem),
            sheet.TextCell(row, 3, detail.PostDate),
            sheet.TextCell(row, 4, detail.CreatedBy),
            sheet.TextCell(row, 5, detail.ApprovedBy),
            sheet.TextCell(row, 6, detail.AccountCode),
            sheet.TextCell(row, 7, detail.AccountName),
            sheet.NumberCell(row, 8, Display(detail.AmountScaled, moneyScale)),
            sheet.TextCell(row, 9, detail.Description)
        };
        tagColumns.AppendRowHitPositionMarks(sheet, cells, row, matched);
        return cells;
    }

    // ================= 資料表共用骨架(DRY=3)=================

    /// <summary>
    /// A1-A4 共同表頭(五張 step1 表逐字相同):公司名 / 測試期間 / 財報準備開始日 / 斜體說明。
    /// A3 取 PeriodEnd 去非數字字元(樣本「財務報表準備期間 - 開始日」即期末日)。
    /// </summary>
    private static void WriteCommonHeader(SheetWriter sheet, WorkpaperContext context, string italicNote)
    {
        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"測試資料期間 :  {context.PeriodStart} ~ {context.PeriodEnd}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1,
            $"財務報表準備期間 - 開始日 : {DigitsOnly(context.PeriodEnd)}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(4, [sheet.TextCell(4, 1, italicNote, WorkpaperStyles.Bold)]);
    }

    /// <summary>
    /// 資料表共用骨架:寫欄標列(可略)+ 逐列資料(列號自 firstDataRow 內部累計);回資料列數。
    /// 列來源是 <see cref="IAsyncEnumerable{T}"/>(每元素為「給列號→cells」的工廠),呼叫端決定
    /// keyset 串流或全載入——本原語只管「不全載入地逐列寫出 + 累計列號」,是 step1 / step1-2 / step1-3 共用骨架。
    /// </summary>
    private static async Task<long> EmitTableSheetAsync(
        SheetWriter sheet, uint headerRow, IReadOnlyList<Cell> headerCells, uint firstDataRow,
        IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> rowFactories, CancellationToken cancellationToken,
        bool writeHeader = true)
    {
        if (writeHeader && headerCells.Count > 0)
        {
            sheet.WriteFixedRow(headerRow, headerCells);
        }

        long count = 0;
        var rowIndex = firstDataRow;
        await foreach (var factory in rowFactories.WithCancellation(cancellationToken))
        {
            sheet.WriteRow(rowIndex, factory(rowIndex));
            rowIndex++;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 條件例外表:先窺第一列,無列則整段不寫(連欄標都不出);有列才寫欄標 + 全部列。
    /// step1-1 借貸不平用——「有不平才出例外表」是 orchestration guard,而非每列判斷的 god-switch。
    /// </summary>
    private static async Task<long> EmitConditionalTableAsync(
        SheetWriter sheet, uint headerRow, IReadOnlyList<Cell> headerCells, uint firstDataRow,
        IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> rowFactories, CancellationToken cancellationToken)
    {
        await using var enumerator = rowFactories.GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync())
        {
            return 0; // 無不平傳票 → 例外表不出現
        }

        sheet.WriteFixedRow(headerRow, headerCells);

        long count = 0;
        var rowIndex = firstDataRow;
        do
        {
            sheet.WriteRow(rowIndex, enumerator.Current(rowIndex));
            rowIndex++;
            count++;
        } while (await enumerator.MoveNextAsync());

        return count;
    }

    /// <summary>keyset 分頁 repo → 列工廠序列(逐頁取、不全載入,直到 nextCursor==null)。</summary>
    private static async IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> StreamRowsAsync<T>(
        Func<string?, CancellationToken, Task<PageResult<T>>> fetchPage,
        Func<T, Func<uint, IReadOnlyList<Cell>>> rowFactory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await fetchPage(cursor, cancellationToken);
            foreach (var item in page.Rows)
            {
                yield return rowFactory(item);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    /// <summary>同 <see cref="StreamRowsAsync"/>,但首頁已取(避免條件 guard 重取):先吐首頁,再續頁。</summary>
    private static async IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> StreamRowsFromAsync<T>(
        PageResult<T> firstPage,
        Func<string?, Task<PageResult<T>>> fetchNext,
        Func<T, Func<uint, IReadOnlyList<Cell>>> rowFactory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in firstPage.Rows)
        {
            yield return rowFactory(item);
        }

        var cursor = firstPage.NextCursor;
        while (cursor is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchNext(cursor);
            foreach (var item in page.Rows)
            {
                yield return rowFactory(item);
            }

            cursor = page.NextCursor;
        }
    }

    /// <summary>有界全載入清單 → 列工廠序列(step1-2 全名單;基數有界故不分頁,但走同骨架)。</summary>
    private static async IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> ToRowStream<T>(
        IReadOnlyList<T> items, Func<T, Func<uint, IReadOnlyList<Cell>>> rowFactory)
    {
        foreach (var item in items)
        {
            yield return rowFactory(item);
        }

        await Task.CompletedTask;
    }

    // ---- 列 cell 工廠(各表欄序;手填欄一律 BlankCell)----

    /// <summary>step1 全科目列:B 編號 / C 名稱 / D TB 變動(A) / E GL 彙總(C) / F 差異(B)-(A)。</summary>
    private static IReadOnlyList<Cell> CompletenessRowCells(
        SheetWriter sheet, uint row, CompletenessDiffAccount acc, int moneyScale) =>
    [
        sheet.TextCell(row, 2, acc.AccountCode),
        sheet.TextCell(row, 3, acc.AccountName),
        sheet.NumberCell(row, 4, Display(acc.TbAmountScaled, moneyScale)),
        sheet.NumberCell(row, 5, Display(acc.GlAmountScaled, moneyScale)),
        sheet.NumberCell(row, 6, Display(acc.DiffScaled, moneyScale))
    ];

    /// <summary>step1-1 不平傳票列:B 傳票號 / C 借方 / D 貸方 / E 借貸差額(皆 scaled→顯示)。</summary>
    private static IReadOnlyList<Cell> UnbalancedRowCells(
        SheetWriter sheet, uint row, UnbalancedDocument doc, int moneyScale) =>
    [
        sheet.TextCell(row, 2, doc.DocumentNumber),
        sheet.NumberCell(row, 3, Display(doc.DebitScaled, moneyScale)),
        sheet.NumberCell(row, 4, Display(doc.CreditScaled, moneyScale)),
        sheet.NumberCell(row, 5, Display(doc.DiffScaled, moneyScale))
    ];

    /// <summary>step1-2 編製人員列:B 人員 / C 空(自動或人工) / D 傳票數 / E 借方彙總 / F-H 空(部門/職稱/說明)。</summary>
    private static IReadOnlyList<Cell> CreatorRowCells(
        SheetWriter sheet, uint row, CreatorSummaryExportRow creator, int moneyScale) =>
    [
        sheet.TextCell(row, 2, creator.CreatedBy),
        sheet.BlankCell(row, 3),
        sheet.NumberCell(row, 4, creator.EntryCount),
        sheet.NumberCell(row, 5, Display(creator.DebitTotalScaled, moneyScale)),
        sheet.BlankCell(row, 6),
        sheet.BlankCell(row, 7),
        sheet.BlankCell(row, 8)
    ];

    /// <summary>step1-3 差異說明列:B 編號 / C 名稱 / D 差異金額 / E-G 空(原因/調節/調節後差異)。</summary>
    private static IReadOnlyList<Cell> DiffExplanationRowCells(
        SheetWriter sheet, uint row, CompletenessDiffAccount acc, int moneyScale) =>
    [
        sheet.TextCell(row, 2, acc.AccountCode),
        sheet.TextCell(row, 3, acc.AccountName),
        sheet.NumberCell(row, 4, Display(acc.DiffScaled, moneyScale)),
        sheet.BlankCell(row, 5),
        sheet.BlankCell(row, 6),
        sheet.BlankCell(row, 7)
    ];

    /// <summary>step1-3-1 差異調節列:A 編號 / B 名稱 / C 差異金額(欄起於 A,與其他表起於 B 不同)。</summary>
    private static IReadOnlyList<Cell> ReconciliationRowCells(
        SheetWriter sheet, uint row, CompletenessDiffAccount acc, int moneyScale) =>
    [
        sheet.TextCell(row, 1, acc.AccountCode),
        sheet.TextCell(row, 2, acc.AccountName),
        sheet.NumberCell(row, 3, Display(acc.DiffScaled, moneyScale))
    ];

    /// <summary>多欄欄標列工廠(欄自 B 起,對齊 step1 家族表頭都從 B 欄開始;BoldWrap 因欄標含換行)。</summary>
    private static IReadOnlyList<Cell> HeaderCells(SheetWriter sheet, uint row, IReadOnlyList<string> labels)
    {
        var cells = new List<Cell>(labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            cells.Add(sheet.TextCell(row, (uint)(i + 2), labels[i], WorkpaperStyles.BoldWrap));
        }

        return cells;
    }

    /// <summary>結論列:A 欄「結論：」標籤 + B 欄結論文字,一次寫出(同列須單次 WriteRow,避免重複 RowIndex)。</summary>
    private static void WriteConclusionRow(SheetWriter sheet, uint row, string text) =>
        sheet.WriteFixedRow(row,
        [
            sheet.TextCell(row, 1, "結論：", WorkpaperStyles.Bold),
            sheet.TextCell(row, 2, text, WorkpaperStyles.Bold)
        ]);

    private static decimal Display(long scaled, int moneyScale) =>
        moneyScale <= 0 ? scaled : (decimal)scaled / moneyScale;

    // ---- 固定字串(逐字對齊福懋/佰鴻兩樣本,xlsx_inspect.py 復解;含原樣換行)----

    private const string IntroBoilerplate =
        "此JE測試的WorkPaper係透過JE Testing Tool產生之工作底稿，並依下列步驟分別記錄會計分錄測試" +
        "(JE Testing)所需之程序。\n(有關高風險JE測試的篩選判斷由查核團隊在該工具執行過程中完成定義。)";

    private const string Step5Banner =
        "若查核團隊發現受查客戶在財務報表關帳後，尚有入帳之調整分錄(Post-closing entries)，" +
        "或未入帳直接對財務報表之調整(Other adjustments)，\n則可將此類調整記錄於此處，或說明無此類情形。\n";

    private const string Step1Conclusion =
        "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。";

    private const string Step1ListNote =
        "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下：" +
        "(有部分科目之差異數不為0，請於step1-3說明其理由，以確認JE母體的完整性)";

    private const string Step11Conclusion =
        "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。";

    private const string Step13Conclusion =
        "基於上述程序，查核團隊對出現差異之科目均已取得足夠的查核證據，已確認其原因尚屬合理或進行調節使其無差異，" +
        "因此可確認JE測試母體之完整性。";

    private const string Step131Note1 =
        "於試算表【TB.IDM】檔案中，篩選科目首碼大於3的科目，取得所有損益科目";

    private const string Step131Note2 =
        "使用Control Total功能，加總所有損益科目，得到前期損益金額為";

    private const string Step131Note3 =
        "經核算，完整性差異金額與前期損益相符，若取得前期損益結轉分錄將無此差異。";

    // ---- step2/3/4 表頭固定文字(逐字對齊福懋樣本;列號/欄/合併皆照樣本)----

    /// <summary>step2 測試說明 + 測試程序(A6-C47 區塊;欄標另見 WriteStep2ColumnHeaders)。</summary>
    private static readonly IReadOnlyList<(uint Row, uint Column, string Text)> Step2HeaderLines =
    [
        (6, 1, "測試說明："),
        (6, 3, "1. 依照KAEG-I [ISA | 815.13507]，JE攸關母體有納入高風險條件(HRC)的欄位即攸關資料元素(RDE)，需先確認RDE的可靠性後，方執行高風險篩選條件。\n    若為初步篩選(Screening)之預篩選程序，因屬於風險評估，則於該階段可不用確認RDE可靠性。"),
        (7, 3, "2. 上述提到之高風險條件的RDE大多屬於非財務性質，例如篩選條件考量過帳日期、分錄摘要的關鍵字、分錄標註為人工分錄者、特定人員。"),
        (8, 3, "3. 若高風險條件除上述非財務性質欄位外，亦有使用會計科目編號、科目名稱或金額等欄位來進行篩選，則這些納入篩選條件的欄位亦屬於RDE。"),
        (9, 3, "4. JE的RDE可靠性確認包括確認完整性及正確性，由於完整性已於JE攸關母體完整性測試程序中執行，故此處可靠性測試係針對JE RDE的正確性進行測試。"),
        (10, 3, "5. 依照KAEG-I [ISA | 2701.1500]，需依照屬性抽樣表格[ISA | 4164.1300]選取樣本(選樣方法可採隨機、隨意或系統抽樣)核對會計傳票附件以確認RDE的正確性。"),
        (11, 3, "   (由於JE具有管理階層逾越控制之顯著風險，因此其固有風險為Significant，對照上述表格後的最低測試樣本量為60筆。)"),
        (13, 1, "測試程序：(若JE高風險條件(HRC)不包含下列A~G程序提到的欄位，則該測試程序可設為N/A)"),
        (14, 1, "- 財務類型RDE (如會計科目編號、科目名稱、借貸方代號、分錄金額)"),
        (15, 1, "A."),
        (15, 3, "確認是否已於JE母體完整性測試時，完成此類RDE的測試。若無，則確認分錄金額是否與傳票附件符合，其餘則核至已核准的會計科目表。"),
        (16, 3, "(此類型RDE通常已於JE母體完整性測試過程與TB(試算表)比對時完成可靠性測試)"),
        (18, 1, "- 非財務類型RDE"),
        (19, 1, "B."),
        (19, 2, "過帳日期/過帳時間：屬內部交易過帳者，核至經核准的內部交易日期。若屬於外部交易者，則核對相關交易憑證的日期"),
        (20, 1, "C."),
        (20, 2, "傳票建立日期/分錄時間：根據案件情況及所選樣本來判斷選擇以下一項或多項程序來執行，以驗證其可靠性。"),
        (27, 1, "D."),
        (27, 2, "分錄編製人員(或過帳人員)：根據案件情況及所選樣本來判斷選擇以下一項或多項程序來執行，以驗證其可靠性。"),
        (34, 1, "E."),
        (34, 2, "分錄來源：核對分錄至傳票附件內容(若為人工分錄)與受查者系統畫面資訊(若為自動分錄)，以確認是否符合其所標註的分錄來源或是對於人工/自動分錄之標記。"),
        (36, 1, "F."),
        (36, 2, "分錄備註/說明：選取以下一項或多項程序來執行，以驗證其可靠性。"),
        (40, 1, "G."),
        (40, 2, "除上述以外之RDE(勾選以下適合程序來執行測試)"),
        (45, 2, "註1:"),
        (45, 3, "建議透過詢問JE資料流與流程作業，來決定JE高風險範圍條件所要篩選的日期要使用過帳日(Posting date)還是編製日/立帳日(Create date/Document date)。"),
        (46, 2, "註2:"),
        (46, 3, "若下列樣本未涵蓋自動分錄且自動分錄未於其他程序執行測試者，當高風險條件有篩選到自動分錄，應補執行上述對自動分錄提到的程序。"),
        (47, 2, "註3:"),
        (47, 3, "若有其他用在高風險條件的RDE欄位，請自行於工作表：可靠性樣本_所有欄位中複製貼上至此底稿中。"),
    ];

    /// <summary>step2 表頭合併範圍(逐字對齊樣本;欄標多層合併)。</summary>
    private static readonly IReadOnlyList<string> Step2HeaderMerges =
    [
        "C6:K6",
        "A49:A52", "C49:L49", "M49:S50", "T49:T50",
        "C50:F50", "G50:L50",
        "B51:B52", "C51:C52", "D51:D52", "E51:E52", "F51:F52", "G51:G52", "H51:H52",
        "I51:I52", "J51:J52", "K51:K52", "L51:L52", "N51:S51", "T51:T52"
    ];

    /// <summary>step3 測試目的 / 範圍 / 理由 / 風險評估固定文字(B 欄為合併大段;欄標另見 EmitStep3)。</summary>
    private static readonly IReadOnlyList<(uint Row, uint Column, string Text)> Step3HeaderLines =
    [
        (5, 1, "測試目的"),
        (5, 2, "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。"),
        (7, 1, "測試範圍"),
        (7, 2, "期末財務報表準備期間與整個財務報導期間之會計分錄"),
        (9, 1, "選擇該測試範圍的理由"),
        (9, 2, "查核團隊考量舞弊風險相關之分錄，除了會於期末財務報表準備期間發生外，某些與舞弊風險因子相關之分錄類型(例如：收入)亦會在整個財務報導期間中被記錄。這些都將會對財務報表的正確性造成影響。因此，除測試期末財務報表準備期間之分錄外， 亦將對整個財務報表期間之會計分錄母體，篩選與舞弊風險類型相關之分錄。"),
        (14, 1, "Step 3"),
        (15, 1, "辨認高風險條件"),
        (15, 2, "風險評估及查核程序："),
        (16, 2, "查核團隊基於下列程序來辨識JE測試之攸關母體及高風險範圍條件：\n•在風險評估與查核團隊討論及計畫討論管理階層踰越控制風險，包含分錄及其他調整\n•了解分錄及其他調整步驟\n•特別詢問處理分錄的會計人員，以及詢問管理階層或其他人員\n•了解交易模式\n•辨認舞弊風險及舞弊因子\n•蒐集查核與特別矛盾之發現，以及辨認舞弊可能已經發生情況之證據\n\n根據上述程序，查核團隊已將所辨認出高風險範圍條件記錄於下方表格。"),
        (17, 2, "辨認出高風險範圍條件，並篩選符合條件之分錄進行測試(相關之傳票分錄明細將列在step4)"),
    ];

    /// <summary>step3 固定文字合併範圍(逐字對齊樣本)。</summary>
    private static readonly IReadOnlyList<string> Step3HeaderMerges =
    [
        "B5:E5", "B7:E7", "A9:A12", "B9:E12", "B15:E15", "B16:E16"
    ];

    /// <summary>step4 測試目的 / 測試程序 + 矩陣區段標題(欄標另見 EmitStep4Async)。</summary>
    private static readonly IReadOnlyList<(uint Row, uint Column, string Text)> Step4HeaderLines =
    [
        (5, 1, "測試目的"),
        (5, 2, "取得查核證據，以測試會計分錄及其他調整：\ni)  不存在因舞弊所導致的重大不實表達；\nii) 有適當的支持性文件；\niii) 反映了相關的事項、情況和交易，並且\niv) 按照財務報導架構，記錄在正確的會計期間。"),
        (7, 1, "測試程序"),
        (7, 2, "A.核至相關傳票之複核紀錄，以確認該分錄之過帳係經適當核准。\nB.核至相關傳票附件，以確認該分錄所載內容與附件一致，分錄係記錄於正確的\n    會計期間，並確認依附件所登錄的科目係屬適當及相關。\nC.詢問負責人員編製分錄之細節，以確認該分錄編製無存在不合理之情形。"),
        (10, 6, "決定進行測試之高風險範圍條件"),
        (10, 16, "查核程序"),
        (10, 19, "有無舞弊\n或不實表達\n(Yes/No)"),
    ];

    /// <summary>step4 固定文字合併範圍(逐字對齊樣本)。</summary>
    private static readonly IReadOnlyList<string> Step4HeaderMerges =
    [
        "B5:E5", "B7:E7", "P10:R10", "S10:S11"
    ];

    private const string Step4SelectionNote =
        "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)";

    /// <summary>
    /// 欄位資訊 GL 段尾的衍生旗標欄(對齊福懋樣本 A49-A52)。原工具於匯入 precompute 這些 K_ 旗標欄並登錄於
    /// Field Mapping Info;JET 以 set-based SQL 即時算規則,無實體欄,故此處以固定字面登錄其存在(對齊樣本版面)。
    /// </summary>
    private static readonly IReadOnlyList<string> DerivedFieldFlags =
        ["K_R條件", "K_情境三四", "K_情境五", "K_情境八"];

    /// <summary>
    /// 假期假日資訊的固定週末對照(DAYOFWEEK / WORKDAY)。WORKDAY=Y 反指「視為非工作日(週末)」:
    /// 週一~五=N、週六日=Y(對齊樣本)。週末恆為固定七天,以資料化常數陣列表達,emitter 逐列輸出,不寫特判。
    /// </summary>
    private static readonly IReadOnlyList<(string DayName, string Workday)> WeekendRows =
    [
        ("Monday", "N"), ("Tuesday", "N"), ("Wednesday", "N"), ("Thursday", "N"),
        ("Friday", "N"), ("Saturday", "Y"), ("Sunday", "Y")
    ];

    // ================= 動態 C 欄集(step3/4/4-1 業務分支:data structure 消特例)=================

    /// <summary>
    /// 高風險情境的欄集 data structure。把「step4 用全 position」「step4-1 只用 rowHitCount&gt;0 的 position」
    /// 這個業務分支(對齊樣本的動態 schema)收斂成兩個預先算好的有序 position list,emitter 只需:
    /// (1) 依 list 順序加欄標 cell;(2) 逐列依該列 matchedPositions 是否含某 position 標 Y——
    /// 完全由 data structure 對映,不寫「第幾欄特判」(Linus 好品味:用資料結構消除特例)。
    ///
    /// <see cref="Scenarios"/> 供 step3 列(name/rationale/voucherHitCount,position 升冪);
    /// <see cref="_allPositions"/> = 全 position 升冪(step4 欄集);
    /// <see cref="_rowHitPositions"/> = rowHitCount&gt;0 的 position 升冪(step4-1 欄集)。
    /// name/rationale/position 取自 scenario store、命中數取自 tagMatrix counts,於此合併(鏡射 query.tagMatrixScenarios);
    /// step3 另需 rationale(D 欄),非 wire 的 <see cref="ScenarioTagSummary"/> 所有,故用內部 <see cref="ScenarioRow"/> 承載。
    /// </summary>
    private sealed class TagColumnSet
    {
        private const uint Step4FixedColumns = 5;   // A-E
        private const uint Step41FixedColumns = 9;  // A-I

        private readonly IReadOnlyList<int> _allPositions;
        private readonly IReadOnlyList<int> _rowHitPositions;

        private TagColumnSet(
            IReadOnlyList<ScenarioRow> scenarios,
            IReadOnlyList<int> allPositions,
            IReadOnlyList<int> rowHitPositions)
        {
            Scenarios = scenarios;
            _allPositions = allPositions;
            _rowHitPositions = rowHitPositions;
        }

        /// <summary>step3 一列(代號 C{Position} / 條件描述=Name / 原因=Rationale / 傳票命中數=VoucherHitCount)。</summary>
        public sealed record ScenarioRow(int Position, string Name, string Rationale, long VoucherHitCount, long RowHitCount);

        /// <summary>step3 列來源:position 升冪的情境摘要(name/rationale/voucherHitCount)。</summary>
        public IReadOnlyList<ScenarioRow> Scenarios { get; }

        public static async Task<TagColumnSet> LoadAsync(
            IFilterScenarioStore scenarioStore, ITagMatrixScenariosRepository countsRepo,
            string projectId, CancellationToken cancellationToken)
        {
            var saved = await scenarioStore.ListAsync(projectId, cancellationToken);
            var counts = await countsRepo.GetCountsAsync(projectId, cancellationToken);

            var scenarios = saved
                .OrderBy(s => s.Position)
                .Select(s =>
                {
                    var (voucherHits, rowHits) = counts.GetValueOrDefault(s.Position);
                    return new ScenarioRow(s.Position, s.Name, s.Rationale, voucherHits, rowHits);
                })
                .ToList();

            var allPositions = scenarios.Select(s => s.Position).ToList();
            var rowHitPositions = scenarios.Where(s => s.RowHitCount > 0).Select(s => s.Position).ToList();
            return new TagColumnSet(scenarios, allPositions, rowHitPositions);
        }

        /// <summary>step4 欄標:全 position 升冪,標頭 C{position}{suffix}(suffix 空)。接在固定欄之後。</summary>
        public void AppendAllPositionHeaders(SheetWriter sheet, List<Cell> cells, uint row, string suffix) =>
            AppendHeaders(sheet, cells, row, _allPositions, Step4FixedColumns, suffix);

        /// <summary>step4-1 欄標:只 rowHitCount&gt;0 的 position 升冪,標頭 C{position}_TAG。接在固定欄之後。</summary>
        public void AppendRowHitPositionHeaders(SheetWriter sheet, List<Cell> cells, uint row) =>
            AppendHeaders(sheet, cells, row, _rowHitPositions, Step41FixedColumns, "_TAG");

        /// <summary>step4 列:逐 position 依 matched 是否含之標 Y/空。</summary>
        public void AppendAllPositionMarks(SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> matched) =>
            AppendMarks(sheet, cells, row, _allPositions, Step4FixedColumns, matched);

        /// <summary>step4-1 列:逐 rowHit position 依 matched 是否含之標 Y/空。</summary>
        public void AppendRowHitPositionMarks(SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> matched) =>
            AppendMarks(sheet, cells, row, _rowHitPositions, Step41FixedColumns, matched);

        private static void AppendHeaders(
            SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> positions, uint fixedColumns, string suffix)
        {
            for (var i = 0; i < positions.Count; i++)
            {
                cells.Add(sheet.TextCell(row, fixedColumns + (uint)i + 1, $"C{positions[i]}{suffix}", WorkpaperStyles.Bold));
            }
        }

        private static void AppendMarks(
            SheetWriter sheet, List<Cell> cells, uint row, IReadOnlyList<int> positions, uint fixedColumns, IReadOnlyList<int> matched)
        {
            for (var i = 0; i < positions.Count; i++)
            {
                var column = fixedColumns + (uint)i + 1;
                cells.Add(matched.Contains(positions[i])
                    ? sheet.TextCell(row, column, "Y")
                    : sheet.BlankCell(row, column));
            }
        }
    }

    // ================= SAX 原語(Task 2 既有,未改邏輯)=================

    /// <summary>新建一張 worksheet 並註冊進 Sheets(DOM append),回傳串流 <see cref="SheetWriter"/>。</summary>
    private static SheetWriter OpenSheet(SpreadsheetDocument document, Sheets sheets, string name)
    {
        var worksheetPart = document.WorkbookPart!.AddNewPart<WorksheetPart>();

        var sheetId = (uint)sheets.ChildElements.Count + 1;
        sheets.Append(new Sheet
        {
            Id = document.WorkbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name
        });

        return new SheetWriter(name, worksheetPart);
    }

    private static string DigitsOnly(string value)
    {
        return string.Concat(value.Where(char.IsDigit));
    }

    /// <summary>
    /// 行事曆日期顯示:儲存格式 yyyy-MM-dd → 樣本顯示形式 yyyy/MM/dd。
    /// 非該格式者原樣返回(不臆造/不竄改;CalendarDayProjector 已保證匯入為 yyyy-MM-dd,此為防禦)。
    /// </summary>
    private static string DisplayDate(string isoDate)
    {
        return DateTime.TryParseExact(
                isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            : isoDate;
    }

    /// <summary>
    /// 單張 worksheet 的串流寫出器(SAX 低階原語)。封裝 OpenXmlPartWriter + 元素順序約束:
    /// 構造時開 worksheet → sheetData(forward-only);WriteRow/WriteFixedRow 即時串流寫列;
    /// AddMerge 累積合併範圍(數量有界、屬版面);<see cref="CloseAndSummarize"/> 關 sheetData →
    /// 寫 mergeCells（schema 要求在 sheetData 之後）→ 關 worksheet。
    /// 資料列計入 RowsWritten;固定文字/表頭列不計(對齊 SheetStat 語意)。
    /// 供資料表 emitter 以 keyset 迴圈逐列呼叫 WriteRow（串流、不全載入）。
    /// </summary>
    private sealed class SheetWriter : IDisposable
    {
        private readonly string _name;
        private readonly OpenXmlWriter _writer;
        private readonly List<string> _merges = [];
        private long _dataRows;
        private bool _closed;

        public SheetWriter(string name, WorksheetPart part)
        {
            _name = name;
            _writer = OpenXmlPartWriter.Create(part);
            _writer.WriteStartDocument();
            _writer.WriteStartElement(new Worksheet());
            _writer.WriteStartElement(new SheetData());
        }

        /// <summary>寫一列「資料列」(計入 RowsWritten)。供資料表 keyset 逐列呼叫。</summary>
        public void WriteRow(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            WriteRowCore(rowIndex, cells);
            _dataRows++;
        }

        /// <summary>寫一列固定文字/表頭(不計入 RowsWritten)。</summary>
        public void WriteFixedRow(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            WriteRowCore(rowIndex, cells);
        }

        /// <summary>累積一個合併範圍(如 "B1:O1");實際寫出延到 sheetData 關閉之後(schema 約束)。</summary>
        public void AddMerge(string range)
        {
            _merges.Add(range);
        }

        /// <summary>inline-string 文字 cell(空字串會被視為空 cell 不輸出值,但保留樣式)。</summary>
        public Cell TextCell(uint rowIndex, uint column, string? value, uint style = WorkpaperStyles.Default)
        {
            var cell = new Cell { CellReference = Reference(column, rowIndex), StyleIndex = style };
            if (!string.IsNullOrEmpty(value))
            {
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(value));
            }

            return cell;
        }

        /// <summary>數值 cell(以整數/小數字面寫入;顯示格式交給 StyleIndex 的 numFmt)。供資料表金額欄用。</summary>
        public Cell NumberCell(uint rowIndex, uint column, decimal value, uint style = WorkpaperStyles.Default)
        {
            return new Cell
            {
                CellReference = Reference(column, rowIndex),
                StyleIndex = style,
                DataType = CellValues.Number,
                CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
            };
        }

        /// <summary>空白 cell(只佔位 + 帶樣式,無值)。手填欄/版面留白用。</summary>
        public Cell BlankCell(uint rowIndex, uint column, uint style = WorkpaperStyles.Default)
        {
            return new Cell { CellReference = Reference(column, rowIndex), StyleIndex = style };
        }

        /// <summary>關 sheetData → 寫 mergeCells（若有）→ 關 worksheet,回該表的 <see cref="SheetStat"/>。</summary>
        public SheetStat CloseAndSummarize(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseElements();
            return new SheetStat(_name, _dataRows);
        }

        /// <summary>同 <see cref="CloseAndSummarize"/>,但以外部累計的資料列數覆寫(資料列由骨架累計而非逐次 WriteRow)。</summary>
        public SheetStat CloseAndSummarizeWith(long dataRows, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseElements();
            return new SheetStat(_name, dataRows);
        }

        public void Dispose()
        {
            // 例外路徑也要關掉開啟的元素並釋放 writer,否則 part 留半截 XML
            CloseElements();
            _writer.Dispose();
        }

        private void WriteRowCore(uint rowIndex, IReadOnlyList<Cell> cells)
        {
            var row = new Row { RowIndex = rowIndex };
            foreach (var cell in cells)
            {
                row.Append(cell);
            }

            // 整列一次寫出:記憶體只持有當前列(串流不變),且型別安全勝於手拆 start/string/end
            _writer.WriteElement(row);
        }

        private void CloseElements()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            _writer.WriteEndElement(); // </sheetData>

            if (_merges.Count > 0)
            {
                // mergeCells 必須在 sheetData 之後(CT_Worksheet 子序列);count 屬必填
                var mergeCells = new MergeCells { Count = (uint)_merges.Count };
                foreach (var range in _merges)
                {
                    mergeCells.Append(new MergeCell { Reference = range });
                }

                _writer.WriteElement(mergeCells);
            }

            _writer.WriteEndElement(); // </worksheet>
        }

        /// <summary>(column, row) → A1 形式參考。column/row 皆 1-based。</summary>
        private static string Reference(uint column, uint rowIndex)
        {
            return ColumnLetters(column) + rowIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>1→A、26→Z、27→AA…(Excel 26 進位欄名)。</summary>
        private static string ColumnLetters(uint column)
        {
            var letters = string.Empty;
            while (column > 0)
            {
                var remainder = (int)((column - 1) % 26);
                letters = (char)('A' + remainder) + letters;
                column = (column - 1) / 26;
            }

            return letters;
        }
    }
}
