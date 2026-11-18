using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Contracts.HmiBasic;
using TiaMcpServer.Contracts.Project;
using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.Contracts.Worker;
using TiaMcpServer.Json;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Safety.Pipeline;
using TiaMcpServer.Tools;
using TiaMcpServer.Worker;

namespace TiaMcpServer.HmiBasic;

public sealed record HmiBasicExportFile(string Device, string Hmi, string Kind, string Path, string Xml, string OutputPath);
public sealed record HmiBasicStudioResponse(string Tool, string ContractVersion, string Action, bool Success,
    WriteToolError? Error, IReadOnlyList<string> Warnings, string? Phase,
    HmiBasicListResult? Inventory, HmiBasicExportFile? Export, HmiBasicPreview? Preview,
    IReadOnlyList<WriteGuardReport> Guards, IReadOnlyList<WriteEffect<HmiBasicImportPlan>> Effects,
    StructuredOperationBatch? Batch, WriteVerification? Verification, StructuredOperationOmission? Omission);

[McpServerToolType]
public sealed class HmiBasicStudioTools
{
    internal const string ToolName = "hmi_basic_studio";

    [McpServerTool(Name = ToolName, ReadOnly = false, Destructive = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HmiBasicStudioResponse))]
    [Description("WinCC Basic/Comfort/Advanced studio for a verified already-open standalone project. Actions: list (optional device), export (device, kind tag|screen, name, out absolute new XML file), design_and_preview (out absolute unused directory, spec or sourceScreen+sourceTags XML paths), import_and_compile (device, out preview directory, previewDigest, approved:true; optional replaceExisting). Design with device binds approval to the current verified project and resolved device/HMI; omit device for an offline preview that cannot be imported. Review preview.html before explicitly approving its digest and target. Approval is mandatory even in full mode. Import validates unchanged preview artifacts, imports tags then screen and compiles; failure may leave earlier stages applied. No implicit save, download or device control. Export refuses overwrite. Offline source previews never attach to TIA Portal.")]
    public static async Task<CallToolResult> HmiBasicStudio(OpennessWorkerClient workerClient, WriteExecution execution,
        string action, string? device = null, string? kind = null, string? name = null, string? @out = null,
        JsonElement? spec = null, string? sourceScreen = null, string? sourceTags = null,
        string? previewDigest = null, bool approved = false, bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        if (action == "import_and_compile")
            return await execution.RunAsync(new HmiBasicWriteDomain(workerClient),
                new WriteCall<HmiBasicImportItem>([new(device, @out, previewDigest, approved, replaceExisting)], false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        if (workerClient.AccessPolicy.Mode == McpAccessMode.ReadOnly)
            return Error(WorkerFailureCategories.AccessDenied, "hmi_basic_studio requires a writable access mode.", action);
        if (action is not ("list" or "export" or "design_and_preview"))
            return Error(WorkerFailureCategories.ValidationError, "action must be list, export, design_and_preview or import_and_compile.", action);
        if (device is not null && string.IsNullOrWhiteSpace(device))
            return Error(WorkerFailureCategories.ValidationError, "device must be nonblank when provided.", action);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action == "design_and_preview" && device is null)
                return Present(Empty(action) with { Success = true, Preview = HmiBasicDesigner.Design(RequiredOutput(@out), spec, sourceScreen, sourceTags, null) });
            if (action == "export" && (kind is not ("tag" or "screen") || string.IsNullOrWhiteSpace(name)))
                return Error(WorkerFailureCategories.ValidationError, "export requires kind tag or screen and a nonblank name.", action);
            if (action == "export") _ = RequiredOutput(@out);
            if (action == "design_and_preview") _ = RequiredOutput(@out);
            var binding = await workerClient.RequireVerifiedBindingAsync().ConfigureAwait(false);
            if (!binding.Success) return Failure(binding, action);
            var pinned = await workerClient.ExecuteWithPinnedBindingAsync(workerClient.BindingSnapshot, async () =>
            {
                if (action == "list")
                {
                    var result = await workerClient.HmiBasicAsync(HmiBasicMethods.List, new() { Device = device }).ConfigureAwait(false);
                    return result.Success ? Present(Empty(action) with { Success = true, Inventory = DecodeList(result.Payload), Warnings = result.Warnings }) : Failure(result, action);
                }
                if (action == "export")
                {
                    var result = await workerClient.HmiBasicAsync(HmiBasicMethods.Export, new() { Device = device, Kind = kind, Name = name }).ConfigureAwait(false);
                    if (!result.Success) return Failure(result, action);
                    var value = CanonicalJson.DeserializeWorkerPayload<HmiBasicExportResult>(result.Payload);
                    if (string.IsNullOrWhiteSpace(value.Device) || string.IsNullOrWhiteSpace(value.Hmi)
                        || value.Kind != kind || string.IsNullOrWhiteSpace(value.Path) || string.IsNullOrWhiteSpace(value.Xml)) throw new JsonException();
                    var path = RequiredOutput(@out);
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(value.Xml);
                    return Present(Empty(action) with { Success = true, Warnings = result.Warnings,
                        Export = new(value.Device, value.Hmi, value.Kind, value.Path, value.Xml, path) });
                }
                var resolved = await ResolveDevice(workerClient, device!).ConfigureAwait(false);
                if (resolved.Error is not null) return Failure(resolved.Error, action);
                var identity = DestinationIdentity(workerClient.BindingSnapshot, resolved.Device!);
                return Present(Empty(action) with { Success = true,
                    Preview = HmiBasicDesigner.Design(RequiredOutput(@out), spec, sourceScreen, sourceTags, identity) });
            }, cancellationToken).ConfigureAwait(false);
            return pinned.Success ? pinned.Value! : Failure(pinned.Failure!, action);
        }
        catch (JsonException) { return Error(WorkerFailureCategories.ProtocolError, "The Basic HMI worker payload did not match its declared contract.", action); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
        { return Error(WorkerFailureCategories.ValidationError, ex.Message, action); }
    }

    internal static string RequiredOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("out must be an absolute output path.");
        return Path.GetFullPath(path);
    }

    internal static HmiBasicListResult DecodeList(string payload)
    {
        var value = CanonicalJson.DeserializeWorkerPayload<HmiBasicListResult>(payload);
        if (value.Devices is null || value.Devices.Any(d => d is null || string.IsNullOrWhiteSpace(d.Device)
            || string.IsNullOrWhiteSpace(d.Hmi) || d.Screens is null || d.Screens.Any(string.IsNullOrWhiteSpace)
            || d.TagTables is null || d.TagTables.Any(t => t is null || string.IsNullOrWhiteSpace(t.Path) || t.Count < 0))) throw new JsonException();
        return value;
    }

    internal static async Task<(HmiBasicDevice? Device, WorkerCallResult? Error)> ResolveDevice(OpennessWorkerClient client, string device)
    {
        var result = await client.HmiBasicAsync(HmiBasicMethods.List, new() { Device = device }).ConfigureAwait(false);
        if (!result.Success) return (null, result);
        var matches = DecodeList(result.Payload).Devices.Where(d => string.Equals(d.Device, device, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? (matches[0], null) : (null, WorkerCallResult.Fail(
            matches.Length == 0 ? WorkerFailureCategories.TargetNotFound : WorkerFailureCategories.TargetAmbiguous,
            "device must resolve to exactly one Basic/Comfort/Advanced HMI."));
    }

    internal static string DestinationIdentity(ProjectBindingSnapshot binding, HmiBasicDevice device)
    {
        if (!binding.IsVerified || string.IsNullOrWhiteSpace(binding.ProjectPath)) throw new ArgumentException("A verified project binding is required.");
        return CanonicalJson.Serialize(new { projectPath = binding.ProjectPath.ToUpperInvariant(), binding.BindingId,
            binding.Revision, binding.WorkerSessionId, binding.SessionGeneration, binding.PortalProcessId,
            device = device.Device.ToUpperInvariant(), hmi = device.Hmi });
    }

    internal static HmiBasicStudioResponse Empty(string action) => new(ToolName, "1.0",
        action is "list" or "export" or "design_and_preview" or "import_and_compile" ? action : "unknown",
        false, null, [], null, null, null, null, [], [], null, null, null);
    internal static CallToolResult Error(string category, string message, string action = "unknown")
        => Present(Empty(action) with { Error = new(category, message) });
    private static CallToolResult Failure(WorkerCallResult failure, string action)
        => Present(Empty(action) with { Error = new(failure.FailureCategory ?? WorkerFailureCategories.WorkerOperationFailed,
            failure.Error ?? "The Basic HMI operation failed."), Warnings = failure.Warnings });
    internal static CallToolResult Present(HmiBasicStudioResponse response)
        => StructuredToolResult.CreateCanonical(CanonicalJson.Serialize(HmiBasicStudioBudget.Apply(response)), response.Error is not null);
}
