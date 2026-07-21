using System;
using System.Collections.Generic;

namespace SoiSim
{
    /// <summary>Hand-rolled statistics — no external deps. Wilson intervals for every
    /// reported proportion, two-proportion z for per-card deltas, Benjamini-Hochberg
    /// for the ~90 simultaneous card tests, IRLS logistic regression for playstyles.</summary>
    public static class Stats
    {
        /// <summary>Wilson 95% score interval for a proportion.</summary>
        public static (double Lo, double Hi) Wilson(int successes, int n, double z = 1.959964)
        {
            if (n == 0) return (0, 1);
            double p = (double)successes / n;
            double z2 = z * z;
            double denom = 1 + z2 / n;
            double center = (p + z2 / (2 * n)) / denom;
            double half = z / denom * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n));
            return (Math.Max(0, center - half), Math.Min(1, center + half));
        }

        /// <summary>Standard normal CDF (Abramowitz-Stegun 7.1.26 via erf).</summary>
        public static double NormalCdf(double x) => 0.5 * (1 + Erf(x / Math.Sqrt(2)));

        public static double Erf(double x)
        {
            double sign = Math.Sign(x);
            x = Math.Abs(x);
            const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741,
                         a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
            double t = 1.0 / (1.0 + p * x);
            double y = 1.0 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
            return sign * y;
        }

        /// <summary>Two-sided p-value for a z statistic.</summary>
        public static double TwoSidedP(double z) => 2 * (1 - NormalCdf(Math.Abs(z)));

        /// <summary>Inverse-variance pooled difference of proportions across strata.
        /// Each stratum: (successes1, n1, successes2, n2). Strata where either side is
        /// empty are skipped. Returns (delta, se, z, p, effectiveN).</summary>
        public static (double Delta, double Se, double Z, double P, int N) PooledDelta(
            IEnumerable<(int S1, int N1, int S2, int N2)> strata)
        {
            double sumW = 0, sumWD = 0;
            int effN = 0;
            foreach (var (s1, n1, s2, n2) in strata)
            {
                if (n1 == 0 || n2 == 0) continue;
                double p1 = (double)s1 / n1, p2 = (double)s2 / n2;
                // Anscombe-adjusted variance keeps degenerate strata (p = 0 or 1) usable.
                double p1t = (s1 + 0.5) / (n1 + 1.0), p2t = (s2 + 0.5) / (n2 + 1.0);
                double v = p1t * (1 - p1t) / n1 + p2t * (1 - p2t) / n2;
                double w = 1.0 / v;
                sumW += w;
                sumWD += w * (p1 - p2);
                effN += n1 + n2;
            }
            if (sumW <= 0) return (0, double.PositiveInfinity, 0, 1, 0);
            double delta = sumWD / sumW;
            double se = Math.Sqrt(1.0 / sumW);
            double z = delta / se;
            return (delta, se, z, TwoSidedP(z), effN);
        }

        /// <summary>Benjamini-Hochberg: returns the significance flag per input index
        /// at the given false-discovery rate.</summary>
        public static bool[] BenjaminiHochberg(IReadOnlyList<double> pValues, double fdr = 0.10)
        {
            int m = pValues.Count;
            var order = new int[m];
            for (int i = 0; i < m; i++) order[i] = i;
            Array.Sort(order, (a, b) => pValues[a].CompareTo(pValues[b]));
            int cutoff = -1;
            for (int k = 0; k < m; k++)
                if (pValues[order[k]] <= (k + 1.0) / m * fdr)
                    cutoff = k;
            var significant = new bool[m];
            for (int k = 0; k <= cutoff; k++)
                significant[order[k]] = true;
            return significant;
        }

        /// <summary>Logistic regression via IRLS. X rows are observations (a leading 1
        /// intercept column is added here), y in {0,1}. Returns coefficients
        /// (intercept first) with Wald standard errors; null if it fails to converge.</summary>
        public static (double[] Beta, double[] Se)? Logistic(double[][] x, int[] y, int maxIter = 30)
        {
            int n = x.Length;
            if (n == 0) return null;
            int k = x[0].Length + 1;
            var beta = new double[k];

            for (int iter = 0; iter < maxIter; iter++)
            {
                // Build X'WX and X'Wz for the weighted least-squares step.
                var xtwx = new double[k, k];
                var xtwz = new double[k];
                for (int i = 0; i < n; i++)
                {
                    double eta = beta[0];
                    for (int j = 1; j < k; j++) eta += beta[j] * x[i][j - 1];
                    double mu = 1.0 / (1.0 + Math.Exp(-eta));
                    double w = Math.Max(mu * (1 - mu), 1e-9);
                    double z = eta + (y[i] - mu) / w;
                    for (int a = 0; a < k; a++)
                    {
                        double xa = a == 0 ? 1 : x[i][a - 1];
                        xtwz[a] += w * xa * z;
                        for (int b = a; b < k; b++)
                        {
                            double xb = b == 0 ? 1 : x[i][b - 1];
                            xtwx[a, b] += w * xa * xb;
                        }
                    }
                }
                for (int a = 0; a < k; a++)
                    for (int b = 0; b < a; b++)
                        xtwx[a, b] = xtwx[b, a];
                // Tiny ridge keeps near-collinear feature sets solvable.
                for (int a = 0; a < k; a++) xtwx[a, a] += 1e-8;

                var step = Solve(xtwx, xtwz, k);
                if (step == null) return null;
                double maxChange = 0;
                for (int a = 0; a < k; a++)
                {
                    maxChange = Math.Max(maxChange, Math.Abs(step[a] - beta[a]));
                    beta[a] = step[a];
                }
                if (maxChange < 1e-8) break;
                if (double.IsNaN(maxChange) || double.IsInfinity(maxChange)) return null;
            }

            // Wald SEs from the inverse of the final information matrix.
            var info = new double[k, k];
            for (int i = 0; i < n; i++)
            {
                double eta = beta[0];
                for (int j = 1; j < k; j++) eta += beta[j] * x[i][j - 1];
                double mu = 1.0 / (1.0 + Math.Exp(-eta));
                double w = Math.Max(mu * (1 - mu), 1e-9);
                for (int a = 0; a < k; a++)
                {
                    double xa = a == 0 ? 1 : x[i][a - 1];
                    for (int b = a; b < k; b++)
                    {
                        double xb = b == 0 ? 1 : x[i][b - 1];
                        info[a, b] += w * xa * xb;
                    }
                }
            }
            for (int a = 0; a < k; a++)
                for (int b = 0; b < a; b++)
                    info[a, b] = info[b, a];
            var se = new double[k];
            var inv = Invert(info, k);
            if (inv == null) return null;
            for (int a = 0; a < k; a++)
                se[a] = Math.Sqrt(Math.Max(0, inv[a, a]));
            return (beta, se);
        }

        private static double[] Solve(double[,] m, double[] v, int k)
        {
            var a = (double[,])m.Clone();
            var b = (double[])v.Clone();
            for (int col = 0; col < k; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < k; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                        pivot = r;
                if (Math.Abs(a[pivot, col]) < 1e-12) return null;
                if (pivot != col)
                {
                    for (int c = 0; c < k; c++)
                        (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                    (b[col], b[pivot]) = (b[pivot], b[col]);
                }
                for (int r = col + 1; r < k; r++)
                {
                    double f = a[r, col] / a[col, col];
                    for (int c = col; c < k; c++) a[r, c] -= f * a[col, c];
                    b[r] -= f * b[col];
                }
            }
            var x = new double[k];
            for (int r = k - 1; r >= 0; r--)
            {
                double s = b[r];
                for (int c = r + 1; c < k; c++) s -= a[r, c] * x[c];
                x[r] = s / a[r, r];
            }
            return x;
        }

        private static double[,] Invert(double[,] m, int k)
        {
            var result = new double[k, k];
            for (int col = 0; col < k; col++)
            {
                var e = new double[k];
                e[col] = 1;
                var x = Solve(m, e, k);
                if (x == null) return null;
                for (int r = 0; r < k; r++) result[r, col] = x[r];
            }
            return result;
        }

        public static double Percentile(List<int> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            double idx = p * (sorted.Count - 1);
            int lo = (int)Math.Floor(idx);
            int hi = (int)Math.Ceiling(idx);
            return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
        }
    }
}
