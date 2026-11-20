using System;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection.Emit;
using Playful.SuperLucky.Camera;
using Playful.SuperLucky.Global;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NSLTVR
{
    // NSLTVR build 1 (Phase 1): New Super Lucky's Tale on the VR camera rig left over from Lucky's Tale VR, both eyes from
    // one game tick through OpenXR, the HUD on its own panel, and a basic in-headset menu. Skeleton from CupheadVR (MIT,
    // scubasteve2365). Lab probes are read from vrlab_command.txt in the game folder while Lab.Commands is on.
    [BepInPlugin("com.scubasteve2365.nsltvr", "NSLTVR", "0.5.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal const string Version = "build 5";
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static ConfigEntry<string> CameraRig;
        internal static Plugin Instance;
        ConfigEntry<bool> labCommands, autoStart, bloomOff;
        ConfigEntry<float> turnSpeed, resolution, ipdScale, hudWidth, hudX, hudY, hudDistance, menuWidth, menuX, menuY, menuDistance;
        ConfigEntry<string> hudMode, menuKey, forcePreset;
        string commandFile;
        int nextCommandCheck, nextSlowCheck;
        bool triedAutoStart;
        internal XrRig Rig;
        internal SpeedLog Speed;
        internal MenuUI Menu;

        const float DefHudWidth = 1.2f, DefHudX = 0f, DefHudY = -0.40f, DefHudDistance = 1.3f;
        const float DefMenuWidth = 0.8f, DefMenuX = 0f, DefMenuY = -0.1f, DefMenuDistance = 0.9f;

        void Awake()
        {
            Log = Logger;
            Instance = this;
            labCommands = Config.Bind("Lab", "Commands", false, "Developer test commands read from vrlab_command.txt in the game folder. Leave off.");
            forcePreset = Config.Bind("Lab", "ForcePreset", "", "Lab only: put the game on this graphics preset at start (graphics_low, graphics_medium, ...). Empty leaves your settings alone.");
            ipdScale = Config.Bind("Lab", "IpdOverride", 0f, "0 uses the game's own camera scale (zone IPDScale). Above 0, use this scale instead.");
            autoStart = Config.Bind("VR", "AutoStart", true, "Start VR by itself once the game is running.");
            turnSpeed = Config.Bind("VR", "RightStickTurn", 0f, "Right stick turns the camera around Lucky, in degrees a second at full tilt. 0 is off (default): the VR camera was not made to turn and pulled far away when it did (build 4).");
            CameraRig = Config.Bind("VR", "CameraRig", "VR", "VR: the camera rig left over from Lucky's Tale VR. Flat: the game's normal camera. Read at game start.");
            resolution = Config.Bind("VR", "Resolution", 1.0f, "Eye size as a share of what the headset asks for. 1 is 100%. Used from the next start.");
            bloomOff = Config.Bind("Picture", "BloomOff", true, "Turn off the game's bloom and god rays, which look different in each eye.");
            hudMode = Config.Bind("HUD", "Mode", "Fixed", "Fixed: the HUD stays in place in the room. FollowsHead: it moves with your head. Hidden: no HUD.");
            hudWidth = Config.Bind("HUD", "Width", DefHudWidth, "HUD width in metres.");
            hudX = Config.Bind("HUD", "X", DefHudX, "HUD left (-) or right (+) in metres.");
            hudY = Config.Bind("HUD", "Y", DefHudY, "HUD height from eye level in metres.");
            hudDistance = Config.Bind("HUD", "Distance", DefHudDistance, "HUD distance in metres.");
            menuWidth = Config.Bind("Menu", "Width", DefMenuWidth, "Mod menu width in metres.");
            menuX = Config.Bind("Menu", "X", DefMenuX, "Mod menu left (-) or right (+) in metres.");
            menuY = Config.Bind("Menu", "Y", DefMenuY, "Mod menu height from eye level in metres.");
            menuDistance = Config.Bind("Menu", "Distance", DefMenuDistance, "Mod menu distance in metres.");
            menuKey = Config.Bind("Menu", "Key", "Insert", "Keyboard key that opens and closes the mod menu (a Unity KeyCode name).");
            commandFile = Path.Combine(Paths.GameRootPath, "vrlab_command.txt");
            Rig = gameObject.AddComponent<XrRig>();
            Rig.PluginDir = Path.GetDirectoryName(Info.Location);
            Rig.ShotDir = Path.Combine(Paths.GameRootPath, "vrlab_shots");
            Rig.Resolution = Mathf.Clamp(resolution.Value, 0.5f, 2f);
            Rig.IpdOverride = ipdScale.Value;
            Menu = gameObject.AddComponent<MenuUI>();
            Menu.Version = "NSLT VR " + Version;
            Menu.VrActive = () => Rig.Running;
            Menu.Problem = () => Rig.StartProblem;
            try { Menu.MenuKey = (KeyCode)Enum.Parse(typeof(KeyCode), menuKey.Value, true); }
            catch (Exception) { Log.LogWarning("Menu key \"" + menuKey.Value + "\" is not a key name, using Insert"); Menu.MenuKey = KeyCode.Insert; }
            Rig.Menu = Menu;
            BuildMenu();
            PushSettings();
            Speed = gameObject.AddComponent<SpeedLog>();
            Speed.Rig = Rig;
            SceneManager.sceneLoaded += (scene, mode) => Log.LogInfo("SCENE loaded " + scene.name + " mode " + mode + " frame " + Time.frameCount);
            new Harmony("com.scubasteve2365.nsltvr").PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("NSLTVR " + Version + " loaded. Unity " + Application.unityVersion + ", pointer size " + IntPtr.Size + ", camera rig " + CameraRig.Value + ", menu key " + Menu.MenuKey);
            Log.LogInfo("ONLINE telemetry: TelemetryClientWrapper.FireEvent is empty in this build (static read), nothing to block");
        }

        internal static bool WantVrRig { get { return string.Equals(CameraRig.Value, "VR", StringComparison.OrdinalIgnoreCase); } }

        void Update()
        {
            if (labCommands.Value && Time.frameCount >= nextCommandCheck)
            {
                nextCommandCheck = Time.frameCount + 15;
                ReadCommands();
            }
            if (!triedAutoStart && autoStart.Value && Time.frameCount >= 120)
            {
                triedAutoStart = true;
                Rig.Begin();
            }
            PreferPadPatch.Tick();
            if (Time.frameCount >= nextSlowCheck)
            {
                nextSlowCheck = Time.frameCount + 120;
                Effects.ForcePreset(forcePreset.Value);
                Effects.LogGameSettings("check");
                if (Rig.Running && !Speed.On) Speed.Begin(10f);
                // The HUD and title screens are drawn for a 16:9 picture: keep the window 16:9 while VR runs.
                // The title screens are drawn for a 16:9 picture, and exclusive full screen changes the monitor's mode, which
                // moves other displays (RigB's SteamVR window lives on one). While VR runs the game is a 1920x1080 window.
                if (Rig.Running && (Screen.fullScreen || Screen.width != 1920 || Screen.height != 1080))
                {
                    Log.LogInfo("WINDOW " + Screen.width + "x" + Screen.height + (Screen.fullScreen ? " full screen" : " window") + ", set to a 1920x1080 window while VR runs");
                    Screen.SetResolution(1920, 1080, false);
                }
            }
        }

        // ---- settings and menu ----

        void PushSettings()
        {
            Rig.HudMode = hudMode.Value.Equals("FollowsHead", StringComparison.OrdinalIgnoreCase) ? 1 : hudMode.Value.Equals("Hidden", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            Rig.HudWidth = hudWidth.Value; Rig.HudX = hudX.Value; Rig.HudY = hudY.Value; Rig.HudDistance = hudDistance.Value;
            Rig.MenuWidth = menuWidth.Value; Rig.MenuX = menuX.Value; Rig.MenuY = menuY.Value; Rig.MenuDistance = menuDistance.Value;
            Effects.BloomOff = bloomOff.Value;
            RightStickTurnPatch.DegreesPerSecond = turnSpeed.Value;
        }

        static string M(float v) { return v.ToString("F2", CultureInfo.InvariantCulture) + " m"; }
        static string Cm(float v) { return (v >= 0f ? "+" : "") + Mathf.RoundToInt(v * 100f) + " cm"; }

        MenuUI.Row Value(string label, ConfigEntry<float> e, float step, float min, float max, Func<string> show, string help, bool menuPlace = false)
        {
            return new MenuUI.Row
            {
                Label = label,
                Value = show,
                Change = dir =>
                {
                    e.Value = Mathf.Clamp((float)Math.Round(e.Value + dir * step, 3), min, max);
                    PushSettings();
                    if (menuPlace) Rig.ReplaceMenu();
                    Log.LogInfo("MENU " + label + " " + show());
                },
                Help = () => help,
            };
        }

        static readonly string[] HudModes = { "Fixed", "FollowsHead", "Hidden" };
        static readonly string[] HudModeNames = { "Fixed", "Follows head", "Hidden" };

        void BuildMenu()
        {
            var general = new MenuUI.Tab { Name = "General" };
            general.Rows.Add(new MenuUI.Row { Label = "Recenter", Press = () => { Rig.Recenter(); Log.LogInfo("MENU recenter"); },
                Help = () => "Press A to put the view, the HUD and this menu straight ahead of where you face now. Your headset's own recenter does the same." });
            general.Rows.Add(new MenuUI.Row
            {
                Label = "Resolution",
                Value = () => Mathf.RoundToInt(resolution.Value * 100f) + "%",
                Change = dir => { resolution.Value = Mathf.Clamp((float)Math.Round(resolution.Value + dir * 0.05f, 2), 0.5f, 2f); Log.LogInfo("MENU resolution " + resolution.Value + " (next start)"); },
                Help = () => "How sharp the game is drawn, as a share of what your headset asks for. Takes effect the next time you start the game. Running now: "
                    + (Rig.Running ? Rig.EyeWidth + " x " + Rig.EyeHeight : "VR not running") + " per eye. Next start: " + NextSize() + " per eye.",
            });
            general.Rows.Add(new MenuUI.Row { Label = "Close", Press = () => Menu.Close(), Help = () => "Close the menu. B, Insert or holding both sticks also closes it." });

            var hud = new MenuUI.Tab { Name = "HUD" };
            hud.Rows.Add(new MenuUI.Row
            {
                Label = "HUD",
                Value = () => HudModeNames[Rig.HudMode],
                Change = dir => { hudMode.Value = HudModes[(Rig.HudMode + dir + 3) % 3]; PushSettings(); Log.LogInfo("MENU HUD " + hudMode.Value); },
                Help = () => Rig.HudMode == 0 ? "Fixed: the hearts, coins and clovers stay in one place in the room, in front of where you recentered."
                    : Rig.HudMode == 1 ? "Follows head: the HUD moves with your head and always sits in the same spot in your view."
                    : "Hidden: no HUD in the headset.",
            });
            hud.Rows.Add(Value("Size", hudWidth, 0.05f, 0.3f, 4f, () => M(hudWidth.Value) + " wide", "How wide the HUD is."));
            hud.Rows.Add(Value("Left / right", hudX, 0.05f, -2f, 2f, () => Cm(hudX.Value), "Moves the HUD left or right."));
            hud.Rows.Add(Value("Height", hudY, 0.05f, -2f, 2f, () => Cm(hudY.Value), "Moves the HUD up or down from eye level."));
            hud.Rows.Add(Value("Distance", hudDistance, 0.05f, 0.4f, 5f, () => M(hudDistance.Value), "How far away the HUD is."));
            hud.Rows.Add(new MenuUI.Row { Label = "Reset HUD", Press = () => { hudWidth.Value = DefHudWidth; hudX.Value = DefHudX; hudY.Value = DefHudY; hudDistance.Value = DefHudDistance; hudMode.Value = "Fixed"; PushSettings(); Log.LogInfo("MENU reset HUD"); },
                Help = () => "Press A to put the HUD back to Fixed, 1.20 m wide, 1.30 m away, 40 cm below eye level, where you look down at Lucky." });

            var menuTab = new MenuUI.Tab { Name = "Mod Menu" };
            menuTab.Rows.Add(Value("Size", menuWidth, 0.05f, 0.3f, 3f, () => M(menuWidth.Value) + " wide", "How wide this menu is.", true));
            menuTab.Rows.Add(Value("Left / right", menuX, 0.05f, -2f, 2f, () => Cm(menuX.Value), "Moves this menu left or right.", true));
            menuTab.Rows.Add(Value("Height", menuY, 0.05f, -2f, 2f, () => Cm(menuY.Value), "Moves this menu up or down from eye level.", true));
            menuTab.Rows.Add(Value("Distance", menuDistance, 0.05f, 0.4f, 5f, () => M(menuDistance.Value), "How far away this menu is.", true));
            menuTab.Rows.Add(new MenuUI.Row { Label = "Reset menu", Press = () => { menuWidth.Value = DefMenuWidth; menuX.Value = DefMenuX; menuY.Value = DefMenuY; menuDistance.Value = DefMenuDistance; PushSettings(); Rig.ReplaceMenu(); Log.LogInfo("MENU reset menu"); },
                Help = () => "Press A to put this menu back to 0.80 m wide, 0.90 m away, a little below eye level." });
            Menu.Tabs.Add(general);
            Menu.Tabs.Add(hud);
            Menu.Tabs.Add(menuTab);
        }

        string NextSize() { var r = Rig.Resolution; Rig.Resolution = resolution.Value; var s = Rig.NextLaunchSize(); Rig.Resolution = r; return s; }

        // Lab: Lucky's position, speed and the input the game reads, every 0.1 s.
        System.Collections.IEnumerator Track(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            var wait = new WaitForSecondsRealtime(0.1f);
            while (Time.realtimeSinceStartup < end)
            {
                var p = Playful.SuperLucky.Gameplay.Player.PlayerController.Instance;
                if (p != null)
                    Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "TRACK pos ({0:F2},{1:F2}) vel ({2:F2},{3:F2}) raw ({4:F2},{5:F2}) world ({6:F2},{7:F2}) enabled {8} state {9} {10} cam yaw {11:F1} volatility {12:F3}",
                        p.transform.position.x, p.transform.position.z, p.Velocity.x, p.Velocity.z, p.Input.RawInput.x, p.Input.RawInput.y,
                        p.Input.WorldSpaceInput.x, p.Input.WorldSpaceInput.z, p.Input.InputEnabled, p.ControllerState, p.MoveState,
                        CameraManager.Instance != null && CameraManager.Instance.GetCameraGameObject() != null ? CameraManager.Instance.GetCameraGameObject().transform.eulerAngles.y : -1f, p.Input.InputVolatility));
                yield return wait;
            }
        }

        // ---- lab commands ----

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
                case "stamp": Rig.Stamp = a.Length < 2 || a[1] != "0"; break;
                case "ipd": Rig.IpdOverride = F(a[1]); Log.LogInfo("IPD override " + Rig.IpdOverride); break;
                case "fakehead": Rig.LabHead = new Vector3(F(a[1]), F(a[2]), F(a[3])); break;
                case "recenter": Rig.Recenter(); break;
                case "menu": Menu.LabKey(a.Length > 1 ? a[1] : "open"); break;
                case "speedlog": Speed.Begin(a.Length > 1 ? F(a[1]) : 5f); break;
                case "gfx": Probes.Graphics(); break;
                case "picture": Rig.PictureMode = a[1] == "steady" ? 1 : a[1] == "panel" ? 2 : 0; Log.LogInfo("PICTURE lab override " + a[1]); break;
                case "dump": Rig.Dump(a.Length > 1 ? a[1] : "dump"); break;
                case "pink": Rig.Pink(int.Parse(a[1])); break;
                case "canvases": Probes.Canvases(); break;
                case "track": StartCoroutine(Track(a.Length > 1 ? F(a[1]) : 3f)); break;
                case "preferpad": PreferPadPatch.On = a[1] == "1"; Log.LogInfo("prefer pad " + PreferPadPatch.On); break;
                case "hud": Rig.HudMode = a[1] == "head" ? 1 : a[1] == "hidden" ? 2 : 0; break;
                default: Log.LogWarning("unknown command " + a[0]); break;
            }
        }
    }

    // The switch between the two camera rigs. CalculateShouldBeInVR is compiled to "return false" in this build (static read),
    // and GameConfiguration.Awake stores its answer in _inVrCameraMode, which every other VR check reads through
    // InVrCameraMode(). The field is also set directly after Awake, in case the JIT inlined the tiny method.
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

