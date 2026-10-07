using System.Globalization;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;

public readonly record struct EngineInstance(int Pid, Luid Luid, int Phys, int Engine, EngineKind Kind);

public readonly record struct MemoryInstance(Luid Luid, int Phys);
public static class GpuInstanceParser
{
    public static bool TryParseEngine(ReadOnlySpan<char> name, out EngineInstance instance)
    {
        instance = default;
        var s = name;
        if (!Expect(ref s, "pid_") || !ReadInt(ref s, out var pid)) return false;
        if (!Expect(ref s, "_")) return false;
        if (!TryReadLuidPhys(ref s, out var luid, out var phys)) return false;
        if (!Expect(ref s, "_eng_") || !ReadInt(ref s, out var eng)) return false;
        if (!Expect(ref s, "_engtype_")) return false;
        instance = new EngineInstance(pid, luid, phys, eng, ParseEngineKind(s));
        return true;
    }

    public static bool TryParseMemory(ReadOnlySpan<char> name, out MemoryInstance instance)
    {
        instance = default;
        var s = name;
        if (!TryReadLuidPhys(ref s, out var luid, out var phys) || !s.IsEmpty) return false;
        instance = new MemoryInstance(luid, phys);
        return true;
    }
    public static EngineKind ParseEngineKind(ReadOnlySpan<char> type)
    {
        type = type.Trim();
        if (StartsWith(type, "3d") || StartsWith(type, "graphics") || StartsWith(type, "high priority 3d")) return EngineKind.Graphics3D;
        if (StartsWith(type, "compute") || StartsWith(type, "cuda")) return EngineKind.Compute;
        if (StartsWith(type, "copy")) return EngineKind.Copy;
        if (StartsWith(type, "videodecode")) return EngineKind.VideoDecode;
        if (StartsWith(type, "videoencode")) return EngineKind.VideoEncode;
        if (StartsWith(type, "videoprocessing")) return EngineKind.VideoProcessing;
        if (StartsWith(type, "video codec")) return EngineKind.VideoCodec; // AMD VCN encode/decode engine
        return EngineKind.Other;
    }

    private static bool TryReadLuidPhys(ref ReadOnlySpan<char> s, out Luid luid, out int phys)
    {
        luid = default;
        phys = 0;
        if (!Expect(ref s, "luid_0x") || !ReadHex8(ref s, out var hi)) return false;
        if (!Expect(ref s, "_0x") || !ReadHex8(ref s, out var lo)) return false;
        if (!Expect(ref s, "_phys_") || !ReadInt(ref s, out phys)) return false;
        luid = Luid.FromParts(unchecked((int)hi), lo);
        return true;
    }

    private static bool StartsWith(ReadOnlySpan<char> s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool Expect(ref ReadOnlySpan<char> s, string token)
    {
        if (!s.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return false;
        s = s[token.Length..];
        return true;
    }

    private static bool ReadInt(ref ReadOnlySpan<char> s, out int value)
    {
        var len = 0;
        while (len < s.Length && char.IsAsciiDigit(s[len])) len++;
        if (len == 0 || !int.TryParse(s[..len], NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }
        s = s[len..];
        return true;
    }

    private static bool ReadHex8(ref ReadOnlySpan<char> s, out uint value)
    {
        value = 0;
        if (s.Length < 8 || !uint.TryParse(s[..8], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)) return false;
        s = s[8..];
        return true;
    }
}
