using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Shards.AI
{
    /// <summary>CPU inference for the frozen generic-effect policy. No optimizer or hidden game state.</summary>
    public sealed class FrozenPolicy
    {
        private readonly Dictionary<string, float[]> _weights = new();
        private readonly int _width;
        private readonly float _clip;
        private readonly bool _valueTanh;
        public FrozenPolicy(byte[] bytes)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes));
            if (reader.ReadInt32() != 0x534F4931) throw new InvalidDataException("Unknown Shards policy");
            _width = reader.ReadInt32(); _clip = reader.ReadSingle(); _valueTanh = reader.ReadBoolean();
            int entries = reader.ReadInt32();
            for (int i = 0; i < entries; i++)
            {
                string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                int count = reader.ReadInt32();
                var values = new float[count];
                for (int j = 0; j < count; j++) values[j] = reader.ReadSingle();
                _weights.Add(name, values);
            }
            if (_width != 128 || reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Unsupported policy dimensions or trailing data");
        }

        private float Numeric(float x)
        {
            x = Math.Max(-_clip, Math.Min(_clip, x));
            return (float)(Math.Sign(x) * Math.Log(1.0 + Math.Abs(x)));
        }
        private static float Silu(float x) => (float)(x / (1.0 + Math.Exp(-x)));
        private float[] Linear(string name, float[] input, int output, int offset = 0, int length = -1)
        {
            if (length < 0) length = input.Length - offset;
            var weight = _weights[name + ".weight"];
            _weights.TryGetValue(name + ".bias", out var bias);
            var result = new float[output];
            for (int row = 0; row < output; row++)
            {
                double sum = bias == null ? 0 : bias[row];
                for (int col = 0; col < length; col++) sum += (double)weight[row * length + col] * input[offset + col];
                result[row] = (float)sum;
            }
            return result;
        }

        /// <summary>Returns the exact stochastic behavior distribution used in frozen evaluation.</summary>
        public float[] Probabilities(float[] observation, float[] candidates, float[] mask, out float value)
        {
            if (observation.Length != 3328 || candidates.Length != 2048 || mask.Length != 64)
                throw new ArgumentException("Policy observation dimensions differ from the export");
            var x = new float[3328];
            for (int i = 0; i < x.Length; i++) x[i] = Numeric(observation[i]);
            var semantic = _weights["semantic_table"];
            var embeddings = _weights["combined_embeddings"];
            // Older exports retain their original 11-zone projection. New exports
            // additionally represent four known center cards in top-to-bottom order.
            int contextWidth = _weights["core.effect_state.weight"].Length / _width;
            var context = new float[contextWidth];
            for (int zone = 0; zone < 10; zone++)
                for (int card = 0; card < 189; card++)
                {
                    float count = observation[zone < 9 ? 128 + zone * 192 + card : 2560 + card];
                    if (count == 0) continue;
                    for (int k = 0; k < 64; k++) context[zone * 64 + k] += count * semantic[(card + 1) * 64 + k];
                }
            for (int relic = 3005; relic < 3008; relic++)
            {
                int id = CardId(observation[relic]);
                for (int k = 0; k < 64; k++) context[640 + k] += semantic[id * 64 + k];
            }
            if (contextWidth == 960)
                for (int top = 0; top < 4; top++)
                {
                    int id = CardId(observation[HeroFeatures.Slots[top * 3]]);
                    if (id == 0) continue;
                    Array.Copy(semantic, id * 64, context, 704 + top * 64, 64);
                }
            else if (contextWidth != 704) throw new InvalidOperationException("Unknown semantic context schema");
            var pooled = new float[64]; int legalCount = 0, relicCount = 0, destinyCount = 0;
            for (int a = 0; a < 64; a++) if (mask[a] != 0)
            {
                legalCount++;
                if (candidates[a * 32 + 7] == 1) relicCount++;
                if (candidates[a * 32 + 6] == 1) destinyCount++;
                int id = CardId(candidates[a * 32 + 16]);
                for (int k = 0; k < 64; k++) pooled[k] += embeddings[id * 64 + k];
            }
            if (legalCount == 0) throw new InvalidOperationException("No legal policy action");
            for (int k = 0; k < 64; k++) pooled[k] /= legalCount;
            var hidden = Linear("core.trunk.0", x, _width, 0, 2048);
            for (int k = 0; k < _width; k++) hidden[k] = Silu(hidden[k]);
            var state = Linear("core.trunk.2", hidden, _width);
            var info = Linear("core.information", x, _width, 2048);
            var menu = Linear("core.menu_information", pooled, _width);
            var effects = Linear("core.effect_state", context, _width);
            for (int k = 0; k < _width; k++) state[k] = Silu(state[k] + info[k] + menu[k] + effects[k]);
            var query = Linear("core.query", state, 64);
            value = Linear("core.value", state, 1)[0];
            if (_valueTanh) value = (float)Math.Tanh(value);
            var volosModes = Linear("core.volos_head", state, 4);
            var decisionInput = new float[_width + 1280];
            Array.Copy(state, decisionInput, _width); Array.Copy(x, 2048, decisionInput, _width, 1280);
            var modes = Linear("core.decision_head", decisionInput, 16);
            bool volos = true;
            for (int k = 0; k < 4; k++) volos &= observation[112 + k] == _weights["volos_context"][k];
            bool typed = volos || observation[5] == 3f / 8 || observation[5] == 5f / 8;
            var probabilities = new float[64]; float max = float.NegativeInfinity;
            for (int a = 0; a < 64; a++)
            {
                if (mask[a] == 0) { probabilities[a] = float.NegativeInfinity; continue; }
                var c = new float[32];
                for (int k = 0; k < 32; k++) c[k] = k < 17 ? candidates[a * 32 + k] : Numeric(candidates[a * 32 + k]);
                int id = CardId(c[16]);
                var key = Linear("core.candidate.0", c, 64);
                double score = 0;
                for (int k = 0; k < 64; k++) score += (Silu(key[k]) + embeddings[id * 64 + k]) * (double)query[k];
                score = score / 8 + Linear("core.candidate_bias", c, 1)[0];
                int ready = (int)_weights["readiness_index"][id];
                if (ready >= 0 && observation[ready] == 0 && c[4] == 1) score -= _weights["readiness_strength"][0];
                int ordinal = (int)Math.Round(candidates[a * 32 + 25] * 128);
                if (c[12] == 1)
                {
                    if (volos) score += volosModes[Math.Max(0, Math.Min(3, ordinal))];
                    if (typed && ordinal >= 0 && ordinal < 16) score += modes[ordinal];
                }
                for (int k = 0; k < 16; k++) score += c[k] * _weights["action_kind_bias"][k];
                probabilities[a] = (float)score; max = Math.Max(max, (float)score);
            }
            double sum = 0;
            for (int a = 0; a < 64; a++) { probabilities[a] = (float)Math.Exp(probabilities[a] - max); sum += probabilities[a]; }
            float ar = relicCount > 0 ? _weights["choice_exploration"][0] : 0;
            float ad = destinyCount > 0 ? _weights["choice_exploration"][1] : 0;
            for (int a = 0; a < 64; a++) if (mask[a] != 0)
                probabilities[a] = (float)(probabilities[a] / sum) * (1 - ar - ad)
                    + (candidates[a * 32 + 7] == 1 ? ar / Math.Max(1, relicCount) : 0)
                    + (candidates[a * 32 + 6] == 1 ? ad / Math.Max(1, destinyCount) : 0);
            return probabilities;
        }
        private static int CardId(float code) => Math.Max(0, Math.Min(192, (int)Math.Round(code * 192)));
    }
}
