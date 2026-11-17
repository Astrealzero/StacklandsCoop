using System.Collections.Generic;
using StacklandsCoop.Session;
using UnityEngine;

namespace StacklandsCoop.UI
{
    /// <summary>
    /// Client-side conflict marker. The host's own Conflict.UpdateConflictArrows is frozen
    /// on a client (world clock is pinned), so fight markers would otherwise never appear
    /// there. Participants arrive in the snapshot; this draws a red line between them and a
    /// small marker over each card.
    /// </summary>
    public static class ConflictOverlay
    {
        private static Texture2D _pixel;

        private static Texture2D Pixel
        {
            get
            {
                if (_pixel == null)
                {
                    _pixel = new Texture2D(1, 1);
                    _pixel.SetPixel(0, 0, Color.white);
                    _pixel.Apply();
                }

                return _pixel;
            }
        }

        public static void Draw()
        {
            if (SessionController.Instance.Role != SessionRole.Client)
            {
                return;
            }

            World.ClientWorld world = World.ClientWorld.Instance;
            List<List<uint>> groups = world.Conflicts;
            if (groups == null || groups.Count == 0)
            {
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Color previous = GUI.color;
            for (int i = 0; i < groups.Count; i++)
            {
                List<uint> group = groups[i];
                if (group == null || group.Count < 2)
                {
                    continue;
                }

                Vector2 first = Vector2.zero;
                bool haveFirst = false;
                for (int j = 0; j < group.Count; j++)
                {
                    GameCard card;
                    if (!world.TryGetCard(group[j], out card) || card == null)
                    {
                        continue;
                    }

                    // Players can stand on different boards; only mark fights happening on
                    // the board this player is looking at.
                    if (card.MyBoard != null && !string.IsNullOrEmpty(world.MyBoard) &&
                        card.MyBoard.Id != world.MyBoard)
                    {
                        continue;
                    }

                    Vector3 screen = cam.WorldToScreenPoint(card.transform.position + new Vector3(0f, 0.45f, 0f));
                    if (screen.z < 0f)
                    {
                        continue;
                    }

                    var point = new Vector2(screen.x, Screen.height - screen.y);
                    if (!haveFirst)
                    {
                        first = point;
                        haveFirst = true;
                    }
                    else
                    {
                        Line(first, point, new Color(1f, 0.25f, 0.2f, 0.85f), 2f);
                    }

                    GUI.color = new Color(1f, 0.2f, 0.15f, 0.95f);
                    GUI.Label(new Rect(point.x - 6f, point.y - 10f, 30f, 20f), "!");
                }
            }

            GUI.color = previous;
        }

        private static void Line(Vector2 from, Vector2 to, Color color, float width)
        {
            float dx = to.x - from.x;
            float dy = to.y - from.y;
            int steps = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dy * dy) / 6f), 1, 64);
            GUI.color = color;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float x = from.x + dx * t;
                float y = from.y + dy * t;
                GUI.DrawTexture(new Rect(x - width * 0.5f, y - width * 0.5f, width, width), Pixel);
            }
        }
    }
}
