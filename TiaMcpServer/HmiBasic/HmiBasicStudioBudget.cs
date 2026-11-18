using TiaMcpServer.Json;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Safety.Pipeline;

namespace TiaMcpServer.HmiBasic;

internal static class HmiBasicStudioBudget
{
    internal static HmiBasicStudioResponse Apply(HmiBasicStudioResponse response)
    {
        var budget = new GuardedWriteResponseBudget(HmiBasicStudioTools.ToolName,
            "Narrow device or inspect the saved preview/export. Raise response limits for a complete stage report.", "Diagnostic omitted to fit the response budget.");
        var original = CanonicalJson.Serialize(response).Length;
        var maxItem = StructuredOperationBatchPayloadBudget.MaxItemChars;
        var maxDocument = StructuredOperationBatchPayloadBudget.MaxDocumentChars;
        var omitted = response.Omission;
        if (response.Inventory is not null && CanonicalJson.Serialize(response.Inventory).Length > maxItem
            || response.Preview is not null && CanonicalJson.Serialize(response.Preview).Length > maxItem
            || response.Export is not null && CanonicalJson.Serialize(response.Export).Length > maxItem
            || response.Effects.Any(e => CanonicalJson.Serialize(e).Length > maxItem))
        {
            omitted = budget.Omission(StructuredOperationBatchPayloadBudget.ItemLimitReason, maxItem, original);
            response = response with { Inventory = null, Preview = null, Export = null, Effects = [], Success = false, Omission = omitted };
        }
        var diagnostics = budget.Diagnostics(messageLimit: Math.Min(2000, maxItem));
        response = response with { Error = diagnostics.Error(response.Error), Warnings = diagnostics.Warnings(response.Warnings),
            Guards = diagnostics.Guards(response.Guards), Batch = diagnostics.Batch(response.Batch) };
        if (CanonicalJson.Serialize(response).Length > maxDocument)
        {
            diagnostics = budget.Diagnostics(omitWarnings: true);
            omitted = budget.Omission(StructuredOperationBatchPayloadBudget.DocumentLimitReason, maxDocument, original);
            response = response with { Inventory = null, Preview = null, Export = null, Effects = [], Success = false, Omission = omitted,
                Error = diagnostics.Error(response.Error), Warnings = diagnostics.Warnings(response.Warnings), Guards = diagnostics.Guards(response.Guards) };
        }
        if (response.Batch is { } batch)
        {
            var root = response;
            response = response with { Batch = budget.ApplyBatch(batch, candidate => root with { Batch = candidate }, maxItem, maxDocument) };
        }
        omitted = budget.DiagnosticOmission(diagnostics, omitted, maxDocument, original);
        return response with { Omission = omitted, Success = response.Success && omitted is null && response.Batch?.Operations.Any(i => i.Omission is not null) != true };
    }
}
