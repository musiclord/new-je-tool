namespace JET.Domain;

/// <summary>
/// logical key → 正準中文欄名(GL 側 *_JE / *_JE_S、TB 側 *_TB)。
///
/// 為什麼存在:匯出底稿的「自動化工具-檔案欄位資訊」表(Field Mapping Info)要把每個已配對欄位
/// 以事務所慣用的正準中文名列出;E2 的欄位配對匯入 round-trip 也要用同一份對照反向重建配對。
/// 把對照集中成單一資料結構,匯出與 round-trip 共用、避免雙向各寫一份而發散。
///
/// 與 <see cref="GlFieldWhitelist"/> 的關係:白名單是 logical key → 實體欄(SQL 識別字防線);
/// 本表是 logical key → 對外顯示/匯入比對用的正準中文名。兩者概念相鄰但用途不同,故分立。
/// 涵蓋範圍:只列樣本 Field Mapping Info 有對應正準名的鍵;樣本未給正準名者
/// (docDate/voucherDate/jeSource 等)刻意不列——不臆造名稱(no silent assumption)。
/// </summary>
public static class GlCanonicalNames
{
    /// <summary>GL 欄位的 logical key → 正準中文名(對照樣本 Field Mapping Info 配對後欄名)。</summary>
    public static IReadOnlyDictionary<string, string> Gl { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["docNum"] = "傳票號碼_JE",
            ["lineID"] = "傳票文件項次_JE_S",
            ["postDate"] = "總帳日期_JE",
            ["createBy"] = "傳票建立人員_JE",
            ["approveBy"] = "傳票核准人員_JE",
            ["accNum"] = "會計科目編號_JE",
            ["accName"] = "會計科目名稱_JE",
            ["amount"] = "傳票金額_JE",
            ["description"] = "傳票摘要_JE"
        };

    /// <summary>TB 欄位的 logical key → 正準中文名(對照樣本 Field Mapping Info 配對後欄名)。</summary>
    public static IReadOnlyDictionary<string, string> Tb { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accNum"] = "會計科目編號_TB",
            ["accName"] = "會計科目名稱_TB",
            ["changeAmount"] = "試算表變動金額_TB"
        };
}
