using System;

namespace StacklandsCoop.UI
{
    /// <summary>
    /// Edge-detected function keys.
    ///
    /// wasPressedThisFrame looked right but never fired in the running game, so the hotkeys
    /// were dead. Reading isPressed and remembering the previous frame is independent of
    /// which frame the Input System processes events in, which makes it reliable here.
    /// </summary>
    internal static class Hotkeys
    {
        private static bool _f9Down;
        private static bool _f10Down;
        private static bool _warned;

        public static bool F9Pressed()
        {
            return Pressed(UnityEngine.InputSystem.Key.F9, ref _f9Down, UnityEngine.KeyCode.F9);
        }

        public static bool F10Pressed()
        {
            return Pressed(UnityEngine.InputSystem.Key.F10, ref _f10Down, UnityEngine.KeyCode.F10);
        }

        private static bool Pressed(UnityEngine.InputSystem.Key key, ref bool down, UnityEngine.KeyCode legacy)
        {
            bool isDown = false;
            try
            {
                UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        CoopLog.Warn("input: no Input System keyboard device");
                    }
                }
                else
                {
                    isDown = kb[key].isPressed;
                }
            }
            catch (Exception)
            {
                isDown = false;
            }

            if (!isDown)
            {
                try
                {
                    isDown = UnityEngine.Input.GetKey(legacy);
                }
                catch (Exception)
                {
                    isDown = false;
                }
            }

            bool pressed = isDown && !down;
            down = isDown;
            return pressed;
        }
    }
}
