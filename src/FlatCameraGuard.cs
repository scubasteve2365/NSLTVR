using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Playful.SuperLucky.Camera;
using UnityEngine;

namespace NSLTVR
{
    // With the VR rig, CameraManager.GetFlatScreenCamera() is null (the flat motor is never added), yet about twenty places
    // in the game call a method on it without a check: cutscenes, fox holes, teleporters, carried objects, climbing poles.
    // Each throws, and a throw inside a coroutine stops it halfway (the stuck fades of builds 5 to 8). This replaces every
    // such call in those classes with a null-safe one that does nothing when there is no flat camera.
    internal static class FlatCameraGuard
    {
        static readonly string[] TypeNames =
        {
            "AnimationEffects", "FoxHole", "PlayerSpawnPoint", "TeleportPlatform",
            "Playful.SuperLucky.Camera.AutoHeadLookMoment", "Playful.SuperLucky.Camera.CameraSimulator",
            "Playful.SuperLucky.Camera.Cinemachine.CameraTrackingTargetPositioner", "Playful.SuperLucky.Camera.Cinemachine.DefaultFreeLookCameraModifier",
            "Playful.SuperLucky.Gameplay.ClimbingPole", "Playful.SuperLucky.Gameplay.Context.ContextInteractor",
            "Playful.SuperLucky.Gameplay.Interactables.Carryable", "Playful.SuperLucky.Gameplay.Minigames.Marble.MarbleController",
            "Playful.SuperLucky.Gameplay.Player.PlayerController", "Playful.SuperLucky.Sequences.CinematicSequence",
            "Playful.SuperLucky.Sequences.CutsceneSequence", "Playful.SuperLucky.Timeline.TimelineHelper",
            "Playful.SuperLucky.UI.BookInteractHandler", "Playful.SuperLucky.UI.UIScreenCostume",
            "Playful.Unity.DesignerTools.TransforminatorEventListener", "Playful.Unity.Events.SharedTriggerable",
        };

        static Dictionary<MethodInfo, MethodInfo> map;
        internal static int Calls;

        internal static void Apply(Harmony harmony)
        {
            map = new Dictionary<MethodInfo, MethodInfo>();
            foreach (var safe in typeof(FlatCameraGuard).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Where(m => m.Name.StartsWith("Safe_", StringComparison.Ordinal)))
            {
                var pars = safe.GetParameters().Skip(1).Select(p => p.ParameterType).ToArray();
                var target = AccessTools.Method(typeof(FramingCamera), safe.Name.Substring(5), pars);
                if (target != null) map[target] = safe;
            }
            var asm = typeof(CameraManager).Assembly;
            var transpiler = new HarmonyMethod(typeof(FlatCameraGuard), nameof(Transpiler));
            int patched = 0, missing = 0;
            foreach (var name in TypeNames)
            {
                var type = asm.GetType(name);
                if (type == null) { missing++; continue; }
                foreach (var t in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        List<CodeInstruction> il;
                        try { il = PatchProcessor.GetOriginalInstructions(m); } catch (Exception) { continue; }
                        if (!il.Any(c => (c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && c.operand is MethodInfo mi && map.ContainsKey(mi))) continue;
                        harmony.Patch(m, transpiler: transpiler);
                        patched++;
                    }
            }
            Plugin.Log.LogInfo("HOOK flat-camera calls made safe in " + patched + " methods (" + map.Count + " FramingCamera methods covered, " + missing + " classes not found)");
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (var c in code)
            {
                if ((c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && c.operand is MethodInfo mi && map.TryGetValue(mi, out var safe))
                    yield return new CodeInstruction(OpCodes.Call, safe) { labels = c.labels, blocks = c.blocks };
                else
                    yield return c;
            }
        }

        static bool Ok(FramingCamera c) { if (c != null) return true; if (Calls++ < 10) Plugin.Log.LogInfo("HOOK skipped a flat-camera call (no flat camera with the VR rig)"); return false; }
        static void Safe_RemoveAllInputRotation(FramingCamera c, bool isCutscene) { if (Ok(c)) c.RemoveAllInputRotation(isCutscene); }
        static void Safe_LevelStartCinemachineHeadlook(FramingCamera c) { if (Ok(c)) c.LevelStartCinemachineHeadlook(); }
        static void Safe_ResetLeadOffset(FramingCamera c, bool smoothReset) { if (Ok(c)) c.ResetLeadOffset(smoothReset); }
        static Vector3 Safe_GetLeadOffset(FramingCamera c) { return Ok(c) ? c.GetLeadOffset() : Vector3.zero; }
        static void Safe_AutoPitch(FramingCamera c, bool pitchUp, bool stopAtNeutral, float destination, float autoPitchSpeed) { if (Ok(c)) c.AutoPitch(pitchUp, stopAtNeutral, destination, autoPitchSpeed); }
        static void Safe_EndHeadLook(FramingCamera c) { if (Ok(c)) c.EndHeadLook(); }
        static void Safe_ChangeFlatScreenCameraSettings(FramingCamera c, FlatScreenCameraSettings s) { if (Ok(c)) c.ChangeFlatScreenCameraSettings(s); }
    }
}
