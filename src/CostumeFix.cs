using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Playful.SuperLucky.Camera;
using Playful.SuperLucky.Global;
using Playful.SuperLucky.Ui;
using UnityEngine;

namespace NSLTVR
{
    // The costume shop (Steve, build 14, hub world): UIScreenCostume.ShowRoutine starts with
    //   flat = CameraManager.Instance.GetFlatScreenCamera(); if (flat == null) give up; sound position = flat.MyListener.transform.position
    // With the VR rig there is no working flat camera (or one without a listener), so the shop either gave up silently or threw
    // (NullReferenceException in ShowRoutine, Steve's log) and nothing showed. In VR the shop gets a stand-in flat camera that is
    // never switched on, whose listener is the VR camera, so the menu sound plays where you are and the screen opens.
    internal static class CostumeFix
    {
        static GameObject holder;
        static FramingCamera standIn;
        static int logged;

        // Every game method that reads the flat camera's listener (all found by a full decompile of Assembly-CSharp, build 15):
        // the costume shop's ShowRoutine, and four "how far is this sound from the listener" checks (AnimationEffects,
        // TransforminatorEventListener, SharedTriggerable, SharedTriggerableReaction). In VR they get the stand-in, whose
        // listener is the VR camera, so sound range checks measure from where you really are.
        static readonly string[] ListenerTypes =
        {
            "Playful.SuperLucky.Ui.UIScreenCostume", "AnimationEffects", "Playful.Unity.DesignerTools.TransforminatorEventListener",
            "Playful.Unity.Events.SharedTriggerable", "Playful.Unity.Events.SharedTriggerableReaction",
        };

        internal static void Apply(Harmony harmony)
        {
            var get = AccessTools.Method(typeof(CameraManager), nameof(CameraManager.GetFlatScreenCamera));
            var listener = AccessTools.Field(typeof(FramingCamera), nameof(FramingCamera.MyListener));
            var asm = typeof(CameraManager).Assembly;
            int patched = 0;
            foreach (var name in ListenerTypes)
            {
                var type = asm.GetType(name);
                if (type == null) { Plugin.Log.LogWarning("HOOK flat-camera listener: class " + name + " not found"); continue; }
                foreach (var t in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        List<CodeInstruction> il;
                        try { il = PatchProcessor.GetOriginalInstructions(m); } catch (Exception) { continue; }
                        if (!il.Any(c => c.operand is MethodInfo mi && mi == get) || !il.Any(c => c.operand is FieldInfo f && f == listener)) continue;
                        harmony.Patch(m, transpiler: new HarmonyMethod(typeof(CostumeFix), nameof(Transpiler)));
                        patched++;
                    }
            }
            Plugin.Log.LogInfo("HOOK flat-camera listener: " + patched + " methods use the VR camera as the listener in VR (costume shop, sound range checks)");
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var get = AccessTools.Method(typeof(CameraManager), nameof(CameraManager.GetFlatScreenCamera));
            var safe = AccessTools.Method(typeof(CostumeFix), nameof(FlatOrStandIn));
            foreach (var c in code)
            {
                if ((c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && c.operand is MethodInfo mi && mi == get)
                    yield return new CodeInstruction(OpCodes.Call, safe) { labels = c.labels, blocks = c.blocks };
                else
                    yield return c;
            }
        }

        static FramingCamera FlatOrStandIn(CameraManager cm)
        {
            var flat = cm.GetFlatScreenCamera();
            if (!GameConfiguration.InVrCameraMode()) return flat;
            if (standIn == null)
            {
                holder = new GameObject("NSLTVR flat camera stand-in");
                holder.SetActive(false);  // never runs: no Awake, no Update
                UnityEngine.Object.DontDestroyOnLoad(holder);
                standIn = holder.AddComponent<FramingCamera>();
                standIn.enabled = false;
            }
            var listener = cm.GetCameraGameObject();
            standIn.MyListener = listener != null ? listener : cm.gameObject;
            if (logged++ < 5) Plugin.Log.LogInfo("HOOK flat-camera listener: stand-in used, listener " + standIn.MyListener.name);
            return standIn;
        }

        // Lab: open or close the shop wherever Lucky is.
        internal static void Lab(string what)
        {
            var shop = UIScreenCostume.Instance;
            if (shop == null) { Plugin.Log.LogInfo("COSTUME no shop instance"); return; }
            if (what == "cams")
            {
                foreach (var c in Camera.allCameras)
                    Plugin.Log.LogInfo("COSTUME camera " + Probes.PathOf(c.transform) + " depth " + c.depth + " mask 0x" + c.cullingMask.ToString("X") + " target " + (c.targetTexture != null ? c.targetTexture.name + " " + c.targetTexture.width + "x" + c.targetTexture.height : "window") + " clear " + c.clearFlags + " layer " + c.gameObject.layer);
                return;
            }
            if (what == "close") shop.Hide(false);
            else AccessTools.Method(typeof(UIScreenCostume), "Show").Invoke(shop, null);
            Plugin.Log.LogInfo("COSTUME " + what + "; Camera.main is " + (Camera.main != null ? Camera.main.name + " (" + Camera.main.transform.parent?.name + ")" : "none"));
        }
    
    }

    // In VR the CameraManager still carries the flat game's FramingCamera component, switched off and never set up (no camera,
    // no listener). About 25 places in the game check "is there a flat camera?" and then drive it: zone blends that move the
    // camera rig, cutscenes that set its field of view and rotation, spawn head-look. In VR the answer is now always "no".
    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.GetFlatScreenCamera))]
    static class NoFlatCameraInVr
    {
        static void Postfix(ref FramingCamera __result)
        {
            if (__result != null && GameConfiguration.InVrCameraMode()) __result = null;
        }
    }

    // The storybook (BookInteractHandler.DoEndSequence) calls CameraManager.GetComponent<FramingCamera>().Warp() directly.
    // In VR the VR camera motor is snapped instead.
    [HarmonyPatch(typeof(BookInteractHandler), "DoEndSequence")]
    static class BookWarpPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var motor = AccessTools.Method(typeof(BookWarpPatch), nameof(Motor));
            foreach (var c in code)
            {
                if (c.operand is MethodInfo mi && mi.Name == "GetComponent" && mi.IsGenericMethod && mi.GetGenericArguments()[0] == typeof(FramingCamera))
                    yield return new CodeInstruction(OpCodes.Call, motor) { labels = c.labels, blocks = c.blocks };
                else
                    yield return c;
            }
        }

        static CameraMotor Motor(Component cm)
        {
            if (!GameConfiguration.InVrCameraMode()) return cm.GetComponent<FramingCamera>();
            var m = CameraMotor.Instance != null ? CameraMotor.Instance : cm.GetComponent<CurveTrackingCamera>();
            Plugin.Log.LogInfo("HOOK storybook closed: VR camera snapped back (" + (m != null ? m.GetType().Name : "none") + ")");
            return m;
        }
    }
}
