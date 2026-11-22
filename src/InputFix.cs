using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
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

    // Right stick camera (1.01, Steve): turn the view around Lucky.
    // Build 4 rotated the game's VR camera motor (CurveTrackingCamera) itself, and that went wrong: the motor assumes it faces
    // +z (its "too close" check measures only the z distance to Lucky and pushes the camera back along its own forward, so a
    // turned camera kept backing away: Steve's "zooms out really far"), and camera zones clamp its position to boxes made for
    // the front view. Now the game's camera is left exactly as the game moves it, and only the view is turned: the eyes orbit
    // Lucky by the turn angle when they are drawn, and the left stick is turned by the same angle, so up still runs away from
    // you. Cutscene cameras and 2D sections are never turned (2D sections ease back to straight).
    internal static class CameraOrbit
    {
        internal static float DegreesPerSecond = 90f;
        internal static float Yaw;
        internal static bool MenuOpen;
        static int logged;
        static float lastLog;

        internal static void Tick()
        {
            if (DegreesPerSecond <= 0f || !GameConfiguration.InVrCameraMode()) { Yaw = 0f; return; }
            var player = Playful.SuperLucky.Gameplay.Player.PlayerController.Instance;
            if (player == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (player.Use2DMovement()) { Yaw = Mathf.MoveTowardsAngle(Yaw, 0f, 180f * dt); return; }
            if (MenuOpen || player.Input == null || !player.Input.InputEnabled) return;
            float x = GlobalInputManager.RightStick.Value.x;
            if (Mathf.Abs(x) < 0.2f) return;
            var settings = PersistentSettings.Instance;
            if (settings != null && settings.HorizontalCameraInputInverted) x = -x;
            float mag = (Mathf.Abs(x) - 0.2f) / 0.8f;
            Yaw = Mathf.Repeat(Yaw + Mathf.Sign(x) * mag * mag * DegreesPerSecond * dt + 180f, 360f) - 180f;
            if (logged++ == 0 || Time.unscaledTime > lastLog + 2f)
            {
                lastLog = Time.unscaledTime;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "ORBIT right stick turns the view around Lucky: {0:F0} degrees ({1} degrees a second at full tilt)", Yaw, DegreesPerSecond));
            }
        }

        internal static void Reset() { if (Yaw != 0f) Plugin.Log.LogInfo("ORBIT view turned back to the game's own camera"); Yaw = 0f; }

        // From XrRig.Origin: turn the eye origin around Lucky, only for the game's own VR camera.
        internal static void Apply(Camera cam, ref Vector3 pos, ref Quaternion rot)
        {
            if (Yaw == 0f || cam == null || CameraTurn.On) return;
            var cm = CameraManager.Instance;
            var player = Playful.SuperLucky.Gameplay.Player.PlayerController.Instance;
            if (cm == null || player == null || cm.GetCameraGameObject() != cam.gameObject) return;
            var q = Quaternion.Euler(0f, Yaw, 0f);
            var pivot = player.transform.position;
            pos = pivot + q * (pos - pivot);
            rot = q * rot;
        }

        // PlayerInputManager.Update takes stick directions from the camera's right vector; turn it with the view.
        internal static Vector3 TurnInput(Vector3 right)
        {
            return Yaw == 0f || CameraTurn.On ? right : Quaternion.Euler(0f, Yaw, 0f) * right;
        }
    }

    [HarmonyPatch(typeof(PlayerInputManager), "Update")]
    static class OrbitInputPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var getRight = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.right));
            var turn = AccessTools.Method(typeof(CameraOrbit), nameof(CameraOrbit.TurnInput));
            int n = 0;
            foreach (var c in code)
            {
                yield return c;
                if (n == 0 && c.operand is MethodInfo mi && mi == getRight) { n++; yield return new CodeInstruction(OpCodes.Call, turn); }
            }
            Plugin.Log.LogInfo("HOOK PlayerInputManager.Update: stick directions follow the right stick camera (" + n + " place)");
        }
    }
}
