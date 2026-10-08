using System;
using Pascension.Core;
using Pascension.Engine.Actions;

namespace Shards.AI
{
    /// <summary>A local game engine that can think off-thread and submit through its host.</summary>
    public interface IPlayablePolicyEngine : IEngineAdapter
    {
        void BindSubmit(Action<PlayerAction> submit);
        void PreparePolicy();
        bool TryStepPolicy();
    }
}
