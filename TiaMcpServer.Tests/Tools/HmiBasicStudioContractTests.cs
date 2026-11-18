using Microsoft.Extensions.Logging.Abstractions;
using TiaMcpServer.Contracts.HmiBasic;
using TiaMcpServer.Contracts.Project;
using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.HmiBasic;
using TiaMcpServer.Json;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Safety;
using TiaMcpServer.Worker;
using Xunit;

namespace TiaMcpServer.Tests.Tools;

public sealed class HmiBasicStudioContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"devices\":null}")]
    [InlineData("{\"devices\":[{\"device\":\"HMI_1\",\"hmi\":\"HMI\",\"tagTables\":[],\"screens\":null}]}")]
    public void InventoryRejectsMissingOrNullWorkerMembers(string payload)
        => Assert.Throws<System.Text.Json.JsonException>(() => HmiBasicStudioTools.DecodeList(payload));

    [Fact]
    public void InventoryPreservesStructuredTagTables()
    {
        var result = HmiBasicStudioTools.DecodeList("{\"devices\":[{\"device\":\"Panel\",\"hmi\":\"Runtime\",\"tagTables\":[{\"path\":\"Folder/Tags\",\"count\":3}],\"screens\":[\"Folder/Main\"]}]}");
        Assert.Equal("Folder/Tags", Assert.Single(Assert.Single(result.Devices).TagTables).Path);
        Assert.Equal(3, result.Devices[0].TagTables[0].Count);
    }

    [Fact]
    public void ApprovalIdentityChangesWithProjectBindingDeviceAndHmi()
    {
        var binding = new ProjectBindingSnapshot("verified", "binding-a", 1, @"C:\Projects\A.ap21", "worker", 1, 42, null);
        var first = new HmiBasicDevice { Device = "Panel", Hmi = "Runtime" };
        var identity = HmiBasicStudioTools.DestinationIdentity(binding, first);
        Assert.NotEqual(identity, HmiBasicStudioTools.DestinationIdentity(new("verified", "binding-a", 1, @"C:\Projects\B.ap21", "worker", 1, 42, null), first));
        Assert.NotEqual(identity, HmiBasicStudioTools.DestinationIdentity(new("verified", "binding-b", 1, @"C:\Projects\A.ap21", "worker", 1, 42, null), first));
        Assert.NotEqual(identity, HmiBasicStudioTools.DestinationIdentity(binding, new() { Device = "Other", Hmi = "Runtime" }));
        Assert.NotEqual(identity, HmiBasicStudioTools.DestinationIdentity(binding, new() { Device = "Panel", Hmi = "Other" }));
        Assert.Equal(identity, HmiBasicStudioTools.DestinationIdentity(binding, new() { Device = "PANEL", Hmi = "Runtime" }));
    }

    [Fact]
    public void OversizedInventoryIsOmittedWithBudgetDisclosure()
    {
        var response = HmiBasicStudioBudget.Apply(HmiBasicStudioTools.Empty("list") with { Success = true,
            Inventory = new() { Devices = [new() { Device = new string('x', StructuredOperationBatchPayloadBudget.MaxItemChars), Hmi = "Runtime" }] } });
        Assert.False(response.Success);
        Assert.Null(response.Inventory);
        Assert.NotNull(response.Omission);
        Assert.True(CanonicalJson.Serialize(response).Length <= StructuredOperationBatchPayloadBudget.MaxDocumentChars);
    }

    [Fact]
    public void ImportUsesExactCapabilityAndRejectsApprovalInFullMode()
    {
        using var client = new OpennessWorkerClient(new ProjectSessionBinding(null), NullLogger<OpennessWorkerClient>.Instance,
            accessPolicy: new OperationAccessPolicy(McpAccessMode.Full));
        var domain = new HmiBasicWriteDomain(client);
        var item = new HmiBasicImportItem("Panel", @"C:\Preview", new string('a', 64), false, false);
        Assert.Equal(new[] { "hmi_basic_import_and_compile" }, domain.CapabilityOperations([item]));
        Assert.Equal("access_denied", domain.Validate([item], McpAccessMode.Full).Error!.Category);
        var malformed = domain.Project(item, WorkerCallResult.Ok("{}"));
        Assert.Equal("protocol_error", malformed.Failure!.Category);
    }

    [Fact]
    public void BasicHmiWorkerPolicyDistinguishesObserveAndMutation()
    {
        Assert.Equal(OperationCapability.Observe, OperationPolicyCatalog.GetCapability(HmiBasicMethods.List));
        Assert.Equal(OperationCapability.Observe, OperationPolicyCatalog.GetCapability(HmiBasicMethods.Export));
        Assert.Equal(OperationCapability.Observe, OperationPolicyCatalog.GetCapability(HmiBasicMethods.PlanImport));
        Assert.Equal(OperationCapability.ProjectMutation, OperationPolicyCatalog.GetCapability(HmiBasicMethods.ImportAndCompile));
    }

    [Fact]
    public void DesignWritesFixedSizePreviewAndLoadableArtifacts()
    {
        var output = Environment.GetEnvironmentVariable("TIA_HMI_PREVIEW_TEST_OUT");
        output = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(Path.GetTempPath(), "tia-hmi-preview-test-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(output);
        var preserve = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIA_HMI_PREVIEW_TEST_OUT"));
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        try
        {
            var preview = HmiBasicDesigner.Design(output, null, null, null, null);
            Assert.Equal(800, preview.Width);
            Assert.Equal(480, preview.Height);
            Assert.True(File.Exists(Path.Combine(output, "preview.html")));
            Assert.True(File.Exists(Path.Combine(output, "screen.xml")));
            Assert.True(File.Exists(Path.Combine(output, "tags.xml")));
            Assert.Equal(64, preview.PreviewDigest.Length);
        }
        finally
        {
            if (!preserve && Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
