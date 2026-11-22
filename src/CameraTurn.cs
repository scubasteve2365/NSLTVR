using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Playful.SuperLucky.Camera;
using UnityEngine;

namespace NSLTVR
{
    // Lab prototype (Steve's option A, 2026-11-22): the right stick turns the game's own VR camera (CurveTrackingCamera)
    // instead of only the view (1.01). Off unless the lab command "turnmode camera" is given; then CameraOrbit keeps the angle
    // but no longer turns the view or the stick itself, and this class turns the camera motor.
    // The motor was written to face +z. Two places assume it:
    //  1. "Too close": num4 = |Lucky.z - camera.z|; if num4 < CloseZ, push back along the camera's forward. Turned, the z gap
    //     never grows, so the camera backs away for ever (build 4). Here the gap is measured along the camera's own forward.
    //  2. Camera zones and rails clamp the target to boxes and paths laid out for the front view. While turned the clamp is
    //     faded out (fully off from 30 degrees), so the camera can swing round Lucky.
    // Its wall check (ApplyCollisionAvoidance) only casts along world left/right and its repeller boxes are axis aligned; both
    // still run, unchanged.
    internal static class CameraTurn
    {
        internal static bool On;
        static Quaternion baseRot = Quaternion.identity, lastApplied = Quaternion.identity;
        static bool haveBase;
        static float lastYaw;
        static int others, bases;
        static float nextLog;
        static readonly AccessTools.FieldRef<CameraMotor, Vector3> target = AccessTools.FieldRefAccess<CameraMotor, Vector3>("_targetPosition");
        static readonly AccessTools.FieldRef<CurveTrackingCamera, Vector3> extra = AccessTools.FieldRefAccess<CurveTrackingCamera, Vector3>("_extraOffset");
        static readonly MethodInfo repeller = AccessTools.Method(typeof(CameraMotor), "GetPointOutsideRepeller");

        internal static void Set(bool on)
        {
            // Leaving camera mode: give the motor back the game's own rotation.
            if (On && !on && haveBase && CameraMotor.Instance is CurveTrackingCamera m) m.transform.rotation = baseRot;
            On = on;
            haveBase = false;
            lastYaw = CameraOrbit.Yaw;
            Plugin.Log.LogInfo("TURN mode " + (on ? "camera (the game's VR camera turns)" : "view (1.01)"));
        }

        [HarmonyPatch(typeof(CurveTrackingCamera), nameof(CurveTrackingCamera.DoMotorUpdate))]
        static class MotorPatch
        {
            static void Prefix(CurveTrackingCamera __instance)
            {
                if (!On) return;
                if (!ReferenceEquals(__instance, CameraMotor.Instance)) { if (others++ == 0) Plugin.Log.LogInfo("TURN a second CurveTrackingCamera runs (" + Probes.PathOf(__instance.transform) + "); only the active one is turned"); return; }
                var t = __instance.transform;
                // Swing the camera round Lucky with the turn, so he stays in view while the motor catches up.
                float dYaw = Mathf.DeltaAngle(lastYaw, CameraOrbit.Yaw);
                lastYaw = CameraOrbit.Yaw;
                if (dYaw != 0f && __instance.Tracking != null)
                {
                    var pivot = __instance.Tracking.transform.position;
                    t.position = pivot + Quaternion.Euler(0f, dYaw, 0f) * (t.position - pivot);
                }
                // The game sets the rotation itself sometimes (zone changes, SetRotation, cutscenes): take that as the new base.
                if (!haveBase || Quaternion.Angle(t.rotation, lastApplied) > 0.5f) { baseRot = t.rotation; haveBase = true; if (bases++ < 20) Plugin.Log.LogInfo("TURN the game set the camera rotation itself (yaw " + t.eulerAngles.y.ToString("F0") + "): new base"); }
                t.rotation = Quaternion.Euler(0f, CameraOrbit.Yaw, 0f) * baseRot;
                lastApplied = t.rotation;
            }

