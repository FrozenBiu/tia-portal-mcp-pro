#if TIA_CLASSIC_HMI
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.Hmi.Globalization;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcpServer.Contracts.HmiBasic;
using TiaMcpServer.Contracts.Worker;
using TiaMcpServer.OpennessWorker.Openness.Infrastructure;
using TiaMcpServer.OpennessWorker.Openness.Project;
using TiaMcpServer.OpennessWorker.Worker;

namespace TiaMcpServer.OpennessWorker.Openness.HmiBasic;

internal static class HmiBasicService
{
    private static readonly StringComparer DeviceNames = StringComparer.OrdinalIgnoreCase;

    internal static HmiBasicListResult List(ProjectBase project, HmiBasicRequest request) => new()
    {
        Devices = (request.Device is null ? Targets(project, null) : new[] { Resolve(project, request.Device) })
            .Select(target => ToHmiBasicDevice(project, target)).ToArray()
    };

    private static HmiBasicDevice ToHmiBasicDevice(ProjectBase project, (string Device, HmiTarget Hmi) target)
    {
        var graphics = ReadProjectGraphics(target.Hmi);
        if (graphics.Coverage == "unavailable" && project is Siemens.Engineering.Project standalone)
            graphics = ReadProjectGraphics(standalone);
        return new HmiBasicDevice
        {
            Device = target.Device,
            Hmi = target.Hmi.Name,
            TagTables = Tags(target.Hmi.TagFolder, "").Select(x => new HmiBasicTagTable
                { Path = x.Path, Count = x.Table.Tags.Count }).ToArray(),
            Screens = Screens(target.Hmi.ScreenFolder, "").Select(x => x.Path).ToArray(),
            Graphics = graphics.Graphics,
            GraphicsCoverage = graphics.Coverage
        };
    }

    private static (string[] Graphics, string Coverage) ReadProjectGraphics(IEngineeringServiceProvider owner)
    {
        try
        {
            var provider = owner.GetService<GraphicsProvider>();
            if (provider is null) return (Array.Empty<string>(), "unavailable");
            return (provider.Graphics.Select(graphic => graphic.Name).Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray(), "provider");
        }
        catch (EngineeringException ex) when (OpennessExceptions.IsRecoverablePropertyFailure(ex))
        {
            return (Array.Empty<string>(), "unavailable");
        }
    }

    internal static HmiBasicExportResult Export(ProjectBase project, HmiBasicRequest request)
    {
        var name = Require(request.Name, "name");
        if (request.Kind is not ("tag" or "screen"))
            throw Error(WorkerFailureCategories.ValidationError, "Export kind must be 'tag' or 'screen'.");
        var target = Resolve(project, Require(request.Device, "device"));
        return InTemporaryDirectory(directory =>
        {
            var file = new FileInfo(Path.Combine(directory, "export.xml"));
            var path = request.Kind == "tag"
                ? ExportObject(Tags(target.Hmi.TagFolder, "").Select(x => (x.Path, Object: x.Table)),
                    name, table => table.Export(file, ExportOptions.WithDefaults))
                : ExportObject(Screens(target.Hmi.ScreenFolder, "").Select(x => (x.Path, Object: x.Screen)),
                    name, screen => screen.Export(file, ExportOptions.WithDefaults));
            using var reader = XmlReader.Create(file.FullName, XmlSettings());
            var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return new HmiBasicExportResult
            {
                Device = target.Device, Hmi = target.Hmi.Name, Kind = request.Kind!, Path = path,
                // The host writes UTF-8. Do not forward a declaration for the native file's encoding.
                Xml = xml.ToString(SaveOptions.DisableFormatting)
            };
        });
    }

    internal static HmiBasicImportPlan PlanImport(ProjectBase project, HmiBasicRequest request)
        => InspectImport(Resolve(project, Require(request.Device, "device")), request);

