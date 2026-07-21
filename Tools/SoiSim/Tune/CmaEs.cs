using System;

namespace SoiSim
{
    /// <summary>Separable (diagonal-covariance) CMA-ES. Chosen over full CMA-ES on
    /// purpose: fitness here is a noisy winrate (±5-7% at 200 games), where covariance
    /// learning barely pays, and the diagonal variant needs no eigendecomposition.
    /// Operates in NORMALIZED space — callers scale per-dimension (weight magnitudes
    /// span 0.02 … 2000). Deterministic given the seed.</summary>
    public sealed class CmaEs
    {
        private readonly int _n;
        private readonly int _lambda;
        private readonly int _mu;
        private readonly double[] _selectionWeights;
        private readonly double _muEff, _cSigma, _dSigma, _cCov1, _cCovMu, _chiN;

        private readonly double[] _mean;
        private readonly double[] _variance;       // diagonal of C
        private readonly double[] _pathSigma;
        private readonly double[] _pathCov;
        private double _sigma;
        private readonly Random _rng;

        public double[] Mean => _mean;
        public double Sigma => _sigma;

        public CmaEs(double[] initialMean, double sigma, int lambda, int seed)
        {
            _n = initialMean.Length;
            _lambda = lambda;
            _mu = lambda / 2;
            _mean = (double[])initialMean.Clone();
            _sigma = sigma;
            _rng = new Random(seed);

            _selectionWeights = new double[_mu];
            double sum = 0;
            for (int i = 0; i < _mu; i++)
            {
                _selectionWeights[i] = Math.Log(_mu + 0.5) - Math.Log(i + 1);
                sum += _selectionWeights[i];
            }
            double sumSq = 0;
            for (int i = 0; i < _mu; i++)
            {
                _selectionWeights[i] /= sum;
                sumSq += _selectionWeights[i] * _selectionWeights[i];
            }
            _muEff = 1.0 / sumSq;

            _cSigma = (_muEff + 2) / (_n + _muEff + 5);
            _dSigma = 1 + 2 * Math.Max(0, Math.Sqrt((_muEff - 1) / (_n + 1)) - 1) + _cSigma;
            _cCov1 = 2.0 / ((_n + 1.3) * (_n + 1.3) + _muEff);
            _cCovMu = Math.Min(1 - _cCov1,
                2 * (_muEff - 2 + 1.0 / _muEff) / ((_n + 2) * (_n + 2) + _muEff));
            // sep-CMA speedup: diagonal C may learn ~(n+2)/3 times faster.
            double sepFactor = (_n + 2) / 3.0;
            _cCov1 = Math.Min(1, _cCov1 * sepFactor);
            _cCovMu = Math.Min(1 - _cCov1, _cCovMu * sepFactor);
            _chiN = Math.Sqrt(_n) * (1 - 1.0 / (4.0 * _n) + 1.0 / (21.0 * _n * _n));

            _variance = new double[_n];
            _pathSigma = new double[_n];
            _pathCov = new double[_n];
            for (int i = 0; i < _n; i++) _variance[i] = 1.0;
        }

        private double NextGaussian()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>Sample λ candidates (rows) around the current mean.</summary>
        public double[][] Ask()
        {
            var pop = new double[_lambda][];
            for (int k = 0; k < _lambda; k++)
            {
                var x = new double[_n];
                for (int i = 0; i < _n; i++)
                    x[i] = _mean[i] + _sigma * Math.Sqrt(_variance[i]) * NextGaussian();
                pop[k] = x;
            }
            return pop;
        }

        /// <summary>Update from the sampled population and their fitness (HIGHER is
        /// better). Standard sep-CMA-ES mean/step/variance adaptation.</summary>
        public void Tell(double[][] population, double[] fitness)
        {
            var order = new int[population.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => fitness[b].CompareTo(fitness[a]));

            var oldMean = (double[])_mean.Clone();
            for (int i = 0; i < _n; i++)
            {
                double m = 0;
                for (int k = 0; k < _mu; k++)
                    m += _selectionWeights[k] * population[order[k]][i];
                _mean[i] = m;
            }

            // Evolution paths (diagonal: componentwise normalization).
            double psNormSq = 0;
            for (int i = 0; i < _n; i++)
            {
                double y = (_mean[i] - oldMean[i]) / (_sigma * Math.Sqrt(_variance[i]));
                _pathSigma[i] = (1 - _cSigma) * _pathSigma[i] +
                                Math.Sqrt(_cSigma * (2 - _cSigma) * _muEff) * y;
                psNormSq += _pathSigma[i] * _pathSigma[i];
            }
            double psNorm = Math.Sqrt(psNormSq);
            bool hsig = psNorm / Math.Sqrt(1 - Math.Pow(1 - _cSigma, 2)) / _chiN < 1.4 + 2.0 / (_n + 1);

            for (int i = 0; i < _n; i++)
            {
                double yMean = (_mean[i] - oldMean[i]) / _sigma;
                _pathCov[i] = (1 - _cCov1) * _pathCov[i];
                if (hsig)
                    _pathCov[i] += Math.Sqrt(_cCov1 * (2 - _cCov1) * _muEff) * yMean / Math.Sqrt(_variance[i]);

                double rankMu = 0;
                for (int k = 0; k < _mu; k++)
                {
                    double yk = (population[order[k]][i] - oldMean[i]) / _sigma;
                    rankMu += _selectionWeights[k] * yk * yk / _variance[i];
                }
                _variance[i] = (1 - _cCov1 - _cCovMu) * _variance[i] +
                               _cCov1 * _pathCov[i] * _pathCov[i] * _variance[i] +
                               _cCovMu * rankMu * _variance[i];
                _variance[i] = Math.Max(1e-8, _variance[i]);
            }

            _sigma *= Math.Exp(_cSigma / _dSigma * (psNorm / _chiN - 1));
            _sigma = Math.Min(_sigma, 2.0); // noisy fitness: cap runaway step sizes
        }
    }
}
