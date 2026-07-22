using System.Collections.Generic;
using Pascension.Engine.Events;
using Shards.Engine;

namespace Shards.Stats
{
    /// <summary>Client-side game recorder: consumes the REDACTED per-viewer stream
    /// (events + snapshots — never engine/state peeks) and produces a SoiGameRecord
    /// at game end. Port of SoiSim's omniscient GameRecorder minus the state peeks;
    /// every metric here survives redaction (draw events keep their count, shield
    /// reveals are public by rule).</summary>
    public sealed class SoiGameRecorder
    {
        private readonly List<SoiSeatRecord> _seats = new();
        private readonly List<bool> _everOver1000 = new();
        private ShardsSnapshot _lastSnap;
        private bool _initialized;
        private bool _complete = true;
        private bool _finalized;
        /// <summary>Next expected event Seq — any deviation marks the record incomplete.</summary>
        private int _nextSeq;
        /// <summary>Current round, from the last ShardsTurnStartedEvent.</summary>
        private int _round = 1;
        private int _turns;
        private int _myIndex = -1;

        /// <summary>Feed every snapshot delivered to the viewer. When a delivery tick
        /// carries both events and a snapshot, feed the events FIRST (GameHost already
        /// delivers in that order) — a snapshot ahead of the consumed stream reads as
        /// missed events (mid-game join / resync) and marks the record incomplete.</summary>
        public void OnSnapshot(ShardsSnapshot snap)
        {
            if (snap == null || _finalized) return;
            if (snap.EventSeq > _nextSeq)
            {
                _complete = false;
                _nextSeq = snap.EventSeq;
            }
            if (!_initialized)
            {
                _initialized = true;
                _myIndex = snap.ViewerIndex;
                EnsureSeats(snap.Players.Count);
                for (int i = 0; i < snap.Players.Count; i++)
                    _seats[i].CharacterId = snap.Players[i].CharacterId;
            }
            _lastSnap = snap;
        }

        public void OnEvents(IReadOnlyList<GameEvent> batch)
        {
            if (batch == null || _finalized) return;
            for (int i = 0; i < batch.Count; i++)
            {
                var e = batch[i];
                if (e == null) continue;
                if (e.Seq != _nextSeq) _complete = false;
                _nextSeq = e.Seq + 1;
                Extract(e);
            }
        }

        /// <summary>One-shot: returns null on a second call or while the game is not
        /// over (no final snapshot yet).</summary>
        public SoiGameRecord FinalizeRecord(SoiRecordContext ctx)
        {
            if (_finalized || _lastSnap == null || !_lastSnap.GameOver) return null;
            _finalized = true;

            var snap = _lastSnap;
            EnsureSeats(snap.Players.Count);
            int winner = snap.WinnerIndex;
            bool overwhelmed = winner >= 0 && winner < _everOver1000.Count && _everOver1000[winner];
            var record = new SoiGameRecord
            {
                // Not rules code — this id never has to replay deterministically.
                Guid = System.Guid.NewGuid().ToString("N"),
                EndedAtUtc = ctx?.EndedAtUtc,
                AppVersion = ctx?.AppVersion,
                DurationSeconds = ctx?.DurationSeconds ?? 0,
                Mode = ctx?.Mode,
                Dlc = snap.Dlc,
                MyIndex = _myIndex,
                WinnerIndex = winner,
                Termination = winner < 0 ? "tie" : overwhelmed ? "overwhelm" : "kill",
                Rounds = snap.Round,
                Turns = _turns,
                Complete = _complete
            };

            for (int i = 0; i < snap.Players.Count; i++)
            {
                var seat = _seats[i];
                var player = snap.Players[i];
                seat.CharacterId = player.CharacterId;
                seat.FinalHealth = player.Health;
                seat.FinalMastery = player.Mastery;
                seat.Eliminated = seat.Eliminated || player.Eliminated;

                var identity = ctx != null && ctx.Seats != null && i < ctx.Seats.Count
                    ? ctx.Seats[i]
                    : null;
                if (identity != null)
                {
                    seat.Identity = identity.Identity;
                    seat.Name = identity.Name;
                    seat.IsBot = identity.IsBot;
                    seat.BotKind = identity.BotKind;
                }
                else
                {
                    seat.Name = player.Name;
                    seat.IsBot = player.IsBot;
                    seat.BotKind = player.BotKind;
                    seat.Identity = player.IsBot && player.BotKind != null
                        ? "bot:" + player.BotKind
                        : (player.Name ?? "").ToLowerInvariant();
                }
                record.Players.Add(seat);
            }
            return record;
        }

