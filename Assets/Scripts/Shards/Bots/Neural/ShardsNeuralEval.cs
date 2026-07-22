using System;
using System.Numerics;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>
    /// The trained value net as a hand-rolled MLP forward pass — pure C#, works in
    /// net8 (SIMD via Vector&lt;float&gt;) and Unity Mono/IL2CPP (scalar fallback).
    /// Weights come from the generated ShardsNetWeights blob (f16, dequantized at
    /// load; sha256 + schema verified). Thread-safe: scratch buffers are ThreadStatic,
    /// so root-parallel workers evaluate concurrently without allocation churn.
    /// </summary>
    public sealed class ShardsNeuralEval : IShardsValueEvaluator
    {
        private readonly float[][] _weights; // per layer, row-major [out][in]
        private readonly float[][] _biases;
        private readonly int[] _dims;        // input, hidden…, 1
        // All layers except the LAST are stored TRANSPOSED in one flat block per layer
        // ([in][out] row-major, row i at i*outDim), and computed by accumulating only
        // nonzero inputs' rows: encoded features are ~86% zeros and post-ReLU hidden
        // activations ~half zeros. Profiling showed the dense forward pass was ~72% of
        // self-play CPU, dominated by streaming weights for inputs that were zero.
        // Jagged on purpose: one heap array per input row. A single flat block with
        // power-of-two row stride (2KB for 768→512) measured ~15% SLOWER — strided
        // rows land on the same cache sets; independent allocations spread them out.
        private readonly float[][][] _rowsByLayer; // [layer][in][out]; unused for the last
        private readonly int _scratchSize;   // widest hidden layer of THIS net

        [ThreadStatic] private static float[] _scratchIn;
        [ThreadStatic] private static float[] _scratchA;
        [ThreadStatic] private static float[] _scratchB;

        /// <summary>Loads the CURRENT embedded net; throws if none is available —
        /// callers gate on ShardsNetWeights.Available.</summary>
        public static ShardsNeuralEval LoadCurrent()
        {
            if (!ShardsNetWeights.Available)
                throw new InvalidOperationException("no trained net embedded (ShardsNetWeights.Available is false)");
            return new ShardsNeuralEval(
                Convert.FromBase64String(ShardsNetWeights.CurrentBlob),
                ShardsNetWeights.Layers, ShardsNetWeights.SchemaVersion, ShardsNetWeights.Sha256);
        }

        /// <summary>Loads a specific FROZEN generation — minted ranks pin these so a
        /// rank's strength never drifts when newer nets land.</summary>
        public static ShardsNeuralEval LoadGeneration(int generation)
        {
            foreach (var spec in ShardsNetWeights.All)
                if (spec.Generation == generation)
                    return new ShardsNeuralEval(Convert.FromBase64String(spec.Blob),
                        spec.Layers, spec.SchemaVersion, spec.Sha256);
            throw new InvalidOperationException($"net generation {generation} is not embedded");
        }

        public ShardsNeuralEval(byte[] f16Blob, int[] layers, int schemaVersion, string sha256)
        {
            if (schemaVersion != ShardsStateEncoder.SchemaVersion)
                throw new InvalidOperationException(
                    $"net schema v{schemaVersion} != encoder v{ShardsStateEncoder.SchemaVersion} — retrain required");
            if (!string.IsNullOrEmpty(sha256))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = sha.ComputeHash(f16Blob);
                var hex = new System.Text.StringBuilder(64);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                if (!string.Equals(hex.ToString(), sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("net blob sha256 mismatch — corrupted emit");
            }

            _dims = new int[layers.Length + 1];
            _dims[0] = ShardsStateEncoder.FeatureCount;
            for (int i = 0; i < layers.Length; i++)
                _dims[i + 1] = layers[i];

            _weights = new float[layers.Length][];
            _biases = new float[layers.Length][];
            int offset = 0;
            for (int layer = 0; layer < layers.Length; layer++)
            {
                int inDim = _dims[layer], outDim = _dims[layer + 1];
                _weights[layer] = DecodeF16(f16Blob, ref offset, inDim * outDim);
                _biases[layer] = DecodeF16(f16Blob, ref offset, outDim);
            }
            if (offset != f16Blob.Length)
                throw new InvalidOperationException($"net blob size mismatch: consumed {offset} of {f16Blob.Length}");

            _rowsByLayer = new float[_weights.Length][][];
            for (int layer = 0; layer < _weights.Length - 1; layer++)
            {
                int inDim = _dims[layer], outDim = _dims[layer + 1];
                var rows = new float[inDim][];
                for (int i = 0; i < inDim; i++)
                {
                    var row = new float[outDim];
                    for (int o = 0; o < outDim; o++)
                        row[o] = _weights[layer][o * inDim + i];
                    rows[i] = row;
                }
                _rowsByLayer[layer] = rows;
            }
            _scratchSize = 0;
            for (int k = 1; k < _dims.Length; k++)
                _scratchSize = Math.Max(_scratchSize, _dims[k]);

            // Lock-free encoding under multi-threaded search/selfplay.
            ShardsStateEncoder.Prewarm();
        }

        public double Evaluate(ShardsState state, int playerIndex)
        {
            _scratchIn ??= new float[ShardsStateEncoder.FeatureCount];
            ShardsStateEncoder.Encode(state, playerIndex, _scratchIn);
            return Forward(_scratchIn);
        }

        /// <summary>Sigmoid win-prob for a pre-encoded feature vector (parity tests).</summary>
        public double Forward(float[] input)
        {
            if (_scratchA == null || _scratchA.Length < _scratchSize)
            {
                _scratchA = new float[_scratchSize];
                _scratchB = new float[_scratchSize];
            }

            // Hidden layers: sparse — bias, then accumulate one transposed row per
            // NONZERO input unit (encoded features ~86% zero, ReLU outputs ~half zero),
            // then ReLU in place. The accumulator stays L1-cache-resident.
            var current = input;
            var next = _scratchA;
            for (int layer = 0; layer < _weights.Length - 1; layer++)
            {
                int inDim = _dims[layer], outDim = _dims[layer + 1];
                var rows = _rowsByLayer[layer];
                Array.Copy(_biases[layer], next, outDim);
                for (int i = 0; i < inDim; i++)
                {
                    float xi = current[i];
                    if (xi != 0)
                        AddScaled(next, rows[i], 0, xi, outDim);
                }
                for (int o = 0; o < outDim; o++)
                    next[o] = Math.Max(0, next[o]); // ReLU
                var tmp = current == input ? _scratchB : current;
                current = next;
                next = tmp;
            }

            // Output layer: one dense dot.
            {
                int layer = _weights.Length - 1;
                int inDim = _dims[layer], outDim = _dims[layer + 1];
                var w = _weights[layer];
                var b = _biases[layer];
                for (int o = 0; o < outDim; o++)
                    next[o] = Dot(w, o * inDim, current, inDim) + b[o];
                current = next;
            }
            return 1.0 / (1.0 + Math.Exp(-current[0]));
        }

        /// <summary>acc[0..n) += w[offset..offset+n) * s — SIMD when available,
        /// mirroring Dot's flat-block addressing.</summary>
        private static void AddScaled(float[] acc, float[] w, int offset, float s, int n)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                int width = Vector<float>.Count;
                var vs = new Vector<float>(s);
                for (; i <= n - width; i += width)
                    (new Vector<float>(acc, i) + new Vector<float>(w, offset + i) * vs).CopyTo(acc, i);
            }
            for (; i < n; i++)
                acc[i] += w[offset + i] * s;
        }

        private static float Dot(float[] w, int wOffset, float[] x, int n)
        {
            float sum = 0;
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                int width = Vector<float>.Count;
                var acc = Vector<float>.Zero;
                for (; i <= n - width; i += width)
                    acc += new Vector<float>(w, wOffset + i) * new Vector<float>(x, i);
                sum = Vector.Dot(acc, Vector<float>.One);
            }
            for (; i < n; i++)
                sum += w[wOffset + i] * x[i];
            return sum;
        }

        /// <summary>IEEE 754 half → float (Unity has no System.Half; identical bits
        /// everywhere beats a BCL dependency).</summary>
        private static float[] DecodeF16(byte[] blob, ref int offset, int count)
        {
            var result = new float[count];
            for (int i = 0; i < count; i++)
            {
                ushort h = (ushort)(blob[offset] | (blob[offset + 1] << 8));
                offset += 2;
                result[i] = HalfToFloat(h);
            }
            return result;
        }

        private static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 1;
            int exp = (h >> 10) & 0x1F;
            int mant = h & 0x3FF;
            int bits;
            if (exp == 0)
            {
                if (mant == 0)
                    bits = sign << 31;
                else
                {
                    // subnormal half → normalized float
                    int e = -1;
                    do
                    {
                        e++;
                        mant <<= 1;
                    } while ((mant & 0x400) == 0);
                    mant &= 0x3FF;
                    bits = (sign << 31) | ((127 - 15 - e) << 23) | (mant << 13);
                }
            }
            else if (exp == 31)
            {
                bits = (sign << 31) | (0xFF << 23) | (mant << 13); // inf/nan
            }
            else
            {
                bits = (sign << 31) | ((exp - 15 + 127) << 23) | (mant << 13);
            }
            return BitConverter.Int32BitsToSingle(bits);
        }
    }
}
