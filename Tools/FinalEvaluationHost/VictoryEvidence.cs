using Shards.Engine;
using Pascension.Engine.Events;

namespace Shards.Preflight
{
    // Evaluation-only event attribution. These signatures are locked to the evaluated
    // content: Infinity Shard's M30 bonus is +9994; Comet's DestroyOpponent loses 1M HP.
    // Copied effects count by the effect that resolved, not by ownership of a card.
    internal sealed class VictoryEvidence
    {
        private readonly bool[] _infinityPower = new bool[2];
        private readonly bool[] _lethalHealth = new bool[2];
        private readonly string[] _cause = new string[2];
        internal bool InfinityPower(int seat) => _infinityPower[seat];
        internal void Observe(GameEvent value)
        {
            switch(value)
            {
                case ShardsTurnStartedEvent e: _infinityPower[e.PlayerIndex]=false; break;
                case ShardsCleanupEvent e: _infinityPower[e.PlayerIndex]=false; break;
                case ShardsPowerChangedEvent e:
                    if(e.Delta==9994) _infinityPower[e.PlayerIndex]=true;
                    break;
                case ShardsHealthChangedEvent e:
                    _lethalHealth[e.PlayerIndex]=e.NewValue<=0;
                    _cause[e.PlayerIndex]=e.NewValue<=0 ? (e.Delta==-1_000_000 ? "comet" : "other_health_loss") : null;
                    break;
                case ShardsDamageAssignedEvent e:
                    for(int i=0;i<e.Targets.Count;i++)
                        if(e.Amounts[i]>0 && _lethalHealth[e.Targets[i]])
                            _cause[e.Targets[i]]=e.FromPlayerIndex>=0 && _infinityPower[e.FromPlayerIndex] ? "mastery" : "normal_damage";
                    break;
                case ShardsConcededEvent e: _cause[e.PlayerIndex]="concession"; break;
            }
        }
        internal string Result(int winner) => winner<0 ? "draw" : _cause[1-winner] ?? "unknown";
    }
}
