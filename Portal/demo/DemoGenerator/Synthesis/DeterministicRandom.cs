using System.Text;

namespace DemoGenerator.Synthesis;

/// <summary>
/// The pseudo-random source of the synthesis, written so that C# and KQL compute the SAME numbers: only
/// 64-bit integer multiply, modulo, xor and shift on values below 2^31, which never overflow in either
/// engine. <see cref="KqlPrelude"/> is the exact KQL transcription of <see cref="Mix"/> and <see cref="U"/>.
/// </summary>
public static class DeterministicRandom
{
    public const long M = 2147483647;

    /// <summary>Park-Miller steps interleaved with xor-shifts. Input must be &gt;= 0; output is in [0, M].</summary>
    public static long Mix(long x)
    {
        x = x % (M - 1) + 1;
        x = x * 48271 % M;
        x ^= x >> 15;
        x = x * 69621 % M;
        x ^= x >> 13;
        x = x * 48271 % M;
        return x;
    }

    /// <summary>Uniform in (0, 1) for row key <paramref name="rk"/> and channel <paramref name="channel"/>.</summary>
    public static double U(long rk, long channel) => (Mix(rk + channel * 7919) + 0.5) / M;

    /// <summary>FNV-1a over UTF-8, reduced below <see cref="M"/>: a stable seed for a name.</summary>
    public static long Fnv(string text)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash % M;
    }

    public static string Hex8(long value) => (value & 0xFFFFFFFFL).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    public const string KqlPrelude =
        "let M = 2147483647;\n" +
        "let pmA = (x:long) { (x * 48271) % M };\n" +
        "let pmB = (x:long) { (x * 69621) % M };\n" +
        "let xs = (x:long, n:int) { binary_xor(x, binary_shift_right(x, n)) };\n" +
        "let mix = (x:long) { pmA(xs(pmB(xs(pmA(x % (M - 1) + 1), 15)), 13)) };\n" +
        "let U = (rk:long, c:long) { (todouble(mix(rk + c * 7919)) + 0.5) / todouble(M) };\n" +
        "let hex8 = (x:long) { tohex(x, 8) };\n";
}
