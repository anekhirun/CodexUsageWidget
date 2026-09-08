using System.Text.Json.Nodes;
using Xunit;

namespace CodexUsageWidget.Tests;

public sealed class QuotaSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"widget-quota-tests-{Guid.NewGuid():N}");

    public QuotaSelectionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void NewerSparkEventDoesNotReplaceMainCodexQuotaInSameFile()
    {
        Write("mixed.jsonl", Event("codex", 22, 1) + Event("codex_bengalfox", 0, 2));
        var snapshot = UsageReader.GetLatestSnapshot(_directory);
        Assert.NotNull(snapshot);
        Assert.Equal(22, snapshot.Long.UsedPercent);
        Assert.Equal(78, 100 - snapshot.Long.UsedPercent);
    }

    [Fact]
    public void NewerSparkSessionDoesNotReplaceMainCodexQuotaAcrossFiles()
    {
        Write("main.jsonl", Event("codex", 22, 1));
        Write("spark.jsonl", Event("codex_bengalfox", 0, 2));
        Assert.Equal(22, UsageReader.GetLatestSnapshot(_directory)!.Long.UsedPercent);
    }

    [Theory]
    [InlineData("codex_bengalfox")]
    [InlineData("another_model_pool")]
    public void DedicatedPoolAloneIsUnavailableInsteadOfFalseFullQuota(string id)
    {
        Write("other.jsonl", Event(id, 0, 2));
        Assert.Null(UsageReader.GetLatestSnapshot(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyMissingOrNullPoolIdStillWorks(bool omitId)
    {
        Write("legacy.jsonl", Event(null, 22, 1, omitId: omitId));
        Write("spark.jsonl", Event("codex_bengalfox", 0, 2));
        Assert.Equal(22, UsageReader.GetLatestSnapshot(_directory)!.Long.UsedPercent);
    }

    [Fact]
    public void ActualZeroUsageInMainPoolStillShowsFullRemainingQuota()
    {
        Write("main.jsonl", Event("codex", 0, 1));
        Assert.Equal(0, UsageReader.GetLatestSnapshot(_directory)!.Long.UsedPercent);
    }

    [Fact]
    public void ShortOnlyUpdateCannotReplaceWeeklyUsageWithZero()
    {
        Write("main.jsonl", Event("codex", 22, 1) + Event("codex", 0, 2, weekly: false));
        var snapshot = UsageReader.GetLatestSnapshot(_directory)!;
        Assert.Equal(22, snapshot.Long.UsedPercent);
        Assert.Equal(10080, snapshot.Long.WindowMinutes);
    }

    [Fact]
    public void ZeroUsageInSparkDoesNotPreventLaterMainQuotaUpdates()
    {
        Write("main.jsonl", Event("codex", 22, 1) + Event("codex_bengalfox", 0, 2) + Event("codex", 23, 3));
        Assert.Equal(23, UsageReader.GetLatestSnapshot(_directory)!.Long.UsedPercent);
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_directory, name), content);

    private static string Event(string? id, double used, int hour, bool omitId = false, bool weekly = true)
    {
        var limits = new JsonObject
        {
            ["primary"] = new JsonObject
            {
                ["used_percent"] = used,
                ["window_minutes"] = weekly ? 10080 : 300,
                ["resets_at"] = 1789500597
            },
            ["secondary"] = null
        };
        if (!omitId) limits["limit_id"] = id;
        return new JsonObject
        {
            ["timestamp"] = $"2026-09-09T0{hour}:00:00Z",
            ["payload"] = new JsonObject { ["type"] = "token_count", ["rate_limits"] = limits }
        }.ToJsonString() + Environment.NewLine;
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
