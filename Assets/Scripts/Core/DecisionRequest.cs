using System.Collections.Generic;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Decisions
{
    public enum DecisionKind
    {
        ChooseTargets,
        ChooseCards,
        ChooseMode,
        OrderCards,
        YesNo,
        InnChoice
    }

    /// <summary>One selectable option inside a DecisionRequest.</summary>
    public sealed class DecisionOption
    {
        public int Id;
        public string Label;
        /// <summary>Set when the option refers to a card (for UI display).</summary>
        public int CardInstanceId = -1;
        /// <summary>Card definition id, set alongside CardInstanceId so the UI can render
        /// the actual card face even when the instance is in no client-visible zone
        /// (center-deck reveals, market exile). Decisions only reach the deciding
        /// player, so this leaks nothing. Null-omitted on the wire.</summary>
        public string DefId;
        /// <summary>Set when the option is a target.</summary>
        public TargetRef? Target;

        public DecisionOption(int id, string label) { Id = id; Label = label; }
    }

    /// <summary>
    /// A pause point: the engine cannot continue until this player answers.
    /// Min/Max bound how many options must be chosen; Ordered means the answer's
    /// sequence matters (e.g. bottoming cards in a chosen order).
    /// </summary>
    public sealed class DecisionRequest
    {
        public int Id;
        public int PlayerIndex;
        public DecisionKind Kind;
        public string Title;
        public List<DecisionOption> Options = new();
        public int Min = 1;
        public int Max = 1;
        public bool Ordered;
        /// <summary>Answer applied when the player times out or is disconnected (host decides).</summary>
        public List<int> DefaultOptionIds = new();
        /// <summary>Free-form tag routing this decision to a custom UI surface
        /// (e.g. "soi.split"); null renders the generic decision modal.</summary>
        public string Context;

        public static DecisionRequest YesNo(int player, string title, bool defaultYes = false)
        {
            var req = new DecisionRequest { PlayerIndex = player, Kind = DecisionKind.YesNo, Title = title, Min = 1, Max = 1 };
            req.Options.Add(new DecisionOption(0, "Yes"));
            req.Options.Add(new DecisionOption(1, "No"));
            req.DefaultOptionIds.Add(defaultYes ? 0 : 1);
            return req;
        }
    }

    public sealed class DecisionAnswer
    {
        public int DecisionId;
        /// <summary>Chosen option ids, in order when the request is Ordered.</summary>
        public List<int> ChosenOptionIds = new();

        public bool IsYes => ChosenOptionIds.Count > 0 && ChosenOptionIds[0] == 0;
    }
}
