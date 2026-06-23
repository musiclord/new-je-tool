using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 前端 Query Builder 的 scenario JSON → FilterScenarioSpec。
/// 此層只處理「形狀」（型別字串、金額轉 scaled、關鍵字分割）；
/// 業務規則（白名單、必填、邊界）由 Domain FilterScenarioValidator 負責。
/// 形狀錯誤一律擲 invalid_scenario。
/// </summary>
public static class FilterScenarioPayloadParser
{
    public static FilterScenarioSpec Parse(JsonElement scenario, int moneyScale)
    {
        if (scenario.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("scenario 必須是物件。");
        }

        var name = ReadString(scenario, "name") ?? string.Empty;
        var rationale = ReadString(scenario, "rationale") ?? string.Empty;

        // 選填來源標記（manifest scenario.source）：ReadString 已 trim 並把空白正規化為 null，
        // 故未知/空白來源自然落為「查核員手寫」（Domain 只認得 "kct"，其餘等同 null）。
        var source = ReadString(scenario, "source");

        var groups = new List<FilterGroupSpec>();
        if (scenario.TryGetProperty("groups", out var groupsElement)
            && groupsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var groupElement in groupsElement.EnumerateArray())
            {
                groups.Add(ParseGroup(groupElement, moneyScale));
            }
        }

        return new FilterScenarioSpec(name.Trim(), rationale.Trim(), groups, source);
    }

    private static FilterGroupSpec ParseGroup(JsonElement group, int moneyScale)
    {
        var rules = new List<FilterRuleSpec>();
        if (group.ValueKind == JsonValueKind.Object
            && group.TryGetProperty("rules", out var rulesElement)
            && rulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                rules.Add(ParseRule(ruleElement, moneyScale));
            }
        }

        return new FilterGroupSpec(ParseJoin(group), rules);
    }

    private static FilterRuleSpec ParseRule(JsonElement rule, int moneyScale)
    {
        var typeName = ReadString(rule, "type") ?? string.Empty;
        var type = typeName switch
        {
            "prescreen" => FilterRuleType.Prescreen,
            "text" => FilterRuleType.Text,
            "dateRange" => FilterRuleType.DateRange,
            "numRange" => FilterRuleType.NumRange,
            "drCrOnly" => FilterRuleType.DrCrOnly,
            "manualAuto" => FilterRuleType.ManualAuto,
            "accountPair" => FilterRuleType.AccountPair,
            "specialAccountCategoryPair" => FilterRuleType.SpecialAccountCategoryPair,
            "periodInOut" => FilterRuleType.PeriodInOut,
            "customKeywords" => FilterRuleType.CustomKeywords,
            "customTrailingZeros" => FilterRuleType.CustomTrailingZeros,
            "customPreparerEntryCount" => FilterRuleType.CustomPreparerEntryCount,
            "customAccountEntryCount" => FilterRuleType.CustomAccountEntryCount,
            "revenueDebitNearQuarterEnd" => FilterRuleType.RevenueDebitNearQuarterEnd,
            "revenueWithoutNormalCounterpart" => FilterRuleType.RevenueWithoutNormalCounterpart,
            "manualRevenueEntry" => FilterRuleType.ManualRevenueEntry,
            "trailingDigits" => FilterRuleType.TrailingDigits,
            "preparerEqualsApprover" => FilterRuleType.PreparerEqualsApprover,
            _ => throw Invalid($"不支援的條件型別「{typeName}」。")
        };

        var modeName = ReadString(rule, "mode") ?? "contains";
        var mode = modeName switch
        {
            "contains" => TextMatchMode.Contains,
            "exact" => TextMatchMode.Exact,
            "notContains" => TextMatchMode.NotContains,
            "notExact" => TextMatchMode.NotExact,
            _ => throw Invalid($"不支援的文字比對模式「{modeName}」。")
        };

        var keywords = (ReadString(rule, "keywords") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        long? fromScaled = null;
        long? toScaled = null;
        string? fromDate = null;
        string? toDate = null;

        if (type == FilterRuleType.NumRange)
        {
            fromScaled = ParseAmount(ReadString(rule, "from"), moneyScale);
            toScaled = ParseAmount(ReadString(rule, "to"), moneyScale);
        }
        else if (type == FilterRuleType.DateRange)
        {
            fromDate = ReadString(rule, "from");
            toDate = ReadString(rule, "to");
        }

        return new FilterRuleSpec(
            ParseJoin(rule),
            type,
            ReadString(rule, "prescreenKey"),
            ReadString(rule, "field"),
            keywords,
            mode,
            fromDate,
            toDate,
            fromScaled,
            toScaled,
            ReadString(rule, "drCr"),
            ParseManual(rule),
            PairMode: ReadString(rule, "pairMode"),
            DebitCategory: ReadString(rule, "debitCategory"),
            CreditCategory: ReadString(rule, "creditCategory"),
            InPeriod: ParseBool(rule, "inPeriod"),
            Digits: ParseInt(rule, "digits"),
            MaxEntries: ParseInt(rule, "maxEntries"),
            WindowDays: ParseInt(rule, "windowDays"));
    }

    private static bool? ParseBool(JsonElement rule, string name)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? ParseInt(JsonElement rule, string name)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static FilterJoin ParseJoin(JsonElement element)
    {
        var join = ReadString(element, "join");
        return string.Equals(join, "OR", StringComparison.OrdinalIgnoreCase)
            ? FilterJoin.Or
            : FilterJoin.And;
    }

    private static long? ParseAmount(string? text, int moneyScale)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!MoneyScaling.TryParseAmount(text, out var value)
            || !MoneyScaling.TryToScaled(value, moneyScale, out var scaled))
        {
            throw Invalid($"金額「{text}」格式無效。");
        }

        return scaled;
    }

    private static bool? ParseManual(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("isManual", out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }

    private static JetActionException Invalid(string message) =>
        new(JetErrorCodes.InvalidScenario, message);
}
