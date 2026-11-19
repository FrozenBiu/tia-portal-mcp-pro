using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.Tests.TestSupport;
using Xunit;

namespace TiaMcpServer.Tests.Tools;

[Collection("Mcp protocol serial")]
public sealed class HmiBasicStudioProtocolTests
{
    [Theory]
    [InlineData("Panel", "Graphic_1", 25, null)]
    [InlineData("Panel", "Graphic_1", "25", "validation_error")]
    [InlineData("Panel", "Missing", 25, "validation_error")]
    [InlineData("Unavailable", "Graphic_1", 25, "target_kind_unsupported")]
    public async Task BoundGraphicPreviewUsesWorkerInventory(string device, string picture, object x, string? error)
    {
        using var temp = new TempAuditDirectory();
        Directory.CreateDirectory(temp.Path);
        var screen = Path.Combine(temp.Path, "source.xml");
        var output = Path.Combine(temp.Path, "preview");
        File.WriteAllText(screen, """
            <Document><Hmi.Screen.Screen ID="0"><AttributeList><Name>Base</Name></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID="1"><ObjectList><Hmi.Screen.GraphicView ID="2" CompositionName="ScreenItems"><AttributeList><Height>59</Height><Left>2</Left><ObjectName>Prototype</ObjectName><Top>184</Top><Width>798</Width></AttributeList><LinkList><Picture TargetID="@OpenLink"><Name>Graphic_1</Name></Picture></LinkList></Hmi.Screen.GraphicView></ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>
            """);
        await using var harness = await McpProtocolTestHarness.StartProductionSurfaceAsync(
            McpAccessMode.Full, startupProjectPath: "hmi-basic-graphics");
        var result = await harness.Client.CallToolAsync("hmi_basic_studio", new Dictionary<string, object?>
        {
            ["action"] = "design_and_preview", ["device"] = device, ["out"] = output, ["sourceScreen"] = screen,
            ["spec"] = new { graphic = new { picture, objectName = "NewGraphic", x, y = 10, width = 60, height = 30 } },
        });
        var document = result.StructuredContent!.Value;
        if (error is not null)
        {
            Assert.True(result.IsError == true);
            Assert.Equal(error, document.GetProperty("error").GetProperty("category").GetString());
            Assert.False(Directory.Exists(output));
        }
        else
        {
            Assert.True(document.GetProperty("success").GetBoolean(), document.GetRawText());
            Assert.Single(document.GetProperty("warnings").EnumerateArray());
            Assert.Contains("Graphic reference: Graphic_1 @ (25, 10), 60 x 30", File.ReadAllText(Path.Combine(output, "preview.html")));
            Assert.Equal(64, document.GetProperty("preview").GetProperty("previewDigest").GetString()!.Length);
        }
        Assert.Empty(StructuredContractInspector.FindViolations(result));
    }

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
