using System.Collections.Generic;
using UnityEngine;

namespace StacklandsCoop.World
{
    /// <summary>
    /// Cosmetic attack replay for clients: the host tells us which card hit which, and we
    /// lunge the attacker toward the victim, shove the victim back a little, then settle
    /// both cards where the host put them. Damage numbers arrive as a separate message, so
    /// this never touches game state — the client's world is still the host's copy.
    /// </summary>
    public static class AttackFx
    {
        /// <summary>Total time of one swing (lunge + recoil).</summary>
        private const float SwingTime = 0.36f;

        private sealed class Swing
        {
            public GameCard Attacker;
            public GameCard Target;
            public Vector3 AttackFrom;
            public Vector3 AttackTo;
            public Vector3 TargetFrom;
            public Vector3 TargetTo;
            public float Elapsed;
        }

        private static readonly List<Swing> _active = new List<Swing>();
        private static readonly HashSet<GameCard> _busy = new HashSet<GameCard>();

        /// <summary>
        /// True while a card is being animated by the replay. Snapshot positions must not
        /// fight it, otherwise the swing is overwritten the moment a snapshot lands.
        /// </summary>
        public static bool IsBusy(GameCard card)
        {
            return card != null && _busy.Contains(card);
        }

        public static void Play(GameCard attacker, GameCard target)
        {
            if (attacker == null || target == null || attacker.transform == null || target.transform == null)
            {
                return;
            }

            Vector3 from = attacker.transform.position;
            Vector3 to = target.transform.position;
            Vector3 dir = to - from;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector3 forward = dir.normalized * Mathf.Min(0.5f, dir.magnitude * 0.55f);
            Vector3 knock = dir.normalized * -0.22f;

            _active.Add(new Swing
            {
                Attacker = attacker,
                Target = target,
                AttackFrom = from,
                AttackTo = from + forward,
                TargetFrom = target.transform.position,
                TargetTo = target.transform.position + knock,
                Elapsed = 0f
            });

            _busy.Add(attacker);
            _busy.Add(target);
        }

        public static void Clear()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                Swing s = _active[i];
                if (s.Attacker != null && s.Attacker.transform != null)
                {
                    s.Attacker.transform.position = s.AttackFrom;
                }

                if (s.Target != null && s.Target.transform != null)
                {
                    s.Target.transform.position = s.TargetFrom;
                }
            }

            _active.Clear();
            _busy.Clear();
        }

        /// <summary>Called every frame from the runtime; no-op on the host.</summary>
        public static void Tick(float dt)
        {
            if (_active.Count == 0)
            {
                return;
            }

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Swing s = _active[i];
                if (s.Attacker == null || s.Attacker.transform == null || s.Target == null || s.Target.transform == null)
                {
                    _active.RemoveAt(i);
                    _busy.Remove(s.Attacker);
                    _busy.Remove(s.Target);
                    continue;
                }

                s.Elapsed += dt;
                float t = s.Elapsed / SwingTime;
                if (t >= 1f)
                {
                    s.Attacker.transform.position = s.AttackFrom;
                    s.Target.transform.position = s.TargetFrom;
                    _busy.Remove(s.Attacker);
                    _busy.Remove(s.Target);
                    _active.RemoveAt(i);
                    continue;
                }

                // 0..0.4 strike forward, 0.4..0.6 hold, 0.6..1 return.
                float k;
                if (t < 0.4f)
                {
                    k = t / 0.4f;
                }
                else if (t < 0.6f)
                {
                    k = 1f;
                }
                else
                {
                    k = 1f - (t - 0.6f) / 0.4f;
                }

                s.Attacker.transform.position = Vector3.Lerp(s.AttackFrom, s.AttackTo, k);
                s.Target.transform.position = Vector3.Lerp(s.TargetFrom, s.TargetTo, Mathf.Clamp01(k * 1.6f));
            }
        }
    }
}
