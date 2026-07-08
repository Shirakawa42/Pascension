using Pascension.Engine.Core;
using Pascension.Engine.Effects.Common;
using static Pascension.Content.CardBuilder;

namespace Pascension.Content.Sets
{
    /// <summary>The 10-card starting deck: 7× Move, 1× Redbull, 1× Fire bolt, 1× Pyroblast.</summary>
    public static class DefaultCards
    {
        public static void Register()
        {
            Card("move", "Move").DefaultTier().Action()
                .OnResolve(new GainApEffect(1))
                .Text("+1 action point.")
                .Art("worn leather traveling boots striding along a winding dirt road, rolling green hills, morning light, sense of motion, adventure begins")
                .Register();

            Card("redbull", "Redbull").DefaultTier().Action()
                .OnResolve(new GainApEffect(new LevelScaledValue(2, 3, 5)))
                .Text("Lvl 1-4: +2 action points.\nLvl 5-9: +3 action points.\nLvl 10: +5 action points.")
                .Art("a crackling crimson energy drink in an ornate potion bottle, red lightning arcing around it, wings of red energy sprouting from the glass, dark tavern table")
                .Register();

            Card("fire_bolt", "Fire bolt").DefaultTier().Action()
                .OnResolve(new GainDamageEffect(1))
                .Text("+1 damage.")
                .Art("a small dart of orange flame streaking from a mage's fingertip, sparks trailing, dark background, focused close-up on the hand")
                .Register();

            Card("pyroblast", "Pyroblast").DefaultTier().Action()
                .OnResolve(new GainDamageEffect(new LevelScaledValue(2, 3, 5)))
                .Text("Lvl 1-4: +2 damage.\nLvl 5-9: +3 damage.\nLvl 10: +5 damage.")
                .Art("a massive roaring fireball spell erupting forward, swirling core of white-hot flame, embers and smoke, epic scale, caster silhouetted below")
                .Register();
        }
    }
}
