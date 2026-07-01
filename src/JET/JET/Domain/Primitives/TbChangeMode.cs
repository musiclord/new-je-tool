namespace JET.Domain;

/// <summary>
/// TB 變動金額計算模式。OpenClose / OpenCloseBySide（jet-guide.md §2.2）
/// 尚無對應 mapping keys，待契約擴充後再加入。
/// </summary>
public enum TbChangeMode
{
    DirectChange,
    DebitCredit
}

public static class TbChangeModeNames
{
    public const string Direct = "direct";
    public const string DebitCredit = "debitCredit";

    public static bool TryParse(string? name, out TbChangeMode mode)
    {
        var normalized = name?.Trim();

        if (string.Equals(normalized, Direct, StringComparison.OrdinalIgnoreCase))
        {
            mode = TbChangeMode.DirectChange;
            return true;
        }

        if (string.Equals(normalized, DebitCredit, StringComparison.OrdinalIgnoreCase))
        {
            mode = TbChangeMode.DebitCredit;
            return true;
        }

        mode = default;
        return false;
    }

    public static string ToWireName(TbChangeMode mode) => mode switch
    {
        TbChangeMode.DirectChange => Direct,
        TbChangeMode.DebitCredit => DebitCredit,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}