namespace NSLTVR
{
    // With the VR rig, CameraManager.EnableCamera destroys the flat-screen audio listener, but CameraManager.Update still
    // moves it every frame: a NullReferenceException per frame (27,424 in one RigB session, 2026-11-20), each one logged.
    // The VR rig's listener is the VR camera itself, so the update is skipped once the flat listener is gone.
    [HarmonyPatch(typeof(CameraManager), "Update")]
    static class CameraManagerUpdatePatch
    {
        static readonly AccessTools.FieldRef<CameraManager, GameObject> listener = AccessTools.FieldRefAccess<CameraManager, GameObject>("_flatScreenListener");
        static int skipped;
        static bool Prefix(CameraManager __instance)
        {
            if (listener(__instance) != null) return true;
            if (skipped++ == 0) Plugin.Log.LogInfo("HOOK CameraManager.Update skipped: the flat-screen listener is gone with the VR rig (it threw every frame)");
            return false;
        }
    }
}

namespace NSLTVR
{
    // The level-start wave turns the flat camera to look at Lucky (PlayerController.FadeInRoutine calls
    // CameraManager.GetFlatScreenCamera().LevelStartCinemachineHeadlook()). With the VR rig there is no flat camera, so that
    // call threw, the fade-in coroutine died, and Lucky's input lock and the game's black fade were never released:
    // Steve's build 1 test on RigA (no pad control in the 3D world), seen on RigB too (flat rig moves Lucky, VR rig does not).
    // In VR Lucky skips the wave, so the routine runs to its end.
    [HarmonyPatch(typeof(Playful.SuperLucky.Gameplay.Player.PlayerVisualController.ShouldThePlayerReallyWave), MethodType.Constructor)]
    static class NoWaveInVrPatch
    {
        static int calls;
        static void Postfix(Playful.SuperLucky.Gameplay.Player.PlayerVisualController.ShouldThePlayerReallyWave __instance)
        {
            if (!GameConfiguration.InVrCameraMode()) return;
            __instance.DoNotWave();
            if (calls++ < 3) Plugin.Log.LogInfo("HOOK level start: no wave in VR (the wave needs the flat camera, which the VR rig removes)");
        }
    }
}

