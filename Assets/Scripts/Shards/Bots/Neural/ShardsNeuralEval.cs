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
            _scratchA ??= new float[512];
            _scratchB ??= new float[512];
            var current = input;
            var next = _scratchA;
            for (int layer = 0; layer < _weights.Length; layer++)
            {
                int inDim = _dims[layer], outDim = _dims[layer + 1];
                var w = _weights[layer];
                var b = _biases[layer];
                bool last = layer == _weights.Length - 1;
                for (int o = 0; o < outDim; o++)
                {
                    float sum = Dot(w, o * inDim, current, inDim) + b[o];
                    next[o] = last ? sum : Math.Max(0, sum); // ReLU on hidden layers
                }
                var tmp = current == input ? _scratchB : current;
                current = next;
                next = tmp;
            }
            return 1.0 / (1.0 + Math.Exp(-current[0]));
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
