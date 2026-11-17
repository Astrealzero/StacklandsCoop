using System.Collections.Generic;
using UnityEngine;

namespace StacklandsCoop.UI
{
    /// <summary>
    /// Transient one-line banners (player joined, connection lost, host paused).
    /// IMGUI drawn; these never touch the world.
    /// </summary>
    public static class StatusBanner
    {
        private sealed class Item
        {
            public string Text;
            public float Until;
        }

        private static readonly List<Item> _items = new List<Item>();

        public static void Show(string text)
        {
            Show(text, 4f);
        }

        public static void Show(string text, float seconds)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _items.Add(new Item { Text = text, Until = Time.unscaledTime + Mathf.Max(1f, seconds) });
        }

        public static void Clear()
        {
            _items.Clear();
        }

        /// <summary>Draws every live banner stacked at the top centre of the screen.</summary>
        public static void Draw(Font font)
        {
            float now = Time.unscaledTime;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (now > _items[i].Until)
                {
                    _items.RemoveAt(i);
                }
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16
                };
                if (font != null)
                {
                    style.font = font;
                }

                float y = 14f + i * 26f;
                var rect = new Rect(Screen.width * 0.5f - 220f, y, 440f, 24f);
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(rect, _items[i].Text, style);
            }
        }
    }
}
