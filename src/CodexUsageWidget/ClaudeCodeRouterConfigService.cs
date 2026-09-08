using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageWidget;

internal sealed record CcrContextRollbackState(
    JsonObject OriginalConfig,
    bool ConfigChanged,
    bool BaselineExisted,
    string? OriginalBaselineContent);

internal sealed record CcrContextWriteResult(
    bool Success,
    bool Changed,
    bool Installed,
    string Message,
    CcrContextRollbackState? RollbackState = null,
    bool Unavailable = false);

internal sealed class CcrUnavailableException(string message) : Exception(message);

internal interface IClaudeCodeRouterContextService
{
    Task<CcrContextWriteResult> ApplyAsync(ContextMode mode, CancellationToken cancellationToken = default);
    Task<bool> RollbackAsync(CcrContextRollbackState state, CancellationToken cancellationToken = default);
}

internal interface IClaudeCodeRouterRpcClient
{
    bool IsInstalled { get; }
    Task<JsonNode?> InvokeAsync(string method, JsonArray? arguments = null, CancellationToken cancellationToken = default);
}

internal sealed class ContextConfigCoordinator
{
    private readonly IClaudeCodeRouterContextService _ccr;
    private readonly Func<ContextMode, ConfigWriteResult> _applyCodex;

    internal ContextConfigCoordinator(
        IClaudeCodeRouterContextService ccr,
        Func<ContextMode, ConfigWriteResult>? applyCodex = null)
    {
        _ccr = ccr;
        _applyCodex = applyCodex ?? CodexConfigService.ApplyGlobal;
    }

    internal static ContextConfigCoordinator CreateDefault(string stateDirectory)
    {
        string baselinePath = Path.Combine(stateDirectory, "ccr-context-baseline.json");
        return new ContextConfigCoordinator(
            new ClaudeCodeRouterConfigService(new ClaudeCodeRouterRpcClient(), baselinePath));
    }

    internal async Task<ConfigWriteResult> ApplyGlobalAsync(
        ContextMode mode,
        CancellationToken cancellationToken = default)
    {
        CcrContextWriteResult ccrResult = await _ccr.ApplyAsync(mode, cancellationToken);
        if (!ccrResult.Success && !ccrResult.Unavailable)
        {
            return new ConfigWriteResult(
                false,
                CodexConfigService.GlobalConfigPath,
                $"CCR was not changed and Codex was left untouched. {ccrResult.Message}");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (ccrResult.RollbackState is not null)
                await _ccr.RollbackAsync(ccrResult.RollbackState, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
        }
        ConfigWriteResult codexResult = _applyCodex(mode);
        if (!codexResult.Success)
        {
            bool rolledBack = ccrResult.RollbackState is null ||
                              await _ccr.RollbackAsync(ccrResult.RollbackState, CancellationToken.None);
            string rollbackMessage = rolledBack
                ? "CCR was restored to its previous configuration."
                : "CCR rollback failed; open CCR and verify its model metadata.";
            return codexResult with { Message = $"{codexResult.Message} {rollbackMessage}" };
        }

        return codexResult with { Message = $"{codexResult.Message} {ccrResult.Message}" };
    }
}

internal sealed class ClaudeCodeRouterConfigService : IClaudeCodeRouterContextService
{
    private const int BaselineVersion = 2;
    private static readonly string[] ManagedMetadataProperties =
    [
        "autoCompactWindow",
        "contextWindow",
        "contextWindowOverride",
        "effectiveContextWindowPercent",
        "maxContextWindow"
    ];
    private readonly IClaudeCodeRouterRpcClient _rpc;
    private readonly string _baselinePath;

    internal ClaudeCodeRouterConfigService(IClaudeCodeRouterRpcClient rpc, string baselinePath)
    {
        _rpc = rpc;
        _baselinePath = baselinePath;
    }

    public async Task<CcrContextWriteResult> ApplyAsync(
        ContextMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!_rpc.IsInstalled)
        {
            return new CcrContextWriteResult(
                true,
                false,
                false,
                "Claude Code Router is not installed, so only the Codex configuration was updated.");
        }

        string? originalBaselineContent = File.Exists(_baselinePath)
            ? File.ReadAllText(_baselinePath)
            : null;
        bool baselineExisted = originalBaselineContent is not null;

