using System.Text.Json;
using TiaMcpServer.Contracts.HmiBasic;
using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.Contracts.Worker;
using TiaMcpServer.Json;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Safety.Pipeline;
using TiaMcpServer.Worker;

namespace TiaMcpServer.HmiBasic;

public sealed record HmiBasicImportItem(string? Device, string? OutputDirectory, string? PreviewDigest,
    bool Approved, bool ReplaceExisting) : IOperationBatchItem
{
    public string OperationId => "import";
    public string Operation => "import_and_compile";
}

public sealed class HmiBasicWriteDomain(OpennessWorkerClient client)
    : IWriteDomain<HmiBasicImportItem, HmiBasicImportPlan, WriteVerification, HmiBasicStudioResponse>
{
    private HmiBasicApproved? _approved;
    private HmiBasicImportPlan? _plan;
    public string ToolName => HmiBasicStudioTools.ToolName;
    public string ContractVersion => "1.0";
    public IEnumerable<string> CapabilityOperations(IReadOnlyList<HmiBasicImportItem> items) => [HmiBasicMethods.ImportAndCompile];
    public WriteValidation Validate(IReadOnlyList<HmiBasicImportItem> items, McpAccessMode accessMode)
    {
        if (accessMode == McpAccessMode.ReadOnly) return WriteValidation.Invalid(WorkerFailureCategories.AccessDenied, "hmi_basic_studio requires a writable access mode.");
        if (items.Count != 1) return WriteValidation.Invalid(WorkerFailureCategories.ValidationError, "Exactly one import is required.");
        var item = items[0];
        if (!item.Approved) return WriteValidation.Invalid(WorkerFailureCategories.AccessDenied, "Review the exact preview and destination, then pass approved:true. Approval is required in every mode.");
        if (string.IsNullOrWhiteSpace(item.Device)) return WriteValidation.Invalid(WorkerFailureCategories.ValidationError, "import_and_compile requires one nonblank device.");
        if (item.PreviewDigest is not { Length: 64 } digest || digest != digest.ToLowerInvariant()
            || digest.Any(c => !char.IsAsciiHexDigit(c)))
            return WriteValidation.Invalid(WorkerFailureCategories.ValidationError, "previewDigest must be the 64 hexadecimal characters returned by design_and_preview.");
        try { _ = HmiBasicStudioTools.RequiredOutput(item.OutputDirectory); }
        catch (ArgumentException ex) { return WriteValidation.Invalid(WorkerFailureCategories.ValidationError, ex.Message); }
        return WriteValidation.Valid();
    }

    public async Task<WritePlan<HmiBasicImportPlan>> PlanAsync(IReadOnlyList<HmiBasicImportItem> items)
    {
        var item = items[0];
        try
        {
            var resolved = await HmiBasicStudioTools.ResolveDevice(client, item.Device!).ConfigureAwait(false);
            if (resolved.Error is { } error) return WritePlan<HmiBasicImportPlan>.Fail(error.FailureCategory!, error.Error!);
            _approved = HmiBasicDesigner.LoadApproved(item.OutputDirectory!, item.PreviewDigest!,
                HmiBasicStudioTools.DestinationIdentity(client.BindingSnapshot, resolved.Device!));
            var result = await client.HmiBasicAsync(HmiBasicMethods.PlanImport, new()
            {
                Device = resolved.Device!.Device, TagTableName = _approved.TableName, ScreenName = _approved.ScreenName,
                ReplaceExisting = item.ReplaceExisting,
            }).ConfigureAwait(false);
            if (!result.Success) return WritePlan<HmiBasicImportPlan>.Fail(result.FailureCategory!, result.Error!);
            _plan = CanonicalJson.DeserializeWorkerPayload<HmiBasicImportPlan>(result.Payload);
            if (_plan.Device != resolved.Device.Device || _plan.Hmi != resolved.Device.Hmi || _plan.TagTableName != _approved.TableName
                || _plan.ScreenName != _approved.ScreenName || _plan.ReplaceExisting != item.ReplaceExisting) throw new JsonException();
            if (!item.ReplaceExisting && (_plan.TagTableExists || _plan.ScreenExists))
                return WritePlan<HmiBasicImportPlan>.Fail(WorkerFailureCategories.StateChanged, "An import object already exists. Explicitly authorize replaceExisting to replace it.");
            return WritePlan<HmiBasicImportPlan>.Ok([ItemPlan<HmiBasicImportPlan>.Resolved(_plan)]);
        }
        catch (JsonException) { return WritePlan<HmiBasicImportPlan>.Fail(WorkerFailureCategories.ProtocolError, "The Basic HMI plan payload did not match its declared contract."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
        { return WritePlan<HmiBasicImportPlan>.Fail(WorkerFailureCategories.ValidationError, ex.Message); }
    }
    public IReadOnlyList<FiredGuard> EvaluateGuards(IReadOnlyList<HmiBasicImportItem> items, IReadOnlyList<ItemPlan<HmiBasicImportPlan>> plans) => [];
    public Task<ItemReplan<HmiBasicImportPlan>> ReplanAsync(HmiBasicImportItem item) => throw new InvalidOperationException("Basic HMI imports have no dependent items.");
    public Task<WorkerCallResult> MutateAsync(HmiBasicImportItem item)
    {
        if (_approved is null || _plan is null) throw new InvalidOperationException("An approved, pinned import plan is required.");
        return client.HmiBasicAsync(HmiBasicMethods.ImportAndCompile, new()
        {
            Device = _plan.Device, TagTableName = _approved.TableName, ScreenName = _approved.ScreenName,
            TagXml = _approved.TagXml, ScreenXml = _approved.ScreenXml, PreviewDigest = _approved.PreviewDigest,
            Approved = true, ReplaceExisting = item.ReplaceExisting, ExpectedPlan = _plan,
        });
    }

    public StructuredOperationItem Project(HmiBasicImportItem item, WorkerCallResult result)
    {
        if (!result.Success) return StructuredOperationItem.Failed(item, result.FailureCategory!, result.Error!, result.Warnings)
            with { Failure = new(result.FailureCategory!, result.Error!, EngineeringError: result.EngineeringError) };
        try
        {
            var value = CanonicalJson.DeserializeWorkerPayload<HmiBasicImportResult>(result.Payload);
            var stages = new[] { value.TagImport, value.ScreenImport, value.Compile };
            if (_plan is null || value.Device != _plan.Device || value.Hmi != _plan.Hmi
                || stages.Any(s => s is null || s.Status is not ("succeeded" or "failed" or "skipped")
                    || s.Category is not null && !WorkerFailureCategories.IsKnown(s.Category))
                || value.Success != stages.All(s => s.Status == "succeeded")
                || value.CompileReport is { } report && (report.ErrorCount < 0 || report.WarningCount < 0
                    || report.Messages is null || report.Messages.Any(m => m is null))) throw new JsonException();
            if (value.Success && (value.CompileReport is not { ErrorCount: 0, State: "Success" or "Warning" }
                || value.CompileReport.MessagesOmitted && value.CompileReport.Messages.Length > 30)) throw new JsonException();
            var failed = stages.FirstOrDefault(s => s.Status == "failed");
            return new(item.OperationId, item.Operation, value.Success ? OperationBatchStatus.Succeeded : OperationBatchStatus.Failed,
                CanonicalJson.ToElement(value), value.Success ? null : new(failed?.Category ?? WorkerFailureCategories.WorkerOperationFailed,
                    failed?.Message ?? "An import or compile stage did not succeed.", EngineeringError: failed?.EngineeringError), null, null, result.Warnings);
        }
        catch (JsonException) { return StructuredOperationItem.Failed(item, WorkerFailureCategories.ProtocolError, "The Basic HMI import payload did not match its declared contract.", result.Warnings); }
    }

    public Task<WriteVerification?> VerifyAsync(StructuredOperationBatch batch)
        => Task.FromResult<WriteVerification?>(new(batch.Operations.All(o => o.Status == OperationBatchStatus.Succeeded),
            batch.Operations.Where(o => o.Status == OperationBatchStatus.Succeeded).Select(o =>
                new WriteOperationVerification(o.OperationId, true, WriteVerificationChecks.Trusted, null, null, "Worker reported tag import, screen import and HMI compilation success.")).ToArray(), null));
    public bool VerificationSucceeded(WriteVerification? verification) => verification?.Success == true;
    public HmiBasicStudioResponse Compose(WriteReport<HmiBasicImportPlan, WriteVerification> report)
        => HmiBasicStudioBudget.Apply(HmiBasicStudioTools.Empty("import_and_compile") with
        {
            Phase = report.Phase, Success = report.Success, Error = report.Error, Warnings = report.Warnings,
            Guards = report.Guards, Effects = report.Effects, Batch = report.Batch, Verification = report.Verification,
        });
}
