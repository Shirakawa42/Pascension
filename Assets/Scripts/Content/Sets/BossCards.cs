using Pascension.Engine.Effects.Common;
using static Pascension.Content.CardBuilder;

namespace Pascension.Content.Sets
{
    public static class BossCards
    {
        public static void Register()
        {
            Card("the_gatekeeper", "The Gatekeeper").BossTier()
                .Monster(20, NullEffect.Instance)
                .Text("HP 20. Guards step 50.\nOnly a hero standing on step 50 may attack it.\nDamage on it fades at end of turn — bring it down in a single strike, and victory is yours.")
                .Art("a colossal armored sentinel wreathed in violet flame standing before towering black gates, giant halberd planted, glowing chains across the doors, lightning sky, final boss presence")
                .Register();
        }
    }
}
