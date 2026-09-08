using System.Text.Json.Nodes;
using Xunit;

namespace CodexUsageWidget.Tests;

public sealed class ClaudeCodeRouterConfigServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), $"CodexUsageWidget-ccr-tests-{Guid.NewGuid():N}");

    public ClaudeCodeRouterConfigServiceTests() => Directory.CreateDirectory(_testDirectory);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OfflineCcrDoesNotBlockSavingAndReadingBackCodexMode(int index)
    {
        string configPath = Path.Combine(_testDirectory, "offline-config.toml");
        File.WriteAllText(configPath, "model = \"preserved-model\"\nmodel_context_window = 600000\nmodel_auto_compact_token_limit = 500000\n");
        var service = new ClaudeCodeRouterConfigService(new FailingReadRpc(new HttpRequestException("Connection refused")),
            Path.Combine(_testDirectory, "offline-baseline.json"));
        var coordinator = new ContextConfigCoordinator(service, mode => CodexConfigService.ApplyToPath(configPath, mode));

        var result = await coordinator.ApplyGlobalAsync(CodexConfigService.Modes[index]);

        Assert.True(result.Success, result.Message);
        Assert.Contains("CCR is offline", result.Message);
        Assert.Equal(index, CodexConfigService.ReadState(configPath).Mode!.Index);
        Assert.Contains("preserved-model", File.ReadAllText(configPath));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "offline-baseline.json")));
    }

    [Fact]
    public async Task MalformedCcrDataStillPreventsPartialUpdate()
    {
        bool wroteCodex = false;
        var service = new ClaudeCodeRouterConfigService(new FailingReadRpc(new System.Text.Json.JsonException("Invalid data")),
            Path.Combine(_testDirectory, "bad-baseline.json"));
        var coordinator = new ContextConfigCoordinator(service, _ =>
        {
            wroteCodex = true;
            return new ConfigWriteResult(true, "config.toml", "saved");
        });
        Assert.False((await coordinator.ApplyGlobalAsync(CodexConfigService.Modes[1])).Success);
        Assert.False(wroteCodex);
    }

    [Fact]
    public async Task CancelledSelectionDoesNotWriteCodex()
    {
        bool wroteCodex = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ClaudeCodeRouterConfigService(new FailingReadRpc(new HttpRequestException("Connection refused")),
            Path.Combine(_testDirectory, "cancelled-baseline.json"));
        var coordinator = new ContextConfigCoordinator(service, _ =>
        {
            wroteCodex = true;
            return new ConfigWriteResult(true, "config.toml", "saved");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ApplyGlobalAsync(CodexConfigService.Modes[1], cancellation.Token));
        Assert.False(wroteCodex);
    }

    private sealed class FailingReadRpc(Exception failure) : IClaudeCodeRouterRpcClient
    {
        public bool IsInstalled => true;
        public Task<JsonNode?> InvokeAsync(string method, JsonArray? arguments = null,
            CancellationToken cancellationToken = default) => Task.FromException<JsonNode?>(failure);
    }

    [Fact]
    public async Task BalancedUpdatesOnlyModelsWhoseCatalogCeilingSupportsIt()
    {
        JsonObject original = Config();
        var rpc = new FakeRpcClient(original, Catalog());
        string baselinePath = Path.Combine(_testDirectory, "baseline.json");
        var service = new ClaudeCodeRouterConfigService(rpc, baselinePath);

        CcrContextWriteResult result = await service.ApplyAsync(CodexConfigService.Modes[1]);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Changed);
        Assert.Equal(300_000, Context(rpc.Config, "million", "contextWindow"));
        Assert.Equal(300_000, Context(rpc.Config, "million", "maxContextWindow"));
        Assert.Equal(300_000, Context(rpc.Config, "million", "contextWindowOverride"));
        Assert.Equal(240_000, Context(rpc.Config, "million", "autoCompactWindow"));
        Assert.Equal(100, Context(rpc.Config, "million", "effectiveContextWindowPercent"));
        Assert.Equal(300_000, Context(rpc.Config, "medium", "contextWindow"));
        Assert.Equal(300_000, Context(rpc.Config, "medium", "maxContextWindow"));
        Assert.Equal(128_000, Context(rpc.Config, "small", "contextWindow"));
        Assert.Equal("preserved-test-value", rpc.Config["Providers"]![0]!["api_key"]!.GetValue<string>());
        Assert.False(rpc.LastSaveOptions!["applyProfile"]!.GetValue<bool>());
        Assert.Equal(1, rpc.ContextSyncCount);
        Assert.Contains("2 supported model(s)", result.Message);
        Assert.Contains("1 model(s)", result.Message);

        string baseline = File.ReadAllText(baselinePath);
        Assert.DoesNotContain("api_key", baseline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preserved-test-value", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyActivePresetStillSynchronizesClaudeCodeContext()
    {
        var rpc = new FakeRpcClient(Config(), Catalog());
        var service = new ClaudeCodeRouterConfigService(
            rpc,
            Path.Combine(_testDirectory, "already-active-baseline.json"));

        CcrContextWriteResult first = await service.ApplyAsync(CodexConfigService.Modes[1]);
        int firstSyncCount = rpc.ContextSyncCount;
        CcrContextWriteResult second = await service.ApplyAsync(CodexConfigService.Modes[1]);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.False(second.Changed);
        Assert.Equal(firstSyncCount + 1, rpc.ContextSyncCount);
    }

    [Fact]
    public async Task MillionPresetLeavesSubMillionModelsAtTheirOriginalValues()
    {
        var rpc = new FakeRpcClient(Config(), Catalog());
        var service = new ClaudeCodeRouterConfigService(
            rpc,
            Path.Combine(_testDirectory, "baseline.json"));

        CcrContextWriteResult result = await service.ApplyAsync(CodexConfigService.Modes[3]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1_000_000, Context(rpc.Config, "million", "contextWindow"));
        Assert.Equal(1_000_000, Context(rpc.Config, "million", "maxContextWindow"));
        Assert.Equal(900_000, Context(rpc.Config, "million", "autoCompactWindow"));
        Assert.Equal(256_000, Context(rpc.Config, "medium", "contextWindow"));
        Assert.Equal(128_000, Context(rpc.Config, "small", "contextWindow"));
    }

    [Fact]
    public async Task MovingToAHigherPresetRestoresModelsThatNoLongerQualify()
    {
        var rpc = new FakeRpcClient(Config(), Catalog());
        var service = new ClaudeCodeRouterConfigService(
            rpc,
            Path.Combine(_testDirectory, "baseline.json"));

        Assert.True((await service.ApplyAsync(CodexConfigService.Modes[1])).Success);
        Assert.Equal(300_000, Context(rpc.Config, "medium", "contextWindow"));

        CcrContextWriteResult result = await service.ApplyAsync(CodexConfigService.Modes[2]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(600_000, Context(rpc.Config, "million", "contextWindow"));
        Assert.Equal(256_000, Context(rpc.Config, "medium", "contextWindow"));
        Assert.Equal(256_000, Context(rpc.Config, "medium", "maxContextWindow"));
    }

    [Fact]
    public async Task DefaultRestoresTheExactOriginalContextMetadataAndRemovesBaseline()
    {
        JsonObject original = Config();
        var rpc = new FakeRpcClient(original, Catalog());
        string baselinePath = Path.Combine(_testDirectory, "baseline.json");
        var service = new ClaudeCodeRouterConfigService(rpc, baselinePath);

        CcrContextWriteResult applied = await service.ApplyAsync(CodexConfigService.Modes[1]);
        CcrContextWriteResult restored = await service.ApplyAsync(CodexConfigService.Modes[0]);

        Assert.True(applied.Success, applied.Message);
        Assert.True(restored.Success, restored.Message);
        Assert.True(JsonNode.DeepEquals(original, rpc.Config));
        Assert.False(File.Exists(baselinePath));
        Assert.Equal(2, rpc.SaveCount);
    }

    [Fact]
    public async Task ReapplyingTheSamePresetDoesNotSaveOrRestartAgain()
    {
        var rpc = new FakeRpcClient(Config(), Catalog());
        var service = new ClaudeCodeRouterConfigService(
            rpc,
            Path.Combine(_testDirectory, "baseline.json"));

        Assert.True((await service.ApplyAsync(CodexConfigService.Modes[1])).Success);
        int savesAfterFirstApply = rpc.SaveCount;

        CcrContextWriteResult second = await service.ApplyAsync(CodexConfigService.Modes[1]);

        Assert.True(second.Success, second.Message);
        Assert.False(second.Changed);
        Assert.Equal(savesAfterFirstApply, rpc.SaveCount);
    }

    [Fact]
    public async Task IncompatibleCcrIsRejectedAndRolledBackWithoutLeavingABaseline()
    {
        JsonObject original = Config();
        var rpc = new FakeRpcClient(original, Catalog(), rejectSliderMetadata: true);
        string baselinePath = Path.Combine(_testDirectory, "baseline.json");
        var service = new ClaudeCodeRouterConfigService(rpc, baselinePath);

        CcrContextWriteResult result = await service.ApplyAsync(CodexConfigService.Modes[1]);

        Assert.False(result.Success);
        Assert.Contains("compatibility fork", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(JsonNode.DeepEquals(original, rpc.Config));
        Assert.False(File.Exists(baselinePath));
    }

    [Fact]
    public async Task CoordinatorRollsBackCcrWhenCodexWriteFails()
    {
        var ccr = new FakeContextService();
        var coordinator = new ContextConfigCoordinator(
            ccr,
            _ => new ConfigWriteResult(false, "config.toml", "Codex write failed."));

        ConfigWriteResult result = await coordinator.ApplyGlobalAsync(CodexConfigService.Modes[2]);

        Assert.False(result.Success);
        Assert.True(ccr.RollbackCalled);
        Assert.Contains("restored", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [LiveCcrFact]
    public async Task LiveCcrRoundTripRestoresTheExactOriginalConfiguration()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CODEX_USAGE_WIDGET_LIVE_CCR_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var rpc = new ClaudeCodeRouterRpcClient();
        Assert.True(rpc.IsInstalled);
        JsonObject original = (JsonObject)(await rpc.InvokeAsync("getConfig"))!;
        var service = new ClaudeCodeRouterConfigService(
            rpc,
            Path.Combine(_testDirectory, "live-baseline.json"));
        CcrContextWriteResult? applied = null;

        try
        {
            applied = await service.ApplyAsync(CodexConfigService.Modes[1]);
            Assert.True(applied.Success, applied.Message);
            Assert.NotNull(applied.RollbackState);

            JsonObject current = (JsonObject)(await rpc.InvokeAsync("getConfig"))!;
            JsonObject provider = current["Providers"]!
                .AsArray()
                .OfType<JsonObject>()
                .Single(item => item["id"]?.GetValue<string>() == "codex-api");
            Assert.Equal(300_000, provider["modelMetadata"]!["gpt-5.6-sol"]!["contextWindow"]!.GetValue<int>());
            Assert.Equal(300_000, provider["modelMetadata"]!["gpt-5.6-sol"]!["contextWindowOverride"]!.GetValue<int>());
            Assert.Equal(240_000, provider["modelMetadata"]!["gpt-5.6-sol"]!["autoCompactWindow"]!.GetValue<int>());

            (int maxInputTokens, int autoCompactWindow) = await WaitForGatewayModelAsync(
                current,
                "GPT-5.6 Sol",
                expectedMaxInputTokens: 300_000,
                expectedAutoCompactWindow: 240_000);
            Assert.Equal(300_000, maxInputTokens);
            Assert.Equal(240_000, autoCompactWindow);
        }
        finally
        {
            if (applied?.RollbackState is not null)
                Assert.True(await service.RollbackAsync(applied.RollbackState));
        }

        JsonObject restored = (JsonObject)(await rpc.InvokeAsync("getConfig"))!;
        Assert.True(JsonNode.DeepEquals(original, restored));
    }

    [LiveCcrFact]
    public async Task LiveCcrGatewayPublishesEveryNonDefaultSliderMode()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CODEX_USAGE_WIDGET_LIVE_CCR_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var rpc = new ClaudeCodeRouterRpcClient();
        JsonObject original = (JsonObject)(await rpc.InvokeAsync("getConfig"))!;
        var service = new ClaudeCodeRouterConfigService(
            rpc,
            Path.Combine(_testDirectory, "live-all-modes-baseline.json"));

        foreach (ContextMode mode in CodexConfigService.Modes.Where(mode => !mode.IsDefault))
        {
            CcrContextWriteResult? applied = null;
            try
            {
                applied = await service.ApplyAsync(mode);
                Assert.True(applied.Success, applied.Message);
                JsonObject current = (JsonObject)(await rpc.InvokeAsync("getConfig"))!;
                (int maxInputTokens, int autoCompactWindow) = await WaitForGatewayModelAsync(
                    current,
                    "GPT-5.6 Sol",
                    mode.ContextWindow!.Value,
                    mode.CompactLimit!.Value);
                Assert.Equal(mode.ContextWindow.Value, maxInputTokens);
                Assert.Equal(mode.CompactLimit.Value, autoCompactWindow);
            }
            finally
            {
                if (applied?.RollbackState is not null)
                    Assert.True(await service.RollbackAsync(applied.RollbackState));
            }

            JsonObject restored = (JsonObject)(await rpc.InvokeAsync("getConfig"))!;
            Assert.True(JsonNode.DeepEquals(original, restored));
        }
    }

    private static async Task<(int MaxInputTokens, int AutoCompactWindow)> WaitForGatewayModelAsync(
        JsonObject config,
        string displayName,
        int expectedMaxInputTokens,
        int expectedAutoCompactWindow)
    {
        int port = config["gateway"]?["port"]?.GetValue<int>()
            ?? config["PORT"]?.GetValue<int>()
            ?? throw new InvalidOperationException("CCR gateway port is missing.");
        string apiKey = config["APIKEY"]?.GetValue<string>()
            ?? throw new InvalidOperationException("CCR gateway API key is missing.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.215");
        Uri modelsUri = new($"http://127.0.0.1:{port}/v1/models");
        Uri bootstrapUri = new($"http://127.0.0.1:{port}/api/claude_cli/bootstrap");
        (int MaxInputTokens, int AutoCompactWindow) last = default;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            JsonObject models = JsonNode.Parse(await client.GetStringAsync(modelsUri))!.AsObject();
            JsonObject model = models["data"]!
                .AsArray()
                .OfType<JsonObject>()
                .Single(item => item["display_name"]?.GetValue<string>()
                    .Contains(displayName, StringComparison.OrdinalIgnoreCase) == true);
            string modelId = model["id"]!.GetValue<string>();
            int maxInput = model["max_input_tokens"]!.GetValue<int>();

            JsonObject bootstrap = JsonNode.Parse(await client.GetStringAsync(bootstrapUri))!.AsObject();
            int compact = bootstrap["auto_compact_windows"]?[modelId]?.GetValue<int>() ?? 0;
            last = (maxInput, compact);
            if (last == (expectedMaxInputTokens, expectedAutoCompactWindow))
                return last;

            await Task.Delay(250);
        }

        return last;
    }

    private static JsonObject Config() => new()
    {
        ["Providers"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "codex-api",
                ["name"] = "Codex API",
                ["type"] = "openai_responses",
                ["api_base_url"] = "https://chatgpt.com/backend-api/codex",
                ["api_key"] = "preserved-test-value",
                ["models"] = new JsonArray("million", "medium", "small"),
                ["modelMetadata"] = new JsonObject
                {
                    ["million"] = new JsonObject
                    {
                        ["contextWindow"] = 368_000,
                        ["maxContextWindow"] = 368_000,
                        ["effectiveContextWindowPercent"] = 95,
                        ["displayName"] = "Million"
                    },
                    ["medium"] = new JsonObject
                    {
                        ["contextWindow"] = 256_000,
                        ["maxContextWindow"] = 256_000,
                        ["effectiveContextWindowPercent"] = 95
                    },
                    ["small"] = new JsonObject
                    {
                        ["contextWindow"] = 128_000,
                        ["maxContextWindow"] = 128_000
                    }
                }
            }
        },
        ["Router"] = new JsonObject { ["rules"] = new JsonArray() },
        ["profile"] = new JsonObject { ["enabled"] = true }
    };

    private static JsonObject Catalog() => new()
    {
        ["million"] = new JsonObject
        {
            ["contextWindow"] = 1_050_000,
            ["maxContextWindow"] = 1_050_000
        },
        ["medium"] = new JsonObject
        {
            ["contextWindow"] = 400_000,
            ["maxContextWindow"] = 400_000
        },
        ["small"] = new JsonObject
        {
            ["contextWindow"] = 128_000,
            ["maxContextWindow"] = 128_000
        }
    };

    private static int Context(JsonObject config, string model, string property) =>
        config["Providers"]![0]!["modelMetadata"]![model]![property]!.GetValue<int>();

    public void Dispose()
    {
        string resolved = Path.GetFullPath(_testDirectory);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(resolved).StartsWith("CodexUsageWidget-ccr-tests-", StringComparison.Ordinal))
        {
            Directory.Delete(resolved, true);
        }
    }

    private sealed class FakeRpcClient : IClaudeCodeRouterRpcClient
    {
        private readonly JsonObject _catalog;
        private readonly bool _rejectSliderMetadata;

        internal FakeRpcClient(
            JsonObject config,
            JsonObject catalog,
            bool rejectSliderMetadata = false)
        {
            Config = (JsonObject)config.DeepClone();
            _catalog = (JsonObject)catalog.DeepClone();
            _rejectSliderMetadata = rejectSliderMetadata;
        }

        public bool IsInstalled => true;
        internal JsonObject Config { get; private set; }
        internal JsonObject? LastSaveOptions { get; private set; }
        internal int SaveCount { get; private set; }
        internal int ContextSyncCount { get; private set; }

        public Task<JsonNode?> InvokeAsync(
            string method,
            JsonArray? arguments = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonNode? result = method switch
            {
                "getConfig" => Config.DeepClone(),
                "getProviderCatalogModels" => new JsonObject
                {
                    ["modelMetadata"] = _catalog.DeepClone()
                },
                "saveConfig" => Save(arguments),
                "syncClaudeCodeContextCache" => SynchronizeContext(),
                _ => throw new InvalidOperationException($"Unexpected RPC method: {method}")
            };
            return Task.FromResult<JsonNode?>(result);
        }

        private JsonNode SynchronizeContext()
        {
            ContextSyncCount++;
            return new JsonArray();
        }

        private JsonNode Save(JsonArray? arguments)
        {
            Config = arguments?[0]?.DeepClone() as JsonObject
                ?? throw new InvalidOperationException("saveConfig did not receive a config object.");
            if (_rejectSliderMetadata && Config["Providers"] is JsonArray providers)
            {
                foreach (JsonObject metadata in providers
                             .OfType<JsonObject>()
                             .SelectMany(provider => (provider["modelMetadata"] as JsonObject)?.Select(item => item.Value) ?? [])
                             .OfType<JsonObject>())
                {
                    metadata.Remove("autoCompactWindow");
                    metadata.Remove("contextWindowOverride");
                }
            }
            LastSaveOptions = arguments?[1]?.DeepClone() as JsonObject;
            SaveCount++;
            return Config.DeepClone();
        }
    }

    private sealed class LiveCcrFactAttribute : FactAttribute
    {
        public LiveCcrFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CODEX_USAGE_WIDGET_LIVE_CCR_TEST") != "1")
                Skip = "Requires an explicitly enabled live CCR instance; excluded from isolated CI.";
        }
    }

    private sealed class FakeContextService : IClaudeCodeRouterContextService
    {
        internal bool RollbackCalled { get; private set; }

        public Task<CcrContextWriteResult> ApplyAsync(
            ContextMode mode,
            CancellationToken cancellationToken = default)
        {
            var rollback = new CcrContextRollbackState(new JsonObject(), false, false, null);
            return Task.FromResult(new CcrContextWriteResult(true, true, true, "CCR applied.", rollback));
        }

        public Task<bool> RollbackAsync(
            CcrContextRollbackState state,
            CancellationToken cancellationToken = default)
        {
            RollbackCalled = true;
            return Task.FromResult(true);
        }
    }
}
