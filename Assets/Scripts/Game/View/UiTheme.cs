using TMPro;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// Scene-level bag of shared visual assets (built-in UI sprites, the TMP font and
    /// the card art index), assigned by the SceneBuilder so runtime-constructed UI can
    /// reach them without AssetDatabase access.
    /// </summary>
    public sealed class UiTheme : MonoBehaviour
    {
        [Tooltip("Sliced rounded-rect sprite (builtin UISprite).")]
        public Sprite Rounded;

        [Tooltip("Circle sprite (builtin Knob).")]
        public Sprite Circle;

        [Tooltip("Soft large-radius sliced sprite (builtin Background).")]
        public Sprite Soft;

        public TMP_FontAsset Font;

        public CardArtIndex ArtIndex;

        [Header("Icons (optional — wired once generated; UI degrades gracefully when null)")]
        public TMP_SpriteAsset Icons;
        public Sprite IconDmg;
        public Sprite IconAp;
        public Sprite IconXp;
        public Sprite IconStep;

        public Sprite Art(string id) => ArtIndex != null ? ArtIndex.GetSprite(id) : null;
    }
}
