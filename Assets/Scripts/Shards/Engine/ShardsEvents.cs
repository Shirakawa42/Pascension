using System.Collections.Generic;
using Pascension.Engine.Events;

namespace Shards.Engine
{
    // Concrete events for the Shards engine (base GameEvent + EventLog come from Core).
    // Naming: unique across BOTH games' event sets is not required (separate codecs),
    // but keep a "Shards" prefix to avoid discriminator clashes if assemblies ever merge.

    public sealed class ShardsGameStartedEvent : GameEvent
    {
        public int PlayerCount;
        public int Dlc;
    }

    public sealed class ShardsTurnStartedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Round;
    }

    public sealed class ShardsCardPlayedEvent : GameEvent
    {
        public int PlayerIndex;
        public int InstanceId;
        public string DefId;
    }

    public sealed class ShardsCardBoughtEvent : GameEvent
    {
        public int PlayerIndex;
        public int SlotIndex;
        public string DefId;
        public int CostPaid;
        public bool FastPlay;
    }

    public sealed class ShardsCardDrawnEvent : GameEvent
    {
        public int PlayerIndex;
        public int InstanceId;
        public string DefId;

        public override GameEvent RedactFor(int viewer) =>
            viewer == PlayerIndex
                ? this
                : new ShardsCardDrawnEvent { Seq = Seq, PlayerIndex = PlayerIndex, InstanceId = -1, DefId = null };
    }

    public sealed class ShardsDeckShuffledEvent : GameEvent
    {
        public int PlayerIndex;
    }

    public sealed class ShardsRowRefilledEvent : GameEvent
    {
        public int SlotIndex;
        public int InstanceId;
        public string DefId;
    }

    public sealed class ShardsFocusedEvent : GameEvent
    {
        public int PlayerIndex;
    }

    public sealed class ShardsMasteryChangedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Delta;
        public int NewValue;
    }

    public sealed class ShardsGemsChangedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Delta;
        public int NewValue;
    }

    public sealed class ShardsPowerChangedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Delta;
        public int NewValue;
    }

    public sealed class ShardsHealthChangedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Delta;
        public int NewValue;
    }

    public sealed class ShardsChampionDeployedEvent : GameEvent
    {
        public int PlayerIndex;
        public int InstanceId;
        public string DefId;
    }

    public sealed class ShardsChampionDestroyedEvent : GameEvent
    {
        public int OwnerIndex;
        public int ByPlayerIndex;
        public int InstanceId;
        public string DefId;
    }

    public sealed class ShardsCharacterExhaustedEvent : GameEvent
    {
        public int PlayerIndex;
        public int CardInstanceId; // -1 = the character card itself
    }

    public sealed class ShardsDamageAssignedEvent : GameEvent
    {
        public int FromPlayerIndex;
        /// <summary>Parallel lists: target player index → damage after shields.</summary>
        public List<int> Targets = new();
        public List<int> Amounts = new();
    }

    public sealed class ShardsShieldsRevealedEvent : GameEvent
    {
        public int PlayerIndex;
        /// <summary>Public by rule — revealing IS showing the cards.</summary>
        public List<string> DefIds = new();
        public int Prevented;
    }

    public sealed class ShardsPlayerEliminatedEvent : GameEvent
    {
        public int PlayerIndex;
    }

    public sealed class ShardsRelicRecruitedEvent : GameEvent
    {
        public int PlayerIndex;
        public string DefId;
    }

    public sealed class ShardsDestinyChosenEvent : GameEvent
    {
        public int PlayerIndex;
        public string DefId;
    }

    public sealed class ShardsMonsterDefeatedEvent : GameEvent
    {
        public int PlayerIndex;
        public int SlotIndex;
        public string DefId;
    }

    public sealed class ShardsMercenaryReturnedEvent : GameEvent
    {
        public int PlayerIndex;
        public string DefId;
    }

    public sealed class ShardsCleanupEvent : GameEvent
    {
        public int PlayerIndex;
    }

    public sealed class ShardsGameEndedEvent : GameEvent
    {
        public int WinnerIndex;
    }

    public sealed class ShardsConcededEvent : GameEvent
    {
        public int PlayerIndex;
    }
}
