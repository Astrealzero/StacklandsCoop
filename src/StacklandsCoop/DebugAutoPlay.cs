using System;
using System.Reflection;
using UnityEngine;

namespace StacklandsCoop
{
    /// <summary>
    /// Developer-only helper for automated smoke tests: -coopPlay makes the instance
    /// load the current save and enter the game without clicking through the menu.
    /// Uses reflection because SaveManager.Load is not public.
    /// </summary>
    internal static class DebugAutoPlay
    {
        private static bool _armed;
        private static float _stepAt = -1f;
        private static int _phase;

        internal static void Arm()
        {
            _armed = true;
            _phase = 0;
            CoopLog.Info("debug autoplay armed");
        }

        internal static void Tick()
        {
            if (!_armed)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            if (wm.CurrentGameState != WorldManager.GameState.InMenu)
            {
                _armed = false;
                CoopLog.Info("debug autoplay: left menu, state=" + wm.CurrentGameState);
                return;
            }

            if (_stepAt < 0f)
            {
                _stepAt = Time.unscaledTime + 3f;
                return;
            }

            if (Time.unscaledTime < _stepAt)
            {
                return;
            }

            try
            {
                if (_phase == 0)
                {
                    _phase = 1;
                    _stepAt = Time.unscaledTime + 2f;
                    CoopLog.Info("debug autoplay: SaveManager.Load()");
                    MethodInfo load = typeof(SaveManager).GetMethod(
                        "Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (load == null)
                    {
                        CoopLog.Error("debug autoplay: SaveManager.Load not found");
                        _armed = false;
                        return;
                    }

                    load.Invoke(SaveManager.instance, null);
                    CoopLog.Info("debug autoplay: load returned");
                    return;
                }

                _armed = false;
                CoopLog.Info("debug autoplay: WorldManager.Play()");
                wm.Play();
                CoopLog.Info("debug autoplay: play returned state=" + wm.CurrentGameState +
                             " cards=" + (wm.AllCards == null ? -1 : wm.AllCards.Count));
            }
            catch (Exception ex)
            {
                CoopLog.Error("debug autoplay failed: " + ex);
                _armed = false;
            }
        }
    }
}
