using Xunit;

namespace CodexUsageWidget.Tests;

public sealed class BoundedUsageReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"widget-tail-tests-{Guid.NewGuid():N}");
    private static readonly DateTime FileTime = new(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);

    public BoundedUsageReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DeepTailFindsNewestEventBehindNonUsageAndIncompleteRecords()
    {
        Write("recent.jsonl", Event(3, 17) + "\n" + new string('x', 80_000) + "\n{\"payload\":{\"type\":\"token_count\"", 0);
        Write("older.jsonl", Event(2, 40), 1);
        Assert.Equal(17, UsageReader.GetLatestSnapshot(_root)!.Long.UsedPercent);
    }

    [Fact]
    public void FourthFileGetsDeepFallbackButFifthDoesNot()
    {
        for (int i = 0; i < 3; i++) Write($"empty-{i}.jsonl", "{}", i);
        Write("fourth.jsonl", Event(2, 22) + "\n" + new string('x', 80_000) + "\n", 3);
        Write("fifth.jsonl", Event(3, 11) + "\n" + new string('x', 80_000) + "\n", 4);
        Assert.Equal(22, UsageReader.GetLatestSnapshot(_root)!.Long.UsedPercent);
    }

    [Fact]
    public void EventOutsideTwoMegabyteBudgetIsNotRead()
    {
        Write("recent.jsonl", Event(3, 11) + "\n" + new string('x', 2 * 1024 * 1024 + 100) + "\n", 0);
        Write("fallback.jsonl", Event(2, 22), 1);
        Assert.Equal(22, UsageReader.GetLatestSnapshot(_root)!.Long.UsedPercent);
    }

    [Fact]
    public void CompleteEventAtExactFastTailBoundaryIsPreserved()
    {
        for (int i = 0; i < 4; i++) Write($"empty-{i}.jsonl", "{}", i);
        string token = Event(3, 11) + "\n";
        string exactTail = token + new string('x', 65536 - token.Length - 1) + "\n";
        Write("fifth.jsonl", "prefix\n" + exactTail, 4);
        Assert.Equal(11, UsageReader.GetLatestSnapshot(_root)!.Long.UsedPercent);
    }

    [Fact]
    public void MalformedShapesAndOutOfOrderRecordsDoNotHideNewestValidEvent()
    {
        Write("events.jsonl", Event(3, 11) + "\n" + Event(2, 22) +
            "\n{\"payload\":\"token_count\"}\n{\"payload\":{\"type\":\"token_count\",\"rate_limits\":null}}\n", 0);
        Assert.Equal(11, UsageReader.GetLatestSnapshot(_root)!.Long.UsedPercent);
    }

    [Fact]
    public void RetainsUpstreamFiveHourLimit()
    {
        Write("short.jsonl", Event(3, 11), 0);
        var snapshot = UsageReader.GetLatestSnapshot(_root)!;
        Assert.Equal(15, snapshot.Short!.UsedPercent);
        Assert.Equal(300, snapshot.Short.WindowMinutes);
        Assert.Equal(1_787_200_000, snapshot.Short.ResetsAt);
    }

    private void Write(string name, string content, int rank)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, FileTime.AddSeconds(-rank));
    }

    private static string Event(int hour, int used) => System.Text.Json.JsonSerializer.Serialize(new
    {
        timestamp = $"2026-09-09T0{hour}:00:00Z",
        payload = new
        {
            type = "token_count",
            rate_limits = new
            {
                primary = new { used_percent = 15, window_minutes = 300, resets_at = 1787200000 },
                secondary = new { used_percent = used, window_minutes = 10080, resets_at = 1787200000 }
            }
        }
    });

    public void Dispose() => Directory.Delete(_root, true);
}
