using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sim.Core.Demand;
using Sim.Core.Tests.Gen;
using Sim.Core.Util;

namespace Sim.Core.Tests.Demand;

/// <summary>Целочисленный логит (zoning-demand.md §6.2, ADR-0021): таблица, точность веса, монотонность, выбор, logsum и вектор <c>tests/golden/rust-pilot/detlogit.json</c> для Rust.</summary>
public sealed class DetLogitTests
{
    private const string RegenEnv = "TC_REGEN_GOLDEN";
    private const double Theta = 0.001;                       // 1/дс
    private static readonly int _theta64 = (int)Math.Round(Theta * 64.0 * 65536.0);

    [Fact]
    public void ExpNeg_MatchesMathExp_AndIsStrictlyDecreasing()
    {
        Assert.Equal(1 << 30, DetLogit.ExpNeg[0]);
        double worst = 0;
        for (int k = 0; k < DetLogit.TableSize; k++)
        {
            double err = Math.Abs((DetLogit.ExpNeg[k] / (double)(1 << 30)) - Math.Exp(-k / 64.0));
            worst = Math.Max(worst, err);
            if (k > 0)
            {
                Assert.True(DetLogit.ExpNeg[k] < DetLogit.ExpNeg[k - 1], $"таблица не убывает на k={k}");
            }
        }

        Assert.True(worst <= 1e-6, $"макс. ошибка таблицы {worst}");
        Assert.Equal(DetLogit.E1, (long)Math.Round(Math.Pow(2, 30) * Math.Exp(-1.0 / 64.0)));
    }

    [Fact]
    public void Weight_ErrorIsWithinOnePointSixPercent()
    {
        double worst = 0;
        foreach (int th in new[] { _theta64, (int)Math.Round(0.0005 * 64 * 65536), (int)Math.Round(0.004 * 64 * 65536) })
        {
            double theta = th / (64.0 * 65536.0);
            for (int dc = 0; dc <= 6000; dc += 3)
            {
                double x = dc * theta;
                if (x > 8.0)
                {
                    break;
                }

                double got = DetLogit.Weight(dc, th) / (double)(1 << 30);
                double want = Math.Exp(-x);
                worst = Math.Max(worst, Math.Abs(got - want) / want);
            }
        }

        Assert.True(worst <= 0.016, $"макс. относительная ошибка веса {worst:P3}");
    }

    [Fact]
    public void Weight_IsMonotone_AndSaturatesAtTableEnd()
    {
        int prev = int.MaxValue;
        for (long dc = 0; dc < 200_000; dc += 7)
        {
            int w = DetLogit.Weight(dc, _theta64);
            Assert.True(w <= prev, $"вес вырос при ΔC={dc}");
            prev = w;
        }

        Assert.Equal(DetLogit.ExpNeg[DetLogit.TableSize - 1], DetLogit.Weight(long.MaxValue / 1_000_000, _theta64));
        Assert.Equal(1 << 30, DetLogit.Weight(0, _theta64));
        Assert.Equal(1 << 30, DetLogit.Weight(5000, 0));
    }

    [Fact]
    public void Choose_FollowsWeights_AndMoreExpensiveIsChosenLessOften()
    {
        int[] costs = { 0, 300, 900, DetLogit.NoCost, 300 };
        var rng = Pcg32.Create(7, 3);
        var hits = new int[costs.Length];
        const int n = 400_000;
        for (int i = 0; i < n; i++)
        {
            hits[DetLogit.Choose(ref rng, costs, _theta64)]++;
        }

        Assert.Equal(0, hits[3]);
        double sum = 0;
        var p = new double[costs.Length];
        for (int i = 0; i < costs.Length; i++)
        {
            p[i] = costs[i] == DetLogit.NoCost ? 0 : Math.Exp(-costs[i] * Theta);
            sum += p[i];
        }

        for (int i = 0; i < costs.Length; i++)
        {
            Assert.InRange(hits[i] / (double)n, (p[i] / sum) - 0.012, (p[i] / sum) + 0.012);
        }

        Assert.True(hits[0] > hits[1] && hits[1] > hits[2], "дороже должно выбираться реже");
    }

