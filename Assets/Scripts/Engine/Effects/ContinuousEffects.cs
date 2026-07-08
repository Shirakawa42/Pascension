using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;

namespace Pascension.Engine.Effects
{
    public enum ModifierKind
    {
        MonsterHpDelta
    }

    public enum ModifierDuration
    {
        EndOfTurn,
        Permanent
    }

    /// <summary>A timestamped continuous modifier (simplified MTG layers).</summary>
    public sealed class Modifier
    {
        public int Id;
        public ModifierKind Kind;
        public int TargetInstanceId;
        public int Amount;
        public ModifierDuration Duration;
        public long Timestamp;
        public string SourceDescription = "";
    }

    /// <summary>
    /// Holds all active continuous modifiers. Effective values are always computed by
    /// folding modifiers over base stats — base stats are never overwritten.
    /// </summary>
    public sealed class ContinuousEffects
    {
        public List<Modifier> Modifiers = new();
        private int _nextId = 1;

        public Modifier Add(ModifierKind kind, int targetInstanceId, int amount, ModifierDuration duration, long timestamp, string source)
        {
            var mod = new Modifier
            {
                Id = _nextId++,
                Kind = kind,
                TargetInstanceId = targetInstanceId,
                Amount = amount,
                Duration = duration,
                Timestamp = timestamp,
                SourceDescription = source
            };
            Modifiers.Add(mod);
            return mod;
        }

        public int EffectiveMonsterHp(CardInstance monster)
        {
            int hp = monster.Def.MonsterHp;
            foreach (var m in Modifiers)
                if (m.Kind == ModifierKind.MonsterHpDelta && m.TargetInstanceId == monster.InstanceId)
                    hp += m.Amount;
            return hp < 0 ? 0 : hp;
        }

        public void ExpireEndOfTurn()
        {
            Modifiers.RemoveAll(m => m.Duration == ModifierDuration.EndOfTurn);
        }

        /// <summary>Drop modifiers pointing at cards that left their zone (died/exiled).</summary>
        public void RemoveForInstance(int instanceId)
        {
            Modifiers.RemoveAll(m => m.TargetInstanceId == instanceId);
        }
    }
}