        try
        {
            JsonObject originalConfig;
            try
            {
                originalConfig = await ReadConfigAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or CcrUnavailableException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Only an unavailable initial read is optional. Errors after a write
                // must still take the rollback path and must not be reported as success.
                return new CcrContextWriteResult(false, false, true,
                    "CCR is offline; only Codex was updated. Open CCR and select the mode again to synchronize it.",
                    Unavailable: true);
            }
            JsonObject updatedConfig = (JsonObject)originalConfig.DeepClone();
            JsonObject baseline = ReadBaseline(originalBaselineContent);
            JsonObject originalBaseline = (JsonObject)baseline.DeepClone();

            UpdateSummary summary = mode.IsDefault
                ? RestoreBaseline(updatedConfig, baseline)
                : await ApplyModeAsync(updatedConfig, baseline, mode, cancellationToken);

            bool configChanged = !JsonNode.DeepEquals(originalConfig, updatedConfig);
            bool baselineChanged = mode.IsDefault
                ? baselineExisted
                : !JsonNode.DeepEquals(originalBaseline, baseline);

            if (!configChanged && !baselineChanged)
            {
                await SynchronizeClaudeCodeContextAsync(cancellationToken);
                return new CcrContextWriteResult(
                    true,
                    false,
                    true,
                    SummaryMessage(mode, summary, alreadyActive: true));
            }

            bool configSaved = false;
            try
            {
                if (configChanged)
                {
                    await SaveConfigAsync(updatedConfig, cancellationToken);
                    configSaved = true;
                    if (!mode.IsDefault)
                        await VerifyManagedOverridesPersistedAsync(updatedConfig, cancellationToken);
                }

                if (mode.IsDefault)
                    DeleteBaseline();
                else
                    WriteBaseline(baseline);

                await SynchronizeClaudeCodeContextAsync(cancellationToken);
            }
            catch
            {
                if (configSaved)
                {
                    try
                    {
                        await SaveConfigAsync(originalConfig, CancellationToken.None);
                        await SynchronizeClaudeCodeContextAsync(CancellationToken.None);
                    }
                    catch { }
                }
                RestoreBaselineFile(baselineExisted, originalBaselineContent);
                throw;
            }

            var rollback = new CcrContextRollbackState(
                originalConfig,
                configChanged,
                baselineExisted,
                originalBaselineContent);
            return new CcrContextWriteResult(
                true,
                true,
                true,
                SummaryMessage(mode, summary, alreadyActive: false),
                rollback);
        }
        catch (OperationCanceledException)
        {
            return new CcrContextWriteResult(false, false, true, "CCR update was cancelled.");
        }
        catch (JsonException exception)
        {
            return new CcrContextWriteResult(
                false,
                false,
                true,
                $"CCR returned invalid configuration data: {exception.Message}");
        }
        catch (Exception exception)
        {
            return new CcrContextWriteResult(
                false,
                false,
                true,
                $"Could not update Claude Code Router: {exception.Message}");
        }
    }

