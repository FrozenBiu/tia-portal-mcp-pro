using Microsoft.Extensions.Logging.Abstractions;
using TiaMcpServer.Contracts.HmiBasic;
using TiaMcpServer.Contracts.Project;
using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.HmiBasic;
using TiaMcpServer.Json;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Safety;
using TiaMcpServer.Worker;
using System.Xml.Linq;
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
        var result = HmiBasicStudioTools.DecodeList("{\"devices\":[{\"device\":\"Panel\",\"hmi\":\"Runtime\",\"tagTables\":[{\"path\":\"Folder/Tags\",\"count\":3}],\"screens\":[\"Folder/Main\"],\"graphics\":[],\"graphicsCoverage\":\"unavailable\"}]}");
        Assert.Equal("Folder/Tags", Assert.Single(Assert.Single(result.Devices).TagTables).Path);
        Assert.Equal(3, result.Devices[0].TagTables[0].Count);
    }

    [Fact]
    public void InventoryPreservesProjectGraphicsAndCoverage()
    {
        var result = HmiBasicStudioTools.DecodeList("{\"devices\":[{\"device\":\"Panel\",\"hmi\":\"Runtime\",\"tagTables\":[],\"screens\":[],\"graphics\":[\"Graphic_1\",\"Equipment/Pump\"],\"graphicsCoverage\":\"provider\"}]}");
        var device = Assert.Single(result.Devices);
        Assert.Equal(new[] { "Graphic_1", "Equipment/Pump" }, device.Graphics);
        Assert.Equal("provider", device.GraphicsCoverage);
    }

    [Fact]
    public void InventoryRejectsInvalidGraphicsCoverage()
        => Assert.Throws<System.Text.Json.JsonException>(() => HmiBasicStudioTools.DecodeList(
            "{\"devices\":[{\"device\":\"Panel\",\"hmi\":\"Runtime\",\"tagTables\":[],\"screens\":[],\"graphics\":[],\"graphicsCoverage\":\"toolbox\"}]}"));

    [Fact]
    public void InventoryRejectsMissingGraphicsEvidenceMembers()
        => Assert.Throws<System.Text.Json.JsonException>(() => HmiBasicStudioTools.DecodeList(
            "{\"devices\":[{\"device\":\"Panel\",\"hmi\":\"Runtime\",\"tagTables\":[],\"screens\":[]}]}"));

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

    [Fact]
    public void DesignPlacesOnlyOneProjectLinkedGraphicAndBindsItIntoDigest()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        using var spec = System.Text.Json.JsonDocument.Parse("""{"screen":"Overview","table":"Tags","graphic":{"picture":"Graphic_2","objectName":"PumpGraphic","x":25,"y":40,"width":120,"height":90}}""");
        var screen = WriteGraphicTemplate("Graphic_1");
        try
        {
            var preview = HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", ["Graphic_1", "Graphic_2"]);
            var xml = File.ReadAllText(Path.Combine(output, "screen.xml"));
            var html = File.ReadAllText(preview.PreviewPath);
            var document = System.Xml.Linq.XDocument.Parse(xml);
            var views = document.Descendants().Where(element => element.Name.LocalName == "Hmi.Screen.GraphicView").ToArray();
            Assert.Equal(2, views.Length);
            Assert.Equal(2, views.Select(view => (string)view.Attribute("ID")!).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(views, view => view.Descendants().Any(element => element.Name.LocalName == "Name" && element.Value == "Graphic_1"));
            Assert.Contains(views, view => view.Descendants().Any(element => element.Name.LocalName == "Name" && element.Value == "Graphic_2"));
            Assert.Contains("<ObjectName>PumpGraphic</ObjectName>", xml, StringComparison.Ordinal);
            Assert.Contains("<Left>25</Left>", xml, StringComparison.Ordinal);
            Assert.Contains("<Top>40</Top>", xml, StringComparison.Ordinal);
            Assert.Contains(">120<", xml, StringComparison.Ordinal);
            Assert.Contains(">90<", xml, StringComparison.Ordinal);
        Assert.Contains("TIA graphic reference: Graphic_2", html, StringComparison.Ordinal);
            Assert.DoesNotContain("data:image", html, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(64, preview.PreviewDigest.Length);
            var approved = HmiBasicDesigner.LoadApproved(output, preview.PreviewDigest, "destination");
            Assert.Contains("<Name>Graphic_2</Name>", approved.ScreenXml, StringComparison.Ordinal);
            Assert.Contains("<Name>Graphic_1</Name>", approved.ScreenXml, StringComparison.Ordinal);
            File.AppendAllText(Path.Combine(output, "screen.xml"), " ");
            Assert.Throws<InvalidDataException>(() => HmiBasicDesigner.LoadApproved(output, preview.PreviewDigest, "destination"));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    [Fact]
    public void DesignRejectsGraphicMissingFromSelectedProjectInventory()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        using var spec = System.Text.Json.JsonDocument.Parse("""{"graphic":{"picture":"Missing","objectName":"MissingGraphic","x":0,"y":0,"width":20,"height":20}}""");
        var screen = WriteGraphicTemplate();
        try
        {
            Assert.Throws<ArgumentException>(() => HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", ["Graphic_1"]));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    [Fact]
    public void DesignRejectsGraphicWhenTemplateHasNoGraphicViewPrototype()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        var template = Path.Combine(Path.GetTempPath(), "tia-hmi-screen-template-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(template, "<Document><Hmi.Screen.Screen><AttributeList><Name>Screen</Name></AttributeList><ObjectList /></Hmi.Screen.Screen></Document>");
        using var spec = System.Text.Json.JsonDocument.Parse("""{"graphic":{"picture":"Graphic_1","objectName":"Graphic","x":0,"y":0,"width":20,"height":20}}""");
        try
        {
            Assert.Throws<InvalidDataException>(() => HmiBasicDesigner.Design(output, spec.RootElement, template, null, "destination", ["Graphic_1"]));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            File.Delete(template);
        }
    }

    [Fact]
    public void DesignRejectsGraphicOutsideThePreviewCanvas()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        using var spec = System.Text.Json.JsonDocument.Parse("""{"graphic":{"picture":"Graphic_1","objectName":"Graphic","x":790,"y":0,"width":20,"height":20}}""");
        var screen = WriteGraphicTemplate();
        try
        {
            Assert.Throws<ArgumentException>(() => HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", ["Graphic_1"]));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    [Fact]
    public void DesignEncodesGraphicNameInHtmlPlaceholder()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        var picture = "</text><script>alert(1)</script>";
        using var spec = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            graphic = new { picture, objectName = "SafeName", x = 0, y = 0, width = 20, height = 20 }
        }));
        var screen = WriteGraphicTemplate();
        try
        {
            var preview = HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", [picture]);
            var html = File.ReadAllText(preview.PreviewPath);
            Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
            Assert.Contains("&lt;/text&gt;&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    [Fact]
    public void DesignRejectsGraphicObjectNameCollision()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        using var spec = System.Text.Json.JsonDocument.Parse("""{"graphic":{"picture":"Graphic_2","objectName":"Existing","x":0,"y":0,"width":20,"height":20}}""");
        var screen = WriteGraphicTemplate();
        try
        {
            Assert.Throws<ArgumentException>(() => HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", ["Graphic_2"]));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    [Fact]
    public void DesignReportsPreviewCanvasLimitationForGraphicPlacement()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        using var spec = System.Text.Json.JsonDocument.Parse("""{"graphic":{"picture":"Graphic_2","objectName":"NewGraphic","x":0,"y":0,"width":20,"height":20}}""");
        var screen = WriteGraphicTemplate();
        try
        {
            var preview = HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", ["Graphic_2"]);
            Assert.Contains("Preview canvas is illustrative; requested coordinates are written to the source screen XML.", preview.Warnings);
            var html = File.ReadAllText(preview.PreviewPath);
            Assert.Contains("Preview canvas is illustrative; requested coordinates are written to the source screen XML.", html, StringComparison.Ordinal);
            Assert.Contains("Graphic reference: Graphic_2 @ (0, 0), 20 x 20", html, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    [Fact]
    public void DesignRemapsNestedGraphicViewIdsAndInternalReferences()
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        using var spec = System.Text.Json.JsonDocument.Parse("""{"graphic":{"picture":"Graphic_2","objectName":"NewGraphic","x":0,"y":0,"width":20,"height":20}}""");
        var screen = WriteGraphicTemplate(includeNestedId: true);
        try
        {
            HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", ["Graphic_2"]);
            var document = XDocument.Load(Path.Combine(output, "screen.xml"));
            var ids = document.Descendants().Attributes("ID").Select(attribute => attribute.Value).ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var clonedView = Assert.Single(document.Descendants(), element => element.Name.LocalName == "Hmi.Screen.GraphicView"
                && element.Elements().Where(child => child.Name.LocalName == "AttributeList").SelectMany(list => list.Elements())
                    .Any(value => value.Name.LocalName == "ObjectName" && value.Value == "NewGraphic"));
            var clonedAnimation = Assert.Single(clonedView.Descendants(), element => element.Name.LocalName == "Hmi.Screen.Animation");
            var clonedId = (string)clonedAnimation.Attribute("ID")!;
            Assert.NotEqual("3", clonedId);
            Assert.Equal(clonedId, (string)clonedAnimation.Descendants().Single(element => element.Name.LocalName == "Target").Attribute("TargetID")!);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    [Theory]
    [InlineData("\"25\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("25.5")]
    [InlineData("2147483648")]
    public void DesignRejectsInvalidGraphicCoordinateTypesAsValidation(string x)
    {
        var output = Path.Combine(Path.GetTempPath(), "tia-hmi-graphic-preview-test-" + Guid.NewGuid().ToString("N"));
        using var spec = System.Text.Json.JsonDocument.Parse($"{{\"graphic\":{{\"picture\":\"Graphic_1\",\"objectName\":\"NewGraphic\",\"x\":{x},\"y\":0,\"width\":20,\"height\":20}}}}");
        var screen = WriteGraphicTemplate();
        try
        {
            Assert.Throws<ArgumentException>(() => HmiBasicDesigner.Design(output, spec.RootElement, screen, null, "destination", ["Graphic_1"]));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); File.Delete(screen); }
    }

    private static string WriteGraphicTemplate(string picture = "Graphic_1", bool includeNestedId = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "tia-hmi-screen-template-" + Guid.NewGuid().ToString("N") + ".xml");
        var nested = includeNestedId ? "<ObjectList><Hmi.Screen.Animation ID=\"3\"><AttributeList><Name>Nested</Name></AttributeList><LinkList><Target TargetID=\"3\" /></LinkList></Hmi.Screen.Animation></ObjectList>" : string.Empty;
        File.WriteAllText(path, $"<Document><Hmi.Screen.Screen ID=\"0\"><AttributeList><Width>800</Width><Height>480</Height><Name>Base</Name></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID=\"1\"><AttributeList><Index>0</Index></AttributeList><ObjectList><Hmi.Screen.GraphicView ID=\"2\" CompositionName=\"ScreenItems\"><AttributeList><Height>59</Height><Left>2</Left><ObjectName>Existing</ObjectName><Top>184</Top><Width>798</Width></AttributeList>{nested}<LinkList><Picture TargetID=\"@OpenLink\"><Name>{picture}</Name></Picture></LinkList></Hmi.Screen.GraphicView></ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>");
        return path;
    }
}
