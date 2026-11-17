using System;
using UnityEngine;

namespace StacklandsCoop.UI
{
    /// <summary>
    /// Paper-and-ink styling for the co-op panel, to sit next to Stacklands' own
    /// hand-drawn UI. Everything is generated at runtime (rounded 9-slice textures),
    /// so the mod ships without any art assets.
    /// </summary>
    public static class CoopTheme
    {
        public static readonly Color Paper = new Color(0.965f, 0.937f, 0.863f);
        public static readonly Color PaperShade = new Color(0.910f, 0.867f, 0.769f);
        public static readonly Color Field = new Color(0.988f, 0.973f, 0.929f);
        public static readonly Color Ink = new Color(0.216f, 0.153f, 0.086f);
        public static readonly Color InkSoft = new Color(0.435f, 0.341f, 0.220f);
        public static readonly Color Edge = new Color(0.541f, 0.353f, 0.169f);
        public static readonly Color Accent = new Color(0.933f, 0.675f, 0.263f);
        public static readonly Color AccentDeep = new Color(0.792f, 0.494f, 0.145f);
        public static readonly Color Blue = new Color(0.431f, 0.576f, 0.722f);
        public static readonly Color Danger = new Color(0.749f, 0.286f, 0.239f);
        public static readonly Color Ok = new Color(0.365f, 0.612f, 0.353f);
        public static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        private const int SliceSize = 48;
        private const int SliceRadius = 12;
        private const int SliceBorder = 16;

        private static bool _built;
        private static Texture2D _dot;

        public static GUIStyle Window;
        public static GUIStyle Card;
        public static GUIStyle Title;
        public static GUIStyle Label;
        public static GUIStyle Muted;
        public static GUIStyle Hint;
        public static GUIStyle Button;
        public static GUIStyle ButtonPrimary;
        public static GUIStyle ButtonGhost;
        public static GUIStyle Toggle;
        public static GUIStyle TextField;
        public static Texture2D CursorArrow;
        public static GUIStyle Chip;
        public static GUIStyle ChipOn;
        public static GUIStyle BarBackground;
        public static GUIStyle BarFill;

        public static void EnsureBuilt(Font font)
        {
            if (_built)
            {
                return;
            }

            _built = true;

            Texture2D window = Rounded(Paper, Edge, 3);
            Texture2D card = Rounded(PaperShade, Edge, 2);
            Texture2D field = Rounded(Field, Edge, 2);
            Texture2D button = Rounded(PaperShade, Edge, 2);
            Texture2D buttonHover = Rounded(Paper, Edge, 2);
            Texture2D buttonActive = Rounded(Accent, Edge, 2);
            Texture2D primary = Rounded(Accent, Edge, 2);
            Texture2D primaryHover = Rounded(AccentDeep, Edge, 2);
            Texture2D ghost = Rounded(Clear, Clear, 0);
            Texture2D ghostHover = Rounded(new Color(0f, 0f, 0f, 0.08f), Clear, 0);
            Texture2D chip = Rounded(PaperShade, Edge, 2);
            Texture2D chipOn = Rounded(Accent, Edge, 2);
            Texture2D barBg = Rounded(new Color(0f, 0f, 0f, 0.22f), Clear, 0);
            Texture2D barFill = Rounded(Accent, AccentDeep, 1);

            Window = Box(window, 10, 10, Ink);
            Window.padding = new RectOffset(12, 12, 10, 10);

            Card = Box(card, 10, 8, Ink);
            Card.padding = new RectOffset(8, 8, 6, 6);
            Card.margin = new RectOffset(0, 0, 4, 4);

            Title = Text(font, 15, Ink);
            Title.fontStyle = FontStyle.Bold;
            Title.padding = new RectOffset(0, 0, 2, 2);

            Label = Text(font, 12, Ink);
            Label.padding = new RectOffset(0, 0, 1, 1);

            Muted = Text(font, 11, InkSoft);

            Hint = Text(font, 10, new Color(0.435f, 0.341f, 0.220f, 0.8f));

            Button = Textured(button, buttonHover, buttonActive, font, 12, Ink);
            Button.padding = new RectOffset(10, 10, 6, 6);
            Button.margin = new RectOffset(0, 0, 2, 2);

            ButtonPrimary = Textured(primary, primaryHover, primaryHover, font, 12, new Color(0.231f, 0.153f, 0.071f));
            ButtonPrimary.fontStyle = FontStyle.Bold;
            ButtonPrimary.padding = new RectOffset(10, 10, 7, 7);
            ButtonPrimary.margin = new RectOffset(0, 0, 3, 3);

            ButtonGhost = Textured(ghost, ghostHover, ghostHover, font, 12, InkSoft);
            ButtonGhost.padding = new RectOffset(6, 6, 4, 4);

            Toggle = Textured(button, buttonHover, buttonHover, font, 12, Ink);
            Toggle.padding = new RectOffset(8, 8, 4, 4);

            TextField = Textured(field, field, field, font, 12, Ink);
            CursorArrow = MakeArrow();
            TextField.padding = new RectOffset(8, 8, 5, 5);
            TextField.margin = new RectOffset(0, 0, 1, 1);

            Chip = Textured(chip, buttonHover, chipOn, font, 11, Ink);
            Chip.padding = new RectOffset(8, 8, 4, 4);

            ChipOn = Textured(chipOn, chipOn, chipOn, font, 11, new Color(0.231f, 0.153f, 0.071f));

            BarBackground = Box(barBg, 5, 5, Clear);
            BarFill = Box(barFill, 5, 5, Clear);

            _dot = Circle(8, Blue);
        }