    internal static HmiBasicImportResult ImportAndCompile(ProjectBase project, HmiBasicRequest request,
        TiaPortal portal, Action verifyBinding, Func<long> portalDisposalRevision)
    {
        if (!request.Approved || !IsDigest(request.PreviewDigest))
            throw Error(WorkerFailureCategories.AccessDenied, "Import requires explicit approval and a lowercase SHA-256 preview digest.");
        var expected = request.ExpectedPlan
            ?? throw Error(WorkerFailureCategories.ValidationError, "Import requires the guarded plan for this destination.");
        var tagName = RequireRootName(request.TagTableName, "tagTableName");
        var screenName = RequireRootName(request.ScreenName, "screenName");
        var tagXml = Require(request.TagXml, "tagXml");
        var screenXml = Require(request.ScreenXml, "screenXml");
        var tagEncoding = ValidateXml(tagXml, "Hmi.Tag.TagTable", tagName);
        var screenEncoding = ValidateXml(screenXml, "Hmi.Screen.Screen", screenName);

        return InTemporaryDirectory(directory =>
        {
            var tagFile = StageXml(directory, "tag.xml", tagXml, tagEncoding);
            var screenFile = StageXml(directory, "screen.xml", screenXml, screenEncoding);
            verifyBinding();
            using var exclusiveAccess = portal.ExclusiveAccess("Import and compile Classic HMI");
            verifyBinding();
            var target = Resolve(project, Require(request.Device, "device"));
            var plan = InspectImport(target, request);
            if (plan.Device != expected.Device || plan.Hmi != expected.Hmi ||
                plan.TagTableName != expected.TagTableName || plan.ScreenName != expected.ScreenName ||
                plan.TagTableExists != expected.TagTableExists || plan.ScreenExists != expected.ScreenExists ||
                plan.ReplaceExisting != expected.ReplaceExisting)
                throw Error(WorkerFailureCategories.StateChanged, "The Classic HMI destination changed after planning. Inspect and plan again.");
            if (!plan.ReplaceExisting && (plan.TagTableExists || plan.ScreenExists))
                throw Error(WorkerFailureCategories.GuardBlocked, "An import target exists. Explicitly approve replacement to overwrite it.");

            var hmi = target.Hmi;
            var tags = hmi.TagFolder.TagTables;
            var screens = hmi.ScreenFolder.Screens;
            var compiler = hmi.GetService<ICompilable>()
                ?? throw Error(WorkerFailureCategories.TargetKindUnsupported, "The selected Classic HMI does not expose compilation.");
            var result = new HmiBasicImportResult { Device = plan.Device, Hmi = plan.Hmi };
            var revision = portalDisposalRevision();
            verifyBinding();
            result.TagImport = RunStage(() =>
                CheckImported(tags.Import(tagFile, Options(plan.TagTableExists)).Select(x => x.Name), tagName));
            if (result.TagImport.Status != "succeeded") return result;

            result.ScreenImport = RunStage(() =>
            {
                verifyBinding();
                if (HasCollision(screens.Select(x => x.Name), screenName) != plan.ScreenExists)
                    throw Error(WorkerFailureCategories.StateChanged, "The screen destination changed after tag import. Inspect the HMI before retrying.");
                CheckImported(screens.Import(screenFile, Options(plan.ScreenExists)).Select(x => x.Name), screenName);
            });
            if (result.ScreenImport.Status != "succeeded") return result;

            result.Compile = RunStage(() =>
            {
                verifyBinding();
                var compiled = compiler.Compile();
                var report = new HmiBasicCompileReport
                {
                    State = compiled.State.ToString(), ErrorCount = compiled.ErrorCount, WarningCount = compiled.WarningCount
                };
                result.CompileReport = report;
                try
                {
                    var messages = new List<string>();
                    CollectMessages(compiled.Messages, messages, report);
                    report.Messages = messages.ToArray();
                }
                catch (EngineeringException ex) when (RecoverableEngineering(ex))
                {
                    report.MessagesOmitted = true;
                }

                if (report.ErrorCount != 0 || report.State is not ("Success" or "Warning"))
                    throw Error(WorkerFailureCategories.WorkerOperationFailed, "The Classic HMI compiler did not report success. Inspect the compile report.");
            });
            result.Success = result.Compile.Status == "succeeded";
            return result;

            // A fresh absence observation never authorizes overwriting a newly added object.
            ImportOptions Options(bool existed) => plan.ReplaceExisting && existed ? ImportOptions.Override : ImportOptions.None;

            bool RecoverableEngineering(EngineeringException ex)
                => OpennessExceptions.IsRecoverable(ex) && portalDisposalRevision() == revision;

            HmiBasicStage RunStage(Action action)
            {
                try
                {
                    action();
                    return new HmiBasicStage { Status = "succeeded" };
                }
                catch (EngineeringException ex) when (RecoverableEngineering(ex))
                {
                    var category = WorkerExecution.Classify(ex, mutates: true);
                    return StageFailure(category, WorkerExecution.FailureMessage(ex, category), WorkerExecution.Describe(ex));
                }
                catch (WorkerOperationException ex) when (ex.FailureCategory is not (WorkerFailureCategories.BindingConflict or WorkerFailureCategories.PortalLost))
                {
                    return StageFailure(ex.FailureCategory, ex.Message, ex.EngineeringError);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    return StageFailure(WorkerFailureCategories.WorkerOperationFailed, ex.Message);
                }
            }
        });
    }

