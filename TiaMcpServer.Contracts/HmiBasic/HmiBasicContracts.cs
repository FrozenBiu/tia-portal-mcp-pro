using TiaMcpServer.Contracts.Worker;

namespace TiaMcpServer.Contracts.HmiBasic;

public static class HmiBasicMethods
{
    public const string List = "hmi_basic_list";
    public const string Export = "hmi_basic_export";
    public const string PlanImport = "hmi_basic_plan_import";
    public const string ImportAndCompile = "hmi_basic_import_and_compile";
}

public sealed class HmiBasicRequest
{
    public string? Device { get; set; }
    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string? TagTableName { get; set; }
    public string? ScreenName { get; set; }
    public string? TagXml { get; set; }
    public string? ScreenXml { get; set; }
    public string? PreviewDigest { get; set; }
    public bool Approved { get; set; }
    public bool ReplaceExisting { get; set; }
    public HmiBasicImportPlan? ExpectedPlan { get; set; }
}

public sealed class HmiBasicListResult
{
    public HmiBasicDevice[] Devices { get; set; } = Array.Empty<HmiBasicDevice>();
}

public sealed class HmiBasicDevice
{
    public string Device { get; set; } = string.Empty;
    public string Hmi { get; set; } = string.Empty;
    public HmiBasicTagTable[] TagTables { get; set; } = Array.Empty<HmiBasicTagTable>();
    public string[] Screens { get; set; } = Array.Empty<string>();
    public string[] Graphics { get; set; } = Array.Empty<string>();
    public string GraphicsCoverage { get; set; } = "unavailable";
}

public sealed class HmiBasicTagTable
{
    public string Path { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class HmiBasicExportResult
{
    public string Device { get; set; } = string.Empty;
    public string Hmi { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Xml { get; set; } = string.Empty;
}

public sealed class HmiBasicImportPlan
{
    public string Device { get; set; } = string.Empty;
    public string Hmi { get; set; } = string.Empty;
    public string TagTableName { get; set; } = string.Empty;
    public string ScreenName { get; set; } = string.Empty;
    public bool TagTableExists { get; set; }
    public bool ScreenExists { get; set; }
    public bool ReplaceExisting { get; set; }
}

public sealed class HmiBasicImportResult
{
    public string Device { get; set; } = string.Empty;
    public string Hmi { get; set; } = string.Empty;
    public bool Success { get; set; }
    public HmiBasicStage TagImport { get; set; } = new();
    public HmiBasicStage ScreenImport { get; set; } = new();
    public HmiBasicStage Compile { get; set; } = new();
    public HmiBasicCompileReport? CompileReport { get; set; }
}

public sealed class HmiBasicStage
{
    public string Status { get; set; } = "skipped";
    public string? Category { get; set; }
    public string? Message { get; set; }
    public EngineeringErrorInfo? EngineeringError { get; set; }
}

public sealed class HmiBasicCompileReport
{
    public string State { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public string[] Messages { get; set; } = Array.Empty<string>();
    public bool MessagesOmitted { get; set; }
}