namespace NSLTVR
{
    // CameraManager.EnableCamera destroys the rig it does not use. With the VR rig that removes the flat camera, and with it
    // the CinemachineBrain that NSLT code still reads (PlayerController.Warp -> CinemachineBrain.ActiveVirtualCamera threw on
    // RigB, 2026-11-20, killing the level fade-in, so Lucky never got input). The flat rig and its listener are kept,
    // switched off, instead of destroyed.
    [HarmonyPatch(typeof(CameraManager), "EnableCamera")]
    static class KeepFlatRigPatch
    {
        static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            var destroy = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object) });
            var hide = AccessTools.Method(typeof(KeepFlatRigPatch), nameof(Hide));
            int n = 0;
            foreach (var c in code)
            {
                if (c.Calls(destroy)) { n++; yield return new CodeInstruction(OpCodes.Call, hide) { labels = c.labels, blocks = c.blocks }; }
                else yield return c;
            }
            Plugin.Log.LogInfo("HOOK CameraManager.EnableCamera: " + n + " Destroy calls replaced (the unused rig is switched off, not destroyed)");
        }

        static void Hide(UnityEngine.Object o)
        {
            var go = o as GameObject;
            if (go != null) { go.SetActive(false); Plugin.Log.LogInfo("HOOK kept " + go.name + " switched off instead of destroying it"); }
            else if (o != null) UnityEngine.Object.Destroy(o);
        }
    }
}

