using JET.Application;
using JET.Domain;
using JET.Bridge;
using Xunit;

namespace JET.Tests.Bridge;

public sealed class BridgeErrorMappingTests
{
    [Fact]
    public void JetActionException_SurfacesItsCode()
    {
        var dto = JetWebMessageBridge.ToErrorDto(
            new JetActionException(JetErrorCodes.FileNotFound, "找不到檔案"));

        Assert.Equal("file_not_found", dto.Code);
        Assert.Equal("找不到檔案", dto.Message);
    }

    [Fact]
    public void ArbitraryException_FallsBackToBridgeError()
    {
        var dto = JetWebMessageBridge.ToErrorDto(new InvalidOperationException("boom"));

        Assert.Equal("bridge_error", dto.Code);
        Assert.Equal("boom", dto.Message);
    }

    [Fact]
    public void UnknownAction_FallsBackToBridgeError()
    {
        var dto = JetWebMessageBridge.ToErrorDto(
            new KeyNotFoundException("No JET action handler is registered for 'x.y'."));

        Assert.Equal("bridge_error", dto.Code);
    }
}
