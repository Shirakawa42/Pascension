using System;
using Pascension.Engine.Serialization;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Client-side view model: holds the latest ClientSnapshot and raises an event
    /// when a new one arrives. Views read from here only — never from the engine.
    /// </summary>
    public sealed class ClientGameView
    {
        public ClientSnapshot Snapshot { get; private set; }

        public event Action<ClientSnapshot> SnapshotChanged;

        public int LocalPlayerIndex => Snapshot != null ? Snapshot.ViewerIndex : 0;

        public PlayerSnap LocalPlayer =>
            Snapshot != null && Snapshot.Players.Count > LocalPlayerIndex
                ? Snapshot.Players[LocalPlayerIndex]
                : null;

        public void Apply(ClientSnapshot snapshot)
        {
            Snapshot = snapshot;
            SnapshotChanged?.Invoke(snapshot);
        }

        /// <summary>True when the engine is waiting on the local player.</summary>
        public bool LocalPending =>
            Snapshot?.Pending != null && Snapshot.Pending.PlayerIndex == LocalPlayerIndex;

        public string PlayerName(int index) =>
            Snapshot != null && index >= 0 && index < Snapshot.Players.Count
                ? Snapshot.Players[index].Name
                : $"Player {index}";

        /// <summary>
        /// Find a visible card anywhere in the snapshot (own hand, open zones of all
        /// players, market rows, boss). Null when not visible to this viewer.
        /// </summary>
        public CardSnap FindCard(int instanceId)
        {
            var s = Snapshot;
            if (s == null) return null;

            foreach (var p in s.Players)
            {
                foreach (var c in p.Hand) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Discard) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Exile) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.PlayedThisTurn) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Relics) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Deck) if (c.InstanceId == instanceId) return c;
                foreach (var c in p.Equipment)
                    if (c != null && c.InstanceId == instanceId) return c;
            }
            for (int t = 0; t < s.MarketRows.Length; t++)
            {
                var row = s.MarketRows[t];
                if (row == null) continue;
                foreach (var c in row)
                    if (c != null && c.InstanceId == instanceId) return c;
            }
            if (s.Boss != null && s.Boss.InstanceId == instanceId) return s.Boss;
            return null;
        }
    }
}
