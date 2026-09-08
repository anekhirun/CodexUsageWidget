using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexUsageWidget;

internal sealed record RateLimit(double UsedPercent, int WindowMinutes, long? ResetsAt);
internal sealed record UsageSnapshot(RateLimit Long, RateLimit? Short, DateTimeOffset EventTime);

internal static class UsageReader
{
    private const int DefaultMaxFilesToInspect = 32;
    private const int FastTailSize = 64 * 1024;
    private const int DeepTailSize = 2 * 1024 * 1024;
    private const int MaxDeepFilesToInspect = 4;

    internal static string SessionRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    internal static UsageSnapshot? GetLatestSnapshot(
        string? sessionRoot = null,
        int maxFilesToInspect = DefaultMaxFilesToInspect)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFilesToInspect, 1);

        string root = Path.GetFullPath(sessionRoot ?? SessionRoot);
        if (!Directory.Exists(root)) return null;

        FileInfo[] recentFiles;
        try
        {
            recentFiles = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(maxFilesToInspect)
                .ToArray();
        }
        catch
        {
            return null;
        }

        UsageSnapshot? latest = null;
        for (int index = 0; index < recentFiles.Length; index++)
        {
            FileInfo file = recentFiles[index];
            UsageSnapshot? snapshot = ReadSnapshot(file, FastTailSize);
            if (snapshot is null &&
                index < MaxDeepFilesToInspect &&
                file.Length > FastTailSize)
            {
                snapshot = ReadSnapshot(file, DeepTailSize);
            }

            if (snapshot is not null &&
                (latest is null || snapshot.EventTime > latest.EventTime))
            {
                latest = snapshot;
            }
        }

        return latest;
    }

    private static UsageSnapshot? ReadSnapshot(FileInfo file, int tailSize)
    {
        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            long length = stream.Length;
            long start = Math.Max(0, length - tailSize);
            bool startsAtLineBoundary = start == 0;
            if (start > 0)
            {
                stream.Seek(start - 1, SeekOrigin.Begin);
                startsAtLineBoundary = stream.ReadByte() == '\n';
            }
            stream.Seek(start, SeekOrigin.Begin);
            // Snapshot the read length: a concurrently growing file cannot turn a
            // bounded tail into an unbounded ReadToEnd operation.
            byte[] bytes = new byte[(int)(length - start)];
            stream.ReadExactly(bytes);
            string content = Encoding.UTF8.GetString(bytes);
            if (!startsAtLineBoundary)
            {
                int firstNewline = content.IndexOf('\n');
                if (firstNewline < 0) return null;
                content = content[(firstNewline + 1)..];
            }
            UsageSnapshot? latest = null;
            int lineEnd = content.Length;
            while (lineEnd > 0)
            {
                int newline = content.LastIndexOf('\n', lineEnd - 1);
                int lineStart = newline + 1;
                int lineLength = lineEnd - lineStart;
                if (lineLength > 0 && content[lineEnd - 1] == '\r') lineLength--;

                if (lineLength > 0)
                {
                    ReadOnlySpan<char> line = content.AsSpan(lineStart, lineLength);
                    if (line.Contains("token_count", StringComparison.Ordinal))
                    {
                        UsageSnapshot? snapshot = ParseSnapshot(line, file.LastWriteTime);
                        if (snapshot is not null && (latest is null || snapshot.EventTime > latest.EventTime))
                            latest = snapshot;
                    }
                }

                if (newline < 0) break;
                lineEnd = newline;
            }
            return latest;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static UsageSnapshot? ParseSnapshot(
        ReadOnlySpan<char> line,
        DateTimeOffset fallbackEventTime)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line.ToString());
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("payload", out JsonElement payload) ||
                !payload.TryGetProperty("type", out JsonElement type) ||
                type.GetString() != "token_count" ||
                !payload.TryGetProperty("rate_limits", out JsonElement limitsElement) ||
                limitsElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Dedicated model pools (for example codex_bengalfox / Spark) are
            // separate quotas. Their newer events must not replace the main Codex quota.
            // Older rollouts omitted the ID or recorded null for the main pool.
            if (limitsElement.TryGetProperty("limit_id", out JsonElement limitId) &&
                limitId.ValueKind != JsonValueKind.Null &&
                (limitId.ValueKind != JsonValueKind.String || limitId.GetString() != "codex"))
            {
                return null;
            }

            var limits = new List<RateLimit>(2);
            AddLimit(limitsElement, "primary", limits);
            AddLimit(limitsElement, "secondary", limits);
            if (limits.Count == 0) return null;

            RateLimit? longLimit = limits
                .Where(limit => limit.WindowMinutes >= 1440)
                .OrderByDescending(limit => limit.WindowMinutes)
                .FirstOrDefault();
            // A five-hour-only update is not a weekly quota update.
            if (longLimit is null) return null;
            RateLimit? shortLimit = limits
                .Where(limit => limit.WindowMinutes < 1440)
                .OrderBy(limit => limit.WindowMinutes)
                .FirstOrDefault();

            DateTimeOffset eventTime = fallbackEventTime;
            if (root.TryGetProperty("timestamp", out JsonElement timestamp) &&
                DateTimeOffset.TryParse(timestamp.GetString(), out DateTimeOffset parsed))
            {
                eventTime = parsed.ToLocalTime();
            }

            return new UsageSnapshot(longLimit, shortLimit, eventTime);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void AddLimit(JsonElement parent, string propertyName, ICollection<RateLimit> limits)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
            return;
        if (!element.TryGetProperty("used_percent", out JsonElement usedElement) || !usedElement.TryGetDouble(out double used) || !double.IsFinite(used))
            return;

        int window = element.TryGetProperty("window_minutes", out JsonElement windowElement) && windowElement.TryGetInt32(out int value)
            ? value
            : 0;
        long? resetsAt = element.TryGetProperty("resets_at", out JsonElement resetElement) && resetElement.TryGetInt64(out long reset)
            ? reset
            : null;
        limits.Add(new RateLimit(used, window, resetsAt));
    }
}
