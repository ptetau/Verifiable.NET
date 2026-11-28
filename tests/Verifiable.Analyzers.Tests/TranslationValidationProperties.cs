using System;
using System.Collections.Immutable;
using System.Linq;
using GalaxyCheck;
using Verifiable.Analyzers.Tests.TranslationValidation;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Differential tests of the C# to SMT translation. Each test case is a small random C# method. The CLR runs it, and
/// the analyzer must then prove the result the CLR got, refute any other result, and refute the division-by-zero or
/// overflow obligation exactly when the CLR threw. This is checked in all three arithmetic modes: the default (overflow
/// must not happen), <c>[CheckedArithmetic]</c> (bit-vectors) and <c>[UncheckedArithmetic]</c> (wrap-around). A disagreement means the translation gives some construct a
/// meaning that C# does not. See <see cref="TvOracle.Check"/> for the exact rules, and docs/trusted-core.md for what
/// is and is not covered.
/// </summary>
public class TranslationValidationProperties
{
    private const int Iterations = 40;
    private const int BitIterations = 30;
    private const int InputsPerProgram = 3;

    // No attribute: the default. Overflow is refuted exactly where the CLR's checked run throws.
    [Property(Iterations = Iterations, Seed = 7001, ShrinkLimit = 60)]
    public Property Int_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Default);

    [Property(Iterations = Iterations, Seed = 7002, ShrinkLimit = 60)]
    public Property Long_AnalyzerAgreesWithClr() => Agrees(TvType.Long, TvArith.Default);

    [Property(Iterations = Iterations, Seed = 7003, ShrinkLimit = 60)]
    public Property CheckedArithmeticInt_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Checked);

    [Property(Iterations = Iterations, Seed = 7004, ShrinkLimit = 60)]
    public Property CheckedArithmeticLong_AnalyzerAgreesWithClr() => Agrees(TvType.Long, TvArith.Checked);

    [Property(Iterations = Iterations, Seed = 7005, ShrinkLimit = 60)]
    public Property Strings_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Default, strings: true);

    // [UncheckedArithmetic]: integers wrap as in a default C# build, so values agree even where the arithmetic overflows.
    [Property(Iterations = Iterations, Seed = 7006, ShrinkLimit = 60)]
    public Property UncheckedArithmeticInt_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Unchecked);

    [Property(Iterations = Iterations, Seed = 7007, ShrinkLimit = 60)]
    public Property UncheckedArithmeticLong_AnalyzerAgreesWithClr() => Agrees(TvType.Long, TvArith.Unchecked);

    [Property(Iterations = Iterations, Seed = 7008, ShrinkLimit = 60)]
    public Property UncheckedArithmeticStrings_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Unchecked, strings: true);

    // Bit programs: ~ & | ^ << >>, casts that wrap through a narrower type, and compound assignments on narrow and
    // unsigned locals. The pinned inputs are small and edge values, so a wrong width, sign or wrap shows up as a wrong value.
    [Property(Iterations = BitIterations, Seed = 7009, ShrinkLimit = 60)]
    public Property BitsInt_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Default, bits: true);

    [Property(Iterations = BitIterations, Seed = 7010, ShrinkLimit = 60)]
    public Property BitsLong_AnalyzerAgreesWithClr() => Agrees(TvType.Long, TvArith.Default, bits: true);

    [Property(Iterations = BitIterations, Seed = 7011, ShrinkLimit = 60)]
    public Property CheckedArithmeticBitsInt_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Checked, bits: true);

    [Property(Iterations = BitIterations, Seed = 7012, ShrinkLimit = 60)]
    public Property CheckedArithmeticBitsLong_AnalyzerAgreesWithClr() => Agrees(TvType.Long, TvArith.Checked, bits: true);

    [Property(Iterations = BitIterations, Seed = 7013, ShrinkLimit = 60)]
    public Property UncheckedArithmeticBitsInt_AnalyzerAgreesWithClr() => Agrees(TvType.Int, TvArith.Unchecked, bits: true);

    [Property(Iterations = BitIterations, Seed = 7014, ShrinkLimit = 60)]
    public Property UncheckedArithmeticBitsLong_AnalyzerAgreesWithClr() => Agrees(TvType.Long, TvArith.Unchecked, bits: true);

    private static Property Agrees(TvType t, TvArith arithmetic, bool strings = false, bool bits = false) =>
        (from program in TvGenerators.Program(t, arithmetic, strings, bits)
         from inputs in TvGenerators.Inputs(t, InputsPerProgram, strings)
         select (program, inputs)).ForAll(c =>
        {
            var disagreement = TvOracle.Check(c.program, c.inputs);
            if (disagreement is not null)
            {
                throw new TranslationDisagreement(disagreement);
            }
        });

    private sealed class TranslationDisagreement(string message) : Exception(message);
}

