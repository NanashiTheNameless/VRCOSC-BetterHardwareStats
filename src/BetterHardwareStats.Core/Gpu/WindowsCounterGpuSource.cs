using System.Diagnostics;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public sealed class WindowsCounterGpuSource
{
    private readonly long _absentTicks;
    private readonly Dictionary<Luid, long> _absentSince = new();

    public WindowsCounterGpuSource(long? absentGraceTicks = null) => _absentTicks = absentGraceTicks ?? 10 * Stopwatch.Frequency;

    public static string KeyFor(Luid luid) => luid.ToString();

    public static IReadOnlyList<BackendDevice> DevicesFor(IReadOnlyList<GpuIdentity> catalog) =>
        catalog.Select(c => new BackendDevice(KeyFor(c.Luid), c.Vendor, c.Name, null, null, null, null, c.Luid, null)).ToList();
    public IReadOnlyCollection<Luid> AbsentAdapters => _absentSince.Where(kv => kv.Value == long.MinValue).Select(kv => kv.Key).ToList();
    public BackendSample? Convert(IReadOnlyDictionary<Luid, GpuCounterResult>? results, long timestampTicks)
    {
        if (results is null) return null;

        var anyVisible = results.Values.Any(Visible);
        var map = new Dictionary<string, PartialGpuMetrics>(results.Count);
        foreach (var (luid, r) in results)
        {
            float? util = r.UtilizationPercent;
            if (Visible(r))
            {
                _absentSince.Remove(luid);
            }
            else if (anyVisible)
            {
                if (!_absentSince.TryGetValue(luid, out var since)) _absentSince[luid] = since = timestampTicks;
                if (since == long.MinValue || timestampTicks - since >= _absentTicks)
                {
                    _absentSince[luid] = long.MinValue;
                    util = null;
                }
            }

            map[KeyFor(luid)] = new PartialGpuMetrics
            {
                UtilizationPercent = util,
                EnginePercent = r.EnginePercent,
                VideoEncodePercent = r.VideoEncodePercent,
                VideoDecodePercent = r.VideoDecodePercent,
                VRChatUtilizationPercent = util is null ? null : r.VRChatUtilizationPercent,
                VramUsedBytes = r.DedicatedUsedBytes,
                SharedUsedBytes = r.SharedUsedBytes,
            };
        }
        return new BackendSample(timestampTicks, map);
    }

    private static bool Visible(GpuCounterResult r) => r.EngineInstanceCount > 0 || r.DedicatedUsedBytes is not null || r.SharedUsedBytes is not null;
}
