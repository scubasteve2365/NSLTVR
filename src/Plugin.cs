using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Playful.SuperLucky.Camera;
using Playful.SuperLucky.Global;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NSLTVR
{
    // NSLTVR, feasibility build: switches New Super Lucky's Tale to the VR camera rig left over from Lucky's Tale VR (or keeps the
    // flat rig), and renders both eyes from one game tick into OpenXR. Skeleton copied from CupheadVR (MIT, scubasteve2365).
    // Lab probes are read from vrlab_command.txt in the game folder while Lab.Commands is on.
    [BepInPlugin("com.scubasteve2365.nsltvr", "NSLTVR", "0.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal const string Version = "0.01 feasibility";
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static ConfigEntry<string> CameraRig;
        internal static Plugin Instance;
        ConfigEntry<bool> labCommands, autoStart;
        ConfigEntry<float> resolution, flatScale, ipdScale;
        string commandFile;
        int nextCommandCheck;
        bool triedAutoStart;
        internal XrRig Rig;
        internal SpeedLog Speed;

        void Awake()
        {
            Log = Logger;
            Instance = this;
            labCommands = Config.Bind("Lab", "Commands", true, "Developer test commands read from vrlab_command.txt in the game folder.");
            autoStart = Config.Bind("VR", "AutoStart", true, "Start VR by itself once the game is running.");
            CameraRig = Config.Bind("VR", "CameraRig", "VR", "VR: the camera rig left over from Lucky's Tale VR. Flat: the game's normal camera. Read at game start.");
            resolution = Config.Bind("VR", "Resolution", 1.0f, "Eye size as a share of what the headset asks for. 1 is 100%.");
            flatScale = Config.Bind("VR", "FlatWorldScale", 1.0f, "Flat rig only: how many game units one real metre of head movement is.");
            ipdScale = Config.Bind("Lab", "IpdOverride", 0f, "0 uses the game's own camera scale (zone IPDScale). Above 0, use this scale instead.");
            commandFile = Path.Combine(Paths.GameRootPath, "vrlab_command.txt");
            Rig = gameObject.AddComponent<XrRig>();
            Rig.PluginDir = Path.GetDirectoryName(Info.Location);
            Rig.ShotDir = Path.Combine(Paths.GameRootPath, "vrlab_shots");
            Rig.Resolution = resolution.Value;
            Rig.FlatScale = flatScale.Value;
            Rig.IpdOverride = ipdScale.Value;
            Speed = gameObject.AddComponent<SpeedLog>();
            Speed.Rig = Rig;
            SceneManager.sceneLoaded += (scene, mode) => Log.LogInfo("SCENE loaded " + scene.name + " mode " + mode + " frame " + Time.frameCount);
            new Harmony("com.scubasteve2365.nsltvr").PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("NSLTVR " + Version + " loaded. Unity " + Application.unityVersion + ", pointer size " + IntPtr.Size + ", camera rig setting " + CameraRig.Value);
            Log.LogInfo("ONLINE telemetry: TelemetryClientWrapper.FireEvent is empty in this build (static read), nothing to block");
        }

        internal static bool WantVrRig { get { return string.Equals(CameraRig.Value, "VR", StringComparison.OrdinalIgnoreCase); } }

        void Update()
        {
            if (Time.frameCount >= nextCommandCheck && labCommands.Value)
            {
                nextCommandCheck = Time.frameCount + 15;
                ReadCommands();
            }
            if (!triedAutoStart && autoStart.Value && Time.frameCount >= 120)
            {
                triedAutoStart = true;
                Rig.Begin();
            }
        }

        void ReadCommands()
        {
            if (!File.Exists(commandFile)) return;
            string[] lines;
            try { lines = File.ReadAllLines(commandFile); File.Delete(commandFile); }
            catch (Exception) { return; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                Log.LogInfo("CMD " + line);
                try { Run(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)); }
                catch (Exception e) { Log.LogError("CMD failed: " + e); }
            }
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

        void Run(string[] a)
        {
            switch (a[0].ToLowerInvariant())
            {
                case "probe": Probes.Cameras(); break;
                case "rig": Probes.RigState(); break;
                case "zones": Probes.Zones(); break;
                case "vron": Rig.Begin(); break;
                case "vroff": Rig.End(); break;
                case "shot": Rig.Shot(); break;
                case "eyecheck": Rig.EyeCheck(); break;
                case "zerodiff": Rig.ZeroDiff(); break;
                case "stamp": Rig.Stamp = a.Length < 2 || a[1] != "0"; break;
                case "ipd": Rig.IpdOverride = F(a[1]); Log.LogInfo("IPD override " + Rig.IpdOverride); break;
                case "flatscale": Rig.FlatScale = F(a[1]); break;
                case "fakehead": Rig.LabHead = new Vector3(F(a[1]), F(a[2]), F(a[3])); break;
                case "recenter": Rig.Recenter(); break;
                case "speedlog": Speed.Begin(a.Length > 1 ? F(a[1]) : 5f); break;
                case "speedoff": Speed.On = false; break;
                case "timescale": Time.timeScale = F(a[1]); break;
                case "scene": SceneManager.LoadScene(a[1]); break;
                case "dumpscenes": Probes.Scenes(); break;
                case "gfx": Probes.Graphics(); break;
                default: Log.LogWarning("unknown command " + a[0]); break;
            }
        }
    }

    // The switch between the two camera rigs. CalculateShouldBeInVR is compiled to "return false" in this build (static read),
    // and GameConfiguration.Awake stores its answer in _inVrCameraMode, which every other VR check reads through
    // InVrCameraMode(). A tiny method like CalculateShouldBeInVR may be inlined by Mono's JIT, so the field is also set
    // directly after Awake.
    [HarmonyPatch(typeof(GameConfiguration), nameof(GameConfiguration.CalculateShouldBeInVR))]
    static class ShouldBeInVrPatch
    {
        static int calls;
        static bool Prefix(ref bool __result)
        {
            __result = Plugin.WantVrRig;
            if (calls++ < 3) Plugin.Log.LogInfo("HOOK CalculateShouldBeInVR -> " + __result + " (call " + calls + ")");
            return false;
        }
    }

    [HarmonyPatch(typeof(GameConfiguration), "Awake")]
    static class GameConfigurationAwakePatch
    {
        static void Postfix(GameConfiguration __instance)
        {
            var f = AccessTools.Field(typeof(GameConfiguration), "_inVrCameraMode");
            bool before = (bool)f.GetValue(__instance);
            f.SetValue(__instance, Plugin.WantVrRig);
            Plugin.Log.LogInfo("HOOK GameConfiguration.Awake: _inVrCameraMode was " + before + ", set " + Plugin.WantVrRig);
        }
    }

    [HarmonyPatch(typeof(CameraManager), "EnableCamera")]
    static class EnableCameraPatch
    {
        static void Postfix(CameraManager __instance)
        {
            var cam = __instance.GetCameraGameObject();
            Plugin.Log.LogInfo("HOOK CameraManager.EnableCamera: InVrCameraMode " + GameConfiguration.InVrCameraMode() + ", camera " + (cam != null ? Probes.PathOf(cam.transform) : "none")
                + ", motor " + (CameraMotor.Instance != null ? CameraMotor.Instance.GetType().Name : "none"));
        }
    }
}
