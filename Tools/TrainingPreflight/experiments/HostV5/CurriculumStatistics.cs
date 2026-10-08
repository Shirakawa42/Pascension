using System;
using Shards.Engine;

namespace Shards.Preflight
{
    // Two independent outcome pools. Both know the whole reset shape/seed, but
    // only the matching pool ever sees a lane action, outcome or unfinished game.
    internal sealed class CurriculumStatistics : IDisposable
    {
        private readonly TrainingStatistics _natural, _forced;
        private HeroCurriculum.Setup[] _setups;

        internal CurriculumStatistics(TrainingStatistics natural, TrainingStatistics forced)
        { _natural = natural; _forced = forced; }

        internal static CurriculumStatistics FromEnvironment(string mode)
        {
            HeroSetupRouting.Validate(mode);
            var natural = TrainingStatistics.FromEnvironment("natural-draft", HeroSetupRouting.Population(mode, false));
            var forced = mode == HeroSetupRouting.Curriculum ?
                TrainingStatistics.FromEnvironment("forced-random", HeroSetupRouting.Population(mode, true)) : null;
            return natural == null && forced == null ? null : new CurriculumStatistics(natural, forced);
        }

        internal void Reset(int count, ulong seed)
        {
            _natural?.Reset(count, seed); _forced?.Reset(count, seed);
            if (_setups == null || _setups.Length != count) _setups = new HeroCurriculum.Setup[count];
            else Array.Clear(_setups);
        }

        internal void SetSetup(int lane, HeroCurriculum.Setup setup) => _setups[lane] = setup;
        private TrainingStatistics Pool(int lane)
        {
            var setup = _setups[lane] ?? throw new InvalidOperationException("Statistics require the actual episode setup");
            return setup.Plan.Forced ? _forced : _natural;
        }
        internal TrainingStatistics.StepMark BeginStep(int lane, Adapter game, int choice)
            => Pool(lane)?.BeginStep(lane, game, choice) ?? default;
        internal void ObserveStep(int lane, ShardsEngine engine, TrainingStatistics.StepMark mark)
            => Pool(lane)?.ObserveStep(lane, engine, mark);
        internal void Finish(int lane, ShardsState state, bool terminal, ulong seed)
            => Pool(lane)?.Finish(lane, state, terminal, seed);
        internal void BatchBoundary() { _natural?.BatchBoundary(); _forced?.BatchBoundary(); }
        public void Dispose() { _natural?.Dispose(); _forced?.Dispose(); }
    }
}
