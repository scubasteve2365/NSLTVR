using System.Globalization;
using System.Linq;
using System.Text;
using Playful.SuperLucky.Camera;
using Playful.SuperLucky.Global;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NSLTVR
{
    // Read-only lab probes: they log what the game is doing and change nothing.
    internal static class Probes
    {
        static string V(Vector3 v) { return string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", v.x, v.y, v.z); }

        internal static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        internal static void Cameras()
        {
            var log = Plugin.Log;
            log.LogInfo("PROBE color space " + QualitySettings.activeColorSpace + ", screen " + Screen.width + "x" + Screen.height + ", vsync " + QualitySettings.vSyncCount
                + ", targetFrameRate " + Application.targetFrameRate + ", fixedDelta " + Time.fixedDeltaTime.ToString("F5", CultureInfo.InvariantCulture) + ", timeScale " + Time.timeScale
                + ", maxDelta " + Time.maximumDeltaTime + ", quality level " + QualitySettings.GetQualityLevel() + ", shadows " + QualitySettings.shadows + ", AA " + QualitySettings.antiAliasing);
            log.LogInfo("PROBE main camera " + (Camera.main != null ? PathOf(Camera.main.transform) : "none") + ", InVrCameraMode " + SafeInVr());
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>().Where(c => c.gameObject.scene.IsValid() || c.gameObject.scene.name == "DontDestroyOnLoad").OrderBy(c => c.depth))
            {
                var t = c.transform;
                var comps = string.Join(",", c.GetComponents<MonoBehaviour>().Where(m => m != null).Select(m => m.GetType().Name + (m.enabled ? "" : "(off)")).ToArray());
                log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "PROBE cam {0} active {1} enabled {2} depth {3} mask 0x{4:X8} clear {5} fov {6:F1} near {7:F3} far {8:F0} target {9} hdr {10} pos {11} rot {12} lossy {13} local {14} {15} parent {16} comps [{17}]",
                    PathOf(t), c.gameObject.activeInHierarchy, c.enabled, c.depth, c.cullingMask, c.clearFlags, c.fieldOfView, c.nearClipPlane, c.farClipPlane,
                    c.targetTexture != null ? c.targetTexture.name + " " + c.targetTexture.width + "x" + c.targetTexture.height : "screen", c.allowHDR,
                    V(t.position), V(t.eulerAngles), V(t.lossyScale), V(t.localPosition), V(t.localEulerAngles), t.parent != null ? t.parent.name + " scale " + V(t.parent.lossyScale) : "none", comps));
            }
            RigState();
        }

        static string SafeInVr() { return GameConfiguration.Instance != null ? GameConfiguration.InVrCameraMode().ToString() : "no GameConfiguration"; }

        internal static void RigState()
        {
            var m = CameraMotor.Instance;
            if (m == null) { Plugin.Log.LogInfo("RIG no CameraMotor yet"); return; }
            var t = m.transform;
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "RIG motor {0} on {1} pos {2} rot {3} scale {4}, camera {5}", m.GetType().Name, PathOf(t), V(t.position), V(t.eulerAngles), V(t.lossyScale),
                m.MyCamera != null ? PathOf(m.MyCamera.transform) : "none");
            var zone = m.ActiveZone;
            sb.Append(", zone " + (zone != null ? zone.name + " type " + zone.ActiveZoneType + " vr settings " + (zone.VrZoneSettings != null ? zone.VrZoneSettings.name : "none") : "none"));
            var ct = m as CurveTrackingCamera;
            if (ct != null)
            {
                var s = ct.VrCameraActiveSettings;
                sb.AppendFormat(CultureInfo.InvariantCulture, ", CameraScale {0:F3}", ct.CameraScale);
                if (s != null) sb.AppendFormat(CultureInfo.InvariantCulture, ", active VR settings {0} IPDScale {1:F3} TargetOffset {2} TargetSpeed {3:F2} FollowPath {4}", s.name, s.IPDScale, V(s.TargetOffset), s.TargetSpeed, s.FollowPath);
                if (m.DefaultVrSettings != null) sb.Append(", default " + m.DefaultVrSettings.name + " IPDScale " + m.DefaultVrSettings.IPDScale.ToString("F3", CultureInfo.InvariantCulture));
            }
            if (m.Tracking != null) sb.Append(", Lucky at " + V(m.Tracking.transform.position) + " distance " + (m.Tracking.transform.position - t.position).magnitude.ToString("F2", CultureInfo.InvariantCulture));
            var lvl = GameConfiguration.Instance != null ? GameConfiguration.Instance.ActiveLevelSettings : null;
            if (lvl != null) sb.Append(", level type " + lvl.TypeOfLevel + " default VR settings " + (lvl.DefaultVrCameraSettings != null ? lvl.DefaultVrCameraSettings.name + " IPDScale " + lvl.DefaultVrCameraSettings.IPDScale.ToString("F3", CultureInfo.InvariantCulture) : "none"));
            Plugin.Log.LogInfo(sb.ToString());
        }

        internal static void Zones()
        {
            var zones = Object.FindObjectsOfType<CameraZone>();
            int withVr = zones.Count(z => z.VrZoneSettings != null);
            Plugin.Log.LogInfo("ZONES in " + SceneManager.GetActiveScene().name + ": " + zones.Length + ", with VR settings " + withVr);
            foreach (var g in zones.Where(z => z.VrZoneSettings != null).GroupBy(z => z.VrZoneSettings))
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "ZONES  VR settings {0}: IPDScale {1:F3} TargetOffset {2}, used by {3} zones", g.Key.name, g.Key.IPDScale, V(g.Key.TargetOffset), g.Count()));
        }

        internal static void Scenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                Plugin.Log.LogInfo("SCENES loaded " + i + ": " + s.name + " (" + s.path + ")");
            }
            Plugin.Log.LogInfo("SCENES in build: " + SceneManager.sceneCountInBuildSettings);
        }

        internal static void Graphics()
        {
            var gs = Playful.Unity.Utility.GraphicsSettings.Instance;
            if (gs == null) { Plugin.Log.LogInfo("GAMESET no GraphicsSettings yet"); return; }
            Plugin.Log.LogInfo(string.Format("GAMESET preset {0}, vsync {1}, target {2}, physics {3}, AA {4}, effects {5}, shadows {6} {7}, SSAO {8}, textures {9}, soft particles {10}",
                gs.CurrentPreset, gs.VSyncEnabled, gs.TargetVSyncFPSSetting, gs.TargetPhysicsStepSetting, gs.Antialiasing, gs.EffectQualitySetting, gs.Shadows, gs.ShadowResolution,
                gs.AmbientOcclusionQuality, gs.TextureQualitySetting, gs.SoftParticlesEnabled));
        }
            internal static void Canvases()
        {
            foreach (var c in Object.FindObjectsOfType<Canvas>())
            {
                if (!c.isRootCanvas) continue;
                Plugin.Log.LogInfo(string.Format("CANVAS {0} active {1} mode {2} camera {3} layer {4} sort {5} plane {6:F2} scale {7}",
                    PathOf(c.transform), c.isActiveAndEnabled, c.renderMode, c.worldCamera != null ? PathOf(c.worldCamera.transform) + " mask 0x" + c.worldCamera.cullingMask.ToString("X8") : "none",
                    LayerMask.LayerToName(c.gameObject.layer), c.sortingOrder, c.planeDistance, V(c.transform.lossyScale)));
            }
        }
    }
}