    public async Task<bool> RollbackAsync(
        CcrContextRollbackState state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (state.ConfigChanged)
            {
                await SaveConfigAsync(state.OriginalConfig, cancellationToken);
                await SynchronizeClaudeCodeContextAsync(cancellationToken);
            }
            RestoreBaselineFile(state.BaselineExisted, state.OriginalBaselineContent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonObject> ReadConfigAsync(CancellationToken cancellationToken)
    {
        JsonNode? value = await _rpc.InvokeAsync("getConfig", cancellationToken: cancellationToken);
        return value as JsonObject
            ?? throw new JsonException("CCR getConfig did not return a JSON object.");
    }

    private async Task SaveConfigAsync(JsonObject config, CancellationToken cancellationToken)
    {
        var options = new JsonObject { ["applyProfile"] = false };
        var arguments = new JsonArray(config.DeepClone(), options);
        await _rpc.InvokeAsync("saveConfig", arguments, cancellationToken);
    }

    private async Task SynchronizeClaudeCodeContextAsync(CancellationToken cancellationToken)
    {
        await _rpc.InvokeAsync("syncClaudeCodeContextCache", cancellationToken: cancellationToken);
    }

    private async Task VerifyManagedOverridesPersistedAsync(
        JsonObject expectedConfig,
        CancellationToken cancellationToken)
    {
        JsonObject persistedConfig = await ReadConfigAsync(cancellationToken);
        if (expectedConfig["Providers"] is not JsonArray expectedProviders ||
            persistedConfig["Providers"] is not JsonArray persistedProviders)
        {
            throw new InvalidOperationException("CCR did not return its provider configuration after saving.");
        }

        var persistedByKey = persistedProviders
            .OfType<JsonObject>()
            .ToDictionary(ProviderKey, StringComparer.Ordinal);
        foreach (JsonObject expectedProvider in expectedProviders.OfType<JsonObject>())
        {
            if (!persistedByKey.TryGetValue(ProviderKey(expectedProvider), out JsonObject? persistedProvider) ||
                expectedProvider["modelMetadata"] is not JsonObject expectedMetadata)
            {
                continue;
            }

            foreach ((string model, JsonNode? node) in expectedMetadata)
            {
                if (node is not JsonObject expectedModel ||
                    !expectedModel.ContainsKey("contextWindowOverride"))
                {
                    continue;
                }

                JsonObject? persistedModel = ModelMetadata(persistedProvider, model, create: false);
                bool matches = persistedModel is not null && ManagedMetadataProperties.All(
                    property => JsonNode.DeepEquals(expectedModel[property], persistedModel[property]));
                if (!matches)
                {
                    throw new InvalidOperationException(
                        "The installed CCR build rejected the context-slider metadata. Install the local CCR compatibility fork and retry.");
                }
            }
        }
    }

    private async Task<UpdateSummary> ApplyModeAsync(
        JsonObject config,
        JsonObject baseline,
        ContextMode mode,
        CancellationToken cancellationToken)
    {
        int target = mode.ContextWindow
            ?? throw new InvalidOperationException("A non-default mode must define a context window.");
        int compact = mode.CompactLimit
            ?? throw new InvalidOperationException("A non-default mode must define an auto-compaction limit.");
        int supportedModels = 0;
        int changedModels = 0;
        int unsupportedModels = 0;

        RestoreBaseline(config, baseline);

        if (config["Providers"] is not JsonArray providers)
            return new UpdateSummary(0, 0, 0);

        foreach (JsonObject provider in providers.OfType<JsonObject>())
        {
            if (IsExplicitlyDisabled(provider)) continue;

            JsonObject catalogMetadata = await ReadCatalogMetadataAsync(provider, cancellationToken);
            if (provider["models"] is not JsonArray models) continue;

            foreach (string model in models
                         .OfType<JsonValue>()
                         .Select(value => value.TryGetValue(out string? name) ? name : null)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Cast<string>())
            {
                JsonObject? liveMetadata = ModelMetadata(provider, model, create: false);
                JsonObject? catalogModel = catalogMetadata[model] as JsonObject;
                int supportedMaximum = Math.Max(
                    ReadMaximumContext(liveMetadata),
                    ReadMaximumContext(catalogModel));
                if (supportedMaximum < target)
                {
                    unsupportedModels++;
                    continue;
                }

                supportedModels++;
                JsonObject metadata = liveMetadata ?? ModelMetadata(provider, model, create: true)!;
                bool changesMetadata =
                    ReadInt(metadata["autoCompactWindow"]) != compact ||
                    ReadInt(metadata["contextWindow"]) != target ||
                    ReadInt(metadata["contextWindowOverride"]) != target ||
                    ReadInt(metadata["effectiveContextWindowPercent"]) != 100 ||
                    ReadInt(metadata["maxContextWindow"]) != target;
                if (!changesMetadata) continue;

                CaptureBaseline(baseline, provider, model, liveMetadata);
                metadata["autoCompactWindow"] = compact;
                metadata["contextWindow"] = target;
                metadata["contextWindowOverride"] = target;
                metadata["effectiveContextWindowPercent"] = 100;
                metadata["maxContextWindow"] = target;
                changedModels++;
            }
        }

        return new UpdateSummary(supportedModels, changedModels, unsupportedModels);
    }

    private async Task<JsonObject> ReadCatalogMetadataAsync(
        JsonObject provider,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject();
        CopyString(provider, request, "name", "name");
        CopyFirstString(provider, request, "baseUrl", "api_base_url", "baseUrl", "baseurl");

        var providerIds = new JsonArray();
        AddUnique(providerIds, ReadString(provider["id"]));
        AddUnique(providerIds, ReadString(provider["provider"]));
        if (providerIds.Count > 0) request["providerIds"] = providerIds;

        string? preset = InferProviderPreset(provider);
        if (preset is not null) request["providerPresetId"] = preset;

        JsonNode? response = await _rpc.InvokeAsync(
            "getProviderCatalogModels",
            new JsonArray(request),
            cancellationToken);
        return response?["modelMetadata"] as JsonObject ?? new JsonObject();
    }

    private static UpdateSummary RestoreBaseline(JsonObject config, JsonObject baseline)
    {
        int restored = 0;
        if (baseline["providers"] is not JsonObject baselineProviders ||
            config["Providers"] is not JsonArray providers)
        {
            return new UpdateSummary(0, 0, 0);
        }

        var currentProviders = providers
            .OfType<JsonObject>()
            .ToDictionary(ProviderKey, StringComparer.Ordinal);

        foreach ((string providerKey, JsonNode? providerNode) in baselineProviders)
        {
            if (providerNode is not JsonObject providerBaseline ||
                providerBaseline["models"] is not JsonObject modelBaselines ||
                !currentProviders.TryGetValue(providerKey, out JsonObject? provider))
            {
                continue;
            }

            foreach ((string model, JsonNode? modelNode) in modelBaselines)
            {
                if (modelNode is not JsonObject modelBaseline) continue;

                bool metadataPresent = ReadBool(modelBaseline["metadataPresent"]);
                JsonObject? metadata = ModelMetadata(provider, model, create: metadataPresent);
                if (metadata is null) continue;

                bool changed = false;
                foreach (string property in ManagedMetadataProperties)
                    changed |= RestoreProperty(metadata, modelBaseline, property);
                if (!metadataPresent && metadata.Count == 0 && provider["modelMetadata"] is JsonObject metadataMap)
                    metadataMap.Remove(model);
                if (changed) restored++;
            }
        }

        return new UpdateSummary(restored, restored, 0);
    }

    private static bool RestoreProperty(JsonObject target, JsonObject baseline, string name)
    {
        bool wasPresent = ReadBool(baseline[$"{name}Present"]);
        if (!wasPresent)
            return target.Remove(name);

        JsonNode? original = baseline[name]?.DeepClone();
        if (JsonNode.DeepEquals(target[name], original)) return false;
        target[name] = original;
        return true;
    }

    private static void CaptureBaseline(
        JsonObject baseline,
        JsonObject provider,
        string model,
        JsonObject? originalMetadata)
    {
        JsonObject providers = EnsureObject(baseline, "providers");
        string providerKey = ProviderKey(provider);
        JsonObject providerBaseline = EnsureObject(providers, providerKey);
        JsonObject models = EnsureObject(providerBaseline, "models");
        if (models.ContainsKey(model)) return;

        var modelBaseline = new JsonObject
        {
            ["metadataPresent"] = originalMetadata is not null
        };
        foreach (string property in ManagedMetadataProperties)
        {
            bool present = originalMetadata?.ContainsKey(property) == true;
            modelBaseline[$"{property}Present"] = present;
            if (present)
                modelBaseline[property] = originalMetadata![property]?.DeepClone();
        }
        models[model] = modelBaseline;
    }

    private static JsonObject? ModelMetadata(JsonObject provider, string model, bool create)
    {
        JsonObject? metadataMap = provider["modelMetadata"] as JsonObject;
        if (metadataMap is null)
        {
            if (!create) return null;
            metadataMap = new JsonObject();
            provider["modelMetadata"] = metadataMap;
        }

        if (metadataMap[model] is JsonObject metadata) return metadata;
        if (!create) return null;

        metadata = new JsonObject();
        metadataMap[model] = metadata;
        return metadata;
    }

    private static JsonObject ReadBaseline(string? content)
    {
        if (content is null)
        {
            return new JsonObject
            {
                ["version"] = BaselineVersion,
                ["providers"] = new JsonObject()
            };
        }

        JsonObject baseline = JsonNode.Parse(content) as JsonObject
            ?? throw new JsonException("The CCR context baseline is not a JSON object.");
        if (ReadInt(baseline["version"]) != BaselineVersion)
            throw new JsonException("The CCR context baseline has an unsupported version.");
        if (baseline["providers"] is not JsonObject)
            throw new JsonException("The CCR context baseline has no provider map.");
        return baseline;
    }

    private void WriteBaseline(JsonObject baseline)
    {
        string directory = Path.GetDirectoryName(_baselinePath)
            ?? throw new IOException($"Invalid CCR baseline path: {_baselinePath}");
        Directory.CreateDirectory(directory);
        string content = baseline.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        WriteAtomically(_baselinePath, content);
    }

    private void DeleteBaseline()
    {
        if (File.Exists(_baselinePath)) File.Delete(_baselinePath);
    }

    private void RestoreBaselineFile(bool existed, string? content)
    {
        if (!existed)
        {
            DeleteBaseline();
            return;
        }

        WriteAtomically(_baselinePath, content ?? string.Empty);
    }

    private static void WriteAtomically(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException($"Invalid path: {path}");
        Directory.CreateDirectory(directory);
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

    private static string SummaryMessage(ContextMode mode, UpdateSummary summary, bool alreadyActive)
    {
        if (mode.IsDefault)
        {
            return alreadyActive
                ? "CCR is already using its original per-model context metadata."
                : $"CCR restored the original context metadata for {summary.ChangedModels} model(s); the gateway configuration was reloaded.";
        }

        string skipped = summary.UnsupportedModels > 0
            ? $" {summary.UnsupportedModels} model(s) below the {FormatTokens(mode.ContextWindow!.Value)} ceiling were left unchanged."
            : string.Empty;
        return alreadyActive
            ? $"CCR already has {mode.Name} on {summary.SupportedModels} supported model(s).{skipped}"
            : $"CCR applied {mode.Name} to {summary.ChangedModels} supported model(s); the gateway configuration was reloaded.{skipped}";
    }

    private static string FormatTokens(int value) => value >= 1_000_000
        ? $"{value / 1_000_000d:0.#}M"
        : $"{value / 1_000d:0.#}K";

    private static int ReadMaximumContext(JsonObject? metadata) => metadata is null
        ? 0
        : Math.Max(ReadInt(metadata["contextWindow"]) ?? 0, ReadInt(metadata["maxContextWindow"]) ?? 0);

    private static string ProviderKey(JsonObject provider)
    {
        string? id = ReadString(provider["id"]);
        if (id is not null) return $"id:{id}";
        return $"name:{ReadString(provider["name"]) ?? string.Empty}|type:{ReadString(provider["type"]) ?? string.Empty}|base:{ProviderBaseUrl(provider) ?? string.Empty}";
    }

    private static string? InferProviderPreset(JsonObject provider)
    {
        string name = ReadString(provider["name"])?.ToLowerInvariant() ?? string.Empty;
        string id = ReadString(provider["id"])?.ToLowerInvariant() ?? string.Empty;
        string type = ReadString(provider["type"])?.ToLowerInvariant() ?? string.Empty;
        string host = string.Empty;
        if (Uri.TryCreate(ProviderBaseUrl(provider), UriKind.Absolute, out Uri? uri))
            host = uri.Host.ToLowerInvariant();
        string identity = $"{name} {id} {host}";

        if (identity.Contains("openrouter", StringComparison.Ordinal)) return "openrouter";
        if (identity.Contains("anthropic", StringComparison.Ordinal) || type == "anthropic_messages") return "anthropic";
        if (identity.Contains("gemini", StringComparison.Ordinal) || identity.Contains("google", StringComparison.Ordinal) || type.StartsWith("gemini_", StringComparison.Ordinal)) return "gemini";
        if (identity.Contains("deepseek", StringComparison.Ordinal)) return "deepseek";
        if (identity.Contains("mistral", StringComparison.Ordinal)) return "mistral";
        if (identity.Contains("codex", StringComparison.Ordinal) || identity.Contains("openai", StringComparison.Ordinal) || host == "chatgpt.com") return "openai";
        return null;
    }

    private static string? ProviderBaseUrl(JsonObject provider) =>
        ReadString(provider["api_base_url"]) ??
        ReadString(provider["baseUrl"]) ??
        ReadString(provider["baseurl"]);

    private static void CopyString(JsonObject source, JsonObject target, string targetName, string sourceName)
    {
        string? value = ReadString(source[sourceName]);
        if (value is not null) target[targetName] = value;
    }

    private static void CopyFirstString(JsonObject source, JsonObject target, string targetName, params string[] sourceNames)
    {
        foreach (string sourceName in sourceNames)
        {
            string? value = ReadString(source[sourceName]);
            if (value is null) continue;
            target[targetName] = value;
            return;
        }
    }

    private static void AddUnique(JsonArray values, string? value)
    {
        if (value is null || values.Any(node => string.Equals(ReadString(node), value, StringComparison.Ordinal))) return;
        values.Add(value);
    }

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject value) return value;
        value = new JsonObject();
        parent[name] = value;
        return value;
    }

    private static bool IsExplicitlyDisabled(JsonObject provider) =>
        provider["enabled"] is JsonValue value && value.TryGetValue(out bool enabled) && !enabled;

    private static int? ReadInt(JsonNode? value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out int integer)) return integer;
            if (jsonValue.TryGetValue(out long longValue) && longValue is >= int.MinValue and <= int.MaxValue)
                return (int)longValue;
        }
        return null;
    }

    private static bool ReadBool(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue(out bool result) && result;

    private static string? ReadString(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue(out string? result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;

    private sealed record UpdateSummary(int SupportedModels, int ChangedModels, int UnsupportedModels);
}

internal sealed class ClaudeCodeRouterRpcClient : IClaudeCodeRouterRpcClient
{
    private const string WebTokenParameter = "ccr_web_token";
    private const string WebTokenHeader = "x-ccr-web-auth";
    private readonly string _servicePath;
    private readonly string _configDatabasePath;
    private readonly HttpClient _httpClient;

    internal ClaudeCodeRouterRpcClient()
    {
        string configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "claude-code-router");
        _servicePath = Path.Combine(configDirectory, "service.json");
        _configDatabasePath = Path.Combine(configDirectory, "config.sqlite");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    internal ClaudeCodeRouterRpcClient(
        string servicePath,
        string configDatabasePath,
        HttpClient httpClient)
    {
        _servicePath = servicePath;
        _configDatabasePath = configDatabasePath;
        _httpClient = httpClient;
    }

    public bool IsInstalled => File.Exists(_configDatabasePath) || File.Exists(_servicePath);

    public async Task<JsonNode?> InvokeAsync(
        string method,
        JsonArray? arguments = null,
        CancellationToken cancellationToken = default)
    {
        (Uri endpoint, string token) = ReadConnection();
        var body = new JsonObject
        {
            ["method"] = method,
            ["args"] = arguments?.DeepClone() ?? new JsonArray()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(WebTokenHeader, token);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonObject payload = JsonNode.Parse(responseBody) as JsonObject
            ?? throw new JsonException("CCR RPC returned an invalid JSON response.");
        bool ok = payload["ok"] is JsonValue okValue && okValue.TryGetValue(out bool success) && success;
        if (!response.IsSuccessStatusCode || !ok)
        {
            string message = ReadString(payload["error"]?["message"])
                ?? $"CCR RPC failed with HTTP {(int)response.StatusCode}.";
            throw new InvalidOperationException(message);
        }

        return payload["value"]?.DeepClone();
    }

    private (Uri Endpoint, string Token) ReadConnection()
    {
        if (!File.Exists(_servicePath))
            throw new CcrUnavailableException("CCR is installed, but its management service is not running.");

        JsonObject service = JsonNode.Parse(File.ReadAllText(_servicePath)) as JsonObject
            ?? throw new JsonException("CCR service.json is not a JSON object.");
        string serviceUrl = ReadString(service["url"])
            ?? throw new JsonException("CCR service.json does not contain its management URL.");
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IsLoopbackHost(uri.Host))
        {
            throw new InvalidOperationException("CCR management URL is not a local HTTP endpoint.");
        }

        string token = ReadQueryParameter(uri, WebTokenParameter)
            ?? throw new JsonException("CCR service.json does not contain its web authentication token.");
        var endpoint = new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/api/ccr/rpc").Uri;
        return (endpoint, token);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static string? ReadQueryParameter(Uri uri, string name)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (!Uri.UnescapeDataString(parts[0]).Equals(name, StringComparison.Ordinal)) continue;
            string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    private static string? ReadString(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue(out string? result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;
}