            static void Postfix(CurveTrackingCamera __instance)
            {
                if (!On || !ReferenceEquals(__instance, CameraMotor.Instance) || Time.unscaledTime < nextLog) return;
                nextLog = Time.unscaledTime + (CameraOrbit.Yaw != 0f ? 10f : 60f);
                var p = __instance.Tracking;
                var z = __instance.ActiveZone;
                var t = __instance.transform;
                Vector3 d = p != null ? p.transform.position - t.position : Vector3.zero;
                Vector3 flat = new Vector3(d.x, 0f, d.z);
                var fwd = t.forward; fwd.y = 0f;
                float off = flat.sqrMagnitude > 0.001f ? Vector3.Angle(fwd, flat) : 0f;
                var s = __instance.VrCameraActiveSettings;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "TURN yaw {0:F0} cam ({1:F1},{2:F1},{3:F1}) Lucky ({4:F1},{5:F1},{6:F1}) flat distance {7:F1} Lucky off-centre {8:F0} deg; zone {9} type {10} outside motion {11}; settings {12} follow path {13} CloseZ {14:F1}",
                    CameraOrbit.Yaw, t.position.x, t.position.y, t.position.z, p != null ? p.transform.position.x : 0f, p != null ? p.transform.position.y : 0f, p != null ? p.transform.position.z : 0f,
                    flat.magnitude, off, z != null ? z.name : "none", z != null ? z.ActiveZoneType.ToString() : "-", z != null && z.AllowsForOutsideMotion,
                    s != null ? s.name : "none", s != null && s.FollowPath, s != null ? s.CloseZ : 0f));
            }

            // Replace the one Mathf.Abs in DoMotorUpdate (the z gap to Lucky) with a gap along the camera's own forward.
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var abs = AccessTools.Method(typeof(Mathf), nameof(Mathf.Abs), new[] { typeof(float) });
                var gap = AccessTools.Method(typeof(CameraTurn), nameof(Gap));
                int n = 0;
                foreach (var c in code)
                {
                    if (c.operand is MethodInfo mi && mi == abs) { n++; yield return new CodeInstruction(OpCodes.Call, gap) { labels = c.labels, blocks = c.blocks }; }
                    else yield return c;
                }
                Plugin.Log.LogInfo("HOOK CurveTrackingCamera.DoMotorUpdate: too-close check can measure along the camera's forward (" + n + " place)");
            }
        }

        static float Gap(float zGap)
        {
            if (!On || CameraOrbit.Yaw == 0f) return Mathf.Abs(zGap);
            var m = CameraMotor.Instance;
            if (m == null || m.Tracking == null) return Mathf.Abs(zGap);
            var f = m.transform.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) return Mathf.Abs(zGap);
            return Mathf.Abs(Vector3.Dot(m.Tracking.Position - m.transform.position, f.normalized));
        }

        [HarmonyPatch(typeof(CurveTrackingCamera), nameof(CurveTrackingCamera.CalculatePositionFromOrigin))]
        static class ZonePatch
        {
            static void Postfix(CurveTrackingCamera __instance, Vector3 origin, bool ignoreGrounded)
            {
                if (!On || CameraOrbit.Yaw == 0f || !ReferenceEquals(__instance, CameraMotor.Instance) || __instance.Tracking == null || __instance.IsTargetPositionFrozen) return;
                var s = __instance.VrCameraActiveSettings;
                if (s == null) return;
                float w = Mathf.Clamp01(Mathf.Abs(CameraOrbit.Yaw) / 30f);
                Vector3 off = s.TargetOffset + extra(__instance);
                Vector3 free = origin + __instance.transform.rotation * off + Vector3.Scale(__instance.Tracking.Velocity * 0.15f, new Vector3(1f, 0f, 1f));
                if (!(s.IgnoreGroundedOverride || ignoreGrounded)) free.y = __instance.Tracking.LastGroundContactPosition.y + off.y;
                free.y = Mathf.Clamp(free.y, s.MinY, s.MaxY);
                free = (Vector3)repeller.Invoke(__instance, new object[] { free });
                ref Vector3 tp = ref target(__instance);
                tp = Vector3.Lerp(tp, free, w);
            }
        }
    
        // Lab: list camera zones (type, rail, bounds) and move Lucky into one.
        internal static void Zones(string arg)
        {
            var zones = Object.FindObjectsOfType<CameraZone>().OrderBy(z => z.name).ToArray();
            if (arg == null)
            {
                for (int i = 0; i < zones.Length; i++)
                {
                    var z = zones[i]; var c = z.GetComponent<Collider>();
                    Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "TURNZONE {0} {1} type {2} outside motion {3} vr {4} centre {5} size {6}",
                        i, z.name, z.ActiveZoneType, z.AllowsForOutsideMotion, z.VrZoneSettings != null ? z.VrZoneSettings.name : "-",
                        c != null ? c.bounds.center.ToString("F1") : "-", c != null ? c.bounds.size.ToString("F1") : "-"));
                }
                return;
            }
            int n = int.Parse(arg);
            var zc = zones[n].GetComponent<Collider>();
            var player = Playful.SuperLucky.Gameplay.Player.PlayerController.Instance;
            if (zc == null || player == null) return;
            player.Warp(zc.bounds.center, true, true);
            Plugin.Log.LogInfo("TURNZONE moved Lucky into zone " + n + " " + zones[n].name);
        }
    }
}