    [Fact]
    public void Choose_EqualCosts_AreResolvedByIndexOrderOfTheDraw()
    {
        // Одинаковые стоимости: выбор зависит только от r = слово % Σw и обхода по возрастанию индекса, поэтому два прогона с одним PRNG совпадают, а при r < w первый индекс всегда раньше.
        int[] costs = { 500, 500, 500 };
        var a = Pcg32.Create(1, 1);
        var b = Pcg32.Create(1, 1);
        for (int i = 0; i < 1000; i++)
        {
            int ia = DetLogit.Choose(ref a, costs, _theta64);
            ulong r = DetLogit.NextUInt64(ref b) % (3UL << 30);
            Assert.Equal((int)(r >> 30), ia);
        }
    }

    [Fact]
    public void Choose_SkipsUnavailable_AndDoesNotTouchPrngWhenNothingIsAvailable()
    {
        var rng = Pcg32.Create(5, 5);
        ulong state = rng.State;
        Assert.Equal(-1, DetLogit.Choose(ref rng, new[] { DetLogit.NoCost, DetLogit.NoCost }, _theta64));
        Assert.Equal(-1, DetLogit.Choose(ref rng, ReadOnlySpan<int>.Empty, _theta64));
        Assert.Equal(state, rng.State);
        Assert.Equal(1, DetLogit.Choose(ref rng, new[] { DetLogit.NoCost, 12345 }, _theta64));
        Assert.NotEqual(state, rng.State);
    }

    [Fact]
    public void Choose_DrawsExactlyTwoWords_PerDecision()
    {
        var a = Pcg32.Create(9, 2);
        var b = Pcg32.Create(9, 2);
        DetLogit.Choose(ref a, new[] { 10, 20, 30 }, _theta64);
        b.NextUInt();
        b.NextUInt();
        Assert.Equal(b.State, a.State);
    }

    [Fact]
    public void ChooseWeighted_RespectsZeroWeights()
    {
        var rng = Pcg32.Create(2, 2);
        var w = new long[] { 0, 5, 0, 0, 5, 0 };
        var hits = new int[w.Length];
        for (int i = 0; i < 20_000; i++)
        {
            hits[DetLogit.ChooseWeighted(ref rng, w)]++;
        }

        Assert.True(hits[1] > 9000 && hits[4] > 9000);
        Assert.Equal(0, hits[0] + hits[2] + hits[3] + hits[5]);
        ulong st = rng.State;
        Assert.Equal(-1, DetLogit.ChooseWeighted(ref rng, new long[3]));
        Assert.Equal(st, rng.State);
    }

    [Fact]
    public void Ln64_MatchesNaturalLog()
    {
        Assert.Equal(0, DetLogit.Ln64(1));
        double worst = 0;
        for (long x = 2; x <= 1_000_000; x += (x < 5000 ? 1 : 997))
        {
            worst = Math.Max(worst, Math.Abs(DetLogit.Ln64(x) - (64.0 * Math.Log(x))));
        }

        Assert.True(worst <= 1.0, $"ошибка 64·ln x до {worst}");
    }

    [Fact]
    public void LogSum_MatchesLogSumExp_WithinOneStep()
    {
        var rng = Pcg32.Create(11, 4);
        double step = 1.0 / (64.0 * Theta);            // дс на один шаг таблицы
        for (int iter = 0; iter < 3000; iter++)
        {
            int n = 1 + (int)rng.NextBelow(8);
            var c = new int[n];
            for (int i = 0; i < n; i++)
            {
                c[i] = (int)rng.NextBelow(4000) - 500;
            }

            double s = 0;
            for (int i = 0; i < n; i++)
            {
                s += Math.Exp(-c[i] * Theta);
            }

            double want = -Math.Log(s) / Theta;
            int got = DetLogit.LogSum(c, _theta64);
            Assert.True(Math.Abs(got - want) <= (1.6 * step) + 2, $"iter {iter}: logsum {got}, ожидалось {want:F1} (шаг {step:F1})");
        }

        Assert.Equal(777, DetLogit.LogSum(new[] { 777 }, _theta64));
        Assert.Equal(DetLogit.NoCost, DetLogit.LogSum(new[] { DetLogit.NoCost }, _theta64));
        Assert.Equal(DetLogit.NoCost, DetLogit.LogSum(ReadOnlySpan<int>.Empty, _theta64));
    }

