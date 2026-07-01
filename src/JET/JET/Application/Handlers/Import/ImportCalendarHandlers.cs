using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.holiday / import.makeupDay 的共同流程。
/// Payload `{ dates: ["yyyy-MM-dd", …] }`；replace 語意（同 type 先清後寫）；重複日期只存一次。
/// </summary>
public abstract class ImportCalendarHandler(
    ICalendarStore calendarStore,
    IProjectSession session) : IApplicationActionHandler
{
    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var dates = PayloadReader.GetStringList(payload, "dates");

        foreach (var date in dates)
        {
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"dates 內含無效日期 '{date}'，必須是 yyyy-MM-dd 格式。");
            }
        }

        var distinctDates = dates.Distinct(StringComparer.Ordinal).ToList();

        await calendarStore.ReplaceDaysAsync(
            projectId,
            DayType,
            distinctDates.Select(date => new CalendarDayEntry(date, null)).ToList(),
            cancellationToken);

        return new { count = distinctDates.Count };
    }
}

public sealed class ImportHolidayHandler(ICalendarStore calendarStore, IProjectSession session)
    : ImportCalendarHandler(calendarStore, session)
{
    public override string Action => "import.holiday";

    protected override CalendarDayType DayType => CalendarDayType.Holiday;
}

public sealed class ImportMakeupDayHandler(ICalendarStore calendarStore, IProjectSession session)
    : ImportCalendarHandler(calendarStore, session)
{
    public override string Action => "import.makeupDay";

    protected override CalendarDayType DayType => CalendarDayType.Makeup;
}

/// <summary>
/// import.holiday.fromFile / import.makeupDay.fromFile:事務所行事曆檔匯入(spec F)。
/// 僅 .xlsx;標頭在第 2 列(LeadingRowsToSkip=1);欄位辨識與投影在 Domain;
/// replace 語意,store 在同交易清規則結果。
/// </summary>
public abstract class ImportCalendarFromFileHandler(
    ITabularFileReader reader,
    ICalendarStore calendarStore,
    IProjectSession session) : IApplicationActionHandler
{
    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var sheetName = PayloadReader.GetOptionalString(payload, "sheetName");

        if (!File.Exists(filePath))
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, $"找不到檔案 '{filePath}'。");
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension != ".xlsx")
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{extension}',行事曆檔僅支援 .xlsx。");
        }

        var request = new TabularSourceRequest(filePath, SheetName: sheetName, LeadingRowsToSkip: 1);
        var type = DayType;

        var entries = await Task.Run(async () =>
        {
            var columns = await reader.ReadColumnsAsync(request, cancellationToken);
            var resolution = CalendarImportColumnResolver.Resolve(columns, type);

            var collected = new List<CalendarDayEntry>();
            var errors = new List<CalendarImportRowError>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            await foreach (var row in reader.ReadRowsAsync(request, cancellationToken))
            {
                switch (CalendarDayProjector.Project(row, type, resolution, out var entry, out var error))
                {
                    case CalendarRowOutcome.Included:
                        if (seen.Add(entry.Date))
                        {
                            collected.Add(entry);
                        }

                        break;
                    case CalendarRowOutcome.Failed:
                        errors.Add(error);
                        break;
                    // Skipped:略過
                }
            }

            if (errors.Count > 0)
            {
                var detail = string.Join("；", errors.Take(10).Select(e => e.Message));
                throw new JetActionException(
                    JetErrorCodes.ProjectionFailed,
                    $"行事曆檔有 {errors.Count} 列無法解析:{detail}");
            }

            return collected;
        }, cancellationToken);

        await calendarStore.ReplaceDaysAsync(projectId, type, entries, cancellationToken);

        return new { count = entries.Count };
    }
}

public sealed class ImportHolidayFromFileHandler(
    ITabularFileReader reader, ICalendarStore calendarStore, IProjectSession session)
    : ImportCalendarFromFileHandler(reader, calendarStore, session)
{
    public override string Action => "import.holiday.fromFile";

    protected override CalendarDayType DayType => CalendarDayType.Holiday;
}

public sealed class ImportMakeupDayFromFileHandler(
    ITabularFileReader reader, ICalendarStore calendarStore, IProjectSession session)
    : ImportCalendarFromFileHandler(reader, calendarStore, session)
{
    public override string Action => "import.makeupDay.fromFile";

    protected override CalendarDayType DayType => CalendarDayType.Makeup;
}

/// <summary>
/// calendar.setNonWorkingDays:設定每案「非工作日是週幾」(.NET DayOfWeek 編碼,週日=0…週六=6),
/// 預設週六、週日。寫入 project.json;影響週末過帳/核准規則與週末篩選條件。
/// Payload `{ days: [int, …] }`;值需在 0–6,否則 invalid_payload。
/// </summary>
public sealed class CalendarSetNonWorkingDaysHandler(
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "calendar.setNonWorkingDays";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var normalized = NonWorkingDays.Validate(PayloadReader.GetIntList(payload, "days"));

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        await projectStore.SaveAsync(document with { NonWorkingDays = normalized }, cancellationToken);

        return new { ok = true, nonWorkingDays = normalized };
    }
}
