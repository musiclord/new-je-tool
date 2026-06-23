using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class DocBalancePageTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task WalkAllPages_EqualsFullSet_NoGapNoDupOrderStable()
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.docBalancePage",
                JsonSerializer.Serialize(new { cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
                all.Add(r.GetProperty("documentNumber").GetString()!);
            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // 借貸不平 = GROUP BY document_number HAVING SUM(amount_scaled)<>0 的組數。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM (" +
            " SELECT document_number FROM target_gl_entry " +
            " GROUP BY document_number HAVING SUM(amount_scaled) <> 0) x;");

        Assert.Equal(total, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.True(all.SequenceEqual(all.OrderBy(x => x, System.StringComparer.Ordinal)));
    }
}
