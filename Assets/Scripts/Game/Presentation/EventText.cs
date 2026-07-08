using Pascension.Engine.Cards;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;

namespace Pascension.Game.Presentation
{
    /// <summary>Turns GameEvents into readable log lines. Returns null for noise.</summary>
    public static class EventText
    {
        public static string Describe(GameEvent e, ClientSnapshot snap, int viewerIndex)
        {
            switch (e)
            {
                case GameStartedEvent gs:
                    return $"Game started — {gs.PlayerCount} players.";

                case TurnStartedEvent ts:
                    return $"— {Name(snap, viewerIndex, ts.PlayerIndex)}'s turn (round {ts.Round}) —";

                case CoalescedDrawEvent cd:
                    return $"{Name(snap, viewerIndex, cd.PlayerIndex)} {Verb(viewerIndex, cd.PlayerIndex, "draw")} {cd.Count} cards.";

                case CardDrawnEvent d:
                    return d.DefId != null
                        ? $"{Name(snap, viewerIndex, d.PlayerIndex)} {Verb(viewerIndex, d.PlayerIndex, "draw")} {CardName(d.DefId)}."
                        : $"{Name(snap, viewerIndex, d.PlayerIndex)} {Verb(viewerIndex, d.PlayerIndex, "draw")} a card.";

                case DeckShuffledEvent sh:
                    return $"{Name(snap, viewerIndex, sh.PlayerIndex)} shuffles.";

                case ApChangedEvent ap:
                    return ap.Delta > 0
                        ? $"{Name(snap, viewerIndex, ap.PlayerIndex)} +{ap.Delta} AP ({ap.NewValue})."
                        : null;

                case DamagePoolChangedEvent dp:
                    return dp.Delta > 0
                        ? $"{Name(snap, viewerIndex, dp.PlayerIndex)} +{dp.Delta} damage ({dp.NewValue} pooled)."
                        : null;

                case XpGainedEvent xp:
                    return $"{Name(snap, viewerIndex, xp.PlayerIndex)} +{xp.Amount} XP.";

                case LeveledUpEvent lv:
                    return $"{Name(snap, viewerIndex, lv.PlayerIndex)} reached level {lv.NewLevel}!";

                case PlayerMovedEvent mv:
                    return $"{Name(snap, viewerIndex, mv.PlayerIndex)} {Verb(viewerIndex, mv.PlayerIndex, "move")} {mv.FromStep} → {mv.ToStep}.";

                case InnReachedEvent inn:
                    return $"{Name(snap, viewerIndex, inn.PlayerIndex)} reached the inn at step {inn.InnStep}.";

                case CardBoughtEvent buy:
                    return $"{Name(snap, viewerIndex, buy.PlayerIndex)} {Verb(viewerIndex, buy.PlayerIndex, "buy")} {CardName(buy.DefId)} ({buy.CostPaid} AP).";

                case MarketRefilledEvent mr:
                    return mr.InstanceId >= 0 ? $"Market reveals {CardName(mr.DefId)}." : null;

                case CardPlayedEvent cp:
                    return $"{Name(snap, viewerIndex, cp.PlayerIndex)} {Verb(viewerIndex, cp.PlayerIndex, "play")} {CardName(cp.DefId)}.";

                case StackPushedEvent sp:
                    return sp.DefId != null
                        ? $"{Name(snap, viewerIndex, sp.ControllerIndex)} {Verb(viewerIndex, sp.ControllerIndex, "play")} {CardName(sp.DefId)}."
                        : $"{Name(snap, viewerIndex, sp.ControllerIndex)}: {sp.Description}";

                case SpellCounteredEvent:
                    return "The spell is countered!";

                case StackFizzledEvent:
                    return "…it fizzles (no legal target).";

                case DamageMarkedEvent dm:
                    return $"{dm.Amount} damage → {TargetName(snap, dm.Target)}.";

                case MonsterDiedEvent md:
                    return $"{Name(snap, viewerIndex, md.KillerIndex)} {Verb(viewerIndex, md.KillerIndex, "slay")} {CardName(md.DefId)}!";

                case CardTappedEvent:
                case CardMovedEvent:
                case PermanentsUntappedEvent:
                case TurnDamageClearedEvent:
                case PhaseChangedEvent:
                case StackResolvedEvent:
                case DecisionRequestedEvent:
                case DecisionMadeEvent:
                    return null;

                case ExtraTurnEvent ex:
                    return $"{Name(snap, viewerIndex, ex.PlayerIndex)} takes an extra turn!";

                case PlayerConcededEvent pc:
                    return $"{Name(snap, viewerIndex, pc.PlayerIndex)} conceded.";

                case GameEndedEvent ge:
                    return ge.WinnerIndex >= 0
                        ? $"{Name(snap, viewerIndex, ge.WinnerIndex)} wins — {ge.Reason}"
                        : $"Game over — {ge.Reason}";

                default:
                    return null;
            }
        }

        public static string CardName(string defId)
        {
            if (string.IsNullOrEmpty(defId)) return "a card";
            return CardDatabase.TryGet(defId, out var def) ? def.Name : defId;
        }

        public static string Name(ClientSnapshot snap, int viewerIndex, int playerIndex)
        {
            if (playerIndex == viewerIndex) return "You";
            if (snap != null && playerIndex >= 0 && playerIndex < snap.Players.Count)
                return snap.Players[playerIndex].Name;
            return $"Player {playerIndex}";
        }

        /// <summary>"You draw" vs "Bot draws" — naive s-appending is enough for our verbs.</summary>
        private static string Verb(int viewerIndex, int playerIndex, string baseVerb) =>
            playerIndex == viewerIndex ? baseVerb : baseVerb + "s";

        public static string TargetName(ClientSnapshot snap, TargetRef target)
        {
            switch (target.Kind)
            {
                case TargetKind.Boss:
                    return snap?.Boss != null ? CardName(snap.Boss.DefId) : "the boss";
                case TargetKind.MonsterSlot:
                {
                    int t = target.A - 1;
                    if (snap != null && t >= 0 && t < snap.MarketRows.Length)
                    {
                        var row = snap.MarketRows[t];
                        if (row != null && target.B >= 0 && target.B < row.Length && row[target.B] != null)
                            return CardName(row[target.B].DefId);
                    }
                    return "a monster";
                }
                case TargetKind.Player:
                    return snap != null && target.A < snap.Players.Count ? snap.Players[target.A].Name : "a player";
                case TargetKind.StackItem:
                {
                    if (snap != null)
                        foreach (var item in snap.Stack)
                            if (item.Id == target.A)
                                return item.DefId != null ? CardName(item.DefId) : item.Description;
                    return "a spell";
                }
                case TargetKind.Card:
                    return "a card";
                default:
                    return target.ToString();
            }
        }
    }
}
