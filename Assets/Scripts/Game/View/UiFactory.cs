using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Code-first uGUI construction helpers shared by the runtime views and the editor
    /// SceneBuilder. Everything is built from UiTheme assets — no prefabs, no Resources.
    /// </summary>
    public static class UiFactory
    {
        // ------------------------------------------------------------------ rects

        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            rt.anchoredPosition = Vector2.zero;
            return rt;
        }

        /// <summary>Stretch to fill the parent with edge insets.</summary>
        public static void Stretch(RectTransform rt, float left = 0, float bottom = 0, float right = 0, float top = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>Anchor + pivot at the same normalized point, explicit position/size.</summary>
        public static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // ------------------------------------------------------------------ graphics

        public static Image CreateImage(string name, Transform parent, Sprite sprite, Color color, bool raycast = false)
        {
            var rt = CreateRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = raycast;
            if (sprite != null && sprite.border != Vector4.zero)
                img.type = Image.Type.Sliced;
            return img;
        }

        /// <summary>Rounded panel in the house style: dark fill, subtle border, soft shadow.</summary>
        public static Image CreatePanel(UiTheme theme, string name, Transform parent, Color? fill = null)
        {
            var img = CreateImage(name, parent, theme.Rounded, fill ?? UiPalette.Panel, raycast: true);
            var outline = img.gameObject.AddComponent<Outline>();
            outline.effectColor = UiPalette.WithAlpha(UiPalette.Border, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);
            var shadow = img.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.45f);
            shadow.effectDistance = new Vector2(0f, -4f);
            return img;
        }

        public static TextMeshProUGUI CreateText(UiTheme theme, string name, Transform parent, string text,
            float size, Color color, TextAlignmentOptions align = TextAlignmentOptions.Center,
            FontStyles style = FontStyles.Normal)
        {
            var rt = CreateRect(name, parent);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (theme != null && theme.Font != null)
                tmp.font = theme.Font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.fontStyle = style;
            tmp.raycastTarget = false;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            return tmp;
        }

        // ------------------------------------------------------------------ controls

        public static Button CreateButton(UiTheme theme, string name, Transform parent, string label,
            float fontSize = 22f, Color? fill = null, Color? textColor = null)
        {
            var img = CreateImage(name, parent, theme.Rounded, fill ?? UiPalette.PanelLight, raycast: true);
            var outline = img.gameObject.AddComponent<Outline>();
            outline.effectColor = UiPalette.WithAlpha(UiPalette.Border, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);

            var button = img.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.18f, 1.15f, 1.05f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            button.colors = colors;

            var text = CreateText(theme, "Label", img.transform, label, fontSize,
                textColor ?? UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
            Stretch(text.rectTransform, 8, 4, 8, 4);
            return button;
        }

        public static TextMeshProUGUI ButtonLabel(Button button) =>
            button.GetComponentInChildren<TextMeshProUGUI>();

        public static ScrollRect CreateScrollView(UiTheme theme, string name, Transform parent,
            out RectTransform content)
        {
            var panel = CreateImage(name, parent, theme.Rounded, UiPalette.WithAlpha(UiPalette.Panel, 0.85f), raycast: true);
            var scroll = panel.gameObject.AddComponent<ScrollRect>();

            var viewport = CreateRect("Viewport", panel.transform);
            Stretch(viewport, 6, 6, 6, 6);
            var vpImage = viewport.gameObject.AddComponent<Image>();
            vpImage.color = new Color(0f, 0f, 0f, 0.01f);
            vpImage.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();

            content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            return scroll;
        }

        public static Slider CreateSlider(UiTheme theme, string name, Transform parent)
        {
            var root = CreateRect(name, parent);
            var slider = root.gameObject.AddComponent<Slider>();

            var bg = CreateImage("Background", root, theme.Rounded, UiPalette.PanelLight, raycast: true);
            var bgRt = bg.rectTransform;
            bgRt.anchorMin = new Vector2(0f, 0.5f);
            bgRt.anchorMax = new Vector2(1f, 0.5f);
            bgRt.sizeDelta = new Vector2(0f, 10f);

            var fillArea = CreateRect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.offsetMin = new Vector2(6f, -5f);
            fillArea.offsetMax = new Vector2(-6f, 5f);
            var fill = CreateImage("Fill", fillArea, theme.Rounded, UiPalette.Gold);
            fill.rectTransform.sizeDelta = new Vector2(10f, 0f);

            var handleArea = CreateRect("Handle Slide Area", root);
            Stretch(handleArea, 10, 0, 10, 0);
            var handle = CreateImage("Handle", handleArea, theme.Circle, UiPalette.TextMain, raycast: true);
            handle.rectTransform.sizeDelta = new Vector2(22f, 22f);

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            return slider;
        }

        public static Toggle CreateToggle(UiTheme theme, string name, Transform parent, string label)
        {
            var root = CreateRect(name, parent);
            var toggle = root.gameObject.AddComponent<Toggle>();

            var bg = CreateImage("Background", root, theme.Rounded, UiPalette.PanelLight, raycast: true);
            Place(bg.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(28f, 28f));
            var check = CreateImage("Checkmark", bg.transform, theme.Circle, UiPalette.Gold);
            Stretch(check.rectTransform, 6, 6, 6, 6);

            var text = CreateText(theme, "Label", root, label, 20f, UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            text.rectTransform.anchorMin = new Vector2(0f, 0f);
            text.rectTransform.anchorMax = new Vector2(1f, 1f);
            text.rectTransform.offsetMin = new Vector2(38f, 0f);
            text.rectTransform.offsetMax = Vector2.zero;

            toggle.targetGraphic = bg;
            toggle.graphic = check;
            return toggle;
        }

        public static CanvasGroup AddGroup(GameObject go)
        {
            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();
            return group;
        }

        /// <summary>Full-screen input-blocking dim layer used by modals.</summary>
        public static Image CreateDimmer(string name, Transform parent)
        {
            var img = CreateImage(name, parent, null, new Color(0f, 0f, 0f, 0.66f), raycast: true);
            Stretch((RectTransform)img.transform);
            return img;
        }
    }
}
