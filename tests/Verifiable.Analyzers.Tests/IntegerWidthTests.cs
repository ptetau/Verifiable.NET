using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Integer widths, narrowing casts and bitwise operators are modelled as C# does (#27): <c>~ &amp; | ^ &lt;&lt; &gt;&gt;</c>
/// on every integer width, unchecked casts that wrap, <c>byte</c>/<c>short</c>/<c>char</c> arithmetic that is done in
/// <c>int</c> and converted back, a decimal cast that truncates, <c>Math.Clamp</c>, and a divisor that is checked inside a
/// contract too.
/// </summary>
public class IntegerWidthTests
{
    private static async Task<List<string>> AnalyzeAsync(string source)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync("using System;\nusing Verifiable.Contracts;\n" + source, new VerifiableSmtAnalyzer());
        var problems = diags.Where(d => d.Id is "VER101" or "VER103" or "VER105" or "AD0001").Select(d => d.Id + ": " + d.GetMessage()).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        return diags.Where(d => d.Id == "VER102").Select(d => d.GetMessage()).ToList();
    }

    private static async Task AssertProvedAsync(string source) =>
        Assert.Empty(await AnalyzeAsync(source));

    private static async Task AssertRefutedAsync(string source, string? containing = null)
    {
        var errors = await AnalyzeAsync(source);
        Assert.NotEmpty(errors);
        if (containing is not null)
            Assert.Contains(errors, e => e.Contains(containing, System.StringComparison.Ordinal));
    }

    private static string Wrap(string attribute, string method) => $$"""
        [Verifiable] {{attribute}}
        public static class A
        {
            {{method}}
        }
        """;

    private static readonly string[] Modes = ["", "[CheckedArithmetic]", "[UncheckedArithmetic]"];

    public static TheoryData<string> AllModes() => new(Modes);

    // ---- bitwise operators ----------------------------------------------------------------------------------

    [Theory, MemberData(nameof(AllModes))]
    public async Task And_WithAMask_KeepsTheLowBits(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Low(int x) { Spec.Ensures<int>(r => r >= 0 && r <= 255, "a byte"); return x & 0xFF; }
            public static int LowOfNegative() { Spec.Ensures<int>(r => r == 253, "two's complement"); return -3 & 0xFF; }
            public static int Pinned(int x) { Spec.Requires(x == -3, "pin"); Spec.Ensures<int>(r => r == 253, "two's complement"); return x & 0xFF; }
            """));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task AndOrXor_OnIntAndLong_HaveTheirBitwiseMeaning(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Xor(int x, int y) { Spec.Ensures<int>(r => r == (x ^ y), "xor"); return x ^ y; }
            public static int XorSelf(int x) { Spec.Ensures<int>(r => r == 0, "x ^ x"); return x ^ x; }
            public static int AndNonNegative(int x, int y) { Spec.Requires(y >= 0 && y <= 255, "a byte"); Spec.Ensures<int>(r => r >= 0 && r <= 255, "a byte"); return x & y; }
            public static int Or(int x) { Spec.Requires(x == 5, "pin"); Spec.Ensures<int>(r => r == 7, "5 | 2"); return x | 2; }
            public static int XorPinned(int x, int y) { Spec.Requires(x == -1 && y == 255, "pin"); Spec.Ensures<int>(r => r == -256, "-1 ^ 255"); return x ^ y; }
            public static long LongOr(long x) { Spec.Requires(x == 1L << 40, "pin"); Spec.Ensures<long>(r => r == (1L << 40) + 1, "set bit 0"); return x | 1L; }
            public static long LongAnd(long x) { Spec.Requires(x == -1L, "pin"); Spec.Ensures<long>(r => r == 0xFFFFFFFFL, "low 32 bits"); return x & 0xFFFFFFFFL; }
            """));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task BitwiseOperators_OnUnsignedTypes(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static uint Not(uint x) { Spec.Ensures<uint>(r => r == uint.MaxValue - x, "complement"); return ~x; }
            public static uint NotZero() { Spec.Ensures<uint>(r => r == uint.MaxValue, "all ones"); return ~0u; }
            public static ulong NotUlong(ulong x) { Spec.Ensures<ulong>(r => r == ulong.MaxValue - x, "complement"); return ~x; }
            public static uint And(uint x) { Spec.Ensures<uint>(r => r <= 15u, "a nibble"); return x & 15u; }
            public static uint Xor(uint x, uint y) { Spec.Requires(x == 12u && y == 10u, "pin"); Spec.Ensures<uint>(r => r == 6u, "12 ^ 10"); return x ^ y; }
            public static uint Right(uint x) { Spec.Ensures<uint>(r => r <= x, "shifts down"); return x >> 3; }
            """));
    }

    // Identities over unknown operands need Z3 to reason about the bits: under [CheckedArithmetic] int and long are
    // bit-vectors, so there they are decided. With Int terms, Z3 may answer unknown, which is never a false proof.
    [Fact]
    public async Task CheckedArithmetic_BitIdentities_AreDecided()
    {
        await AssertProvedAsync(Wrap("[CheckedArithmetic]", """
            public static int Xor(int x, int y) { Spec.Ensures<int>(r => r == x, "xor twice"); return x ^ y ^ y; }
            public static int AndNot(int x, int y) { Spec.Ensures<int>(r => (r & y) == 0, "nothing of y is left"); return x & ~y; }
            public static long LongXor(long x, long y) { Spec.Ensures<long>(r => r == x, "xor twice"); return x ^ y ^ y; }
            public static int OrAtLeast(int x, int y) { Spec.Requires(x >= 0 && y >= 0, "non-negative"); Spec.Ensures<int>(r => r >= x, "or only sets bits"); return x | y; }
            """));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task BitwiseNot_IsMinusOneMinusX_NotTheIdentity(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Flip(int x) { Spec.Ensures<int>(r => x == int.MinValue || r == -x - 1, "~x"); return ~x; }
            public static int FlipPinned(int x) { Spec.Requires(x == 0, "pin"); Spec.Ensures<int>(r => r == -1, "~0"); return ~x; }
            public static long FlipLong(long x) { Spec.Requires(x == 5L, "pin"); Spec.Ensures<long>(r => r == -6L, "~5"); return ~x; }
            """));
        await AssertRefutedAsync(Wrap(mode, "public static int Flip(int x) { Spec.Ensures<int>(r => r == x, \"identity\"); return ~x; }"));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task Shifts_HaveCSharpMeaning(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Twice(int x) { Spec.Requires(x >= 0 && x <= 1000, "small"); Spec.Ensures<int>(r => r == x * 2, "doubles"); return x << 1; }
            public static int Eight(int x) { Spec.Requires(x >= -1000 && x <= 1000, "small"); Spec.Ensures<int>(r => r == x * 8, "times 8"); return x << 3; }
            public static int ArithmeticRight(int x) { Spec.Requires(x == -7, "pin"); Spec.Ensures<int>(r => r == -4, "rounds down"); return x >> 1; }
            public static int RightKeepsSign(int x) { Spec.Ensures<int>(r => r <= 0 || x >= 0, "sign"); return x >> 4; }
            public static int CountIsMasked(int n) { Spec.Requires(n == 33, "pin"); Spec.Ensures<int>(r => r == 2, "33 & 31 == 1"); return 1 << n; }
            public static long LongCountIsMasked(int n) { Spec.Requires(n == 65, "pin"); Spec.Ensures<long>(r => r == 2L, "65 & 63 == 1"); return 1L << n; }
            public static int LeftWraps(int x) { Spec.Requires(x == 1, "pin"); Spec.Ensures<int>(r => r == int.MinValue, "1 << 31"); return x << 31; }
            public static int NegativeCount(int n) { Spec.Requires(n == -1, "pin"); Spec.Ensures<int>(r => r == int.MinValue, "-1 & 31 == 31"); return 1 << n; }
            public static uint UnsignedRight(uint x) { Spec.Requires(x == 0x80000000u, "pin"); Spec.Ensures<uint>(r => r == 1u, "logical"); return x >> 31; }
            public static int MinValueRight(int x) { Spec.Requires(x == int.MinValue, "pin"); Spec.Ensures<int>(r => r == -1, "arithmetic"); return x >> 31; }
            public static ulong UlongLeft(ulong x) { Spec.Requires(x == 0x8000000000000001UL, "pin"); Spec.Ensures<ulong>(r => r == 2UL, "high bit shifted out"); return x << 1; }
            """));
        await AssertRefutedAsync(Wrap(mode, "public static int Twice(int x) { Spec.Requires(x >= 0 && x <= 1000, \"small\"); Spec.Ensures<int>(r => r == x * 3, \"wrong\"); return x << 1; }"));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task VariableShiftCount_IsModelled(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Shift(int x, int n)
            {
                Spec.Requires(n >= 0 && n < 4 && x >= 0 && x < 1000, "small");
                Spec.Ensures<int>(r => r >= 0 && r < 16000, "bounded");
                return x << n;
            }
            public static int Count(int x, int n) { Spec.Requires(n >= 0 && n < 4 && x >= 0 && x < 1000, "small"); Spec.Ensures<int>(r => r >= 0, "not negative"); return x << n; }
            """));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task BoolOperators_AndOrXor(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static bool Xor(bool a, bool b) { Spec.Ensures<bool>(r => r == (a != b), "xor"); return a ^ b; }
            public static bool And(bool a, bool b) { Spec.Ensures<bool>(r => r == (a && b), "and"); return a & b; }
            public static bool Or(bool a, bool b) { Spec.Ensures<bool>(r => r == (a || b), "or"); return a | b; }
            """));
    }

    // ---- flags enums ----------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(AllModes))]
    public async Task FlagsEnum_Operators(string mode)
    {
        await AssertProvedAsync($$"""
            [Flags] public enum Access { None = 0, Read = 1, Write = 2, Both = 3 }
            [Verifiable] {{mode}}
            public static class A
            {
                public static Access Combine() { Spec.Ensures<Access>(r => r == (Access)3, "both"); return Access.Read | Access.Write; }
                public static Access Add(Access a) { Spec.Requires(a == Access.Read, "pin"); Spec.Ensures<Access>(r => r == Access.Both, "both"); return a | Access.Write; }
                public static Access Remove(Access a) { Spec.Requires(a == Access.Both, "pin"); Spec.Ensures<Access>(r => r == Access.Read, "read"); return a & ~Access.Write; }
                public static bool Has(Access a) { Spec.Requires(a == Access.Both, "pin"); Spec.Ensures<bool>(r => r, "has write"); return (a & Access.Write) != 0; }
            }
            """);
    }

    // ---- casts ----------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(AllModes))]
    public async Task UncheckedNarrowingCasts_Wrap(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Byte(int x) { Spec.Requires(x == 300, "pin"); Spec.Ensures<int>(r => r == 44, "300 mod 256"); return (byte)x; }
            public static int ByteNegative(int x) { Spec.Requires(x == -1, "pin"); Spec.Ensures<int>(r => r == 255, "-1 mod 256"); return (byte)x; }
            public static int Sbyte(int x) { Spec.Requires(x == 200, "pin"); Spec.Ensures<int>(r => r == -56, "200 - 256"); return (sbyte)x; }
            public static int Short(int x) { Spec.Requires(x == 40000, "pin"); Spec.Ensures<int>(r => r == -25536, "40000 - 65536"); return (short)x; }
            public static int Ushort(int x) { Spec.Requires(x == -1, "pin"); Spec.Ensures<int>(r => r == 65535, "-1 mod 65536"); return (ushort)x; }
            public static int Char(int x) { Spec.Requires(x == 65601, "pin"); Spec.Ensures<int>(r => r == 65, "'A'"); return (char)x; }
            public static int IntOfLong(long x) { Spec.Requires(x == 4294967301L, "pin"); Spec.Ensures<int>(r => r == 5, "2^32 + 5"); return (int)x; }
            public static int IntOfLongMin(long x) { Spec.Requires(x == 2147483648L, "pin"); Spec.Ensures<int>(r => r == int.MinValue, "2^31"); return (int)x; }
            public static uint UintOfInt(int x) { Spec.Requires(x == -1, "pin"); Spec.Ensures<uint>(r => r == uint.MaxValue, "all ones"); return (uint)x; }
            public static int IntOfUint(uint x) { Spec.Requires(x == uint.MaxValue, "pin"); Spec.Ensures<int>(r => r == -1, "all ones"); return (int)x; }
            public static long LongOfUlong(ulong x) { Spec.Requires(x == ulong.MaxValue, "pin"); Spec.Ensures<long>(r => r == -1L, "all ones"); return (long)x; }
            public static ulong UlongOfLong(long x) { Spec.Requires(x == -1L, "pin"); Spec.Ensures<ulong>(r => r == ulong.MaxValue, "all ones"); return (ulong)x; }
            """));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task NarrowingCast_RoundTripClaimIsRefuted(string mode)
    {
        await AssertRefutedAsync(Wrap(mode, "public static int F(int x) { Spec.Requires(x >= 0, \"non-negative\"); Spec.Ensures<int>(r => r == x, \"round trip\"); return (byte)x; }"));
        await AssertRefutedAsync(Wrap(mode, "public static int F(long x) { Spec.Requires(x >= 0, \"non-negative\"); Spec.Ensures<int>(r => r >= 0, \"not negative\"); return (int)x; }"));
        await AssertRefutedAsync(Wrap(mode, "public static int F(int x) { Spec.Requires(x >= 0 && x < 256, \"a byte\"); Spec.Ensures<int>(r => r >= 0, \"not negative\"); return (sbyte)x; }"));
        await AssertRefutedAsync(Wrap(mode, "public static uint F(int x) { Spec.Requires(x >= -5 && x < 10, \"small\"); Spec.Ensures<uint>(r => r < 10u, \"small\"); return (uint)x; }"));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task NarrowingCast_InRange_IsExact(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int F(int x) { Spec.Requires(x >= 0 && x < 256, "a byte"); Spec.Ensures<int>(r => r == x, "round trip"); return (byte)x; }
            public static int G(int x) { Spec.Requires(x >= -128 && x < 128, "an sbyte"); Spec.Ensures<int>(r => r == x, "round trip"); return (sbyte)x; }
            public static int Mask(int x) { Spec.Ensures<int>(r => r >= 0 && r <= 255, "a byte"); return (byte)(x & 0xFF); }
            public static int H(long x) { Spec.Requires(x >= 0 && x < 1000, "small"); Spec.Ensures<int>(r => r == x, "round trip"); return (int)x; }
            """));
    }

    [Fact]
    public async Task CheckedNarrowingCast_MustFit()
    {
        await AssertRefutedAsync(Wrap("", "public static int F(int x) { Spec.Requires(x == 300, \"pin\"); return checked((byte)x); }"), "Overflow");
        await AssertProvedAsync(Wrap("", "public static int F(int x) { Spec.Requires(x >= 0 && x < 256, \"a byte\"); Spec.Ensures<int>(r => r == x, \"exact\"); return checked((byte)x); }"));
    }

    [Fact]
    public async Task UncheckedBlock_WrapsInTheDefaultMode()
    {
        await AssertProvedAsync(Wrap("", "public static int F(int x) { Spec.Requires(x == 300, \"pin\"); Spec.Ensures<int>(r => r == 44, \"wraps\"); return unchecked((byte)x); }"));
    }

    // ---- narrow types ---------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(AllModes))]
    public async Task CompoundAssignmentOnByte_WrapsBack(string mode)
    {
        // [UncheckedArithmetic] lets the byte wrap; elsewhere the wrap is an Overflow the proof must refute.
        var wraps = mode == "[UncheckedArithmetic]";
        var source = Wrap(mode, """
            public static int Bump(byte x) { Spec.Requires(x == 255, "pin"); Spec.Ensures<int>(r => r == 0, "wraps to zero"); byte b = x; b += 1; return b; }
            """);
        if (wraps) await AssertProvedAsync(source); else await AssertRefutedAsync(source, "Overflow");
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task CompoundAssignmentOnByte_NotWrappingClaimIsRefuted(string mode)
    {
        await AssertRefutedAsync(Wrap(mode, "public static int Bump(byte x) { Spec.Ensures<int>(r => r > x, \"bigger\"); byte b = x; b += 1; return b; }"));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task IncrementOnNarrowTypes(string mode)
    {
        var wraps = mode == "[UncheckedArithmetic]";
        // (type, edge value, value after the step, the step). Where the step must not wrap, the program is refuted
        // for the overflow alone, so it makes no claim about the wrapped value.
        var cases = new[]
        {
            ("byte", "255", "0", "b++"),
            ("sbyte", "-128", "127", "b--"),
            ("char", "65535", "0", "b++"),
            ("short", "32767", "-32768", "b++"),
            ("ushort", "0", "65535", "b--"),
        };
        foreach (var (type, edge, wrapped, step) in cases)
        {
            var claim = wraps ? $"Spec.Ensures<int>(r => r == {wrapped}, \"wraps\");" : "";
            var method = $"public static int M({type} x) {{ Spec.Requires(x == {edge}, \"pin\"); {claim} {type} b = x; {step}; return b; }}";
            if (wraps) await AssertProvedAsync(Wrap(mode, method)); else await AssertRefutedAsync(Wrap(mode, method), "Overflow");
        }
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task NarrowArithmetic_InRange_IsProved(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Add(byte x) { Spec.Requires(x < 255, "room"); Spec.Ensures<int>(r => r == x + 1, "adds one"); byte b = x; b += 1; return b; }
            public static int Inc(byte x) { Spec.Requires(x < 255, "room"); Spec.Ensures<int>(r => r == x + 1, "adds one"); byte b = x; b++; return b; }
            public static int Dec(short x) { Spec.Requires(x > short.MinValue, "room"); Spec.Ensures<int>(r => r >= short.MinValue && r < 32767, "in range"); short s = x; s--; return s; }
            public static int Sum(byte a, byte b) { Spec.Ensures<int>(r => r >= 0 && r <= 510, "int arithmetic"); return a + b; }
            public static int Product(ushort a, ushort b) { Spec.Requires(a < 100 && b < 100, "small"); Spec.Ensures<int>(r => r == a * b, "exact"); return a * b; }
            public static int Xor(byte a, byte b) { Spec.Ensures<int>(r => r >= 0 && r <= 255, "a byte"); byte c = a; c ^= b; return c; }
            public static int Shift(byte a) { Spec.Requires(a < 16, "small"); Spec.Ensures<int>(r => r == a * 2, "doubles"); byte c = a; c <<= 1; return c; }
            """));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task UnsignedCompoundAndIncrement(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static uint Bump(uint x) { Spec.Requires(x < 100u, "small"); Spec.Ensures<uint>(r => r == x + 1u, "adds one"); uint u = x; u++; return u; }
            public static uint Add(uint x) { Spec.Requires(x < 100u, "small"); Spec.Ensures<uint>(r => r == x + 5u, "adds five"); uint u = x; u += 5; return u; }
            public static ulong BumpUlong(ulong x) { Spec.Requires(x < 100UL, "small"); Spec.Ensures<ulong>(r => r == x + 1UL, "adds one"); ulong u = x; u++; return u; }
            """));
    }

    [Fact]
    public async Task UnsignedDecrementBelowZero_IsOverflowByDefault()
    {
        await AssertRefutedAsync(Wrap("", "public static uint F(uint x) { uint u = x; u--; return u; }"), "Overflow");
        await AssertRefutedAsync(Wrap("[CheckedArithmetic]", "public static uint F(uint x) { uint u = x; u--; return u; }"), "Overflow");
        await AssertProvedAsync(Wrap("[UncheckedArithmetic]", "public static uint F(uint x) { Spec.Requires(x == 0u, \"pin\"); Spec.Ensures<uint>(r => r == uint.MaxValue, \"wraps\"); uint u = x; u--; return u; }"));
    }

    // ---- bit-vector mode only -------------------------------------------------------------------------------

    [Fact]
    public async Task CheckedArithmetic_ShortAbs_AndStringLength()
    {
        await AssertProvedAsync(Wrap("[CheckedArithmetic]", """
            public static short Magnitude(short x) { Spec.Requires(x > short.MinValue, "not the minimum"); Spec.Ensures<short>(r => r >= 0, "not negative"); return Math.Abs(x); }
            public static int Length(string s) { Spec.Requires(s != null, "given"); Spec.Ensures<int>(r => r == s.Length + 1 && r > 0, "one more"); return s.Length + 1; }
            """));
        await AssertRefutedAsync(Wrap("[CheckedArithmetic]", "public static short Magnitude(short x) { return Math.Abs(x); }"), "Overflow");
        await AssertRefutedAsync(Wrap("", "public static short Magnitude(short x) { return Math.Abs(x); }"), "Overflow");
    }

    [Fact]
    public async Task CheckedArithmetic_UnsignedIncrement_IsNotASortError()
    {
        await AssertProvedAsync(Wrap("[CheckedArithmetic]", "public static uint Bump(uint x) { Spec.Requires(x < 100u, \"small\"); Spec.Ensures<uint>(r => r == x + 1u, \"adds one\"); uint u = x; u++; return u; }"));
    }

    // ---- decimal to int -------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(AllModes))]
    public async Task DecimalToInt_TruncatesTowardZero(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Neg(decimal d) { Spec.Requires(d > -1m && d < 0m, "between"); Spec.Ensures<int>(r => r == 0, "toward zero"); return (int)d; }
            public static int Pos(decimal d) { Spec.Requires(d == 2.9m, "pin"); Spec.Ensures<int>(r => r == 2, "toward zero"); return (int)d; }
            public static int NegPinned(decimal d) { Spec.Requires(d == -2.9m, "pin"); Spec.Ensures<int>(r => r == -2, "toward zero"); return (int)d; }
            public static long Long(decimal d) { Spec.Requires(d == -7.5m, "pin"); Spec.Ensures<long>(r => r == -7L, "toward zero"); return (long)d; }
            """));
        await AssertRefutedAsync(Wrap(mode, "public static int Whole(decimal d) { Spec.Requires(d > -1m && d < 0m, \"between\"); Spec.Ensures<int>(r => r == -1, \"floor\"); return (int)d; }"));
    }

    [Fact]
    public async Task DecimalToInt_OutOfRangeThrows()
    {
        await AssertRefutedAsync(Wrap("", "public static int F(decimal d) { return (int)d; }"), "Overflow");
        await AssertProvedAsync(Wrap("", "public static int F(decimal d) { Spec.Requires(d > -100m && d < 100m, \"small\"); return (int)d; }"));
    }

    // ---- division in a contract -----------------------------------------------------------------------------

    [Theory, MemberData(nameof(AllModes))]
    public async Task DivisionInAPostcondition_NeedsANonZeroDivisor(string mode)
    {
        await AssertRefutedAsync(Wrap(mode, """
            public static int Quotient(int x, int y)
            {
                Spec.Requires(x >= 0, "non-negative");
                Spec.Ensures<int>(r => r == x / y, "quotient");
                return y == 0 ? -1 : x / y;
            }
            """), "ivisor");
        await AssertProvedAsync(Wrap(mode, """
            public static int Quotient(int x, int y)
            {
                Spec.Requires(x >= 0 && y > 0, "positive divisor");
                Spec.Ensures<int>(r => r == x / y, "quotient");
                return x / y;
            }
            public static int Guarded(int x, int y)
            {
                Spec.Requires(x >= 0, "non-negative");
                Spec.Ensures<int>(r => y != 0 && r == x / y || y == 0 && r == -1, "guarded");
                return y == 0 ? -1 : x / y;
            }
            public static int ByLiteral(int x) { Spec.Requires(x >= 0, "non-negative"); Spec.Ensures<int>(r => r == x / 10, "tenth"); return x / 10; }
            """));
    }

    [Fact]
    public async Task DivisionInAPrecondition_IsCheckedAtTheCall()
    {
        await AssertRefutedAsync(Wrap("", """
            public static int Callee(int a, int b) { Spec.Requires(a / b > 0, "positive quotient"); return a; }
            public static int Caller(int a, int b) { Spec.Requires(a > 0 && b >= 0 && a < 1000 && b < 1000, "small"); return Callee(a, b); }
            """), "ivisor");
        await AssertProvedAsync(Wrap("", """
            public static int Callee(int a, int b) { Spec.Requires(a >= 0 && b != 0 && a / b > 0, "positive quotient"); return a / b; }
            public static int Caller(int a, int b) { Spec.Requires(a > 0 && b > 0 && a >= b && a < 1000, "small"); return Callee(a, b); }
            """));
    }

    [Fact]
    public async Task DivisionInALoopInvariant_IsChecked()
    {
        await AssertRefutedAsync(Wrap("", """
            public static int F(int n, int d)
            {
                Spec.Requires(n >= 0 && n < 100, "small");
                int i = 0;
                while (i < n)
                {
                    Spec.LoopInvariant(i >= 0 && i <= n && i / d >= 0, "bounded");
                    Spec.Decreases(n - i);
                    i++;
                }
                return i;
            }
            """));
    }

    // ---- Math -----------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(AllModes))]
    public async Task MathClamp_KeepsTheValueInRange(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Limit(int x) { Spec.Ensures<int>(r => r >= 0 && r <= 10, "between"); return Math.Clamp(x, 0, 10); }
            public static int Inside(int x) { Spec.Requires(x >= 3 && x <= 7, "inside"); Spec.Ensures<int>(r => r == x, "unchanged"); return Math.Clamp(x, 0, 10); }
            public static int Low(int x) { Spec.Requires(x < 0, "below"); Spec.Ensures<int>(r => r == 0, "min"); return Math.Clamp(x, 0, 10); }
            public static long LongClamp(long x) { Spec.Requires(x > 100L, "above"); Spec.Ensures<long>(r => r == 10L, "max"); return Math.Clamp(x, 0L, 10L); }
            public static int Variable(int x, int lo, int hi) { Spec.Requires(lo <= hi, "ordered"); Spec.Ensures<int>(r => r >= lo && r <= hi, "between"); return Math.Clamp(x, lo, hi); }
            """));
        await AssertRefutedAsync(Wrap(mode, "public static int Limit(int x) { Spec.Ensures<int>(r => r >= 1 && r <= 10, \"wrong\"); return Math.Clamp(x, 0, 10); }"));
    }

    [Fact]
    public async Task MathClamp_ThrowsWhenMinExceedsMax()
    {
        await AssertRefutedAsync(Wrap("", "public static int Limit(int x, int lo, int hi) { return Math.Clamp(x, lo, hi); }"));
    }

    [Theory, MemberData(nameof(AllModes))]
    public async Task MathSign(string mode)
    {
        await AssertProvedAsync(Wrap(mode, """
            public static int Sign(int x) { Spec.Ensures<int>(r => r >= -1 && r <= 1 && (r == 0) == (x == 0) && (r > 0) == (x > 0), "sign"); return Math.Sign(x); }
            public static int Neg(long x) { Spec.Requires(x == -50L, "pin"); Spec.Ensures<int>(r => r == -1, "negative"); return Math.Sign(x); }
            """));
    }
}
