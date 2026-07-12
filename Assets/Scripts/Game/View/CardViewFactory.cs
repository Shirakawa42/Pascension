using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Builds the CardView hierarchy in code (native size 220x308; scale the root for
    /// other sizes). Used at runtime by every view that spawns cards — no prefabs.
    /// </summary>
    public static class CardViewFactory
    {
        public static CardView Create(Transform parent, UiTheme theme, float scale = 1f)
        {
            var root = UiFactory.CreateRect("CardView", parent);
            root.sizeDelta = new Vector2(CardView.Width, CardView.Height);
            root.localScale = Vector3.one * scale;

            var view = root.gameObject.AddComponent<CardView>();
            view.Theme = theme;
            view.Group = UiFactory.AddGroup(root.gameObject);

            // Glow halo (behind the frame, slightly larger).
            var glow = UiFactory.CreateImage("Glow", root, theme.Rounded, UiPalette.WithAlpha(UiPalette.Gold, 0.85f));
            UiFactory.Stretch(glow.rectTransform, -9, -9, -9, -9);
            glow.gameObject.SetActive(false);
            view.Glow = glow;

            // Tier-colored frame (whole card background = border ring around inset art).
            var frame = UiFactory.CreateImage("Frame", root, theme.Rounded, UiPalette.TierDefault, raycast: true);
            UiFactory.Stretch(frame.rectTransform);
            view.Frame = frame;

            // Full-bleed art, inset so the frame reads as a thin border.
            var art = UiFactory.CreateImage("Art", frame.transform, null, Color.white);
            UiFactory.Stretch(art.rectTransform, 5, 5, 5, 5);
            view.Art = art;

            // Top bar overlay: name left, cost/HP right.
            var topBar = UiFactory.CreateImage("TopBar", frame.transform, null, new Color(0f, 0f, 0f, 0.6f));
            topBar.rectTransform.anchorMin = new Vector2(0f, 1f);
            topBar.rectTransform.anchorMax = new Vector2(1f, 1f);
            topBar.rectTransform.pivot = new Vector2(0.5f, 1f);
            topBar.rectTransform.offsetMin = new Vector2(5f, -42f);
            topBar.rectTransform.offsetMax = new Vector2(-5f, -5f);
            view.TopBar = topBar;

            var name = UiFactory.CreateText(theme, "Name", topBar.transform, "Card", 19f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Stretch(name.rectTransform, 10, 2, 44, 2);
            name.enableAutoSizing = true;
            name.fontSizeMin = 11f;
            name.fontSizeMax = 19f;
            view.NameText = name;

            // AP cost disc (non-monsters).
            var costGroup = UiFactory.CreateRect("Cost", topBar.transform);
            UiFactory.Place(costGroup, new Vector2(1f, 0.5f), new Vector2(-4f, 0f), new Vector2(32f, 32f));
            var costDisc = UiFactory.CreateImage("Disc", costGroup, theme.Circle, UiPalette.Gold);
            UiFactory.Stretch(costDisc.rectTransform);
            var costText = UiFactory.CreateText(theme, "Value", costGroup, "0", 20f,
                UiPalette.Background, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(costText.rectTransform);
            view.CostGroup = costGroup.gameObject;
            view.CostText = costText;

            // Monster HP badge (replaces the cost disc).
            var hpGroup = UiFactory.CreateRect("Hp", topBar.transform);
            UiFactory.Place(hpGroup, new Vector2(1f, 0.5f), new Vector2(-4f, 0f), new Vector2(32f, 32f));
            var hpBadge = UiFactory.CreateImage("Badge", hpGroup, theme.Circle, UiPalette.Danger);
            UiFactory.Stretch(hpBadge.rectTransform);
            var hpText = UiFactory.CreateText(theme, "Value", hpGroup, "0", 20f,
                Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(hpText.rectTransform);
            hpGroup.gameObject.SetActive(false);
            view.HpGroup = hpGroup.gameObject;
            view.HpText = hpText;

            // Shield badge (Shards of Infinity): shield icon + count, left edge above
            // the rules box. Inactive by default — Pascension never shows it.
            var shieldGroup = UiFactory.CreateRect("Shield", frame.transform);
            UiFactory.Place(shieldGroup, new Vector2(0f, 0f), new Vector2(26f, 130f), new Vector2(44f, 48f));
            var shieldIcon = UiFactory.CreateText(theme, "Icon", shieldGroup, "<sprite name=\"soi_shield\">", 40f,
                Color.white, TextAlignmentOptions.Center);
            if (theme.Icons != null) shieldIcon.spriteAsset = theme.Icons;
            UiFactory.Stretch(shieldIcon.rectTransform);
            shieldIcon.raycastTarget = false;
            var shieldText = UiFactory.CreateText(theme, "Value", shieldGroup, "0", 19f,
                Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(shieldText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 1f), new Vector2(40f, 26f));
            var shieldOutline = shieldText.gameObject.AddComponent<UnityEngine.UI.Outline>();
            shieldOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            shieldOutline.effectDistance = new Vector2(1.2f, -1.2f);
            shieldText.raycastTarget = false;
            shieldGroup.gameObject.SetActive(false);
            view.ShieldGroup = shieldGroup.gameObject;
            view.ShieldText = shieldText;

            // Translucent rules box at the bottom.
            var rulesBox = UiFactory.CreateImage("RulesBox", frame.transform, null, new Color(0f, 0f, 0f, 0.72f));
            rulesBox.rectTransform.anchorMin = new Vector2(0f, 0f);
            rulesBox.rectTransform.anchorMax = new Vector2(1f, 0f);
            rulesBox.rectTransform.pivot = new Vector2(0.5f, 0f);
            rulesBox.rectTransform.offsetMin = new Vector2(5f, 5f);
            rulesBox.rectTransform.offsetMax = new Vector2(-5f, 102f);
            view.RulesBox = rulesBox;

            var typeLine = UiFactory.CreateText(theme, "TypeLine", rulesBox.transform, "Nothing", 11f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Italic);
            typeLine.rectTransform.anchorMin = new Vector2(0f, 1f);
            typeLine.rectTransform.anchorMax = new Vector2(1f, 1f);
            typeLine.rectTransform.pivot = new Vector2(0.5f, 1f);
            typeLine.rectTransform.offsetMin = new Vector2(8f, -20f);
            typeLine.rectTransform.offsetMax = new Vector2(-8f, -2f);
            view.TypeText = typeLine;

            var rules = UiFactory.CreateText(theme, "Rules", rulesBox.transform, "", 15f,
                UiPalette.TextMain, TextAlignmentOptions.TopLeft);
            UiFactory.Stretch(rules.rectTransform, 8, 6, 8, 22);
            rules.enableAutoSizing = true;
            rules.fontSizeMin = 11f;
            rules.fontSizeMax = 16f;
            view.RulesText = rules;

            // Marked-damage counter (bottom-right, above the rules box).
            var dmgGroup = UiFactory.CreateRect("Damage", frame.transform);
            UiFactory.Place(dmgGroup, new Vector2(1f, 0f), new Vector2(-8f, 108f), new Vector2(38f, 38f));
            var dmgBadge = UiFactory.CreateImage("Badge", dmgGroup, theme.Circle, UiPalette.Danger);
            UiFactory.Stretch(dmgBadge.rectTransform);
            var dmgOutline = dmgBadge.gameObject.AddComponent<Outline>();
            dmgOutline.effectColor = new Color(0f, 0f, 0f, 0.8f);
            dmgOutline.effectDistance = new Vector2(1f, -1f);
            var dmgText = UiFactory.CreateText(theme, "Value", dmgGroup, "0", 21f,
                Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(dmgText.rectTransform);
            dmgGroup.gameObject.SetActive(false);
            view.DamageGroup = dmgGroup.gameObject;
            view.DamageText = dmgText;

            return view;
        }
    }
}
