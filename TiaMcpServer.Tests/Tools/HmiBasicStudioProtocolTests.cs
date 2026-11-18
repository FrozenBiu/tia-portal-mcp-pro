using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.Tests.TestSupport;
using Xunit;

namespace TiaMcpServer.Tests.Tools;

[Collection("Mcp protocol serial")]
public sealed class HmiBasicStudioProtocolTests
{
    [Theory]
    [InlineData(McpAccessMode.ReadWrite)]
    [InlineData(McpAccessMode.Full)]
    public async Task ImportWithoutApprovalIsRejectedBeforeWorkerDispatch(McpAccessMode mode)
    {
        using var audit = new TempAuditDirectory();
        Directory.CreateDirectory(audit.Path);
        using var requests = new FakeWorkerRequestLog(audit.Path);
        await using var harness = await McpProtocolTestHarness.StartProductionSurfaceAsync(mode, audit.Path);
        var before = requests.Methods();

        var result = await harness.Client.CallToolAsync("hmi_basic_studio", new Dictionary<string, object?>
        {
            ["action"] = "import_and_compile", ["device"] = "HMI_1",
            ["out"] = audit.Path, ["previewDigest"] = new string('a', 64), ["approved"] = false,
        });

        Assert.True(result.IsError == true);
        Assert.Equal("access_denied", result.StructuredContent!.Value.GetProperty("error").GetProperty("category").GetString());
        Assert.Equal(before, requests.Methods());
        Assert.Single(Network.NetworkGuardedWriteMcpTests.AuditLines(audit.Path));
        Assert.Empty(StructuredContractInspector.FindViolations(result));
    }

    [Fact]
    public async Task ListWithoutBindingFailsClosed()
    {
        await using var harness = await McpProtocolTestHarness.StartProductionSurfaceAsync(McpAccessMode.Full);
        var result = await harness.Client.CallToolAsync("hmi_basic_studio", new Dictionary<string, object?> { ["action"] = "list" });
        Assert.True(result.IsError == true);
        Assert.Equal("binding_conflict", result.StructuredContent!.Value.GetProperty("error").GetProperty("category").GetString());
        Assert.Empty(StructuredContractInspector.FindViolations(result));
    }

    [Fact]
    public async Task ImportRequiresOneDeviceBeforeBindingOrArtifactReads()
    {
        await using var harness = await McpProtocolTestHarness.StartProductionSurfaceAsync(McpAccessMode.Full);
        var result = await harness.Client.CallToolAsync("hmi_basic_studio", new Dictionary<string, object?>
        {
            ["action"] = "import_and_compile", ["approved"] = true,
            ["out"] = Path.GetTempPath(), ["previewDigest"] = new string('a', 64),
        });
        Assert.True(result.IsError == true);
        Assert.Equal("validation_error", result.StructuredContent!.Value.GetProperty("error").GetProperty("category").GetString());
    }
}