        private void Extract(GameEvent e)
        {
            switch (e)
            {
                case ShardsTurnStartedEvent ev:
                    _turns++;
                    _round = ev.Round;
                    break;

                case ShardsCardBoughtEvent ev:
                {
                    var p = Seat(ev.PlayerIndex);
                    // SlotIndex -1 = recruited off the center deck — kept out of
                    // buy-rate math (mirrors the sim recorder).
                    Bump(ev.SlotIndex >= 0 ? p.Buys : p.OffRowRecruits, ev.DefId);
                    if (ev.FastPlay)
                        Bump(p.FastPlays, ev.DefId);
                    p.GemsSpent += ev.CostPaid;
                    break;
                }

                case ShardsCardPlayedEvent ev:
                    Bump(Seat(ev.PlayerIndex).Plays, ev.DefId);
                    break;

                case ShardsFocusedEvent ev:
                    Seat(ev.PlayerIndex).FocusCount++;
                    break;

                case ShardsMasteryChangedEvent ev:
                {
                    var p = Seat(ev.PlayerIndex);
                    if (p.RoundToM10 < 0 && ev.NewValue >= 10) p.RoundToM10 = _round;
                    if (p.RoundToM20 < 0 && ev.NewValue >= 20) p.RoundToM20 = _round;
                    if (p.RoundToM30 < 0 && ev.NewValue >= 30) p.RoundToM30 = _round;
                    break;
                }

                case ShardsPowerChangedEvent ev:
                    Seat(ev.PlayerIndex);
                    if (ev.NewValue > 1000)
                        _everOver1000[ev.PlayerIndex] = true;
                    break;

                case ShardsDamageAssignedEvent ev:
                {
                    var p = Seat(ev.FromPlayerIndex);
                    for (int t = 0; t < ev.Amounts.Count; t++)
                    {
                        p.DamageDealt += ev.Amounts[t];
                        if (ev.Amounts[t] > p.MaxSingleHit)
                            p.MaxSingleHit = ev.Amounts[t];
                    }
                    break;
                }

                case ShardsShieldsRevealedEvent ev:
                {
                    var p = Seat(ev.PlayerIndex);
                    p.ShieldReveals += ev.DefIds.Count;
                    p.DamagePrevented += ev.Prevented;
                    break;
                }

                case ShardsChampionDeployedEvent ev:
                    Bump(Seat(ev.PlayerIndex).ChampionsDeployed, ev.DefId);
                    break;

                case ShardsChampionDestroyedEvent ev:
                    Seat(ev.OwnerIndex).ChampionsLost++;
                    if (ev.ByPlayerIndex >= 0 && ev.ByPlayerIndex != ev.OwnerIndex)
                        Seat(ev.ByPlayerIndex).ChampionsKilled++;
                    break;

                case ShardsRelicRecruitedEvent ev:
                    Seat(ev.PlayerIndex).Relics.Add(ev.DefId);
                    break;

                case ShardsDestinyTakenEvent ev:
                    Seat(ev.PlayerIndex).Destinies[ev.DefId] = _round;
                    break;

                case ShardsMonsterDefeatedEvent ev:
                    Seat(ev.PlayerIndex).MonstersDefeated[ev.DefId] = _round;
                    break;

                case ShardsCardBanishedEvent ev:
                    if (ev.PlayerIndex >= 0)
                        Seat(ev.PlayerIndex).CardsBanished++;
                    break;

                case ShardsCardDrawnEvent ev:
                    // Redacted for other viewers (DefId nulled) but the COUNT survives.
                    Seat(ev.PlayerIndex).CardsDrawn++;
                    break;

                case ShardsConcededEvent ev:
                    Seat(ev.PlayerIndex).Conceded = true;
                    break;

                case ShardsPlayerEliminatedEvent ev:
                    Seat(ev.PlayerIndex).Eliminated = true;
                    break;
            }
        }

        private SoiSeatRecord Seat(int index)
        {
            EnsureSeats(index + 1);
            return _seats[index];
        }

        private void EnsureSeats(int count)
        {
            while (_seats.Count < count)
            {
                _seats.Add(new SoiSeatRecord());
                _everOver1000.Add(false);
            }
        }

        private static void Bump(Dictionary<string, int> dict, string key) =>
            dict[key] = dict.TryGetValue(key, out int v) ? v + 1 : 1;
    }
}
