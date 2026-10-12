namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>Overall tier of a playlist plus progress (0..1) toward the next one.</summary>
internal readonly record struct RankResult(int Rank, double Progress);

/// <summary>
/// Port of evxl's benchmark rank calculations (benchmark-builder bundle). The overall tier is the higher of the
/// "complete" tier (lowest scenario tier) and the playlist's own calculation, exactly as evxl combines them.
/// </summary>
internal static class RankCalculationEngine
{
    public static RankResult Compute(RankSetup setup, IReadOnlyList<string> rankNames, IReadOnlyList<SubcategoryProgress> parts)
    {
        var c = new Ctx(setup, rankNames, parts);
        var complete = CompleteRank(c);
        var custom = Custom(c);
        var top = Math.Max(rankNames.Count - 1, 0);
        var customWins = c.Method == "selectable-top-n" || custom is { } cu && cu.Rank > complete;

        var rank = customWins ? custom!.Value.Rank : complete;
        // The bar follows the playlist's own calculation whatever the complete tier is; methods without one use the share of scenarios above the tier.
        var progress = custom?.Progress ?? (c.All.Count == 0 ? 0 : (double)c.All.Count(s => s.Rank > rank) / c.All.Count);
        return new(Math.Clamp(rank, 0, top), Math.Clamp(double.IsNaN(progress) ? 0 : progress, 0, 1));
    }

    // ---------- shared building blocks ----------

    sealed class Ctx
    {
        public string Method { get; }
        public string Diff { get; }
        public string Key { get; }
        public IReadOnlyList<string> Tiers { get; }
        public IReadOnlyList<SiblingTiers> Siblings { get; }
        public ScenarioSelection? Selection { get; }
        public IReadOnlyList<string> RankNames { get; }
        public List<(string Name, List<SubcategoryProgress> Subs)> Cats { get; }
        public List<SubcategoryProgress> Subs { get; }
        public List<ScenarioProgress> All { get; }

        public Ctx(RankSetup setup, IReadOnlyList<string> rankNames, IReadOnlyList<SubcategoryProgress> parts)
        {
            Method = setup.Method; Diff = setup.Difficulty; Key = setup.Difficulty.Trim().ToLowerInvariant();
            Tiers = setup.Tiers; Siblings = setup.Siblings; Selection = setup.Selection; RankNames = rankNames;
            Subs = parts.ToList();
            Cats = parts.GroupBy(p => p.Parent).Select(g => (g.Key, g.ToList())).ToList();
            All = parts.SelectMany(p => p.Scenarios).ToList();
        }
    }

    sealed record Pos(int Base, double Precise, double Progress, bool Maxed);

    static readonly Pos NoPos = new(0, 0, 0, false);

    /// <summary>evxl's scenario position on its own rank_maxes (U).</summary>
    static Pos Position(double e, IReadOnlyList<double> n)
    {
        if (e <= 0 || n.Count == 0) return NoPos;
        var r = 0;
        for (var i = n.Count - 1; i >= 0; i--) if (e >= n[i]) { r = i + 1; break; }
        if (r == 0)
        {
            var positive = n.Where(x => x > 0).ToList();
            var a = positive.Count == 0 ? double.PositiveInfinity : positive.Min();
            return new(0, 0, double.IsFinite(a) && a > 0 ? Math.Min(e / a, .99) : 0, false);
        }
        if (r == n.Count)
        {
            var a = n[^1];
            var prev = n.Count > 1 ? n[^2] : 0;
            var l = Math.Abs(a - prev);
            if (l == 0) l = 1;
            var u = Math.Max(0, (e - a) / l);
            return new(r, r + u, u - Math.Floor(u), true);
        }
        var t = n[r - 1];
        var s = n[r] - t;
        var c = s > 0 ? (e - t) / s : 0;
        return new(r, r + c, Math.Clamp(c, 0, 1), false);
    }

    static Pos PosOf(ScenarioProgress s) => Position(s.Score, s.RankMaxes);

    static double Floor2(double x) => Math.Floor(x * 100) / 100;

    static double JsRound(double x) => Math.Floor(x + .5);

    static double Round1(double x) => JsRound(x * 10) / 10;

    /// <summary>evxl's K: progress inside the current threshold band.</summary>
    static double Band(double e, double[] n, int rank)
    {
        if (n.Length == 0) return 0;
        if (rank <= 0)
        {
            var positive = n.Where(x => x > 0).ToList();
            if (positive.Count == 0) return 0;
            return Floor2(Math.Clamp(e / positive.Min(), 0, 1));
        }
        if (rank >= n.Length) return 1;
        var o = n[rank - 1];
        var c = n[rank] - o;
        return !double.IsFinite(c) || c <= 0 ? 0 : Floor2(Math.Clamp((e - o) / c, 0, 1));
    }

    /// <summary>evxl's X: highest threshold met, with progress to the next.</summary>
    static RankResult Thresholds(double total, double[] n)
    {
        var rank = 0;
        for (var i = n.Length - 1; i >= 0; i--) if (total >= n[i]) { rank = i + 1; break; }
        return new(rank, Band(total, n, rank));
    }

    static int CountAtOrBelow(double[] n, double value) => n.Count(x => value >= x);

    /// <summary>evxl's re: harmonic mean, 0 unless exactly <paramref name="count"/> positive values.</summary>
    static double Harmonic(IReadOnlyList<double> values, int count) =>
        values.Count != count || values.Any(x => x == 0) ? 0 : count / values.Sum(x => 1 / x);

    static double[] Steps(int count, double start = 100, double step = 100) => Enumerable.Range(0, count).Select(i => start + i * step).ToArray();

    static double At(double[] n, int i) => i >= 0 && i < n.Length ? n[i] : 0;

    // ---------- evxl's "complete" tier ----------

    /// <summary>Lowest scenario tier; 0 when any scenario is unplayed or unranked.</summary>
    static int CompleteRank(Ctx c)
    {
        var min = int.MaxValue;
        foreach (var s in c.All)
        {
            if (s.Score <= 0 || s.Rank <= 0) return 0;
            min = Math.Min(min, s.Rank);
        }
        return min == int.MaxValue || min >= c.RankNames.Count ? 0 : min;
    }

    // ---------- dispatch ----------

