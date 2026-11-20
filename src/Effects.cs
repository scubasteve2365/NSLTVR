using System;
using HarmonyLib;
using Playful.SuperLucky.PostProcess;
using Playful.Unity.Utility;
using UnityEngine;

namespace NSLTVR
{
    // VR-unfriendly effects and the game settings line (standards STANDARDS.md "Rendering and runtime").
    // In this game (static read and live camera probe, 2026-11-19): bloom and god rays come from PostProcess and are switched
    // every frame by GlobalFogControl.Update, so they are turned off right after it. SSAO (PostProcessOpaque) is Off in every
    // preset. Motion blur (PlayfulMotionBlur) is not on the gameplay camera. Depth of field is skipped by the game in VR mode.
    // Chromatic aberration and film grain are not in the game.
    internal static class Effects
    {
        internal static bool BloomOff = true;
        static int fogCalls;
        static string lastLine = "";

        internal static void LogGameSettings(string why)
        {
            var gs = GraphicsSettings.Instance;
            if (gs == null) return;
            string line = string.Format("preset {0}, resolution {1}x{2} fullscreen {3}, game VSync {4}, target {5}, physics {6}, MSAA {7}, effects {8}, shadows {9} {10}, SSAO {11}, textures {12}, soft particles {13}; bloom and god rays {14}",
                gs.CurrentPreset, gs.Resolution.Width, gs.Resolution.Height, gs.FullScreenSetting, gs.VSyncEnabled, gs.TargetVSyncFPSSetting, gs.TargetPhysicsStepSetting,
                gs.Antialiasing, gs.EffectQualitySetting, gs.Shadows, gs.ShadowResolution, gs.AmbientOcclusionQuality, gs.TextureQualitySetting, gs.SoftParticlesEnabled,
                BloomOff ? "off (mod)" : "as the game sets them");
            if (line == lastLine) return;
            lastLine = line;
            Plugin.Log.LogInfo("GAMESET " + line + " (" + why + ")");
        }

        // Lab only: RigB's starting settings (about Medium). Empty leaves the player's settings alone.
        internal static void ForcePreset(string name)
        {
            var gs = GraphicsSettings.Instance;
            if (gs == null || string.IsNullOrEmpty(name)) return;
            GraphicsSettings.Preset p;
            try { p = (GraphicsSettings.Preset)Enum.Parse(typeof(GraphicsSettings.Preset), name, true); }
            catch (Exception) { Plugin.Log.LogWarning("GAMESET unknown preset " + name); return; }
            if (gs.CurrentPreset == p) return;
            gs.SetPreset(p);
            gs.ApplyAllSettings(false);
            Plugin.Log.LogInfo("GAMESET lab preset " + p + " applied ([Lab] ForcePreset)");
        }

        [HarmonyPatch(typeof(GlobalFogControl), "Update")]
        static class FogPatch
        {
            static readonly AccessTools.FieldRef<GlobalFogControl, PostProcess> post = AccessTools.FieldRefAccess<GlobalFogControl, PostProcess>("_postProcess");
            static void Postfix(GlobalFogControl __instance)
            {
                if (!BloomOff) return;
                var pp = post(__instance);
                if (pp == null) return;
                pp.BloomEnabled = false;
                pp.GodRaysEnabled = false;
                if (fogCalls++ == 0) Plugin.Log.LogInfo("HOOK GlobalFogControl.Update: bloom and god rays turned off");
            }
        }
    }
}
