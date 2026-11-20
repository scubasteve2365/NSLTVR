using System.Globalization;
using System.Linq;
using HarmonyLib;
using Playful.SuperLucky.Camera;
using Playful.SuperLucky.Global;
using Playful.SuperLucky.Input;
using UnityEngine;

namespace NSLTVR
{
    // Why the mod touches input at all (Steve, build 3):
    //
    // 1. The game reads the stick from ONE device, InControl's "active device" (GlobalInputManager.GetCurrentDevice).
    //    Steve's build 3 log on RigA lists two devices while VR runs: "XInput Controller" (his pad) and "Unknown Device"
    //    (a second joystick Windows reports; which hardware is not verified: likely something SteamVR or the headset adds).
    //    The active device flipped between them several times a minute, and every frame the unknown one is active the
    //    stick reads centered. The game slows Lucky by how much the stick jumps (input volatility), so each flip stopped
    //    him, and the right stick went dead. Fix: while an Xbox pad is attached the game always reads the Xbox pad.
    //    The build 3 "fill short neutral gaps" patch is removed: it only hid this.
    // 2. The VR camera rig (Lucky's Tale VR's CurveTrackingCamera) never reads the right stick; only the flat camera does.
    //    Fix: the right stick turns the VR rig around Lucky (VR.TurnSpeed, 0 is off).
    //
    // Nothing else changes the game's controls. The mod menu reads the pad only to see the open chord, and switches the
    // game's input off while the menu is open.
    [HarmonyPatch(typeof(GlobalInputManager), nameof(GlobalInputManager.GetCurrentDevice))]
    static class PreferPadPatch
    {
        internal static bool On = true;
        static int swaps, frames;
        static float nextLog;
        static string lastWrong = "";
        static InControl.InputDevice pad;
        static int padFrame = -1;

        static InControl.InputDevice Pad()
        {
            if (padFrame == Time.frameCount) return pad;
            padFrame = Time.frameCount;
            pad = null;
            var devices = InControl.InputManager.Devices;
            if (devices == null) return null;
            for (int i = 0; i < devices.Count; i++)
                if (devices[i] is InControl.XInputDevice && devices[i].IsAttached) { pad = devices[i]; break; }
            return pad;
        }

        static void Postfix(ref InControl.InputDevice __result)
        {
            if (!On || __result is InControl.XInputDevice) return;
            var p = Pad();
            if (p == null || ReferenceEquals(p, __result)) return;
            if (__result != null) { swaps++; lastWrong = __result.Name + " (" + __result.Meta + ", " + __result.DeviceClass + ")"; }
            __result = p;
        }

        // Once a frame from the plugin: every 10 s, how often the game would have read another device.
        internal static void Tick()
        {
            frames++;
            if (Time.realtimeSinceStartup < nextLog) return;
            nextLog = Time.realtimeSinceStartup + 10f;
            if (!InControl.InputManager.IsSetup || InControl.InputManager.Devices == null) return;
            if (swaps > 0 || frames < 2000)
            {
                string names = string.Join(", ", InControl.InputManager.Devices.Select(d => d.Name + " [" + d.Meta + ", " + d.DeviceClass + (d.IsAttached ? "" : ", detached") + "]").ToArray());
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "INPUT the game would have read another device in {0} of {1} calls over 10 s{2}; devices: {3}; Unity joysticks: {4}",
                    swaps, frames, swaps > 0 ? " (last: " + lastWrong + "), the Xbox pad was used instead" : "", names.Length > 0 ? names : "none",
                    string.Join(", ", Input.GetJoystickNames().Select(n => "\"" + n + "\"").ToArray())));
            }
            swaps = 0;
            frames = 0;
        }
    }

    // Right stick: turn the VR camera rig around Lucky. CurveTrackingCamera places the camera at Lucky plus its own rotation
    // times the zone's offset, so turning the rig's yaw orbits it around him, and the left stick follows (the game takes
    // stick directions from the camera's right vector).
    [HarmonyPatch(typeof(CurveTrackingCamera), nameof(CurveTrackingCamera.DoMotorUpdate))]
    static class RightStickTurnPatch
    {
        internal static float DegreesPerSecond = 90f;
        static int logged;

        static void Prefix(CurveTrackingCamera __instance)
        {
            if (DegreesPerSecond <= 0f || !GameConfiguration.InVrCameraMode()) return;
            var player = Playful.SuperLucky.Gameplay.Player.PlayerController.Instance;
            if (player == null || player.Input == null || !player.Input.InputEnabled) return;
            float x = GlobalInputManager.RightStick.Value.x;
            if (Mathf.Abs(x) < 0.2f) return;
            var settings = PersistentSettings.Instance;
            if (settings != null && settings.HorizontalCameraInputInverted) x = -x;
            float sign = Mathf.Sign(x), mag = (Mathf.Abs(x) - 0.2f) / 0.8f;
            __instance.transform.Rotate(Vector3.up, sign * mag * DegreesPerSecond * Time.unscaledDeltaTime, Space.World);
            if (logged++ == 0) Plugin.Log.LogInfo("HOOK CurveTrackingCamera: right stick turns the VR camera around Lucky (" + DegreesPerSecond + " degrees a second at full tilt)");
        }
    }
}
