#if SHARDS_NATIVE_STATS
using Adapter = Shards.AI.Adapter;
using StatisticsEncoder = Shards.AI.Encoder;
#elif SHARDS_ZERO_DEPTH_STATS
using Adapter = Shards.ZeroDepth.Adapter;
using StatisticsEncoder = Shards.ZeroDepth.Encoder;
#else
using StatisticsEncoder = Shards.Preflight.Encoder;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Shards.Engine;

namespace Shards.Preflight
{
    // Observation-independent, outcome-only statistics. All inputs are already
    // public acquisition events or terminal state; nothing is fed to a policy.
    internal sealed class TrainingStatistics : IDisposable
    {
        internal static readonly string[] Kinds = { "buy", "fastplay", "relic", "destiny", "effect_acquire", "effect_fastplay" };
        private const int MaxLanes = 4096;
        private readonly int EveryCompleted;
        private readonly object _gate = new(), _publishGate = new();
        private readonly string _directory, _purpose, _session, _started, _binaryHash;
        private readonly object _heroSetup;
        private readonly ulong _expectedSeed;
        private readonly int _expectedBatch, _cards, _rows, _heroes;
        private readonly string[] _heroIds;
        private readonly Thread _writer;
        private readonly StrategyTrace _strategy;
        private readonly AutoResetEvent _wake = new(false);
        private Lane[] _lanes;
        private readonly Row[] _totals;
        private readonly Row[] _heroChoices;
        private readonly HeroRow[] _heroTotals;
        private readonly MatchupRow[] _matchups;
        private readonly long[] _roundHistogram = new long[402];
        private long _completed, _censored, _draws, _seat0Wins, _seat1Wins, _unfinished, _resets;
        private long _winnerMastery, _winnerHealth, _loserMastery, _decisive, _collectionSum;
        private long _nextPublish, _publicationSequence;
        private ulong _firstReset, _lastReset, _minSeed = ulong.MaxValue, _maxSeed;
        private bool _seenFinished, _disposed, _stopping;
        private bool _historyFailed;
        private volatile bool _failed;
        private int _failureRecorded;
        private Publication _pending;
        private object _pendingError;
        private sealed class Publication
        {
            internal object Value;
            internal long Completed, Sequence;
        }

        private sealed class Lane
        {
            internal readonly int[] Picks, Rounds, Costs;
            internal bool Active;
            internal Lane(int rows) { Picks = new int[rows * 2]; Rounds = new int[rows * 2]; Costs = new int[rows * 2]; }
            internal void Clear() { Array.Clear(Picks); Array.Clear(Rounds); Array.Clear(Costs); Active = false; }
        }
        private struct Row
        {
            internal long Players, Clusters, Wins, Draws, Losses, Picks, Rounds, Costs;
            internal long CensoredPlayers, CensoredClusters, CensoredPicks, CensoredRounds, CensoredCosts;
        }
        private struct HeroRow { internal long Games, Wins, Draws, Losses, Censored; }
        private struct MatchupRow { internal long Games, Seat0Wins, Seat1Wins, Draws, Censored; }
        internal readonly struct StepMark
        {
            internal readonly int LogIndex, Round, Buyer;
            internal readonly string BuyDefinition;
            internal readonly bool Fast;
            internal StepMark(int logIndex, int round, int buyer = -1, string buyDefinition = null, bool fast = false)
            { LogIndex = logIndex; Round = round; Buyer = buyer; BuyDefinition = buyDefinition; Fast = fast; }
        }

