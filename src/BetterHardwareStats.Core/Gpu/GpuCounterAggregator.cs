using BetterHardwareStats.Core.Counters;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public sealed record GpuCounterResult(
    float UtilizationPercent,
    IReadOnlyDictionary<EngineKind, float> EnginePercent,
    float? VideoEncodePercent,
    float? VideoDecodePercent,
    float VRChatUtilizationPercent,
    long? DedicatedUsedBytes,
    long? SharedUsedBytes,
    int EngineInstanceCount);
public sealed class GpuCounterAggregator
{
    private readonly record struct EngineKey(Luid Luid, int Phys, int Engine);

    private readonly Dictionary<EngineKey, (EngineKind Kind, double Sum, double VRChatSum)> _engines = new();
    private readonly Dictionary<Luid, (long Dedicated, long Shared, bool HasDedicated, bool HasShared)> _memory = new();
    public IReadOnlyDictionary<Luid, GpuCounterResult>? Aggregate(
        ICounterReader engine,
        ICounterReader? dedicated,
        ICounterReader? shared,
        IReadOnlyCollection<int> vrchatPids,
        IEnumerable<Luid> knownAdapters)
    {
        if (!engine.Available) return null;

        _engines.Clear();
        _memory.Clear();

        for (var i = 0; i < engine.Count; i++)
        {
            if (!engine.IsValid(i)) continue;
            if (!GpuInstanceParser.TryParseEngine(engine.GetName(i), out var inst)) continue;
            var value = engine.GetValue(i);
            if (double.IsNaN(value) || value < 0) continue;

            var key = new EngineKey(inst.Luid, inst.Phys, inst.Engine);
            _engines.TryGetValue(key, out var acc);
            acc.Kind = inst.Kind;
            acc.Sum += value;
            if (vrchatPids.Contains(inst.Pid)) acc.VRChatSum += value;
            _engines[key] = acc;
        }

        ReadMemory(dedicated, isShared: false);
        ReadMemory(shared, isShared: true);

        var results = new Dictionary<Luid, GpuCounterResult>();
        var luids = new HashSet<Luid>(knownAdapters);
        foreach (var k in _engines.Keys) luids.Add(k.Luid);
        foreach (var k in _memory.Keys) luids.Add(k);

        foreach (var luid in luids)
        {
            float util = 0, vrchat = 0;
            float? enc = null, dec = null;
            var perKind = new Dictionary<EngineKind, float>();
            var count = 0;
            foreach (var (k, acc) in _engines)
            {
                if (k.Luid != luid) continue;
                count++;
                var v = (float)Math.Clamp(acc.Sum, 0, 100);
                var vv = (float)Math.Clamp(acc.VRChatSum, 0, 100);
                util = Math.Max(util, v);
                vrchat = Math.Max(vrchat, vv);
                perKind[acc.Kind] = perKind.TryGetValue(acc.Kind, out var prev) ? Math.Max(prev, v) : v;
                if (acc.Kind is EngineKind.VideoEncode or EngineKind.VideoCodec) enc = Math.Max(enc ?? 0, v);
                if (acc.Kind is EngineKind.VideoDecode or EngineKind.VideoCodec) dec = Math.Max(dec ?? 0, v);
            }

            _memory.TryGetValue(luid, out var mem);
            results[luid] = new GpuCounterResult(
                util, perKind, enc, dec, vrchat,
                mem.HasDedicated ? mem.Dedicated : null,
                mem.HasShared ? mem.Shared : null,
                count);
        }

        return results;
    }

    private void ReadMemory(ICounterReader? reader, bool isShared)
    {
        if (reader is null || !reader.Available) return;
        for (var i = 0; i < reader.Count; i++)
        {
            if (!reader.IsValid(i)) continue;
            if (!GpuInstanceParser.TryParseMemory(reader.GetName(i), out var inst)) continue;
            var bytes = (long)Math.Max(0, reader.GetValue(i));
            _memory.TryGetValue(inst.Luid, out var m);
            // Multiple phys nodes on one LUID (linked adapters) add up.
            if (isShared) { m.Shared += bytes; m.HasShared = true; }
            else { m.Dedicated += bytes; m.HasDedicated = true; }
            _memory[inst.Luid] = m;
        }
    }
}
