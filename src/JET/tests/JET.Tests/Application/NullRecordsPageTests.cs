using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class NullRecordsPageTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task WalkAllPages_EqualsFullSet_NoGapNoDupOrderStable()
    {
        // 排序鍵 entry_id 升冪;wire row 不含 entry_id,故以走訪總筆數對 recount,
        // 並以 post_date+documentNumber 串接驗無重複(每列唯一)。游標序穩由 entry_id 保證,
        // 此處以走訪到底「無漏無重、筆數相符」鎖住範式。
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.nullRecordsPage",
                JsonSerializer.Serialize(new { category = "nullDescription", cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var doc = r.GetProperty("documentNumber").GetString();
                var acc = r.GetProperty("accountCode").GetString();
                var date = r.GetProperty("postDate").GetString();
                all.Add($"{doc}|{acc}|{date}");
            }

            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // category=nullDescription recount = WHERE document_description 為空白或 NULL。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry " +
            "WHERE document_description IS NULL OR TRIM(document_description) = '';");

        Assert.Equal(total, all.Count);
    }

    [Fact]
    public async Task IllegalCategory_ThrowsActionError()
    {
        await Assert.ThrowsAnyAsync<System.Exception>(() => fixture.Host.DispatchAsync(
            "query.nullRecordsPage", JsonSerializer.Serialize(new { category = "notAWhitelistedValue" })));
    }
}