/// <summary>
/// Regression tests for the translation bugs the differential suite found, and tests that pin down the known gaps
/// it skips on purpose.
/// </summary>
public class TranslationValidationRegressions
{
    private static void AssertRefutes(string source, params (string Method, string Kind)[] expected)
    {
        var (refutations, problems) = TvOracle.Analyze(source);
        Assert.Empty(problems);
        var actual = refutations.Select(r => (r.Method, r.Kind)).Distinct().OrderBy(r => r.Method).ThenBy(r => r.Kind);
        Assert.Equal(expected.OrderBy(r => r.Method).ThenBy(r => r.Kind), actual);
    }

    // Found by Int_AnalyzerAgreesWithClr: `/` and `%` were SMT-LIB div/mod, which are Euclidean.
    // The old translation proved -7 / 2 == -4 and -7 % 2 == 1.
    [Fact]
    public void IntegerDivisionAndRemainder_TruncateTowardZero()
    {
        const string source = """
            using Verifiable.Contracts;
            namespace Tv;
            [Verifiable]
            public static class D
            {
                public static int DivPinned(int x)
                {
                    Spec.Requires(x == -7);
                    Spec.Ensures<int>(r => r == -3, "C# truncates toward zero");
                    return x / 2;
                }

                public static long RemPinned(long x, long y)
                {
                    Spec.Requires(x == -7L && y == 2L);
                    Spec.Ensures<long>(r => r == -1L, "the remainder takes the sign of the dividend");
                    return x % y;
                }

                public static int DivNegative(int x)
                {
                    Spec.Requires(x < 0);
                    Spec.Ensures<int>(r => r * 2 >= x && r <= 0, "truncation moves a negative quotient up");
                    return x / 2;
                }

                public static int RemNegative(int x, int y)
                {
                    Spec.Requires(x < 0 && x > int.MinValue && y != 0);
                    Spec.Ensures<int>(r => r <= 0, "the remainder of a negative dividend is never positive");
                    return x % y;
                }

                public static int FloorIsWrong(int x)
                {
                    Spec.Requires(x == -7);
                    Spec.Ensures<int>(r => r == -4, "floor division is not what C# does");
                    return x / 2;
                }

                public static int ContractUsesCSharpDivisionToo(int x)
                {
                    Spec.Requires(x == -7);
                    Spec.Ensures<int>(r => r == x / 2 + x % 2, "contracts read / and % the C# way");
                    return -4;
                }
            }
            """;

        AssertRefutes(source, ("FloorIsWrong", "Postcondition"));
    }

    // Found by CheckedArithmeticInt_AnalyzerAgreesWithClr: under [CheckedArithmetic] (bit-vectors) a zero divisor was
    // not checked at all, and neither was MinValue / -1, which throws OverflowException even in unchecked code.
    [Fact]
    public void CheckedArithmetic_DivisionChecksZeroDivisorAndMinValueOverMinusOne()
    {
        const string source = """
            using Verifiable.Contracts;
            namespace Tv;
            [Verifiable]
            [CheckedArithmetic]
            public static class D
            {
                public static int Unguarded(int x, int y) => x / y;

                public static int NonZero(int x, int y)
                {
                    Spec.Requires(y != 0);
                    return x / y;
                }

                public static long RemNonZero(long x, long y)
                {
                    Spec.Requires(y != 0L);
                    return x % y;
                }

                public static int Positive(int x, int y)
                {
                    Spec.Requires(y > 0);
                    Spec.Ensures<int>(r => x >= 0 || r <= 0, "a negative dividend gives a non-positive quotient");
                    return x / y;
                }

                public static long LongNotMinusOne(long x, long y)
                {
                    Spec.Requires(y != 0L && y != -1L);
                    return x % y;
                }
            }
            """;

        AssertRefutes(source,
            ("Unguarded", "DivisionByZero"),
            ("Unguarded", "Overflow"),
            ("NonZero", "Overflow"),
            ("RemNonZero", "Overflow"));
    }

    // Found by CheckedArithmeticLong_AnalyzerAgreesWithClr: Math.Abs on a long compared it with a 32-bit zero, a Z3
    // sort error (VER103). Math.Abs(MinValue) also throws OverflowException, which was not checked.
    [Fact]
    public void CheckedArithmetic_MathAbs_WorksOnLongAndChecksMinValue()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;
            namespace Tv;
            [Verifiable]
            [CheckedArithmetic]
            public static class A
            {
                public static long AbsLong(long x)
                {
                    Spec.Requires(x > long.MinValue);
                    Spec.Ensures<long>(r => r >= 0L, "an absolute value is never negative");
                    return Math.Abs(x);
                }

                public static int AbsInt(int x) => Math.Abs(x);
            }
            """;

        AssertRefutes(source, ("AbsInt", "Overflow"));
    }

    // `%` on decimal was SMT-LIB mod applied to reals, and a zero divisor was not checked.
    [Fact]
    public void DecimalRemainder_TakesSignOfDividendAndChecksZeroDivisor()
    {
        const string source = """
            using Verifiable.Contracts;
            namespace Tv;
            [Verifiable]
            public static class R
            {
                public static decimal Negative(decimal x)
                {
                    Spec.Requires(x == -7m);
                    Spec.Ensures<decimal>(r => r == -1m, "the remainder takes the sign of the dividend");
                    return x % 2m;
                }