    private static HmiBasicImportPlan InspectImport((string Device, HmiTarget Hmi) target, HmiBasicRequest request)
    {
        var tag = RequireRootName(request.TagTableName, "tagTableName");
        var screen = RequireRootName(request.ScreenName, "screenName");
        return new HmiBasicImportPlan
        {
            Device = target.Device, Hmi = target.Hmi.Name, TagTableName = tag, ScreenName = screen,
            TagTableExists = HasCollision(target.Hmi.TagFolder.TagTables.Select(x => x.Name), tag),
            ScreenExists = HasCollision(target.Hmi.ScreenFolder.Screens.Select(x => x.Name), screen),
            ReplaceExisting = request.ReplaceExisting
        };
    }

    private static bool HasCollision(IEnumerable<string> names, string name)
    {
        var matches = names.Where(x => DeviceNames.Equals(x, name)).Take(2).ToArray();
        if (matches.Length > 1 || (matches.Length == 1 && matches[0] != name))
            throw Error(WorkerFailureCategories.TargetAmbiguous, $"A same-name object does not match '{name}' uniquely with exact casing.");
        return matches.Length == 1;
    }

    private static void CheckImported(IEnumerable<string> names, string expected)
    {
        if (!names.SequenceEqual(new[] { expected }, StringComparer.Ordinal))
            throw Error(WorkerFailureCategories.PostconditionFailed, "Portal did not return the exact expected imported object. Inspect the HMI before retrying.");
    }

    private static void CollectMessages(IEnumerable<CompilerResultMessage> source, List<string> messages, HmiBasicCompileReport report)
    {
        foreach (var message in source)
        {
            if (messages.Count >= 30)
            {
                report.MessagesOmitted = true;
                return;
            }
            var description = message.Description ?? string.Empty;
            if (description.Length > 500) report.MessagesOmitted = true;
            messages.Add(Bound(description, 500));
            CollectMessages(message.Messages, messages, report);
        }
    }

    private static HmiBasicStage StageFailure(string category, string message, EngineeringErrorInfo? engineering = null)
        => new() { Status = "failed", Category = category, Message = Bound(message, 500), EngineeringError = engineering };

    private static string Bound(string message, int length) => message.Length > length ? message.Substring(0, length) : message;

    private static (string Device, HmiTarget Hmi) Resolve(ProjectBase project, string device)
    {
        Require(device, "device");
        var targets = Targets(project, device).Take(2).ToArray();
        return targets.Length switch
        {
            1 => targets[0],
            0 => throw Error(WorkerFailureCategories.TargetNotFound, $"No Classic HMI was found on device '{device}'."),
            _ => throw Error(WorkerFailureCategories.TargetAmbiguous, $"Device '{device}' does not identify exactly one Classic HMI.")
        };
    }