    static RankResult? Custom(Ctx c)
    {
        if (c.All.Count == 0) return null;
        return c.Method switch
        {
            "basic" => Basic(c),
            "ra-s4" => RaS4(c),
            "vt-energy" => VtEnergy(c),
            "cb-s1" => CbS1(c),
            "aplus-s1" => AplusS1(c),
            "ca-s1" => Energy(c, Steps(8, 1500, 50).Take(7).ToArray(), NonStrafe, 50, 2),
            "sa-s2" => Energy(c, [1200, 1300, 1400, 1500, 1600], NonStrafe, 50, 1),
            "mira" => Energy(c, c.Key == "easy" ? [100, 200, 300, 400, 500] : [600, 700, 800, 900, 1000], _ => true, 50, 1),
            "val-energy" => Energy(c, ValThresholds(c.Key), _ => true, 100, 1),
            "tpt" => CountRank(c, 5, exactNext: true),
            "asb" => CountRank(c, 8, exactNext: true),
            "e1se" => CountRank(c, 6, exactNext: true),
            "dojo" => CountRank(c, 4),
            "dojo2" => CountRank(c, 3),
            "dojo3" => CountRank(c, 5),
            "rbe" => CountRank(c, 9),
            "ssb2" => Ssb2(c),
            "tsk" => Tsk(c),
            "aplus-alt" => AplusAlt(c),
            "xyz" => Xyz(c),
            "xyz2" => Xyz2(c),
            "xyz-smoothness-v2" => XyzSmoothness(c),
            "mh" => PointsHarmonic(c, new() { ["easy"] = [100, 200, 300, 400], ["medium"] = [500, 600, 700, 800], ["hard"] = [900, 1000, 1100, 1200, 1300] }, "easy"),
            "avasive" => PointsHarmonic(c, new() { ["genesis"] = [100, 200, 300, 400, 500], ["ascension"] = [600, 700, 800, 900, 1000], ["enlightenment"] = [1100, 1200, 1300, 1400, 1500], ["wallhack"] = Steps(9) }, "easy"),
            "aoi" => Aoi(c),
            "真・葵Aim" => Aoi2(c),
            "hewchy" => Hewchy(c),
            "generic-energy-alt" => GenericEnergyAlt(c),
            "MIYU" => Miyu(c),
            "complete" => null,
            "dm" => Dm(c),
            "dm-s3" => DmS3(c),
            "mira-apex" => MiraApex(c),
            "routine" => Routine(c),
            "selectable-top-n" => SelectableTopN(c),
            "RXZU" => Rxzu(c),
            "aimbeast" => Aimbeast(c),
            "aimbeast-partial" => AimbeastPartial(c),
            "generic-energy" => GenericEnergy(c),
            "generic-energy-uncapped" => Energy(c, SiblingThresholds(c), _ => true, 100, 9999, cap: null),
            "Avasive-S2" => SubEnergy(c, ThresholdsFromSiblings(c), c.Key switch { "easier" => 600d, "medium" => 1000d, "hard" => 1400d, _ => (double?)null }),
            "snakbox" => SnakBox(c),
            "mh-tracking" or "mh-reactive" or "mh-precise" => SubEnergy(c, ThresholdsFromSiblings(c), null),
            "33" or "iris" => IrisEnergy(c),
            "ra-s5" => RaS5(c),
            "jade-palace" => JadeEnergy(c, fixedTop: null, uncappedName: "easy", alwaysUncapped: false),
            "Good-Energy" => JadeEnergy(c, fixedTop: 3, uncappedName: "normal", alwaysUncapped: true),
            _ => null,
        };
    }

    static bool NonStrafe(string sub) => !sub.ToLowerInvariant().Contains("strafe");

    static double[] ValThresholds(string key)
    {
        double[] all = Steps(15);
        return key switch { "easy" => all[0..4], "medium" => all[4..8], "hard" => all[8..12], _ => all };
    }

    // ---------- best-per-subcategory (evxl's be) ----------

    sealed record Best(Pos P, ScenarioProgress? S)
    {
        public double Score => S?.Score ?? 0;
    }

    static List<(string Cat, string Sub, Best Best)> BestPerSub(Ctx c, double[]? thr, double lower, double upper)
    {
        var result = new List<(string, string, Best)>();
        foreach (var (cat, subs) in c.Cats)
            foreach (var sub in subs)
            {
                var best = new Best(NoPos, null);
                var first = true;
                foreach (var s in sub.Scenarios)
                {
                    if (s.Score == 0) continue;
                    var cur = new Best(PosOf(s), s);
                    bool take;
                    if (first) take = true;
                    else if (cur.P.Base > 0 && best.P.Base == 0) take = true;
                    else if (cur.P.Base > 0 && best.P.Base > 0) take = cur.P.Precise > best.P.Precise;
                    else if (cur.P.Base == 0 && best.P.Base == 0)
                    {
                        if (thr != null) take = Energy(cur, thr, lower, upper, true) > Energy(best, thr, lower, upper, true);
                        else
                        {
                            var o = s.RankMaxes.Count > 0 ? s.Score / s.RankMaxes[0] : 0;
                            var p = best.S is { RankMaxes.Count: > 0 } b ? b.Score / b.RankMaxes[0] : 0;
                            take = o > p;
                        }
                    }
                    else take = false;
                    if (take) best = cur;
                    first = false;
                }
                result.Add((cat, sub.Name, best));
            }
        return result;
    }

    /// <summary>evxl's ne: a scenario's position on a point-threshold scale with fake tiers below and above.</summary>
    static double Energy(Best e, double[] n, double lower, double upper, bool trunc)
    {
        double T(double d) => trunc ? Math.Truncate(d) : d;
        if (e.P.Base == 0 && e.P.Precise == 0)
        {
            var maxes = e.S?.RankMaxes ?? [];
            if (e.Score <= 0 || maxes.Count < 2) return 0;
            var m = maxes[0];
            var f = maxes[1] - m;
            var b = m - f;
            var p = n[0] - lower;
            var v = e.Score < b ? e.Score / b * p : p + (e.Score - b) / (m - b) * (n[0] - p);
            return T(v);
        }
        var a = n.Length;
        var first = n[0] - lower;
        var top = n[^1];
        var step = a > 1 ? top - n[^2] : 0;
        if (step == 0 || double.IsNaN(step)) step = 100;
        var h = top + step;
        var pr = e.P.Precise;
        if (pr <= 0) return 0;
        if (pr < 1) return T(first + pr * (n[0] - first));
        if (pr < a)
        {
            var d = (int)Math.Floor(pr);
            var from = d == 0 ? first : n[d - 1];
            return T(from + (pr - d) * (n[d] - from));
        }
        if (pr < a + upper) return T(top + (pr - a) * (h - top));
        return T(h);
    }

    // ---------- basic (evxl's le) ----------

    static RankResult Basic(Ctx c)
    {
        var best = BestPerSub(c, null, 0, 0);
        var ranks = new List<int>();
        var unranked = false;
        foreach (var b in best) { if (b.Best.P.Base > 0) ranks.Add(b.Best.P.Base); else unranked = true; }
        var s = unranked ? 0 : ranks.Count > 0 ? ranks.Min() : 0;
        var tiers = c.Tiers.Count;
        double sum = 0;
        foreach (var b in best)
        {
            double m;
            if (b.Best.P.Base == 0 || b.Best.P.Precise == 0) m = .001;
            else if (b.Best.P.Precise < s) m = 0;
            else if (b.Best.P.Precise >= s + 1) m = 1;
            else m = b.Best.P.Precise - s;
            sum += m;
        }
        var progress = s >= tiers ? 1 : best.Count > 0 ? sum / best.Count : 0;
        return new(s, progress);
    }