                public static decimal Fraction(decimal x, decimal y)
                {
                    Spec.Requires(x == 7.5m && y == -2m);
                    Spec.Ensures<decimal>(r => r == 1.5m, "the remainder is exact");
                    return x % y;
                }

                public static decimal Unguarded(decimal x, decimal y)
                {
                    Spec.Requires(x >= 0m, "any contract makes the analyzer check the method");
                    return x % y;
                }
            }
            """;

        AssertRefutes(source, ("Unguarded", "DivisionByZero"));
        Assert.Equal(-1m, -7m % 2m);
        Assert.Equal(1.5m, 7.5m % -2m);
    }

    // This used to be a known gap: without [CheckedArithmetic] int and long were unbounded integers, so the analyzer
    // proved contracts that wrap-around or the OverflowException of MinValue / -1 make false in C#. Now overflow
    // is checked by default, and these programs are refuted.
    [Fact]
    public void Default_IntegersAreCheckedForOverflow()
    {
        var addOne = new TvProgram(TvType.Int, TvArith.Default, ImmutableArray<TvStmt>.Empty,
            new TvBin("+", new TvVar("x"), new TvLit(1)));
        var run = TvOracle.Execute(addOne, [new TvInput(int.MaxValue, 0, false)])!;
        Assert.Equal(int.MinValue, run[0].Unchecked.Value);
        Assert.Equal("OverflowException", run[0].Checked.Exception);

        const string source = """
            using Verifiable.Contracts;
            namespace Tv;
            [Verifiable]
            public static class G
            {
                public static int AddOne(int x)
                {
                    Spec.Requires(x == int.MaxValue);
                    Spec.Ensures<int>(r => r > x, "false in C#: int.MaxValue + 1 wraps to int.MinValue");
                    return x + 1;
                }

                public static int MinOverMinusOne(int x, int y)
                {
                    Spec.Requires(x == int.MinValue && y == -1);
                    Spec.Ensures<int>(r => r > 0, "false in C#: this division throws OverflowException");
                    return x / y;
                }

                public static int Negate(int x)
                {
                    Spec.Requires(x == int.MinValue);
                    Spec.Ensures<int>(r => r > 0, "false in C#: -int.MinValue wraps to int.MinValue");
                    return -x;
                }

                public static long LongMultiply(long x)
                {
                    Spec.Requires(x == long.MaxValue);
                    return x * 2L;
                }
            }
            """;

        // Both contracts are false in C#, and the arithmetic that makes them false is refuted.
        AssertRefutes(source,
            ("AddOne", "Overflow"),
            ("MinOverMinusOne", "Overflow"),
            ("Negate", "Overflow"),
            ("LongMultiply", "Overflow"));
    }

    // With [UncheckedArithmetic] the analyzer models wrap-around, so what is true in C# is proved and what is false
    // is refuted. Only the throw of MinValue / -1 remains an Overflow obligation, because C# throws even unchecked.
    [Fact]
    public void UncheckedArithmetic_IntegersWrapLikeCSharp()
    {
        const string source = """
            using Verifiable.Contracts;
            namespace Tv;
            [Verifiable]
            [UncheckedArithmetic]
            public static class G
            {
                public static int AddOne(int x)
                {
                    Spec.Requires(x == int.MaxValue);
                    Spec.Ensures<int>(r => r == int.MinValue, "true in C#: int.MaxValue + 1 wraps");
                    Spec.Ensures<int>(r => r > x, "false in C#");
                    return x + 1;
                }

                public static int MinOverMinusOne(int x, int y)
                {
                    Spec.Requires(x == int.MinValue && y == -1);
                    return x / y;
                }

                public static long LongMultiply(long x)
                {
                    Spec.Requires(x == long.MaxValue);
                    Spec.Ensures<long>(r => r == -2L, "true in C#: the product wraps");
                    return x * 2L;
                }
            }
            """;

        AssertRefutes(source, ("AddOne", "Postcondition"), ("MinOverMinusOne", "Overflow"));
    }

    // KNOWN GAP, not fixed. decimal is a real number in SMT, so division is exact, while C# rounds 1m / 3m to 28
    // digits. The analyzer therefore refutes the true result and proves a false one. For this reason the
    // differential suite does not generate decimal programs.
    [Fact]
    public void KnownGap_DecimalDivisionDoesNotRound()
    {
        Assert.Equal(0.3333333333333333333333333333m, 1m / 3m);
        Assert.NotEqual(1m, 1m / 3m * 3m);

        const string source = """
            using Verifiable.Contracts;
            namespace Tv;
            [Verifiable]
            public static class G
            {
                public static decimal Third(decimal x)
                {
                    Spec.Requires(x == 1m);
                    Spec.Ensures<decimal>(r => r == 0.3333333333333333333333333333m, "true in C#");
                    Spec.Ensures<decimal>(r => r * 3m == 1m, "false in C#: the quotient is rounded");
                    return x / 3m;
                }
            }
            """;

        var (refutations, problems) = TvOracle.Analyze(source);
        Assert.Empty(problems);
        var refuted = Assert.Single(refutations);
        Assert.Equal("true in C#", refuted.Reason);
    }
}