    private static IEnumerable<(string Device, HmiTarget Hmi)> Targets(ProjectBase project, string? name)
    {
        foreach (var device in ProjectDeviceEnumerator.Enumerate(project))
        {
            var deviceName = device.Name;
            if (name is not null && !DeviceNames.Equals(deviceName, name)) continue;
            foreach (var item in Items(device.DeviceItems))
                if (item.GetService<SoftwareContainer>()?.Software is HmiTarget target)
                    yield return (deviceName, target);
        }
    }

    private static IEnumerable<DeviceItem> Items(DeviceItemComposition items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Items(item.DeviceItems)) yield return child;
        }
    }

    private static IEnumerable<(string Path, TagTable Table)> Tags(TagFolder folder, string prefix)
    {
        foreach (var table in folder.TagTables) yield return (prefix + table.Name, table);
        foreach (var child in folder.Folders)
            foreach (var row in Tags(child, prefix + child.Name + "/")) yield return row;
    }

    private static IEnumerable<(string Path, Screen Screen)> Screens(ScreenFolder folder, string prefix)
    {
        foreach (var screen in folder.Screens) yield return (prefix + screen.Name, screen);
        foreach (var child in folder.Folders)
            foreach (var row in Screens(child, prefix + child.Name + "/")) yield return row;
    }

    private static string ExportObject<T>(IEnumerable<(string Path, T Object)> objects, string name, Action<T> export)
    {
        var rows = objects.ToArray();
        var matches = rows.Where(x => x.Path == name).ToArray();
        if (matches.Length == 0 && name.IndexOf('/') < 0)
            matches = rows.Where(x => x.Path.Substring(x.Path.LastIndexOf('/') + 1) == name).ToArray();
        if (matches.Length != 1)
            throw Error(matches.Length == 0 ? WorkerFailureCategories.TargetNotFound : WorkerFailureCategories.TargetAmbiguous,
                $"Expected one Classic HMI object for '{name}'; found {matches.Length}. Use its exact listed path.");
        export(matches[0].Object);
        return matches[0].Path;
    }

    private static T InTemporaryDirectory<T>(Func<string, T> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tia-mcp-classic-hmi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { return action(directory); }
        finally { TemporaryDirectoryCleanup.TryDeleteTempDirectory(directory); }
    }

    private static FileInfo StageXml(string directory, string name, string xml, Encoding encoding)
    {
        var file = new FileInfo(Path.Combine(directory, name));
        using var output = new StreamWriter(file.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None), encoding);
        output.Write(xml);
        return file;
    }

    private static Encoding ValidateXml(string xml, string kind, string expectedName)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), XmlSettings());
            var document = XDocument.Load(reader);
            var root = document.Root;
            var objects = root?.Elements().Where(x => x.Name != "Engineering" && x.Name != "DocumentInfo").ToArray();
            if (root?.Name != "Document" || objects is not { Length: 1 } || objects[0].Name != kind ||
                root!.Descendants().Count(x => x.Name == "Hmi.Tag.TagTable" || x.Name == "Hmi.Screen.Screen") != 1 ||
                objects[0].Elements("AttributeList").Count() != 1 ||
                objects[0].Elements("AttributeList").Elements("Name").SingleOrDefault() is not { HasElements: false } name ||
                name.Value != expectedName)
                throw Error(WorkerFailureCategories.ValidationError, $"Expected one {kind} named '{expectedName}' in the SimaticML document.");

            return document.Declaration?.Encoding is string encoding
                ? Encoding.GetEncoding(encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw Error(WorkerFailureCategories.ValidationError, $"Invalid SimaticML for '{expectedName}'.");
        }
    }

    private static XmlReaderSettings XmlSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    private static string Require(string? value, string selector)
        => !string.IsNullOrWhiteSpace(value) ? value! : throw Error(WorkerFailureCategories.ValidationError, $"{selector} is required.");

    private static string RequireRootName(string? value, string selector)
    {
        var name = Require(value, selector);
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            throw Error(WorkerFailureCategories.ValidationError, $"{selector} must name one root object, not a path.");
        return name;
    }

    private static bool IsDigest(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static WorkerOperationException Error(string category, string message) => new(category, message);
}
#endif