    // ---------- energy family (evxl's te / ee) ----------

    static RankResult Energy(Ctx c, double[] thr, Func<string, bool> filter, double lower, double upper, double? cap = null, bool uncapped = false)
    {
        if (thr.Length == 0) return new(0, 0);
        var best = BestPerSub(c, thr, lower, upper);
        var advanced = c.Key == "advanced";
        double? limit = cap ?? (advanced && !uncapped ? 1200 : null);
        var energies = new List<double>();
        foreach (var b in best)
        {
            if (!filter(b.Sub)) continue;
            var e = Energy(b.Best, thr, lower, upper, true);
            if (limit is { } l && e > l) e = l;
            energies.Add(e);
        }
        var count = best.Count(b => filter(b.Sub));
        var total = energies.Count == count ? Round1(Harmonic(energies.Where(x => x > 0).ToList(), count)) : 0;
        var result = Thresholds(total, thr);
        if (uncapped && result.Rank >= thr.Length && thr.Length > 0)
        {
            var last = thr[^1];
            var prev = thr.Length > 1 ? thr[^2] : last - 100;
            var step = last - prev;
            if (step == 0) step = 100;
            result = result with { Progress = 1 + Math.Max(0, (total - last) / step) };
        }
        return result;
    }

    static RankResult VtEnergy(Ctx c)
    {
        var thr = VtThresholds(c);
        var lower = c.Key == "novice" ? 0 : 100;
        var result = Energy(c, thr, NonStrafe, lower, 1);
        if (c.Key == "advanced" && c.Tiers.Count > 0 && result.Rank >= c.Tiers.Count)
            result = Energy(c, thr, NonStrafe, lower, double.PositiveInfinity, uncapped: true);
        return result;
    }

