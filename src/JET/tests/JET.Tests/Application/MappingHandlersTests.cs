using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class MappingHandlersTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2025-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31"
        }
        """;

    [Fact]
    public async Task CommitGl_InvalidAmountMode_ThrowsUnsupportedModeBeforeRepositoryAccess()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "mapping.commit.gl",
            """{ "mapping": {}, "amountMode": "bogus" }"""));

        Assert.Equal(JetErrorCodes.UnsupportedMode, ex.Code);
        Assert.Contains("amountMode 'bogus' 無效", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitTb_InvalidChangeMode_ThrowsUnsupportedModeBeforeRepositoryAccess()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "mapping.commit.tb",
            """{ "mapping": {}, "changeMode": "bogus" }"""));

        Assert.Equal(JetErrorCodes.UnsupportedMode, ex.Code);
        Assert.Contains("changeMode 'bogus' 無效", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureValid_UnknownColumns_ThrowsMappingColumnNotFound()
    {
        var validation = new MappingValidationResult([], ["missing_column", "typo_column"]);

        var ex = Assert.Throws<JetActionException>(() => MappingCommitShared.EnsureValid(validation));

        Assert.Equal(JetErrorCodes.MappingColumnNotFound, ex.Code);
        Assert.Contains("missing_column、typo_column", ex.Message, StringComparison.Ordinal);
    }
}
