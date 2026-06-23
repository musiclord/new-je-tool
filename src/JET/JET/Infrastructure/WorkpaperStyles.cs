using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// 匯出底稿的最小樣式表(WorkbookStylesPart 的 Stylesheet)+ cellXfs 索引常數。
///
/// 為什麼用具名索引常數而非魔術數字:cellXfs 是 0-based 索引集合,cell 以 StyleIndex 指進來;
/// 用 <see cref="Default"/>/<see cref="Bold"/>… 命名讓 emitter 讀得懂、避免錯位(介面誤用困難)。
///
/// 子元素順序是硬約束:ISO/IEC 29500 的 CT_Stylesheet 規定 styleSheet 子序列為
/// numFmts → fonts → fills → borders → cellStyleXfs → cellXfs(strongly-typed Stylesheet 會驗證順序)。
/// fills 還有 Excel 慣例:索引 0 必為 PatternType.None、索引 1 必為 Gray125,自訂填色從索引 2 起,
/// 否則 Excel 會判檔損並嘗試修復。本表只放三表(封面 ×2 + step5)+ 後續資料表會用到的少量樣式,
/// 不提前堆砌(no over-engineering);Task 3-5 需要新樣式時在此擴充並更新索引常數。
/// </summary>
internal static class WorkpaperStyles
{
    // ---- cellXfs 索引(對應下方 BuildCellFormats 的順序)----

    /// <summary>預設:微軟正黑體、靠上對齊。資料表內文用。</summary>
    public const uint Default = 0;

    /// <summary>粗體、靠左靠上。封面 A1/A2、各表「說明：」標籤。</summary>
    public const uint Bold = 1;

    /// <summary>粗體 + 自動換行 + 靠上。含換行的整段 boilerplate(JE WorkingPaper說明 B1)。</summary>
    public const uint BoldWrap = 2;

    /// <summary>一般、靠左靠上。封面固定說明段(A5/A6)。</summary>
    public const uint Plain = 3;

    /// <summary>黃底橫幅:12 級、黃色填滿、自動換行、靠左靠上。step5 A1。</summary>
    public const uint YellowBanner = 4;

    // ---- fonts 索引 ----
    private const uint FontRegular = 0;
    private const uint FontBold = 1;
    private const uint FontBanner = 2;

    // ---- fills 索引(0/1 為 Excel 保留;黃色自 2 起)----
    private const uint FillYellow = 2;

    private const string FontName = "微軟正黑體";
    private const double BannerFontSize = 12d;
    private const string YellowHex = "FFFFFF00"; // ARGB:不透明黃(對齊樣本 bg#FFFF00)

    /// <summary>組出整份 Stylesheet。一次性建構(styles.xml 恆小,DOM 即可,不需串流)。</summary>
    public static Stylesheet Build()
    {
        // 子元素順序固定:numFmts、fonts、fills、borders、cellStyleXfs、cellXfs(見類別註解)
        return new Stylesheet(
            BuildFonts(),
            BuildFills(),
            BuildBorders(),
            BuildCellStyleFormats(),
            BuildCellFormats());
    }

    private static Fonts BuildFonts()
    {
        // 索引必須與 FontRegular/FontBold/FontBanner 一致
        var regular = new SpreadsheetFont(new FontSize { Val = 11d }, new FontName { Val = FontName });
        var bold = new SpreadsheetFont(new Bold(), new FontSize { Val = 11d }, new FontName { Val = FontName });
        var banner = new SpreadsheetFont(new FontSize { Val = BannerFontSize }, new FontName { Val = FontName });
        return new Fonts(regular, bold, banner) { Count = 3 };
    }

    private static Fills BuildFills()
    {
        // 索引 0 = None、索引 1 = Gray125(Excel 保留,不可省);黃色為索引 2
        var none = new Fill(new PatternFill { PatternType = PatternValues.None });
        var gray = new Fill(new PatternFill { PatternType = PatternValues.Gray125 });
        var yellow = new Fill(new PatternFill(new ForegroundColor { Rgb = YellowHex })
        {
            PatternType = PatternValues.Solid
        });
        return new Fills(none, gray, yellow) { Count = 3 };
    }

    private static Borders BuildBorders()
    {
        // 單一空白邊框(索引 0):cellXfs 的 BorderId 都指 0。本 task 三表無框線。
        var border = new Border(
            new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder());
        return new Borders(border) { Count = 1 };
    }

    private static CellStyleFormats BuildCellStyleFormats()
    {
        // 至少一筆主格式(索引 0),cellXfs 的 xfId 指它
        var master = new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 };
        return new CellStyleFormats(master) { Count = 1 };
    }

    private static CellFormats BuildCellFormats()
    {
        // 順序對應頂部索引常數;ApplyFont/ApplyFill/ApplyAlignment 明示讓 Excel 套用對應記錄
        var defaultXf = TopAligned(FontRegular, fillId: 0, horizontalLeft: false, wrap: false);
        var boldXf = TopAligned(FontBold, fillId: 0, horizontalLeft: true, wrap: false);
        var boldWrapXf = TopAligned(FontBold, fillId: 0, horizontalLeft: false, wrap: true);
        var plainXf = TopAligned(FontRegular, fillId: 0, horizontalLeft: true, wrap: false);
        var bannerXf = TopAligned(FontBanner, fillId: FillYellow, horizontalLeft: true, wrap: true);

        return new CellFormats(defaultXf, boldXf, boldWrapXf, plainXf, bannerXf) { Count = 5 };
    }

    /// <summary>共用 cellXf 工廠:靠上對齊(樣本全表 vertical=top),可選靠左、自動換行、指定填色。</summary>
    private static CellFormat TopAligned(uint fontId, uint fillId, bool horizontalLeft, bool wrap)
    {
        var alignment = new Alignment { Vertical = VerticalAlignmentValues.Top };
        if (horizontalLeft)
        {
            alignment.Horizontal = HorizontalAlignmentValues.Left;
        }

        if (wrap)
        {
            alignment.WrapText = true;
        }

        return new CellFormat
        {
            NumberFormatId = 0,
            FontId = fontId,
            FillId = fillId,
            BorderId = 0,
            FormatId = 0,
            ApplyFont = true,
            ApplyFill = fillId != 0, // index 0 = 無填色,不需套用
            ApplyAlignment = true,
            Alignment = alignment
        };
    }
}
