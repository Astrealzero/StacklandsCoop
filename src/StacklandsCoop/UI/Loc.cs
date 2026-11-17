using System;
using UnityEngine;

namespace StacklandsCoop.UI
{
    /// <summary>
    /// Minimal two-language helper. The mod follows the language picked in the game's
    /// settings (<see cref="SokLoc.instance"/>); anything that is not Chinese falls back
    /// to English. Only player-facing text goes through here — log lines stay as they are
    /// because the self-test scripts assert on their ASCII prefixes.
    /// </summary>
    internal static class Loc
    {
        private static bool _isChinese;
        private static float _checkedAt = -100f;

        public static bool IsChinese
        {
            get
            {
                Refresh();
                return _isChinese;
            }
        }

        public static string T(string english, string chinese)
        {
            return IsChinese ? chinese : english;
        }

        public static string T(string english, string chinese, string traditional)
        {
            if (!IsChinese)
            {
                return english;
            }

            return IsTraditional(out bool traditionalNow) && traditionalNow ? traditional : chinese;
        }

        /// <summary>0 = follow the game, 1 = force Chinese, 2 = force English.</summary>
        private static int _forced;

        /// <summary>Testing hook: "-coopLang zh" / "-coopLang en".</summary>
        public static void Force(string code)
        {
            if (string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase))
            {
                _forced = 1;
            }
            else if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
            {
                _forced = 2;
            }
            else
            {
                _forced = 0;
            }

            _checkedAt = -100f;
        }

        public static string Describe()
        {
            return _forced == 1 ? "zh (forced)" : _forced == 2 ? "en (forced)" : (IsChinese ? "zh" : "en");
        }

        /// <summary>Re-reads the game language at most once per second.</summary>
        public static void Refresh()
        {
            float now = Time.unscaledTime;
            if (now - _checkedAt < 1f)
            {
                return;
            }

            _checkedAt = now;

            if (_forced != 0)
            {
                _isChinese = _forced == 1;
                _lastTraditional = false;
                return;
            }

            _isChinese = DetectChinese(out bool traditional);
            _lastTraditional = traditional;
        }

        private static bool _lastTraditional;

        private static bool IsTraditional(out bool traditional)
        {
            Refresh();
            traditional = _lastTraditional;
            return _isChinese;
        }

        private static bool DetectChinese(out bool traditional)
        {
            traditional = false;

            try
            {
                SokLoc loc = SokLoc.instance;
                string language = loc != null ? loc.CurrentLanguage : null;
                if (!string.IsNullOrEmpty(language))
                {
                    if (language.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase))
                    {
                        traditional = language.IndexOf("Traditional", StringComparison.OrdinalIgnoreCase) >= 0;
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception)
            {
                // SokLoc not ready yet; fall through to the OS language.
            }

            switch (Application.systemLanguage)
            {
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                    return true;
                case SystemLanguage.ChineseTraditional:
                    traditional = true;
                    return true;
                default:
                    return false;
            }
        }
    }
}
