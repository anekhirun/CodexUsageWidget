using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexUsageWidget;

internal sealed record ContextMode(
    int Index,
    string Name,
    string ContextLabel,
    string CompactionLabel,
    string BestFor,
    int? ContextWindow,
    int? CompactLimit)
{
    internal bool IsDefault => ContextWindow is null && CompactLimit is null;
}

internal sealed record ConfigWriteResult(bool Success, string ConfigPath, string Message);

internal sealed record ContextConfigState(
    ContextMode? Mode,
    int? ContextWindow,
    int? CompactLimit,
    bool ReadFailed = false)
{
    internal bool IsCustom => Mode is null;
}

internal static partial class CodexConfigService
{
    private const string BackupSuffix = ".codex-usage-widget.bak";
    private const int Gpt56MaximumContextWindow = 1_050_000;
    private static readonly HashSet<string> Gpt56Models =
    [
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna"
    ];

    internal static readonly IReadOnlyList<ContextMode> Modes =
    [
        new(0, "Default", "Model default", "Standard compaction", "Uses the context settings supplied by Codex", null, null),
        new(1, "Balanced", "~300K context", "Earlier compaction", "Best for normal coding tasks", 300_000, 240_000),
        new(2, "Large Codebase", "~600K context", "Moderate compaction", "Retains more repository and tool history", 600_000, 500_000),
        new(3, "Long-Running Investigation", "~1M context", "Late compaction", "Best for reverse engineering, migrations and debugging", 1_000_000, 900_000)
    ];

    private static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configuredHome
            ? configuredHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    internal static string GlobalConfigPath => Path.Combine(CodexHome, "config.toml");

    internal static ConfigWriteResult ApplyGlobal(ContextMode mode) => Apply(GlobalConfigPath, mode);

    internal static ConfigWriteResult ApplyToPath(string configPath, ContextMode mode) => Apply(configPath, mode);

    internal static ContextConfigState ReadState(string configPath)
    {
        if (!File.Exists(configPath)) return new ContextConfigState(Modes[0], null, null);

        try
        {
            string content = File.ReadAllText(configPath);
            int sectionStart = FindFirstSectionStart(content);
            string topLevel = sectionStart < 0 ? content : content[..sectionStart];
            int? context = ReadNumber(topLevel, "model_context_window");
            int? compact = ReadNumber(topLevel, "model_auto_compact_token_limit");
            if (context is null && compact is null)
                return new ContextConfigState(Modes[0], null, null);

            ContextMode? exactMode = Modes
                .Where(mode => !mode.IsDefault)
                .SingleOrDefault(mode => mode.ContextWindow == context && mode.CompactLimit == compact);
            return new ContextConfigState(exactMode, context, compact);
        }
        catch
        {
            return new ContextConfigState(null, null, null, ReadFailed: true);
        }
    }

