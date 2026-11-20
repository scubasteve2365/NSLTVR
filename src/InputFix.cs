using System.Globalization;
using HarmonyLib;
using Playful.SuperLucky.Input;
using UnityEngine;

namespace NSLTVR
{
    // Steve, build 2 (RigA): holding the left stick moves Lucky, but the slightest change of angle stops him. The game slows
    // Lucky by "input volatility" (PlayerInputManager.CalculateInputVolatility: how far the stick jumped frame to frame over
    // the last 45 frames). On RigB a single frame of a neutral stick between two held directions gave a volatility of 2.6
    // and stopped Lucky dead; a smooth turn of the same stick did not. So a stick that briefly reads neutral while it is
    // being turned stops him. Not yet known why Steve's stick reads neutral (Steam Input, a second device, the headset
    // runtime). This fills up to 70 ms of neutral stick right after a held direction with the last held value, counts them, and logs the input devices.
    [HarmonyPatch(typeof(GlobalInputManager), "get_LeftStick")]
    static class StickDropoutPatch
    {
        internal static bool On = true;
        static Vector2 lastStrong;
        static float lastStrongTime = -100f;
        static int lastStrongFrame = -100, filledFrame = -1, frames, filled;
        static float nextLog;

        static void Postfix(ref GlobalInputManager.AnalogState __result)
        {
            int f = Time.frameCount;
            var v = __result.Value;
            if (v.sqrMagnitude > 0.25f) { lastStrong = v; lastStrongFrame = f; lastStrongTime = Time.unscaledTime; return; }
            if (!On || f == lastStrongFrame || Time.unscaledTime - lastStrongTime > 0.07f) return;
            if (v.sqrMagnitude < 0.04f)
            {
                if (filledFrame != f) { filledFrame = f; filled++; }
                __result = new GlobalInputManager.AnalogState(lastStrong.x, lastStrong.y);
            }
        }

        // Called once a frame by the plugin: counts frames and logs a line every 10 s while anything happened.
        internal static void Tick()
        {
            frames++;
            if (Time.realtimeSinceStartup < nextLog) return;
            nextLog = Time.realtimeSinceStartup + 10f;
            if (!InControl.InputManager.IsSetup || InControl.InputManager.Devices == null) return;
            var active = InControl.InputManager.ActiveDevice;
            string names = "";
            foreach (var d in InControl.InputManager.Devices) names += (names.Length > 0 ? ", " : "") + d.Name + (d.IsAttached ? "" : " (detached)");
            if (filled > 0 || frames < 2000)
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "INPUT stick neutral-frame dropouts filled {0} in {1} frames (fill {2}); active device \"{3}\"; devices: {4}",
                    filled, frames, On ? "on" : "off", active != null ? active.Name : "none", names.Length > 0 ? names : "none"));
            filled = 0;
            frames = 0;
        }
    }
}
