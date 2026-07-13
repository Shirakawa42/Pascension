using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Pascension.Game.UI
{
    /// <summary>
    /// Display-only localization. Screens build labels through <see cref="T"/> at
    /// construction time; switching language reloads the scene (nothing re-renders
    /// static text). English is the source language AND the key — untranslated strings
    /// fall through unchanged, so partial coverage degrades gracefully. Engine/wire
    /// strings stay English; card faces translate via SoiFrenchCards at the display
    /// layer only.
    /// </summary>
    public static class Loc
    {
        private static bool? _french;

        public static bool French
        {
            get
            {
                _french ??= PlayerPrefs.GetString(SceneFlow.PrefLanguage, "en") == "fr";
                return _french.Value;
            }
        }

        public static void SetFrench(bool french)
        {
            PlayerPrefs.SetString(SceneFlow.PrefLanguage, french ? "fr" : "en");
            PlayerPrefs.Save();
            _french = french;
        }

        /// <summary>Translate a UI string (English text is the lookup key).</summary>
        public static string T(string english)
        {
            if (!French || string.IsNullOrEmpty(english)) return english;
            return LocFrench.Ui.TryGetValue(english, out var fr) ? fr : english;
        }

        /// <summary>French "de {name}" with elision: "de Tetra" but "d'Alice".</summary>
        public static string De(string name)
        {
            if (string.IsNullOrEmpty(name)) return "de " + name;
            char c = char.ToUpperInvariant(name[0]);
            bool vowel = c is 'A' or 'E' or 'I' or 'O' or 'U' or 'Y' or 'É' or 'À' or 'Â' or 'Î' or 'Ô' or 'H';
            return vowel ? "d'" + name : "de " + name;
        }

        /// <summary>Engine decision titles arrive as English text with interpolated
        /// numbers/names; translate the known templates by pattern, pass through the
        /// rest, then localize interpolated faction words and SoI card names.
        /// Display-only.</summary>
        public static string DecisionTitle(string title)
        {
            if (!French || string.IsNullOrEmpty(title)) return title;
            foreach (var (pattern, replacement) in LocFrench.DecisionTitles)
                if (pattern.IsMatch(title))
                    return LocalizeInterpolations(pattern.Replace(title, replacement));
            return LocalizeInterpolations(title);
        }

        /// <summary>Fixed decision-option labels ("Reveal", "Banish", "Yes"…).</summary>
        public static string OptionLabel(string english)
        {
            if (!French || string.IsNullOrEmpty(english)) return english;
            if (LocFrench.OptionLabels.TryGetValue(english, out var fr)) return fr;
            return LocalizeInterpolations(english);
        }

        private static Dictionary<string, string> _soiNameMap;

        /// <summary>Engine strings interpolate English faction enum names and English
        /// card names; map both to their official French forms (longest names first so
        /// no name replaces inside another).</summary>
        private static string LocalizeInterpolations(string text)
        {
            text = Regex.Replace(text, @"\bUndergrowth\b", "Maquis");
            text = Regex.Replace(text, @"\bWraethe\b", "Spectra");
            text = Regex.Replace(text, @"\bOrder\b", "Ordre");

            if (_soiNameMap == null)
            {
                _soiNameMap = new Dictionary<string, string>();
                Shards.Content.ShardsContentRegistry.EnsureRegistered();
                foreach (var def in Shards.Engine.ShardsCardDatabase.All)
                    if (Soi.SoiFrenchCards.Cards.TryGetValue(def.Id, out var entry) &&
                        !string.IsNullOrEmpty(entry.Name) && entry.Name != def.Name)
                        _soiNameMap[def.Name] = entry.Name;
            }
            foreach (var pair in _soiNameMap)
                if (text.Contains(pair.Key))
                    text = text.Replace(pair.Key, pair.Value);
            return text;
        }

        /// <summary>SoI card display name for the current language.</summary>
        public static string CardName(string defId, string englishName)
        {
            if (French && SoiFrenchNames(defId, out var name)) return name;
            return englishName;
        }

        /// <summary>SoI card rules text for the current language.</summary>
        public static string CardText(string defId, string englishText)
        {
            if (French && Soi.SoiFrenchCards.Cards.TryGetValue(defId, out var entry) &&
                !string.IsNullOrEmpty(entry.Text))
                return entry.Text;
            return englishText;
        }

        private static bool SoiFrenchNames(string defId, out string name)
        {
            if (defId != null && Soi.SoiFrenchCards.Cards.TryGetValue(defId, out var entry) &&
                !string.IsNullOrEmpty(entry.Name))
            {
                name = entry.Name;
                return true;
            }
            name = null;
            return false;
        }
    }
}