        internal static TrainingStatistics FromEnvironment(string population = null, object heroSetup = null)
        {
            string directory = Environment.GetEnvironmentVariable("SHARDS_STATS_DIRECTORY");
            if (directory == null) return null;
            try
            {
                string purpose = Environment.GetEnvironmentVariable("SHARDS_STATS_PURPOSE");
                if (purpose != "final_evaluation") throw new InvalidOperationException("Statistics require explicit final_evaluation launch routing");
                ulong seed = ulong.Parse(Environment.GetEnvironmentVariable("SHARDS_STATS_EXPECTED_SEED") ?? "", CultureInfo.InvariantCulture);
                int batch = int.Parse(Environment.GetEnvironmentVariable("SHARDS_STATS_EXPECTED_BATCH") ?? "", CultureInfo.InvariantCulture);
                if (batch < 1 || batch > MaxLanes) throw new InvalidOperationException("Statistics batch must be 1..4096");
                if (population != null) directory = Path.Combine(directory, population);
                return new TrainingStatistics(directory, purpose, seed, batch, heroSetup);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { statistics_error = true, phase = "initialization", error = error.Message }));
                return null; // Statistics failure never changes rules or the wire.
            }
        }

        internal TrainingStatistics(string directory, string purpose, ulong expectedSeed, int expectedBatch, object heroSetup = null, int publicationEvery = 1000)
        {
            if(publicationEvery<1)throw new ArgumentOutOfRangeException(nameof(publicationEvery));
            EveryCompleted=publicationEvery;_nextPublish=publicationEvery;
            _directory = Path.GetFullPath(directory);
            if (Environment.GetEnvironmentVariable("SHARDS_STRATEGY_TRACE") == "1") _strategy = new StrategyTrace(_directory); _purpose = purpose; _heroSetup = heroSetup;
            _expectedSeed = expectedSeed; _expectedBatch = expectedBatch;
            _cards = StatisticsEncoder.CardIds.Length; _rows = _cards * Kinds.Length;
            _heroIds = ShardsEngine.DraftableCharacters.Concat(new[] { "unknown" }).ToArray(); _heroes = _heroIds.Length;
            _totals = new Row[_rows]; _heroChoices = new Row[_heroes * _rows];
            _heroTotals = new HeroRow[_heroes * 2]; _matchups = new MatchupRow[_heroes * _heroes];
            _started = DateTime.UtcNow.ToString("O");
            _session = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            _binaryHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))).ToLowerInvariant();
            _writer = new Thread(PublishLoop) { IsBackground = true, Name = "training-statistics-publisher" };
            _writer.Start();
        }

        internal string SnapshotPath => Path.Combine(_directory, "session-" + _session + ".json");
        internal string ErrorPath => Path.Combine(_directory, "session-" + _session + ".error.json");

        internal void Reset(int count, ulong seed)
        {
            if (_failed) return;
            try
            {
                lock (_gate)
                {
                    if (count < 1 || count > MaxLanes || (_resets == 0 && (count != _expectedBatch || seed != _expectedSeed)))
                        throw new InvalidOperationException("Statistics launch metadata does not match the first actual reset");
                    if (_lanes != null) foreach (var lane in _lanes)
                    {
                        if (lane.Active) _unfinished++;
                        lane.Clear();
                    }
                    if (_lanes == null || _lanes.Length != count)
                        _lanes = Enumerable.Range(0, count).Select(_ => new Lane(_rows)).ToArray();
                    _strategy?.Reset(count);
                    if (_resets == 0) _firstReset = seed;
                    _lastReset = seed; _resets++;
                    if (_resets == 1) QueueSnapshot(Snapshot(false));
                }
            }
            catch (Exception error) { Fail("reset", error); }
        }

        internal StepMark BeginStep(int lane, Adapter game, int choice)
        {
            if (_failed) return default;
            try
            {
                _lanes[lane].Active = true;
                _strategy?.Begin(lane, game, choice);
                string definition = null; int buyer = -1; bool fast = false;
                if (game.Visible(choice).Action is ShardsBuyCardAction buy)
                {
                    definition = game.Engine.State.CenterRow[buy.SlotIndex]?.DefId;
                    buyer = buy.PlayerIndex; fast = buy.FastPlay;
                }
                return new StepMark(game.Engine.Log.Count, game.Engine.State.Round, buyer, definition, fast);
            }
            catch (Exception error) { Fail("begin_step", error); return default; }
        }

        internal void ObserveStep(int lane, ShardsEngine engine, StepMark mark)
        {
            if (_failed) return;
            try
            {
                _strategy?.Observe(lane, engine, mark.LogIndex, mark.Round);
                int round = mark.Round; bool matchedBuy = false;
                for (int i = mark.LogIndex; i < engine.Log.Count; i++)
                {
                    switch (engine.Log[i])
                    {
                        case ShardsTurnStartedEvent turn: round = turn.Round; break;
                        case ShardsCardBoughtEvent bought:
                            bool selected = !matchedBuy && mark.BuyDefinition != null &&
                                bought.PlayerIndex == mark.Buyer && bought.DefId == mark.BuyDefinition && bought.FastPlay == mark.Fast;
                            if (selected) matchedBuy = true;
                            Record(lane, bought.PlayerIndex, bought.DefId,
                                selected ? (bought.FastPlay ? 1 : 0) : (bought.FastPlay ? 5 : 4), round, bought.CostPaid);
                            break;
                        case ShardsRelicRecruitedEvent relic: Record(lane, relic.PlayerIndex, relic.DefId, 2, round, 0); break;
                        case ShardsDestinyTakenEvent destiny: Record(lane, destiny.PlayerIndex, destiny.DefId, 3, round, 0); break;
                    }
                }
            }
            catch (Exception error) { Fail("observe", error); }
        }

        private void Record(int lane, int seat, string definition, int kind, int round, int cost)
        {
            int card = StatisticsEncoder.CardIndex(definition);
            if (seat < 0 || seat > 1 || card < 0 || round < 0 || cost < 0) throw new InvalidOperationException("Invalid public acquisition event");
            int index = seat * _rows + kind * _cards + card;
            var history = _lanes[lane]; history.Active = true;
            checked { history.Picks[index]++; history.Rounds[index] += round; history.Costs[index] += cost; }
        }

        internal void Finish(int lane, ShardsState state, bool terminal, ulong seed)
        {
            if (_failed) return;
            try
            {
                lock (_gate)
                {
                    if (state.Players.Count != 2 || (terminal && (!state.GameOver || state.WinnerIndex < -1 || state.WinnerIndex > 1)))
                        throw new InvalidOperationException("Invalid two-seat terminal statistics input");
                    var history = _lanes[lane]; int winner = state.WinnerIndex;
                    if (!history.Active) throw new InvalidOperationException("Terminal statistics lane has no observed action");
                    int hero0 = Hero(state.Players[0].CharacterId), hero1 = Hero(state.Players[1].CharacterId);
                    for (int row = 0; row < _rows; row++)
                    {
                        int a = history.Picks[row], b = history.Picks[_rows + row];
                        if (a == 0 && b == 0) continue;
                        ref var total = ref _totals[row];
                        int present = (a > 0 ? 1 : 0) + (b > 0 ? 1 : 0);
                        long rounds = (long)history.Rounds[row] + history.Rounds[_rows + row];
                        long costs = (long)history.Costs[row] + history.Costs[_rows + row];
                        if (terminal)
                        {
                            total.Players += present; total.Clusters++; total.Picks += (long)a + b;
                            total.Rounds += rounds; total.Costs += costs;
                            if (winner < 0) total.Draws += present;
                            else
                            {
                                int won = winner == 0 ? (a > 0 ? 1 : 0) : (b > 0 ? 1 : 0);
                                total.Wins += won; total.Losses += present - won;
                            }
                        }
                        else
                        {
                            total.CensoredPlayers += present; total.CensoredClusters++;
                            total.CensoredPicks += (long)a + b; total.CensoredRounds += rounds; total.CensoredCosts += costs;
                        }
                        MergeHeroChoice(ref _heroChoices[hero0 * _rows + row], a, hero0 == hero1 ? b : 0,
                            history.Rounds[row], hero0 == hero1 ? history.Rounds[_rows + row] : 0,
                            history.Costs[row], hero0 == hero1 ? history.Costs[_rows + row] : 0, terminal, winner);
                        if (hero0 != hero1)
                            MergeHeroChoice(ref _heroChoices[hero1 * _rows + row], 0, b, 0,
                                history.Rounds[_rows + row], 0, history.Costs[_rows + row], terminal, winner);
                    }
                    ref var matchup = ref _matchups[hero0 * _heroes + hero1];
                    if (terminal)
                    {
                        _completed++; matchup.Games++;
                        _roundHistogram[Math.Clamp(state.Round, 0, 401)]++;
                        if (winner < 0) { _draws++; matchup.Draws++; }
                        else if (winner == 0) { _seat0Wins++; matchup.Seat0Wins++; }
                        else { _seat1Wins++; matchup.Seat1Wins++; }
                        for (int seat = 0; seat < 2; seat++)
                        {
                            ref var hero = ref _heroTotals[(seat == 0 ? hero0 : hero1) * 2 + seat];
                            hero.Games++;
                            if (winner < 0) hero.Draws++; else if (winner == seat) hero.Wins++; else hero.Losses++;
                            var p = state.Players[seat];
                            _collectionSum += p.Deck.Count + p.Hand.Count + p.Discard.Count + p.Champions.Count;
                            foreach (var card in p.PlayZone) if (!card.FastPlayed) _collectionSum++;
                        }
                        if (winner >= 0)
                        {
                            _decisive++; _winnerMastery += state.Players[winner].Mastery;
                            _winnerHealth += state.Players[winner].Health; _loserMastery += state.Players[1 - winner].Mastery;
                        }
                    }
                    else
                    {
                        _censored++; matchup.Censored++;
                        _heroTotals[hero0 * 2].Censored++; _heroTotals[hero1 * 2 + 1].Censored++;
                    }
                    _minSeed = Math.Min(_minSeed, seed); _maxSeed = Math.Max(_maxSeed, seed); _seenFinished = true;
                    _strategy?.Finish(lane, state, terminal, seed);
                    history.Clear();
                }
            }
            catch (Exception error) { Fail("finish", error); }
        }

        private int Hero(string id) { int index = Array.IndexOf(_heroIds, id); return index < 0 ? _heroes - 1 : index; }

        private static void MergeHeroChoice(ref Row total, int a, int b, int round0, int round1,
            int cost0, int cost1, bool terminal, int winner)
        {
            if (a == 0 && b == 0) return;
            int present = (a > 0 ? 1 : 0) + (b > 0 ? 1 : 0);
            if (terminal)
            {
                total.Players += present; total.Clusters++; total.Picks += (long)a + b;
                total.Rounds += (long)round0 + round1; total.Costs += (long)cost0 + cost1;
                if (winner < 0) total.Draws += present;
                else
                {
                    int won = winner == 0 ? (a > 0 ? 1 : 0) : (b > 0 ? 1 : 0);
                    total.Wins += won; total.Losses += present - won;
                }
            }
            else
            {
                total.CensoredPlayers += present; total.CensoredClusters++;
                total.CensoredPicks += (long)a + b; total.CensoredRounds += (long)round0 + round1;
                total.CensoredCosts += (long)cost0 + cost1;
            }
        }

        internal void BatchBoundary()
        {
            if (_failed || _completed < _nextPublish) return;
            try
            {
                lock (_gate)
                {
                    QueueSnapshot(Snapshot(false));
                    _nextPublish = (_completed / EveryCompleted + 1) * EveryCompleted;
                }
            }
            catch (Exception error) { Fail("snapshot", error); }
        }

        // Snapshot construction touches only terminal aggregates, never live
        // game state or lane histories. One bounded immutable message is pending.
        private object Snapshot(bool final)
        {
            var rows = new List<object>();
            for (int index = 0; index < _rows; index++)
            {
                var row = _totals[index];
                if (row.Clusters == 0 && row.CensoredClusters == 0) continue;
                rows.Add(new { card_id = StatisticsEncoder.CardIds[index % _cards], choice_kind = Kinds[index / _cards],
                    selected_player_games = row.Players, selected_game_clusters = row.Clusters,
                    wins = row.Wins, draws = row.Draws, losses = row.Losses,
                    censored_player_games = row.CensoredPlayers, censored_game_clusters = row.CensoredClusters,
                    pick_count = row.Picks, acquisition_round_sum = row.Rounds,
                    cost_paid_sum_known = row.Costs, cost_known_pick_count = row.Picks,
                    censored_pick_count = row.CensoredPicks, censored_round_sum = row.CensoredRounds,
                    censored_cost_paid_sum = row.CensoredCosts });
            }
            var heroChoices = new List<object>();
            for (int index = 0; index < _heroChoices.Length; index++)
            {
                var row = _heroChoices[index]; if (row.Clusters + row.CensoredClusters == 0) continue;
                int choice = index % _rows;
                heroChoices.Add(new { hero_id = _heroIds[index / _rows], card_id = StatisticsEncoder.CardIds[choice % _cards],
                    choice_kind = Kinds[choice / _cards], selected_player_games = row.Players,
                    selected_game_clusters = row.Clusters, wins = row.Wins, draws = row.Draws, losses = row.Losses,
                    censored_player_games = row.CensoredPlayers, censored_game_clusters = row.CensoredClusters,
                    pick_count = row.Picks, acquisition_round_sum = row.Rounds, cost_paid_sum_known = row.Costs,
                    cost_known_pick_count = row.Picks, censored_pick_count = row.CensoredPicks,
                    censored_round_sum = row.CensoredRounds, censored_cost_paid_sum = row.CensoredCosts });
            }
            var heroes = new List<object>(); var matchups = new List<object>();
            for (int index = 0; index < _heroTotals.Length; index++)
            {
                var row = _heroTotals[index]; if (row.Games + row.Censored == 0) continue;
                heroes.Add(new { hero_id = _heroIds[index / 2], seat = index % 2,
                    games = row.Games, wins = row.Wins, draws = row.Draws, losses = row.Losses, censored_games = row.Censored });
            }
            for (int index = 0; index < _matchups.Length; index++)
            {
                var row = _matchups[index]; if (row.Games + row.Censored == 0) continue;
                matchups.Add(new { seat0_hero_id = _heroIds[index / _heroes], seat1_hero_id = _heroIds[index % _heroes],
                    games = row.Games, seat0_wins = row.Seat0Wins, seat1_wins = row.Seat1Wins,
                    draws = row.Draws, censored_games = row.Censored });
            }
            return new { schema = "shards-training-pool-stats-v1", source_kind = "frozen_final_evaluation_outcomes",
                hero_setup = _heroSetup,
                controlled_strength = false, opportunities = (object)null, purpose = _purpose,
                session_id = _session, pid = Environment.ProcessId, started_utc = _started,
                published_utc = DateTime.UtcNow.ToString("O"), final,
                host_binary_sha256 = _binaryHash, observation_schema = StatisticsEncoder.SchemaVersion,
                publication_sequence = ++_publicationSequence, publish_every_completed_games = EveryCompleted,
                history_retention_snapshots = 512,
                cumulative = true, first_reset_seed_hex = _firstReset.ToString("x16"), last_reset_seed_hex = _lastReset.ToString("x16"),
                reset_count = _resets, min_finished_seed_hex = _seenFinished ? _minSeed.ToString("x16") : null,
                max_finished_seed_hex = _seenFinished ? _maxSeed.ToString("x16") : null,
                counters_scope = "Completed frozen-policy self-play games only. Identical policy in both seats; no optimizer, archive opponents or training games. Caps invalidate this evaluation.",
                choice_scope = "buy/fastplay require an accepted selected buy plus its actual event; effect_* label acquisition paths, not final ownership; relic/destiny include reward events.",
                row_scope = "Primary picks/rounds/costs are resolved games only; separate censored fields retain unknown outcomes. Clusters count either seat once per game per row.",
                totals = new { completed_games = _completed, draws = _draws, seat0_wins = _seat0Wins,
                    seat1_wins = _seat1Wins, censored_games = _censored, unfinished_discarded_games = _unfinished },
                rows, hero_choice_rows = heroChoices, hero_seat_rows = heroes, matchup_rows = matchups,
                final_round_histogram = (long[])_roundHistogram.Clone(), final_round_overflow_from = 401,
                final_state_sums = new { winner_mastery_sum = _winnerMastery, winner_health_sum = _winnerHealth,
                    loser_mastery_sum = _loserMastery, resolved_decisive_games = _decisive,
                    total_own_permanent_collection_sum = _collectionSum, player_count = _completed * 2 } };
        }

        private void QueueSnapshot(object snapshot)
        {
            lock (_publishGate) _pending = new Publication { Value = snapshot, Completed = _completed, Sequence = _publicationSequence };
            _wake.Set();
        }

        private void Fail(string phase, Exception error)
        {
            _failed = true;
            if (Interlocked.Exchange(ref _failureRecorded, 1) != 0) return;
            string message = error.Message.Length > 2048 ? error.Message.Substring(0, 2048) : error.Message;
            var report = new { schema = "shards-training-pool-stats-error-v1", session_id = _session,
                pid = Environment.ProcessId, utc = DateTime.UtcNow.ToString("O"), phase, error = message,
                statistics_disabled = true, game_continues = true };
            Console.Error.WriteLine(JsonSerializer.Serialize(report));
            lock (_publishGate) { _pending = null; _pendingError = report; }
            _wake.Set();
        }

        private void PublishLoop()
        {
            while (true)
            {
                _wake.WaitOne(); Publication snapshot; object error; bool stopping;
                lock (_publishGate)
                {
                    snapshot = _pending; error = _pendingError; _pending = null; _pendingError = null; stopping = _stopping;
                }
                try
                {
                    if (snapshot != null && !_failed)
                    {
                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Value);
                        AtomicBytes(SnapshotPath, bytes, false);
                        if (!_historyFailed)
                        {
                            try { PublishHistory(snapshot, bytes); }
                            catch (Exception historyError)
                            {
                                _historyFailed = true;
                                var report = new { schema = "shards-training-pool-stats-error-v1", session_id = _session,
                                    pid = Environment.ProcessId, utc = DateTime.UtcNow.ToString("O"), phase = "history",
                                    error = historyError.Message, statistics_disabled = false, history_disabled = true, game_continues = true };
                                Console.Error.WriteLine(JsonSerializer.Serialize(report));
                                try { AtomicJson(Path.Combine(_directory, "session-" + _session + ".history-error.json"), report); }
                                catch (Exception) { }
                            }
                        }
                    }
                    if (error != null) AtomicJson(ErrorPath, error);
                }
                catch (Exception failure)
                {
                    Fail("publication", failure);
                    object report;
                    lock (_publishGate) { report = _pendingError; _pendingError = null; }
                    try { if (report != null) AtomicJson(ErrorPath, report); }
                    catch (Exception) { /* stderr already contains the bounded error. */ }
                }
                if (stopping)
                {
                    lock (_publishGate) if (_pending == null && _pendingError == null) return;
                    _wake.Set();
                }
            }
        }

        private static void AtomicJson(string destination, object value)
            => AtomicBytes(destination, JsonSerializer.SerializeToUtf8Bytes(value), false);

        private void PublishHistory(Publication snapshot, byte[] bytes)
        {
            string directory = Path.Combine(_directory, "history");
            string name = "session-" + _session + "-completed-" + snapshot.Completed.ToString("D12") +
                "-seq-" + snapshot.Sequence.ToString("D8") + ".json";
            AtomicBytes(Path.Combine(directory, name), bytes, true);
            var files = Directory.GetFiles(directory, "session-" + _session + "-completed-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - 512; i++) File.Delete(files[i]);
        }

        private static void AtomicBytes(string destination, byte[] bytes, bool noReplace)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                File.Move(temporary, destination, !noReplace);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (!_failed)
            {
                try
                {
                    lock (_gate)
                    {
                        if (_lanes != null) foreach (var lane in _lanes) if (lane.Active) { _unfinished++; lane.Active = false; }
                        QueueSnapshot(Snapshot(true));
                    }
                }
                catch (Exception error) { Fail("final_snapshot", error); }
            }
            _strategy?.Dispose();
            lock (_publishGate) _stopping = true;
            _wake.Set();
            if (_writer.Join(TimeSpan.FromSeconds(2))) _wake.Dispose();
            else Console.Error.WriteLine(JsonSerializer.Serialize(new { statistics_error = true, session_id = _session,
                phase = "final_flush_timeout", error = "Final statistics write did not complete within two seconds", game_continues = true }));
        }
    }
}