    static double[] VtThresholds(Ctx c)
    {
        double[] all = Steps(15);
        if (c.Key == "elite (unofficial)")
        {
            static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsAsciiLetterLower).ToArray());
            static bool Placeholder(string s) => Norm(s) is "unranked" or "norank" or "nodata" or "notcomplete";
            var first = c.Tiers.FirstOrDefault(t => t.Trim().Length > 0 && !Placeholder(t))
                ?? c.RankNames.Select(n => n.Trim()).FirstOrDefault(n => n.Length > 0 && !Placeholder(n));
            return first != null && Norm(first).StartsWith("grandmaster") ? all[8..14] : all[9..15];
        }
        return c.Key switch { "novice" => all[0..4], "intermediate" => all[4..8], "advanced" => all[8..12], _ => all };
    }

    /// <summary>evxl's Ce: thresholds continue from the previous difficulties, minus shared boundary tiers.</summary>
    static double[] ThresholdsFromSiblings(Ctx c)
    {
        var sib = c.Siblings;
        var index = Math.Max(sib.ToList().FindIndex(s => s.Name.Trim().ToLowerInvariant() == c.Key), 0);
        var before = sib.Take(index).Sum(s => s.Tiers.Count);
        var shared = 0;
        for (var u = 0; u < index && u + 1 < sib.Count; u++)
        {
            var prev = sib[u].Tiers.LastOrDefault()?.Trim().ToLowerInvariant();
            var next = sib[u + 1].Tiers.FirstOrDefault()?.Trim().ToLowerInvariant();
            if (prev != null && prev == next) shared++;
        }
        return Steps(c.Tiers.Count, (before - shared + 1) * 100);
    }

    /// <summary>evxl's oe over (1..total+1)*100: this difficulty's slice including one extra tier on top.</summary>
    static double[] SiblingSlice(Ctx c)
    {
        var all = Steps(c.Siblings.Sum(s => s.Tiers.Count) + 1);
        var skip = 0;
        foreach (var s in c.Siblings) { if (s.Name == c.Diff) break; skip += s.Tiers.Count; }
        return all.Skip(skip).Take(c.Tiers.Count + 1).ToArray();
    }

    static double[] SiblingThresholds(Ctx c) => SiblingSlice(c).SkipLast(1).ToArray();

    static RankResult GenericEnergy(Ctx c)
    {
        var slice = SiblingSlice(c);
        return slice.Length == 0 ? new(0, 0) : Energy(c, slice[..^1], _ => true, 100, 1, cap: slice[^1]);
    }

    // ---------- top-2 subcategory energy (evxl's ue) ----------

    static RankResult SubEnergy(Ctx c, double[] thr, double? cap)
    {
        if (thr.Length == 0) return new(0, 0);
        var energies = new List<double>();
        foreach (var (_, subs) in c.Cats)
            foreach (var sub in subs)
            {
                var list = new List<double>();
                foreach (var s in sub.Scenarios)
                {
                    if (s.Score <= 0) continue;
                    var e = Energy(new Best(PosOf(s), s), thr, 100, double.PositiveInfinity, false);
                    list.Add(cap is { } l ? Math.Min(e, l) : e);
                }
                list.Sort((a, b) => b.CompareTo(a));
                energies.Add(list.Count == 1 ? list[0] / 2 : list.Count >= 2 ? (list[0] + list[1]) / 2 : 0);
            }
        var total = energies.Count > 0 ? Round1(Harmonic(energies, energies.Count)) : 0;
        var rank = CountAtOrBelow(thr, total);
        return new(rank, Band(total, thr, rank));
    }

    static RankResult SnakBox(Ctx c)
    {
        double[] thr = c.Key == "medium" ? [400, 500, 600, 700, 800, 900, 1100] : ThresholdsFromSiblings(c);
        return SubEnergy(c, thr, thr[^1]);
    }

    // ---------- tier-count methods (dojo, tpt, ...) ----------

    /// <summary>exactNext: evxl's ce counts only scenarios at exactly the next tier toward progress.</summary>
    static RankResult CountRank(Ctx c, int required, bool exactNext = false)
    {
        var ranks = c.All.Where(s => s.Rank > 0).Select(s => s.Rank).ToList();
        if (ranks.Count == 0) return new(0, 0);
        var counts = ranks.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        var rank = 0;
        foreach (var r in counts.Keys.OrderDescending())
            if (counts.Where(kv => kv.Key >= r).Sum(kv => kv.Value) >= required) { rank = r; break; }
        var next = exactNext ? counts.GetValueOrDefault(rank + 1) : counts.Where(kv => kv.Key >= rank + 1).Sum(kv => kv.Value);
        var progress = rank > 0 ? Math.Min((double)next / required, 1) : 0;
        return new(rank, progress);
    }

    static RankResult Hewchy(Ctx c)
    {
        var ranks = c.All.Select(s => s.Rank).ToList();
        var rank = 0;
        for (var s = ranks.Max(); s > 0; s--) if (ranks.Count(r => r >= s) >= 12) { rank = s; break; }
        var progress = rank > 0 ? Math.Min(ranks.Count(r => r >= rank + 1) / 12.0, 1) : 0;
        return new(rank, progress);
    }

    static RankResult Routine(Ctx c)
    {
        var ranks = c.All.Where(s => s.Rank > 0).Select(s => s.Rank).ToList();
        var total = c.All.Count;
        var need = (int)Math.Ceiling(total / 2.0);
        var counts = ranks.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        var keys = counts.Keys.OrderDescending().ToList();
        var rank = 0;
        foreach (var g in keys) if (keys.Where(k => k >= g).Sum(k => counts[k]) >= need) { rank = g; break; }
        if (rank >= (keys.Count == 0 ? 0 : keys.Max())) return new(rank, 1);
        return new(rank, Math.Min((double)keys.Where(k => k >= rank + 1).Sum(k => counts[k]) / need, 1));
    }

    // ---------- Revenge selectable top-N ----------

    static RankResult SelectableTopN(Ctx c)
    {
        var baseCount = c.Selection?.BaseRankScoreCount ?? 12;
        var fullPool = c.Selection?.FullPoolRankScoreCount;
        var minCat = c.Selection?.MinPerCategory ?? 0;
        var minSub = c.Selection?.MinPerSubcategory ?? 0;
        var scored = new List<Pos>();
        foreach (var s in c.All)
        {
            if (s.Score <= 0) continue;
            scored.Add(s.RankMaxes.Count > 0 ? PosOf(s) : new(s.Rank, s.Rank, 0, false));
        }
        var required = fullPool ?? baseCount;
        var catIssue = c.Cats.Any(g => g.Subs.Sum(s => s.Scenarios.Count) < minCat);
        var subIssue = c.Subs.Any(s => s.Scenarios.Count < minSub);
        var top = scored.OrderByDescending(p => p.Precise).Take(required).ToList();
        if (catIssue || subIssue || top.Count < required) return new(0, 0);
        var x = Math.Max(0, (int)Math.Floor((double)top[required - 1].Base));
        var t = x + 1;
        var sum = top.Sum(p => p.Precise >= t ? 1 : p.Precise < x ? 0 : p.Precise - x);
        return new(x, Math.Clamp(sum / required, 0, 1));
    }

    // ---------- point / harmonic tables ----------

    /// <summary>evxl's Ne (mh, avasive): every scenario needs a score; harmonic mean of per-scenario points.</summary>
    static RankResult PointsHarmonic(Ctx c, Dictionary<string, double[]> table, string fallback)
    {
        var t = table.TryGetValue(c.Key, out var own) ? own : table[fallback];
        var energies = new List<double>();
        foreach (var s in c.All)
        {
            var p = PosOf(s);
            double e;
            if (p.Base == 0) e = Math.Truncate(s.Score / (s.RankMaxes.Count > 0 && s.RankMaxes[0] != 0 ? s.RankMaxes[0] : 1) * t[0]);
            else if (p.Maxed && p.Base == s.RankMaxes.Count)
            {
                var r = s.RankMaxes[^1];
                var f = s.RankMaxes.Count > 1 ? s.RankMaxes[^2] : 0;
                var b = r - f;
                if (b == 0) b = 1;
                e = Math.Truncate(t[^1] + (s.Score - r) / b * 100);
            }
            else
            {
                var k = p.Base - 1 < t.Length ? t[p.Base - 1] : 0;
                var m = p.Base < t.Length ? t[p.Base] : double.NaN;
                e = Math.Truncate(k + p.Progress * (m - k));
            }
            energies.Add(e);
        }
        if (energies.Any(e => e == 0)) return new(0, 0);
        var total = Round1(Harmonic(energies, energies.Count));
        return Thresholds(total, t);
    }

    static RankResult Ssb2(Ctx c)
    {
        double[] thr = Steps(10);
        var energies = c.All.Select(s =>
        {
            var p = PosOf(s);
            if (p.Base == 0 && s.Score == 0) return 0d;
            return p.Maxed && p.Base == s.RankMaxes.Count ? Math.Min(p.Base * 100 + p.Progress * 100, 1050) : p.Base * 100 + p.Progress * 100;
        }).ToList();
        var total = Math.Truncate(Harmonic(energies.Where(x => x > 0).ToList(), energies.Count));
        return Thresholds(total, thr);
    }

    static RankResult AplusAlt(Ctx c)
    {
        var thr = c.Tiers.Count > 0 ? Steps(c.Tiers.Count) : Steps(10);
        var share = c.All.Count > 0 ? 100.0 / c.All.Count : 0;
        double sum = 0;
        foreach (var s in c.All)
        {
            var p = PosOf(s);
            sum += p.Base == 0 ? s.Score / (s.RankMaxes.Count > 0 && s.RankMaxes[0] != 0 ? s.RankMaxes[0] : 1) * share : (p.Base + p.Progress) * share;
        }
        return Thresholds(Math.Round(sum * 100) / 100, thr);
    }

    static RankResult Rxzu(Ctx c)
    {
        double[] t = c.Key switch
        {
            "easy" => [200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200],
            "hard" => [400, 500, 600, 700, 800, 850, 900, 950, 1000, 1050, 1100, 1150, 1200],
            _ => [],
        };
        if (t.Length == 0) return new(0, 0);
        double total = c.All.Sum(s => s.Rank > 0 ? t[Math.Min(s.Rank, t.Length) - 1] : 0);
        var avg = total / c.All.Count;
        var rank = CountAtOrBelow(t, avg);
        return new(rank, Band(avg, t, rank));
    }

    static RankResult Miyu(Ctx c)
    {
        double[] t = [16, 24, 32, 40, 48, 56, 63];
        double total = c.All.Sum(s => s.Rank <= 0 ? 0 : 2 + (s.Rank - 1));
        var rank = CountAtOrBelow(t, total);
        return new(rank, Band(total, t, rank));
    }

    static RankResult GenericEnergyAlt(Ctx c)
    {
        var count = c.Tiers.Count > 0 ? c.Tiers.Count : c.All[0].RankMaxes.Count;
        var s = Steps(count);
        if (s.Length == 0) return new(0, 0);
        var cap = s[^1] + 50;
        double sum = 0;
        foreach (var sc in c.All)
        {
            double f = 0;
            if (sc.Score > 0)
            {
                var p = PosOf(sc);
                if (p.Base == 0) f = sc.Score / (sc.RankMaxes.Count > 0 && sc.RankMaxes[0] != 0 ? sc.RankMaxes[0] : 1) * s[0];
                else if (p.Maxed)
                {
                    var idx = sc.RankMaxes.Count - 1;
                    var n = sc.RankMaxes[idx];
                    var prev = idx > 0 ? sc.RankMaxes[idx - 1] : 0;
                    var span = Math.Abs(n - prev);
                    if (span == 0) span = 1;
                    f = s[^1] + Math.Min((sc.Score - n) / span, .5) * 100;
                }
                else
                {
                    var lo = At(s, p.Base - 1);
                    var hi = p.Base < s.Length && s[p.Base] != 0 ? s[p.Base] : cap;
                    f = lo + p.Progress * (hi - lo);
                }
                f = Math.Min(Math.Truncate(f), cap);
            }
            sum += f;
        }
        var total = Round1(sum / c.All.Count);
        var rank = CountAtOrBelow(s, total);
        return new(rank, Band(total, s, rank));
    }

    // ---------- per-subcategory energy tables ----------

    static List<double> SubMaxima(Ctx c, Func<ScenarioProgress, Pos, double> value)
    {
        var list = new List<double>();
        foreach (var sub in c.Subs)
        {
            double best = 0;
            foreach (var s in sub.Scenarios)
            {
                if (s.Score <= 0) continue;
                best = Math.Max(best, value(s, PosOf(s)));
            }
            list.Add(best);
        }
        return list;
    }

    static RankResult MiraApex(Ctx c)
    {
        if (c.Tiers.Count == 0) return new(0, 0);
        var t = Steps(c.Tiers.Count, 10, 10);
        var energies = SubMaxima(c, (s, p) =>
        {
            if (s.RankMaxes.Count == 0) return 0;
            double y;
            if (p.Base == 0) { var x = s.RankMaxes[0]; y = x > 0 ? Math.Min(s.Score / x, 1) * t[0] : 0; }
            else if (p.Maxed && p.Base == s.RankMaxes.Count) y = t[^1];
            else { var lo = At(t, p.Base - 1); var hi = p.Base < t.Length ? t[p.Base] : t[^1]; y = lo + p.Progress * (hi - lo); }
            return Math.Max(0, Math.Truncate(y));
        });
        if (energies.Count == 0) return new(0, 0);
        double u = 0;
        var positive = energies.Where(x => x > 0).ToList();
        if (positive.Count == energies.Count) u = energies.Count / energies.Sum(x => 1 / x);
        else if (positive.Count > 0) u = energies.Count / energies.Sum(x => 1 / (x == 0 ? .1 : x));
        u = Round1(u);
        var rank = CountAtOrBelow(t, u);
        return new(rank, Band(u, t, rank));
    }

    static RankResult Dm(Ctx c)
    {
        var r = c.Tiers.Count;
        if (r == 0) return new(0, 0);
        double o = 100;
        if (c.Key == "boss" && r == 8 || c.Key == "boss+") o = 500;
        else if (c.Key == "boss++") o = 900;
        var s = new double[r];
        for (var i = 0; i < r; i++) s[i] = i == 0 ? o : i == r - 1 ? s[i - 1] + 10 : s[i - 1] + 100;
        var energies = SubMaxima(c, (sc, p) =>
        {
            if (p.Base == 0) return 0;
            double x;
            if (p.Maxed && p.Base == sc.RankMaxes.Count)
            {
                var last = sc.RankMaxes[^1];
                var prev = sc.RankMaxes.Count > 1 ? sc.RankMaxes[^2] : 0;
                var w = Math.Abs(last - prev);
                if (w == 0) w = 1;
                x = s[^1] + Math.Max(0, (sc.Score - last) / w) * 10;
            }
            else { var lo = At(s, p.Base - 1); var hi = p.Base < s.Length ? s[p.Base] : s[^1]; x = lo + p.Progress * (hi - lo); }
            return Math.Max(0, Math.Truncate(x));
        });
        var g = c.Subs.Count;
        double h = 0;
        var positive = energies.Where(x => x > 0).ToList();
        if (positive.Count == g && energies.Count == g) h = g / positive.Sum(x => 1 / x);
        h = Round1(h);
        var rank = CountAtOrBelow(s, h);
        return new(rank, Band(h, s, rank));
    }

    static RankResult DmS3(Ctx c)
    {
        double[] t = [100, 200, 300, 1510];
        if (c.Key.Contains("boss")) t = [1300, 1400, 1500, 1510];
        else
        {
            var m = System.Text.RegularExpressions.Regex.Match(c.Key, @"level\s*([1-4])");
            if (m.Success) { var r = (int.Parse(m.Groups[1].Value) - 1) * 300 + 100; t = [r, r + 100, r + 200, 1510]; }
        }
        var energies = SubMaxima(c, (sc, p) =>
        {
            double y;
            if (p.Base == 0) { var x = sc.RankMaxes.Count > 0 && sc.RankMaxes[0] != 0 ? sc.RankMaxes[0] : 1; y = Math.Min(sc.Score / x * t[0], t[0]); }
            else if (p.Maxed && p.Base == sc.RankMaxes.Count) y = t[^1];
            else { var lo = At(t, p.Base - 1); var hi = p.Base < t.Length ? t[p.Base] : t[^1]; y = lo + p.Progress * (hi - lo); }
            return Math.Max(0, Math.Truncate(y));
        });
        var l = c.Subs.Count;
        var positive = energies.Where(x => x > 0).ToList();
        var u = positive.Count == l && energies.Count == l ? l / positive.Sum(x => 1 / x) : 0;
        u = Round1(u);
        var rank = CountAtOrBelow(t, u);
        return new(rank, Band(u, t, rank));
    }

    static RankResult RaS5(Ctx c)
    {
        var thr = SiblingThresholds(c);
        if (thr.Length == 0) return new(0, 0);
        var subEnergies = new List<double>();
        foreach (var sub in c.Subs)
        {
            var reactive = sub.Name.ToLowerInvariant().Contains("reactive");
            var x = new List<double>();
            foreach (var s in sub.Scenarios)
            {
                var l = s.Score;
                if (l == 0) { x.Add(0); continue; }
                var p = PosOf(s);
                double q;
                if (p.Base == 0)
                {
                    var h = s.RankMaxes.Count > 0 && s.RankMaxes[0] != 0 ? s.RankMaxes[0] : 1;
                    double g = 0, z = 0;
                    if (reactive) { g = c.Key == "entry" ? 800 : 830; z = 2; }
                    var d = h - g;
                    double j = 0;
                    if (d > 0)
                    {
                        var pct = Math.Max(0, (l - g) / d) * 100;
                        j = JsRound(pct * Math.Pow(10, z)) / Math.Pow(10, z);
                    }
                    q = thr[0] * (j / 100);
                }
                else if (p.Maxed && p.Base == s.RankMaxes.Count) q = thr[^1];
                else
                {
                    var lo = At(thr, p.Base - 1);
                    var hi = p.Base < thr.Length && thr[p.Base] != 0 ? thr[p.Base] : thr[^1];
                    var z = JsRound(p.Progress * 100);
                    q = lo + (hi - lo) * (z / 100);
                }
                x.Add(Math.Max(0, JsRound(q * 100) / 100));
            }
            double t;
            if (x.Count == 0) t = 0;
            else if (reactive) t = x.Count != 4 ? 0 : (Math.Max(x[0], x[1]) + Math.Max(x[2], x[3])) / 2;
            else { x.Sort((a, b) => b.CompareTo(a)); t = x.Count == 1 ? x[0] / 2 : (x[0] + x[1]) / 2; }
            subEnergies.Add(JsRound(t));
        }
        double k = 0;
        if (subEnergies.All(v => v > 0)) k = subEnergies.Count / subEnergies.Sum(v => 1 / v);
        k = JsRound(k);
        var rank = CountAtOrBelow(thr, k);
        return new(rank, Band(k, thr, rank));
    }

    static RankResult IrisEnergy(Ctx c)
    {
        var tierCount = c.Tiers.Count > 0 ? c.Tiers.Count : c.All[0].RankMaxes.Count;
        double start = c.Key switch
        {
            "novice" or "beginner" or "easy" or "intermediate" => 100,
            "adv" or "advanced" or "hard" => 200,
            _ => (c.Siblings.ToList().FindIndex(s => s.Name.Trim().ToLowerInvariant() == c.Key) is var i && i >= 0 ? i + 1 : 1) * 100d,
        };
        if (tierCount == 0 || start == 0) return new(0, 0);
        var a = Steps(tierCount, start);
        var catEnergies = new List<double>();
        foreach (var (_, subs) in c.Cats)
        {
            var subEnergies = new List<double>();
            foreach (var sub in subs)
            {
                var values = new List<double>();
                foreach (var s in sub.Scenarios)
                {
                    var o = s.Score;
                    double v = 0;
                    if (o > 0 && s.RankMaxes.Count > 0)
                    {
                        var p = PosOf(s);
                        if (p.Base == 0) v = 0;
                        else if (p.Maxed && p.Base == s.RankMaxes.Count) v = a[^1];
                        else { var lo = At(a, p.Base - 1); var hi = p.Base < a.Length && a[p.Base] != 0 ? a[p.Base] : a[^1]; v = lo + p.Progress * (hi - lo); }
                        v = Math.Max(0, JsRound(v * 100) / 100);
                    }
                    values.Add(v);
                }
                var half = (int)Math.Ceiling(values.Count / 2.0);
                var passing = values.Count(x => x >= a[0]);
                subEnergies.Add(values.Count == 0 ? 0
                    : passing >= half ? JsRound(values.OrderDescending().Take(half).Sum() / half)
                    : JsRound(values.Sum() / (half * 2)));
            }
            catEnergies.Add(JsRound(subEnergies.Count > 0 ? subEnergies.Average() : 0));
        }
        var h = catEnergies.Count > 0 && catEnergies.All(x => x > 0) ? catEnergies.Average() : 0;
        h = JsRound(h);
        var rank = CountAtOrBelow(a, h);
        return new(rank, Band(h, a, rank));
    }

    static RankResult JadeEnergy(Ctx c, int? fixedTop, string uncappedName, bool alwaysUncapped)
    {
        var a = SiblingSlice(c).SkipLast(1).ToArray();
        if (a.Length == 0) return new(0, 0);
        var fundamentals = c.Key == "fundamentals";
        var uncapDiff = c.Key == uncappedName;
        var capped = new List<double>();
        var uncapped = new List<double>();
        foreach (var sub in c.Subs)
        {
            var low = new List<double>();
            var high = new List<double>();
            foreach (var s in sub.Scenarios)
            {
                var j = s.Score;
                double b = 0, y = 0;
                if (j > 0 && s.RankMaxes.Count > 0)
                {
                    var p = PosOf(s);
                    if (p.Base == 0)
                    {
                        var d = s.RankMaxes[0];
                        if (d > 0) { b = Math.Min(j / d, 1) * a[0]; y = b; }
                    }
                    else if (p.Maxed && p.Base == s.RankMaxes.Count)
                    {
                        var w = s.RankMaxes[^1];
                        var d = s.RankMaxes.Count > 1 ? s.RankMaxes[^2] : 0;
                        var span = Math.Abs(w - d);
                        if (span == 0) span = 1;
                        var ke = Math.Max(0, (j - w) / span);
                        b = a[^1] + Math.Min(ke, 1) * 100;
                        y = uncapDiff ? a[^1] + ke * 100 : b;
                    }
                    else
                    {
                        var lo = At(a, p.Base - 1);
                        var hi = p.Base < a.Length && a[p.Base] != 0 ? a[p.Base] : a[^1];
                        b = lo + p.Progress * (hi - lo);
                        y = b;
                    }
                    b = Math.Truncate(b); y = Math.Truncate(y);
                }
                low.Add(b); high.Add(y);
            }
            var q = fixedTop ?? (fundamentals ? Math.Min(3, low.Count) : low.Count / 2);
            capped.Add(TopMean(low, q));
            uncapped.Add(TopMean(high, q));
        }
        var n = TruncHarmonic(capped);
        var useUncapped = uncapDiff && (alwaysUncapped || n >= 600);
        var o = useUncapped ? TruncHarmonic(uncapped) : n;
        var rank = CountAtOrBelow(a, o);
        return new(rank, Band(o, a, rank));
    }

    static double TopMean(List<double> values, int n)
    {
        var top = values.OrderDescending().Take(n).ToList();
        return top.Count == 0 ? 0 : Math.Truncate(top.Sum() / top.Count);
    }

    static double TruncHarmonic(List<double> values)
    {
        if (values.Count == 0) return 0;
        var v = values.Select(x => x > 0 ? x : .1).ToList();
        return Math.Truncate(v.Count / v.Sum(x => 1 / x));
    }

    // ---------- category-based methods ----------

    static RankResult RaS4(Ctx c)
    {
        var easy = c.Key == "easy";
        double[] t = easy ? [20, 50, 80, 110, 140, 170] : [200, 235, 270, 320, 360, 400];
        double[] o = easy ? [240, 600, 960, 1320, 1680, 2040] : [2400, 2820, 3240, 3840, 4320, 4800];
        var total = c.Cats.Sum(cat =>
            cat.Subs.SelectMany(s => s.Scenarios).Select(s => (S: s, P: PosOf(s)))
                .OrderByDescending(x => x.P.Precise).Take(4)
                .Sum(x => Weighted(x.S.Score, x.S.RankMaxes, t, 3 / 2.0)));
        return Thresholds(total, o);
    }

    /// <summary>evxl's we: piecewise-linear points for a score against the scenario's rank maxes.</summary>
    static double Weighted(double e, IReadOnlyList<double> n, double[] r, double t)
    {
        if (n.Count < 2 || r.Length < 2 || n.Count != r.Length) return 0;
        if (e < n[1])
        {
            var s = n[1] - n[0];
            var c = (r[2] - r[1]) / (2.0 / 3);
            return Math.Max(0, Math.Ceiling(r[1] + (e - n[1]) / s * c));
        }
        for (var s = 1; s < n.Count - 1; s++)
            if (e <= n[s + 1]) return r[s] + (e - n[s]) / (n[s + 1] - n[s]) * (r[s + 1] - r[s]);
        var o = n.Count - 1;
        return r[o] + (e - n[o]) / (n[o] - n[o - 1]) * (r[o] - r[o - 1]) / t;
    }

    static RankResult CbS1(Ctx c)
    {
        double[] r = [300, 600, 900, 1200, 1500, 1800];
        double sum = 0;
        foreach (var s in c.All)
        {
            var p = PosOf(s);
            double m;
            if (p.Base == 0) m = s.Score / (s.RankMaxes.Count > 0 ? s.RankMaxes[0] : double.NaN) * r[0];
            else if (p.Maxed && p.Base == s.RankMaxes.Count)
            {
                var last = s.RankMaxes[p.Base - 1];
                var prev = p.Base > 1 ? s.RankMaxes[p.Base - 2] : 0;
                var span = Math.Abs(last - prev);
                if (span == 0) span = 1;
                var idx = Math.Min(p.Base + Math.Max(0, (s.Score - last) / span), r.Length - 1);
                m = idx == Math.Floor(idx) ? r[(int)idx] : double.NaN;
            }
            else m = r[p.Base - 1] + (r[p.Base] - r[p.Base - 1]) * p.Progress;
            sum += m / 1800 * 100;
        }
        return Thresholds(sum, r);
    }

    static RankResult AplusS1(Ctx c)
    {
        var basic = Basic(c);
        double plus = double.PositiveInfinity;
        foreach (var (_, subs) in c.Cats)
        {
            var ranks = subs.SelectMany(s => s.Scenarios).Select(s => s.Rank).Where(x => x > 0).OrderDescending().ToList();
            plus = Math.Min(plus, ranks.Count >= 3 ? ranks[2] : 0);
        }
        if (double.IsInfinity(plus)) plus = 0;
        return new((int)Math.Max(basic.Rank, plus), basic.Progress);
    }

    static RankResult Tsk(Ctx c)
    {
        var data = c.All.Select(s => (Pos: PosOf(s), S: s)).ToList();
        var overMax = data.Count(d => d.Pos.Maxed && d.Pos.Base == d.S.RankMaxes.Count && d.S.Score > d.S.RankMaxes[^1]);
        var counts = new Dictionary<int, int>();
        foreach (var d in data) if (d.Pos.Base > 0) counts[d.Pos.Base] = counts.GetValueOrDefault(d.Pos.Base) + 1;
        (int? OverMax, int? MaxRank, int Needed)? cfg = c.Key switch
        {
            "beginner" => (null, null, 4),
            "main" => (null, null, 8),
            "ultimate" => (8, 10, 12),
            "static" => (null, null, 9),
            "strafes" => (null, null, 7),
            "thundah's bouncesphere" => (2, 2, 4),
            "reactive by slapped" => (2, 4, 6),
            "beginner classic" => (null, null, 5),
            "main classic" => (null, null, 7),
            "extra classic" => (3, 4, 9),
            _ => null,
        };
        if (cfg is not { } a) return Basic(c);
        var top = c.RankNames.Count > 0 ? c.RankNames.Count - 1 : Math.Max(0, data.Max(d => d.S.RankMaxes.Count));
        int Threshold(int k)
        {
            foreach (var r in counts.Keys.OrderDescending())
                if (counts.Where(kv => kv.Key >= r).Sum(kv => kv.Value) >= k) return r;
            return 0;
        }
        double Next(int k, int m)
        {
            if (m == 0) return 0;
            if (k >= top) return 1;
            return Math.Min((double)counts.Where(kv => kv.Key >= k + 1 && kv.Key <= top).Sum(kv => kv.Value) / m, 1);
        }
        int g = 0;
        double d2 = 0;
        if (a.OverMax is { } om && overMax >= om) { g = c.RankNames.Count > 0 ? c.RankNames.Count - 1 : 10; d2 = 1; }
        else if (a.MaxRank is { } mr)
        {
            var k = data.Where(d => d.S.RankMaxes.Count > 0 && d.Pos.Base == d.S.RankMaxes.Count).ToList();
            if (k.Count >= mr) { g = k.Max(d => d.Pos.Base); if (g >= top) d2 = 1; }
            else { g = Threshold(a.Needed); if (g > 0) d2 = Next(g, a.Needed); }
        }
        else { g = Threshold(a.Needed); if (g > 0) d2 = Next(g, a.Needed); }
        return new(g, d2);
    }

    static RankResult Xyz(Ctx c)
    {
        var groups = c.Subs.Select(s => s.Scenarios.Select(x => x.Rank).ToList()).ToList();
        var flat = groups.SelectMany(g => g).ToList();
        var maxLen = c.All.Count == 0 ? 0 : c.All.Max(s => s.RankMaxes.Count);
        var g0 = Math.Max(maxLen, c.Tiers.Count);
        var l = flat.Count;
        if (g0 <= 0) return new(0, 0);
        if (l == 0) return new(0, 0);
        if (flat.All(x => x == g0)) return new(g0, 1);
        var newcomer = c.Key == "newcomer";
        var d = groups.Count;
        const int k = 4, m = 3, R = 2;
        var f = 0;
        for (var y = g0; y >= 1; y--)
        {
            if (newcomer)
            {
                if (groups.All(g => g.Count(q => q >= y) >= R) && flat.Count(q => q >= y) >= R * d) { f = y; break; }
                continue;
            }
            var x = y == g0 ? m : k;
            if (groups.All(g => g.Count(q => q >= y) >= x) && flat.Count(q => q >= y) >= x * d) { f = y; break; }
        }
        static double H(double v) => Math.Clamp(v, 0, 1);
        if (newcomer)
        {
            if (f >= g0)
            {
                var n = flat.Count(q => q >= g0);
                var v = Math.Min(R * d, l);
                var w = l - v;
                return new(g0, w > 0 ? H((double)(n - v) / w) : 1);
            }
            var y2 = f == 0 ? 1 : f + 1;
            var cap = R * d;
            var t = groups.Sum(g => Math.Min(g.Count(q => q >= y2), R));
            return new(f, cap > 0 ? H((double)t / cap) : 0);
        }
        if (f >= g0)
        {
            var n = flat.Count(q => q >= g0);
            var cap = Math.Min(m * d, l);
            var x = l - cap;
            return new(g0, x > 0 ? H((double)(n - cap) / x) : 1);
        }
        var b = f == 0 ? 1 : f + 1;
        var need = b == g0 ? m : k;
        var total = need * d;
        var have = groups.Sum(g => Math.Min(g.Count(q => q >= b), need));
        return new(f, total > 0 ? H((double)have / total) : 0);
    }

    static RankResult Xyz2(Ctx c)
    {
        int[]? order = c.Key switch
        {
            "easy" => [12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1],
            "hard" => [10, 9, 8, 7, 6, 5, 4, 3, 2, 1],
            _ => null,
        };
        if (order == null) return new(0, 0);
        var ranks = new List<List<int>>();
        var poss = new List<List<Pos>>();
        foreach (var (_, subs) in c.Cats)
        {
            var r = new List<int>();
            var p = new List<Pos>();
            foreach (var s in subs.SelectMany(x => x.Scenarios))
                if (s.Rank > 0) { var pos = PosOf(s); r.Add(pos.Base); p.Add(pos); }
            ranks.Add(r); poss.Add(p);
        }
        var total = c.All.Count;
        var flat = ranks.SelectMany(r => r).ToList();
        var g = order[0];
        if (total > 0 && flat.Count == total && flat.All(x => x == g)) return new(g, 1);
        if (ranks.All(r => r.Count(x => x == g) >= 3)) return new(g, 1);
        var fourth = new List<int>();
        foreach (var r in ranks)
        {
            if (r.Count < 4) return new(0, 0);
            fourth.Add(r.OrderDescending().ElementAt(3));
        }
        var m = fourth.Min();
        double sum = 0;
        var n = 0;
        foreach (var p in poss)
        {
            if (p.Count < 4) continue;
            double mm = 0;
            foreach (var x in p.OrderByDescending(q => q.Base).Take(4))
            {
                if (x.Precise < m) continue;
                if (x.Precise >= m + 1) { mm += 1; continue; }
                mm += x.Precise - m;
            }
            sum += mm / 4;
            n++;
        }
        return new(m, n > 0 ? Math.Clamp(sum / n, 0, 1) : 0);
    }

    static RankResult XyzSmoothness(Ctx c)
    {
        var t = c.All.Count;
        var o = c.Tiers.Count;
        const int s = 9, cc = 6;
        var a = t;
        if (t == 0 || o <= 0) return new(0, 0);
        var ranks = c.All.Select(x =>
        {
            if (x.Score <= 0) return 0;
            return x.RankMaxes.Count > 0 ? Math.Max(0, PosOf(x).Base) : Math.Max(0, x.Rank);
        }).ToList();
        int Count(int r) => ranks.Count(x => x >= r);
        var g = Count(o);
        if (a > 0 && g >= a) return new(o, 1);
        if (g >= cc) return new(o, a - cc > 0 ? Math.Clamp((double)(g - cc) / (a - cc), 0, 1) : 1);
        var h = 0;
        for (var f = o - 1; f >= 1; f--) if (Count(f) >= s) { h = f; break; }
        var d = h + 1;
        var k = d >= o ? cc : s;
        var m = d <= o ? Count(d) : 0;
        return new(h, k > 0 ? Math.Clamp((double)m / k, 0, 1) : 0);
    }

    static RankResult Aoi(Ctx c)
    {
        var lists = c.Subs.Select(sub => sub.Scenarios.Where(s => s.Score > 0).Select(s => s.Rank).ToList()).ToList();
        var distinct = lists.SelectMany(l => l).Where(x => x > 0).Distinct().OrderDescending().ToList();
        var rank = 0;
        foreach (var i in distinct)
        {
            var one = lists.Count(l => l.Count(x => x >= i) >= 1);
            var two = lists.Count(l => l.Count(x => x >= i) >= 2);
            if (one >= 4 || two >= 3) { rank = i; break; }
        }
        double progress = 0;
        if (rank > 0 && distinct.Count > 0 && rank < distinct[0])
        {
            var next = rank + 1;
            var one = lists.Count(l => l.Count(x => x >= next) >= 1);
            var two = lists.Count(l => l.Count(x => x >= next) >= 2);
            progress = Math.Max(Math.Min(one / 4.0, 1), Math.Min(two / 3.0, 1));
        }
        return new(rank, progress);
    }

    static RankResult Aoi2(Ctx c)
    {
        var required = c.Diff == "輝" ? 1 : c.All.Count;
        var done = c.All.Count(s => s.Score > 0 && s.Rank > 0);
        return Thresholds(done, [required]);
    }

    static RankResult Aimbeast(Ctx c)
    {
        var anyPlayed = false;
        var unrankedCategory = false;
        double total = 0;
        var count = 0;
        foreach (var (_, subs) in c.Cats)
        {
            double sum = 0;
            var ranked = 0;
            var anyUnranked = false;
            foreach (var s in subs.SelectMany(x => x.Scenarios))
            {
                var m = s.Score > 0 ? s.Rank : 0;
                if (s.Score > 0) anyPlayed = true;
                if (m <= 0) anyUnranked = true; else { sum += m; ranked++; }
            }
            var f = anyUnranked || ranked == 0 ? 0 : sum / ranked;
            if (f <= 0) unrankedCategory = true;
            total += f; count++;
        }
        var avg = count > 0 ? total / count : 0;
        var rank = anyPlayed && !unrankedCategory ? (int)Math.Floor(avg) : 0;
        var top = c.RankNames.Count > 0 ? c.RankNames.Count - 1 : 0;
        var progress = anyPlayed && !unrankedCategory ? Math.Clamp(avg - rank, 0, 1) : 0;
        if (rank >= top && top > 0) { rank = top; progress = 1; }
        return new(rank, progress);
    }

    static RankResult AimbeastPartial(Ctx c)
    {
        var needed = (int)Math.Ceiling(c.All.Count / 2.0);
        double total = 0;
        var count = 0;
        var ranked = 0;
        foreach (var (_, subs) in c.Cats)
        {
            double sum = 0;
            var n = 0;
            foreach (var s in subs.SelectMany(x => x.Scenarios))
            {
                var p = PosOf(s);
                if (p.Base > 0) { sum += p.Precise; n++; ranked++; }
            }
            var b = n > 0 ? sum / n : 0;
            if (b > 0) { total += b; count++; }
        }
        var enough = needed > 0 && ranked >= needed;
        var avg = count > 0 ? total / count : 0;
        var rank = enough ? (int)Math.Floor(avg) : 0;
        var top = c.RankNames.Count > 0 ? c.RankNames.Count - 1 : 0;
        var progress = enough ? Math.Clamp(avg - rank, 0, 1) : 0;
        if (rank >= top && top > 0) { rank = top; progress = 1; }
        return new(rank, progress);
    }
}