        public static Texture2D Dot(Color color)
        {
            if (_dot == null)
            {
                _dot = Circle(8, color);
            }

            return _dot;
        }

        /// <summary>Draws a rounded filled bar; fraction is clamped to 0..1.</summary>
        public static void DrawBar(Rect rect, float fraction)
        {
            GUI.DrawTexture(rect, BarBackground.normal.background, ScaleMode.StretchToFill);
            float w = Mathf.Clamp01(fraction) * Mathf.Max(0f, rect.width - 4f);
            if (w > 1f)
            {
                GUI.DrawTexture(new Rect(rect.x + 2f, rect.y + 2f, w, rect.height - 4f),
                    BarFill.normal.background, ScaleMode.StretchToFill);
            }
        }

        private static GUIStyle Box(Texture2D tex, int pad, int border, Color textColor)
        {
            var style = new GUIStyle
            {
                normal = { background = tex },
                border = new RectOffset(SliceBorder, SliceBorder, SliceBorder, SliceBorder),
                padding = new RectOffset(pad, pad, pad, pad),
                margin = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                richText = false
            };
            style.normal.textColor = textColor;
            return style;
        }

        private static GUIStyle Text(Font font, int size, Color color)
        {
            var style = new GUIStyle(GUIStyle.none)
            {
                font = font,
                fontSize = size,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                margin = new RectOffset(0, 0, 1, 1)
            };
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            return style;
        }

        private static GUIStyle Textured(Texture2D normal, Texture2D hover, Texture2D active,
            Font font, int size, Color color)
        {
            var style = new GUIStyle
            {
                font = font,
                fontSize = size,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(SliceBorder, SliceBorder, SliceBorder, SliceBorder),
                normal = { background = normal },
                hover = { background = hover },
                active = { background = active },
                focused = { background = hover },
                clipping = TextClipping.Clip,
                wordWrap = false
            };
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            return style;
        }


        /// <summary>
        /// A plain arrow pointer shaped like the desktop cursor the game already uses, drawn
        /// procedurally: white fill with a dark rim, which is then tinted per player at draw time.
        /// </summary>
        private static Texture2D MakeArrow()
        {
            const int w = 20;
            const int h = 30;
            const int ss = 3;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[w * h];

            // Classic arrow outline, scaled to the texture and nudged in from the edge.
            float[] ax = { 0.6f, 0.6f, 5.8f, 9.3f, 13.0f, 15.8f, 9.5f };
            float[] ay = { 0.6f, 21.3f, 16.5f, 26.5f, 24.8f, 14.9f, 14.9f };

            var cover = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float hit = 0f;
                    for (int sy = 0; sy < ss; sy++)
                    {
                        for (int sx = 0; sx < ss; sx++)
                        {
                            float px0 = x + (sx + 0.5f) / ss;
                            float py0 = y + (sy + 0.5f) / ss;
                            if (InPolygon(px0, py0, ax, ay))
                            {
                                hit += 1f;
                            }
                        }
                    }

                    cover[y * w + x] = hit / (ss * ss);
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float inside = cover[y * w + x];
                    float dilated = inside;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox;
                            int ny = y + oy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                            {
                                continue;
                            }

                            dilated = Mathf.Max(dilated, cover[ny * w + nx]);
                        }
                    }

                    Color32 c = new Color32(0, 0, 0, 0);
                    if (inside > 0.3f)
                    {
                        c = new Color32(255, 255, 255, 255);
                    }
                    else if (dilated > 0.3f)
                    {
                        c = new Color32(48, 34, 16, 245);
                    }

                    px[y * w + x] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        private static bool InPolygon(float x, float y, float[] ax, float[] ay)
        {
            bool inside = false;
            for (int i = 0, j = ax.Length - 1; i < ax.Length; j = i++)
            {
                if ((ay[i] > y) != (ay[j] > y) &&
                    x < (ax[j] - ax[i]) * (y - ay[i]) / (ay[j] - ay[i]) + ax[i])
                {
                    inside = !inside;
                }
            }

            return inside;
        }


        private static Texture2D Rounded(Color fill, Color border, int borderWidth)
        {
            var tex = new Texture2D(SliceSize, SliceSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[SliceSize * SliceSize];
            float half = SliceSize * 0.5f;
            float inner = half - SliceRadius;

            for (int y = 0; y < SliceSize; y++)
            {
                for (int x = 0; x < SliceSize; x++)
                {
                    float px = x + 0.5f - half;
                    float py = y + 0.5f - half;
                    float qx = Mathf.Abs(px) - inner;
                    float qy = Mathf.Abs(py) - inner;
                    float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) +
                                               Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
                    float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - SliceRadius;
                    float alpha = Mathf.Clamp01(0.5f - d);

                    Color color;
                    if (borderWidth > 0 && d > -borderWidth - 0.5f)
                    {
                        color = border;
                    }
                    else
                    {
                        color = fill;
                    }

                    color.a *= alpha;
                    pixels[y * SliceSize + x] = color;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static Texture2D Circle(int size, Color color)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half;
                    float dy = y + 0.5f - half;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    Color c = color;
                    c.a *= Mathf.Clamp01(half - dist);
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
