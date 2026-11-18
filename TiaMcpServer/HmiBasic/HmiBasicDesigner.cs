using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.HmiBasic;

public sealed record HmiBasicPreview(string OutputDirectory, string PreviewPath, string ScreenName, string TagTableName,
    int Width, int Height, string PreviewDigest);

internal sealed record HmiBasicApproved(string ScreenName, string TableName, string ScreenXml, string TagXml,
    string PreviewDigest);

internal static class HmiBasicDesigner
{
    private const int Width = 800;
    private const int Height = 480;
    private const string ManifestName = "hmi-basic-preview.json";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static HmiBasicPreview Design(string outputDirectory, JsonElement? spec, string? sourceScreen,
        string? sourceTags, string? destinationIdentity)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("out must be a new or empty directory.");
        Directory.CreateDirectory(output);

        var screen = LoadTemplate(sourceScreen, "overview-preview/screen.xml");
        var tags = LoadTemplate(sourceTags, "overview-preview/tags.xml");
        var screenName = ReadName(spec, "screen", "Screen_Main_Overview");
        var tableName = ReadName(spec, "table", "Tags_Line_Control");
        ValidateRootName(screenName, "screen");
        ValidateRootName(tableName, "table");
        ReplaceName(screen, "Hmi.Screen.Screen", screenName);
        ReplaceName(tags, "Hmi.Tag.TagTable", tableName);
        ApplyBackground(screen, spec);

        var screenXml = screen.ToString(SaveOptions.DisableFormatting);
        var tagXml = tags.ToString(SaveOptions.DisableFormatting);
        var screenPath = Path.Combine(output, "screen.xml");
        var tagPath = Path.Combine(output, "tags.xml");
        File.WriteAllText(screenPath, screenXml, Utf8);
        File.WriteAllText(tagPath, tagXml, Utf8);

        var previewPath = Path.Combine(output, "preview.html");
        var template = File.ReadAllText(FindAsset("preview-template.html"), Utf8);
        var previewHtml = template.Replace("{{SCREEN_NAME}}", WebUtility.HtmlEncode(screenName), StringComparison.Ordinal)
            .Replace("{{TAG_TABLE_NAME}}", WebUtility.HtmlEncode(tableName), StringComparison.Ordinal)
            .Replace("{{SCREEN_XML}}", WebUtility.HtmlEncode(screenXml), StringComparison.Ordinal)
            .Replace("{{TAG_XML}}", WebUtility.HtmlEncode(tagXml), StringComparison.Ordinal);
        File.WriteAllText(previewPath, previewHtml, Utf8);

        var digest = Digest(screenXml, tagXml, previewHtml, destinationIdentity);
        var manifest = new PreviewManifest(screenName, tableName, digest,
            destinationIdentity is null ? null : DigestText(destinationIdentity));
        File.WriteAllText(Path.Combine(output, ManifestName), JsonSerializer.Serialize(manifest), Utf8);
        return new HmiBasicPreview(output, previewPath, screenName, tableName, Width, Height, digest);
    }

    internal static HmiBasicApproved LoadApproved(string outputDirectory, string expectedDigest, string destinationIdentity)
    {
        var output = Path.GetFullPath(outputDirectory);
        var screenPath = Path.Combine(output, "screen.xml");
        var tagPath = Path.Combine(output, "tags.xml");
        var previewPath = Path.Combine(output, "preview.html");
        var manifestPath = Path.Combine(output, ManifestName);
        if (!File.Exists(screenPath) || !File.Exists(tagPath) || !File.Exists(previewPath) || !File.Exists(manifestPath))
            throw new InvalidDataException("The preview directory is incomplete; run design_and_preview again.");

        var screenXml = File.ReadAllText(screenPath, Utf8);
        var tagXml = File.ReadAllText(tagPath, Utf8);
        var previewHtml = File.ReadAllText(previewPath, Utf8);
        var manifest = JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllText(manifestPath, Utf8))
            ?? throw new InvalidDataException("The preview manifest is invalid.");
        var digest = Digest(screenXml, tagXml, previewHtml, destinationIdentity);
        if (!FixedTimeEquals(digest, expectedDigest) || !FixedTimeEquals(digest, manifest.PreviewDigest)
            || manifest.DestinationIdentityDigest != DigestText(destinationIdentity))
            throw new InvalidDataException("The preview artifacts or destination changed after review.");
        return new HmiBasicApproved(manifest.ScreenName, manifest.TagTableName, screenXml, tagXml, digest);
    }

    private static XDocument LoadTemplate(string? sourcePath, string templateRelativePath)
    {
        var path = sourcePath is null ? FindTemplate(templateRelativePath) : Path.GetFullPath(sourcePath);
        if (!File.Exists(path)) throw new FileNotFoundException("The requested HMI template was not found.", path);
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string FindTemplate(string relativePath)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "templates", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(Directory.GetCurrentDirectory(), "templates", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "templates", relativePath.Replace('/', Path.DirectorySeparatorChar))
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Packaged HMI template '{relativePath}' was not found.");
    }

    private static string FindAsset(string name)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "HmiBasic", "Assets", name),
            Path.Combine(Directory.GetCurrentDirectory(), "TiaMcpServer", "HmiBasic", "Assets", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TiaMcpServer", "HmiBasic", "Assets", name)
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Packaged HMI preview asset '{name}' was not found.");
    }

    private static string ReadName(JsonElement? spec, string property, string fallback)
        => spec is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var name)
            ? name.GetString() ?? throw new ArgumentException($"spec.{property} must be a string.")
            : fallback;

    private static void ValidateRootName(string value, string selector)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '/', '\\' }) >= 0 || value.Length > 128)
            throw new ArgumentException($"spec.{selector} must be a nonblank root object name up to 128 characters.");
    }

    private static void ReplaceName(XDocument document, string elementName, string name)
    {
        var roots = document.Root?.Elements().Where(x => x.Name.LocalName == elementName).ToArray();
        if (document.Root?.Name.LocalName != "Document" || roots is not { Length: 1 })
            throw new InvalidDataException($"Template must contain exactly one {elementName} object.");
        var nameElement = roots[0].Element("AttributeList")?.Element("Name")
            ?? throw new InvalidDataException($"Template {elementName} has no root name.");
        nameElement.Value = name;
    }

    private static void ApplyBackground(XDocument screen, JsonElement? spec)
    {
        if (spec is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("background", out var property)) return;
        var color = property.GetString();
        if (color is null || color.Length != 7 || color[0] != '#' || !color.AsSpan(1).ToString().All(Uri.IsHexDigit))
            throw new ArgumentException("spec.background must be a #RRGGBB color.");
        var rgb = string.Join(", ", Enumerable.Range(0, 3).Select(i => Convert.ToInt32(color.Substring(1 + i * 2, 2), 16)));
        var attribute = screen.Descendants().FirstOrDefault(x => x.Name.LocalName == "Hmi.Screen.Screen")?
            .Element("AttributeList")?.Element("BackColor");
        if (attribute is not null) attribute.Value = rgb;
    }

    private static string Digest(string screenXml, string tagXml, string previewHtml, string? destinationIdentity)
    {
        var content = string.Join("\n", screenXml, tagXml, previewHtml, destinationIdentity ?? "offline");
        return Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(content))).ToLowerInvariant();
    }

    private static string DigestText(string value)
        => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeEquals(string actual, string expected)
        => expected.Length == 64 && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected.ToLowerInvariant()));

    private sealed record PreviewManifest(string ScreenName, string TagTableName, string PreviewDigest,
        string? DestinationIdentityDigest);
}
