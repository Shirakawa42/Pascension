using System.Collections.Generic;

namespace Pascension.Engine.Effects
{
    /// <summary>
    /// A resolvable game effect. Implementations MUST be stateless (instances are shared
    /// between all copies of a card) and MUST only mutate the game through the
    /// <see cref="EffectContext"/>. Pause for a player choice by yielding
    /// <c>EngineStep.AwaitDecision(...)</c> and reading <c>ctx.Answer</c> afterwards.
    /// </summary>
    public interface IEffect
    {
        IEnumerable<EngineStep> Resolve(EffectContext ctx);
    }
}