    [Fact]
    public void LogSum_IsMonotoneInEachCost_AndNotAboveTheBest()
    {
        var rng = Pcg32.Create(12, 4);
        int oneStep = (int)Math.Ceiling(1.0 / (64.0 * Theta));
        for (int iter = 0; iter < 2000; iter++)
        {
            int n = 2 + (int)rng.NextBelow(6);
            var c = new int[n];
            for (int i = 0; i < n; i++)
            {
                c[i] = (int)rng.NextBelow(3000);
            }

            int best = int.MaxValue;
            foreach (int v in c)
            {
                best = Math.Min(best, v);
            }

            int before = DetLogit.LogSum(c, _theta64);
            Assert.True(before <= best, "logsum не может быть выше лучшего варианта");
            int j = (int)rng.NextBelow((uint)n);
            c[j] += 1 + (int)rng.NextBelow(500);
            int after = DetLogit.LogSum(c, _theta64);
            // Квантование веса шагом 1/64 относительно лучшего варианта допускает «дребезг» не больше одного шага таблицы (1/(64θ) дс).
            Assert.True(after >= before - oneStep, $"iter {iter}: удорожание варианта снизило logsum {before} -> {after}");
        }
    }

    // ------------------------------------------------------------ вектор для Rust

    [Fact]
    public void RustVector_MatchesFixtureFile()
    {
        string json = BuildVector();
        string path = Path.Combine(GenFixture.RepoRoot(), "tests", "golden", "rust-pilot", "detlogit.json");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RegenEnv)))
        {
            File.WriteAllText(path, json);
        }

        Assert.True(File.Exists(path), $"нет {path}: перегенерируй {RegenEnv}=1 dotnet test tests/Sim.Core.Tests --filter FullyQualifiedName~DetLogitTests");
        Assert.True(File.ReadAllText(path) == json, $"вектор detlogit.json устарел: перегенерируй {RegenEnv}=1 dotnet test tests/Sim.Core.Tests --filter FullyQualifiedName~DetLogitTests");
    }

    private static string BuildVector()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\n  \"golden_version\": 1,\n  \"e1\": ").Append(DetLogit.E1.ToString(inv)).Append(",\n  \"exp_neg\": [");
        for (int k = 0; k < DetLogit.TableSize; k++)
        {
            sb.Append(k == 0 ? "" : ",").Append(k % 16 == 0 ? "\n    " : " ").Append(DetLogit.ExpNeg[k].ToString(inv));
        }

        sb.Append("\n  ],\n  \"weight\": [");
        int[] thetas = { _theta64, 0, 33_554, 4_194_304 };
        long[] deltas = { 0, 1, 7, 100, 999, 5_000, 12_345, 100_000, 2_000_000 };
        bool first = true;
        foreach (int th in thetas)
        {
            foreach (long dc in deltas)
            {
                sb.Append(first ? "\n    " : ",\n    ").Append("{ \"theta64\": ").Append(th.ToString(inv)).Append(", \"delta\": ").Append(dc.ToString(inv)).Append(", \"w\": ")
                    .Append(DetLogit.Weight(dc, th).ToString(inv)).Append(" }");
                first = false;
            }
        }

        sb.Append("\n  ],\n  \"ln64\": [");
        long[] xs = { 1, 2, 3, 7, 10, 64, 100, 255, 256, 257, 1000, 4096, 65_536, 123_456, 1_000_000, 8_000_000, 8_600_000, 9_000_000, 1L << 30, (1L << 30) + 1 };
        for (int i = 0; i < xs.Length; i++)
        {
            sb.Append(i == 0 ? "\n    " : ",\n    ").Append("{ \"x\": ").Append(xs[i].ToString(inv)).Append(", \"k\": ").Append(DetLogit.Ln64(xs[i]).ToString(inv)).Append(" }");
        }

        sb.Append("\n  ],\n  \"logsum\": [");
        var gen = Pcg32.Create(2026, 21);
        for (int i = 0; i < 40; i++)
        {
            int n = 1 + (int)gen.NextBelow(8);
            var c = new int[n];
            for (int j = 0; j < n; j++)
            {
                c[j] = gen.NextBelow(10) == 0 ? DetLogit.NoCost : (int)gen.NextBelow(6000) - 1000;
            }

            int th = i % 3 == 0 ? _theta64 : (i % 3 == 1 ? 33_554 : 4_194_304);
            sb.Append(i == 0 ? "\n    " : ",\n    ").Append("{ \"theta64\": ").Append(th.ToString(inv)).Append(", \"costs\": ").Append(Arr(c)).Append(", \"result\": ")
                .Append(DetLogit.LogSum(c, th).ToString(inv)).Append(" }");
        }

        sb.Append("\n  ],\n  \"choose\": [");
        ulong[] seeds = { 1, 42, 0x9E3779B97F4A7C15UL };
        ulong[] streams = { 0, 8 };
        bool firstC = true;
        foreach (ulong seed in seeds)
        {
            foreach (ulong stream in streams)
            {
                var rng = Pcg32.Create(seed, stream);
                var picks = new List<int>();
                var cgen = Pcg32.Create(seed ^ 0xABCDUL, stream + 3);
                var costsAll = new List<string>();
                for (int i = 0; i < 12; i++)
                {
                    int n = 1 + (int)cgen.NextBelow(8);
                    var c = new int[n];
                    for (int j = 0; j < n; j++)
                    {
                        c[j] = cgen.NextBelow(8) == 0 ? DetLogit.NoCost : (int)cgen.NextBelow(5000);
                    }

                    costsAll.Add(Arr(c));
                    picks.Add(DetLogit.Choose(ref rng, c, _theta64));
                }

                sb.Append(firstC ? "\n    " : ",\n    ").Append("{ \"seed\": \"").Append(seed.ToString(inv)).Append("\", \"stream\": ").Append(stream.ToString(inv)).Append(", \"theta64\": ")
                    .Append(_theta64.ToString(inv)).Append(", \"costs\": [").Append(string.Join(", ", costsAll)).Append("], \"picks\": ").Append(Arr(picks.ToArray())).Append(", \"state\": \"")
                    .Append(rng.State.ToString(inv)).Append("\" }");
                firstC = false;
            }
        }

        sb.Append("\n  ],\n  \"choose_weighted\": [");
        var wr = Pcg32.Create(77, 8);
        var wpicks = new List<int>();
        long[] weights = { 0, 3, 0, 1_000_000_007, 5, 0, 1L << 40 };
        for (int i = 0; i < 16; i++)
        {
            wpicks.Add(DetLogit.ChooseWeighted(ref wr, weights));
        }

        sb.Append("\n    { \"seed\": 77, \"stream\": 8, \"weights\": [").Append(string.Join(", ", weights)).Append("], \"picks\": ").Append(Arr(wpicks.ToArray())).Append(", \"state\": \"")
            .Append(wr.State.ToString(inv)).Append("\" }");
        sb.Append("\n  ]\n}\n");
        return sb.ToString();
    }

    private static string Arr(int[] a)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < a.Length; i++)
        {
            sb.Append(i == 0 ? "" : ", ").Append(a[i].ToString(CultureInfo.InvariantCulture));
        }

        return sb.Append(']').ToString();
    }
}