    private static ConfigWriteResult Apply(string configPath, ContextMode mode)
    {
        string? directory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directory))
            return new ConfigWriteResult(false, configPath, "Invalid configuration path.");

        string? temporaryPath = null;
        string? catalogPath = null;
        string? originalCatalog = null;
        bool catalogWritten = false;
        try
        {
            if (mode.IsDefault && !File.Exists(configPath))
            {
                return new ConfigWriteResult(
                    true,
                    configPath,
                    "Default is already active; Codex is using the model's standard context settings.");
            }

            Directory.CreateDirectory(directory);
            string content = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            string updated = UpdateTopLevel(content, mode);
            string? updatedCatalog = null;

            catalogPath = ResolveCatalogPath(content, directory);
            if (catalogPath is not null && !mode.IsDefault)
                (originalCatalog, updatedCatalog) = PrepareCatalogBootstrap(catalogPath);

            bool configChanged = !string.Equals(content, updated, StringComparison.Ordinal);
            bool catalogChanged = updatedCatalog is not null &&
                                  !string.Equals(originalCatalog, updatedCatalog, StringComparison.Ordinal);
            if (!configChanged && !catalogChanged)
            {
                string message = mode.IsDefault
                    ? "Default is already active; Codex is using the model's standard context settings."
                    : $"{mode.Name} is already active.";
                return new ConfigWriteResult(true, configPath, message);
            }

            if (catalogChanged)
            {
                if (!File.Exists(catalogPath! + BackupSuffix))
                    File.Copy(catalogPath!, catalogPath! + BackupSuffix, false);
                WriteAtomically(catalogPath!, updatedCatalog!);
                catalogWritten = true;
            }

            if (configChanged)
            {
                if (File.Exists(configPath)) File.Copy(configPath, configPath + BackupSuffix, true);
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(configPath)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temporaryPath, updated, new UTF8Encoding(false));
                File.Move(temporaryPath, configPath, true);
                temporaryPath = null;
            }
            string successMessage = catalogChanged
                ? $"{mode.Name} applied. Restart Codex once to load the GPT-5.6 catalog ceiling; later changes apply to new tasks without restarting. Project .codex/config.toml overrides still take priority."
                : $"{mode.Name} applied. Start a new Codex task; no restart is required. Project .codex/config.toml overrides still take priority.";
            return new ConfigWriteResult(true, configPath, successMessage);
        }
        catch (JsonException exception)
        {
            RollBackCatalog(catalogPath, originalCatalog, catalogWritten);
            return new ConfigWriteResult(false, configPath, $"The custom model catalog is not valid JSON: {exception.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            RollBackCatalog(catalogPath, originalCatalog, catalogWritten);
            return new ConfigWriteResult(false, configPath, "Access denied while updating config.toml.");
        }
        catch (IOException exception)
        {
            RollBackCatalog(catalogPath, originalCatalog, catalogWritten);
            return new ConfigWriteResult(false, configPath, $"Could not update Codex configuration: {exception.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static string? ResolveCatalogPath(string configContent, string configDirectory)
    {
        int sectionStart = FindFirstSectionStart(configContent);
        string topLevel = sectionStart < 0 ? configContent : configContent[..sectionStart];
        Match match = Regex.Match(
            topLevel,
            @"(?m)^\s*model_catalog_json\s*=\s*(?<value>""(?:\\.|[^""])*""|'[^']*')\s*(?:#.*)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        string literal = match.Groups["value"].Value;
        string? configuredPath = literal[0] == '\''
            ? literal[1..^1]
            : JsonSerializer.Deserialize<string>(literal);
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;

        return Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(configDirectory, configuredPath));
    }

    private static (string Original, string Updated) PrepareCatalogBootstrap(string catalogPath)
    {
        if (!File.Exists(catalogPath))
            throw new IOException($"Custom model catalog not found: {catalogPath}");

        string original = File.ReadAllText(catalogPath);
        JsonNode currentRoot = JsonNode.Parse(original)
            ?? throw new JsonException("The custom model catalog is empty.");
        Dictionary<string, JsonObject> currentModels = FindGpt56Models(currentRoot);
        if (currentModels.Count == 0) return (original, original);

        bool changed = false;
        foreach (JsonObject currentModel in currentModels.Values)
        {
            // A backup is a recovery artifact, not proof that the current catalog
            // was written by an old widget. Preserve intentional context_window edits.
            if (!JsonNumberEquals(currentModel["max_context_window"], Gpt56MaximumContextWindow))
            {
                currentModel["max_context_window"] = Gpt56MaximumContextWindow;
                changed = true;
            }
        }

        return changed ? (original, SerializeCatalog(currentRoot)) : (original, original);
    }

    private static Dictionary<string, JsonObject> FindGpt56Models(JsonNode root)
    {
        JsonArray? models = root is JsonArray array ? array : root["models"] as JsonArray;
        if (models is null) return [];

        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonObject model in models.OfType<JsonObject>())
        {
            if (model["slug"] is not JsonValue slugNode ||
                !slugNode.TryGetValue(out string? slug) ||
                slug is null ||
                !Gpt56Models.Contains(slug))
            {
                continue;
            }

            if (!result.TryAdd(slug, model))
                throw new JsonException($"The custom model catalog contains duplicate {slug} entries.");
        }

        return result;
    }

    private static bool JsonNumberEquals(JsonNode? node, int expected) =>
        node is not null && int.TryParse(node.ToJsonString(), out int value) && value == expected;

    private static string SerializeCatalog(JsonNode root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    private static void WriteAtomically(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException($"Invalid path: {path}");
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static void RollBackCatalog(string? path, string? content, bool wasWritten)
    {
        if (!wasWritten || path is null || content is null) return;
        try { WriteAtomically(path, content); } catch { }
    }

    private static string UpdateTopLevel(string content, ContextMode mode)
    {
        string newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsWithNewline = content.EndsWith("\n", StringComparison.Ordinal);
        var lines = content.Length == 0
            ? new List<string>()
            : content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (endsWithNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int sectionIndex = lines.FindIndex(line => SectionLineRegex().IsMatch(line));
        if (sectionIndex < 0) sectionIndex = lines.Count;

        if (mode.IsDefault)
        {
            RemoveTopLevelKey(lines, ref sectionIndex, "model_context_window");
            RemoveTopLevelKey(lines, ref sectionIndex, "model_auto_compact_token_limit");
        }
        else
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model_context_window"] = mode.ContextWindow!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["model_auto_compact_token_limit"] = mode.CompactLimit!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };

            foreach ((string key, string value) in values)
            {
                var matches = FindTopLevelKeys(lines, sectionIndex, key);

                if (matches.Count > 0)
                {
                    lines[matches[0]] = $"{key} = {value}";
                    for (int index = matches.Count - 1; index >= 1; index--)
                    {
                        lines.RemoveAt(matches[index]);
                        sectionIndex--;
                    }
                }
                else
                {
                    lines.Insert(sectionIndex, $"{key} = {value}");
                    sectionIndex++;
                }
            }
        }

        if (sectionIndex < lines.Count && sectionIndex > 0 && lines[sectionIndex - 1].Length > 0)
            lines.Insert(sectionIndex, string.Empty);

        string result = string.Join(newline, lines);
        if (result.Length > 0 && !result.EndsWith(newline, StringComparison.Ordinal)) result += newline;
        return result;
    }

    private static void RemoveTopLevelKey(List<string> lines, ref int sectionIndex, string key)
    {
        List<int> matches = FindTopLevelKeys(lines, sectionIndex, key);
        for (int index = matches.Count - 1; index >= 0; index--)
        {
            lines.RemoveAt(matches[index]);
            sectionIndex--;
        }
    }

    private static List<int> FindTopLevelKeys(List<string> lines, int sectionIndex, string key)
    {
        var matches = new List<int>();
        for (int index = 0; index < sectionIndex; index++)
        {
            if (Regex.IsMatch(lines[index], $@"^\s*{Regex.Escape(key)}\s*=", RegexOptions.CultureInvariant))
                matches.Add(index);
        }

        return matches;
    }

    private static int FindFirstSectionStart(string content)
    {
        Match match = Regex.Match(content, @"(?m)^\s*\[", RegexOptions.CultureInvariant);
        return match.Success ? match.Index : -1;
    }

    private static int? ReadNumber(string content, string key)
    {
        Match match = Regex.Match(
            content,
            $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*(?<value>[0-9_]+)",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["value"].Value.Replace("_", string.Empty), out int value)
            ? value
            : null;
    }

    [GeneratedRegex(@"^\s*\[[^\]]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex SectionLineRegex();
}