namespace NSLTVR
{
    // In-level cutscenes call CameraManager.DisableMainCamera, which (once a flat camera exists) switches the whole camera
    // rig off. Since build 2 keeps the flat rig, that also switched off the VR camera, which carries the game's audio
    // listener in VR mode: cutscene sound went wrong (Steve, build 4, meeting the cat). In VR only the VR and HUD cameras
    // stop drawing; the rig and its listener stay on, and the mod moves the listener to the cutscene camera (XrRig.LateUpdate).
    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.DisableMainCamera))]
    static class CutsceneDisablePatch
    {
        static bool Prefix(CameraManager __instance)
        {
            if (!GameConfiguration.InVrCameraMode()) return true;
            SetCameras(__instance, false);
            return false;
        }

        internal static void SetCameras(CameraManager cm, bool on)
        {
            var vr = cm.VRCamera != null ? cm.VRCamera.GetComponent<Camera>() : null;
            var hud = cm.HudCamera != null ? cm.HudCamera.GetComponent<Camera>() : null;
            if (vr != null) vr.enabled = on;
            if (hud != null) hud.enabled = on;
            Plugin.Log.LogInfo("HOOK CameraManager." + (on ? "EnableMainCamera" : "DisableMainCamera") + " in VR: VR and HUD cameras " + (on ? "drawing again" : "paused") + ", rig and audio listener kept on");
        }
    }

    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.EnableMainCamera))]
    static class CutsceneEnablePatch
    {
        static bool Prefix(CameraManager __instance)
        {
            if (!GameConfiguration.InVrCameraMode()) return true;
            CutsceneDisablePatch.SetCameras(__instance, true);
            return false;
        }
    }
}
