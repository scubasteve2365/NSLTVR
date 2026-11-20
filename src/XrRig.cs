using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Playful.SuperLucky.Camera;
using UnityEngine;

namespace NSLTVR
{
    // Build 1: renders the game's camera once per eye from one game tick and hands the eye images, the HUD and the menu to
    // NSLTVRNative, whose own XR thread sends them to the headset (the game never waits on the headset; the mod caps the
    // game at the headset's real rate itself). OpenXR plumbing ported from CupheadVR's XrRig (MIT, scubasteve2365).
    //
    // Each eye: the game's camera transform is the tracking origin (Lucky's Tale VR's CurveTrackingCamera drives it), the
    // head pose since the last recenter goes on top, scaled by the camera's world scale (the rig's CameraScale, which the
    // game sets from the zone's IPDScale). The eye's off-axis lens comes from OpenXR.
    internal class XrRig : MonoBehaviour
    {
        const string Dll = "NSLTVRNative";
        const int EventInit = 1, EventSubmit = 2, EventShutdown = 3, EventEyeBegin = 5;
        const int FlagHud = 1, FlagMenu = 2, FlagSteady = 4, FlagViewsValid = 8, FlagHudOpaque = 16;
        internal const int HudW = 1920, HudH = 1080;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct EyeView { public float px, py, pz, qx, qy, qz, qw, left, right, up, down; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct ViewInfo
        {
            public int running, viewsValid, sessionState;
            public float periodMs;
            public long displayTime;
            public EyeView eye0, eye1;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct FrameMeta
        {
            public int flags, frame;
            public long displayTime;
            public EyeView eye0, eye1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public float[] hudPose;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public float[] menuPose;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct Stats
        {
            public uint submitted, repeats, skipped, replaced, acquireFail;
            public float waitAvg, waitMax, endAvg, endMax, copyAvg, copyMax, gpuEyeAvg, gpuEyeMax, gpuFrameAvg, gpuFrameMax;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)] static extern IntPtr LoadLibraryA(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetRenderEventFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StartFn(IntPtr any, float scale, int hudW, int hudH, int menuW, int menuH);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetViewsFn(out ViewInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void QueueFrameFn(IntPtr l, IntPtr r, IntPtr hud, IntPtr menu, ref FrameMeta meta);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float CapWaitFn(float hz);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StatsFn(out Stats s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PopLogFn(StringBuilder buffer, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RuntimeInfoFn(StringBuilder runtime, int runtimeSize, StringBuilder system, int systemSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FailedLayersFn(StringBuilder outText, int size);

        GetRenderEventFn xGetRenderEvent;
        IntFn xState, xTakeRecenter, xRecW, xRecH, xEyeW, xEyeH, xLastError;
        StartFn xStart;
        GetViewsFn xGetViews;
        QueueFrameFn xQueueFrame;
        CapWaitFn xCapWait;
        StatsFn xStats;
        PopLogFn xPopLog;
        RuntimeInfoFn xRuntimeInfo;
        FailedLayersFn xFailedLayers;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DumpNextFn([MarshalAs(UnmanagedType.LPStr)] string basePath);
        DumpNextFn xDumpNext;
        StartFn2 xPink;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StartFn2(int on);
        internal void Pink(int on) { if (nativeLoaded) xPink(on); }
        internal void Dump(string name) { if (nativeLoaded) { Directory.CreateDirectory(ShotDir); xDumpNext(Path.Combine(ShotDir, name)); } }

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            var p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        // ---- settings, set by the plugin ----
        internal string PluginDir, ShotDir;
        internal float Resolution = 1f, IpdOverride;
        internal Vector3 LabHead;
        internal bool Stamp;
        internal int HudMode;  // 0 Fixed, 1 Follows head, 2 Hidden
        internal int PictureMode;  // lab: 0 Auto, 1 Steady, 2 Panel
        internal float HudWidth = 1.2f, HudX, HudY = -0.40f, HudDistance = 1.3f;
        internal float MenuWidth = 0.8f, MenuX, MenuY = -0.1f, MenuDistance = 0.9f;
        internal MenuUI Menu;

        // ---- state ----
        internal float PeriodMs;
        internal int EyeWidth, EyeHeight;
        internal string RuntimeName = "", HeadsetName = "";
        internal string StartProblem;
        bool nativeLoaded, active, made, wantShot, wantRecenter = true, autoRecentered, loggedView, runtimeRead;
        float focusedSince = -1f;
        IntPtr renderEvent;
        readonly RenderTexture[] eyeRender = new RenderTexture[2];
        readonly RenderTexture[] eyeOut = new RenderTexture[2];
        RenderTexture hudRt, hudOut;
        Material copyMat;
        Texture2D stampTex;
        readonly StringBuilder logBuf = new StringBuilder(512);
        int lastState = -1, eyeCheckFrames;
        // Recenter point, kept in Unity space (eyes) and OpenXR space (panels).
        Vector3 viewPoint, xrViewPoint;
        Quaternion viewTurn = Quaternion.identity, xrTurn = Quaternion.identity;
        float lastScale;
        string lastRig = "";
        Camera hudCam;
        CameraClearFlags hudClear;
        Color hudBg;
        Vector3 menuPos;
        Quaternion menuTurn = Quaternion.identity;
        bool menuWasOpen;
        FrameMeta meta;
        ViewInfo lastView;

        internal bool Active { get { return active; } }
        internal bool Running { get { return active && made; } }

        bool LoadNative()
        {
            if (nativeLoaded) return true;
            var path = Path.Combine(PluginDir, Dll + ".dll");
            var module = LoadLibraryA(path);
            if (module == IntPtr.Zero) { Plugin.Log.LogError("could not load " + path + ", error " + Marshal.GetLastWin32Error()); return false; }
            xGetRenderEvent = Fn<GetRenderEventFn>(module, "NslXR_GetRenderEvent");
            xState = Fn<IntFn>(module, "NslXR_State");
            xTakeRecenter = Fn<IntFn>(module, "NslXR_TakeRecenter");
            xRecW = Fn<IntFn>(module, "NslXR_RecommendedWidth");
            xRecH = Fn<IntFn>(module, "NslXR_RecommendedHeight");
            xEyeW = Fn<IntFn>(module, "NslXR_EyeWidth");
            xEyeH = Fn<IntFn>(module, "NslXR_EyeHeight");
            xLastError = Fn<IntFn>(module, "NslXR_LastError");
            xStart = Fn<StartFn>(module, "NslXR_Start");
            xGetViews = Fn<GetViewsFn>(module, "NslXR_GetViews");
            xQueueFrame = Fn<QueueFrameFn>(module, "NslXR_QueueFrame");
            xCapWait = Fn<CapWaitFn>(module, "NslXR_CapWait");
            xStats = Fn<StatsFn>(module, "NslXR_Stats");
            xPopLog = Fn<PopLogFn>(module, "NslXR_PopLog");
            xRuntimeInfo = Fn<RuntimeInfoFn>(module, "NslXR_RuntimeInfo");
            xFailedLayers = Fn<FailedLayersFn>(module, "NslXR_FailedLayers");
            xDumpNext = Fn<DumpNextFn>(module, "NslXR_DumpNext");
            xPink = Fn<StartFn2>(module, "NslXR_PanelPink");
            renderEvent = xGetRenderEvent();
            nativeLoaded = true;
            return true;
        }

        internal void Begin()
        {
            if (active || !LoadNative()) return;
            var shader = Shader.Find("Hidden/BlitCopy");
            if (shader == null) { Plugin.Log.LogError("VR: missing shader Hidden/BlitCopy"); return; }
            copyMat = new Material(shader);
            stampTex = new Texture2D(32, 1, TextureFormat.RGB24, false) { filterMode = FilterMode.Point };
            hudRt = new RenderTexture(HudW, HudH, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR HUD" };
            hudRt.Create();
            hudOut = new RenderTexture(HudW, HudH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR HUD out" };
            hudOut.Create();
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;  // the mod's own cap paces the game
            xStart(hudOut.GetNativeTexturePtr(), Resolution, HudW, HudH, MenuUI.TexW, MenuUI.TexH);
            GL.IssuePluginEvent(renderEvent, EventInit);
            active = true;
            made = false;
            StartProblem = null;
            Plugin.Log.LogInfo("VR starting, color space " + QualitySettings.activeColorSpace + ", camera rig " + Plugin.CameraRig.Value + ", Resolution " + Mathf.RoundToInt(Resolution * 100f) + "%");
            StartCoroutine(Loop());
        }

        internal void End()
        {
            if (!active) return;
            active = false;
            ReleaseHudCamera();
            GL.IssuePluginEvent(renderEvent, EventShutdown);
            Plugin.Log.LogInfo("VR stopped");
        }

        void MakeEyes()
        {
            EyeWidth = xEyeW();
            EyeHeight = xEyeH();
            int msaa = Mathf.Max(1, QualitySettings.antiAliasing);
            for (int i = 0; i < 2; i++)
            {
                eyeRender[i] = new RenderTexture(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR eye render " + i, antiAliasing = msaa };
                eyeRender[i].Create();
                eyeOut[i] = new RenderTexture(EyeWidth, EyeHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR eye out " + i };
                eyeOut[i].Create();
            }
            made = true;
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "RENDERSIZE runtime recommends {0}x{1}, Resolution {2:F0}%, the game renders {3}x{4} per eye ({5:F3} of the recommended pixels), MSAA {6}x",
                xRecW(), xRecH(), Resolution * 100f, EyeWidth, EyeHeight, (double)EyeWidth * EyeHeight / Math.Max(1.0, (double)xRecW() * xRecH()), msaa));
        }

        internal void Shot() { wantShot = true; }
        internal void Recenter() { wantRecenter = true; }
        internal void EyeCheck() { eyeCheckFrames = 4; }
        internal string NextLaunchSize()
        {
            int rw = nativeLoaded ? xRecW() : 0, rh = nativeLoaded ? xRecH() : 0;
            return rw > 0 ? (Mathf.RoundToInt(rw * Resolution) / 2 * 2) + "x" + (Mathf.RoundToInt(rh * Resolution) / 2 * 2) : "unknown until VR starts";
        }

        // Steve's rule for every mod: projection (Steady) for Virtual Desktop, quad (Panel) for every other runtime.
        internal static bool AutoPicksSteady(string runtime)
        {
            string name = (runtime ?? "").ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            return name.IndexOf("virtualdesktop", StringComparison.Ordinal) >= 0 || name.IndexOf("vdxr", StringComparison.Ordinal) >= 0;
        }

        void ReadRuntime()
        {
            var runtime = new StringBuilder(128);
            var headset = new StringBuilder(256);
            xRuntimeInfo(runtime, runtime.Capacity, headset, headset.Capacity);
            RuntimeName = runtime.ToString();
            HeadsetName = headset.ToString();
            runtimeRead = true;
            Plugin.Log.LogInfo("PICTURE setting Auto, runtime \"" + RuntimeName + "\", headset \"" + HeadsetName + "\", world as projection, HUD and menu " +
                (AutoPicksSteady(RuntimeName) ? "Steady (projection)" : "Panel (quad)"));
        }

        void Update()
        {
            if (!nativeLoaded) return;
            while (xPopLog(logBuf, logBuf.Capacity) != 0)
            {
                Plugin.Log.LogInfo("XR: " + logBuf);
                logBuf.Length = 0;
            }
            logBuf.Length = 0;
        }

        static string DescribeStartFailure(int error, string layers)
        {
            const string log = " Details are in BepInEx\\LogOutput.log in the game folder.";
            if (error == -32) return "An OpenXR add-on could not load (error -32)" + (layers.Length > 0 ? ": " + layers : "") + ". Switch that add-on off for VR games, then restart the game." + log;
            if (error == -51 || error == -1000) return "No OpenXR runtime was found (error " + error + "). Set SteamVR as the OpenXR runtime, or start Virtual Desktop, then restart the game." + log;
            return "OpenXR could not start" + (error != 0 ? " (error " + error + ")" : "") + ". Check your headset is on and its VR software is running, then restart the game." + log;
        }

        // ---- the frame ----

        IEnumerator Loop()
        {
            var wait = new WaitForEndOfFrame();
            float nextStat = Time.realtimeSinceStartup + 5f;
            int framesAtStat = Time.frameCount;
            float cpuSum = 0f, cpuMax = 0f, capSum = 0f, capMax = 0f, dtSum = 0f, dtMax = 0f;
            int n = 0;
            while (active)
            {
                yield return wait;
                if (!active) break;
                int state = xState();
                if (state != lastState) { lastState = state; Plugin.Log.LogInfo("XR native state " + state); }
                if (state == 3)
                {
                    var layers = new StringBuilder(512);
                    xFailedLayers(layers, layers.Capacity);
                    StartProblem = DescribeStartFailure(xLastError(), layers.ToString());
                    Plugin.Log.LogWarning("VR DID NOT START: " + StartProblem);
                    End();
                    yield break;
                }
                if (state != 2) continue;
                if (!made) MakeEyes();
                if (!runtimeRead) ReadRuntime();
                if (Application.targetFrameRate != -1 || QualitySettings.vSyncCount != 0)
                {
                    Plugin.Log.LogInfo("GAMESET the game turned VSync or a frame cap on during VR (targetFrameRate " + Application.targetFrameRate + ", vsync " + QualitySettings.vSyncCount + "). Turned off again: the mod paces the game");
                    Application.targetFrameRate = -1;
                    QualitySettings.vSyncCount = 0;
                }
                ViewInfo vi;
                xGetViews(out vi);
                if (vi.running == 0) continue;
                PeriodMs = vi.periodMs;
                if (xTakeRecenter() != 0) { wantRecenter = true; Plugin.Log.LogInfo("RECENTER from the runtime"); }
                if (vi.sessionState == 5) { if (focusedSince < 0f) focusedSince = Time.realtimeSinceStartup; } else focusedSince = -1f;
                if (!autoRecentered && focusedSince > 0f && Time.realtimeSinceStartup - focusedSince > 3f && GameCamera() != null)
                {
                    autoRecentered = true;
                    wantRecenter = true;
                    Plugin.Log.LogInfo("RECENTER automatic, once at start (session focused 3 s, game camera running)");
                }
                if (vi.viewsValid != 0 && wantRecenter) { wantRecenter = false; TakeView(vi); }
                if (vi.viewsValid != 0) lastView = vi;

                float t0 = Time.realtimeSinceStartup;
                ReadFade();
                GL.IssuePluginEvent(renderEvent, EventEyeBegin);
                bool world = RenderEyes(vi);
                int flags = vi.viewsValid != 0 ? FlagViewsValid : 0;
                bool hud = false;
                try { hud = UpdateHud(world); }
                catch (Exception e) { if (hudErrors++ < 5) Plugin.Log.LogError("HUD update failed: " + e); }
                if (hud) flags |= FlagHud;
                bool menuOpen = Menu != null && Menu.IsOpen && Menu.Texture != null;
                if (menuOpen) flags |= FlagMenu;
                if (PictureMode == 1 || (PictureMode == 0 && AutoPicksSteady(RuntimeName))) flags |= FlagSteady;
                if (screenMode) flags |= FlagHudOpaque;
                FillMeta(vi, flags, world);
                xQueueFrame(eyeOut[0].GetNativeTexturePtr(), eyeOut[1].GetNativeTexturePtr(), hud ? hudOut.GetNativeTexturePtr() : IntPtr.Zero,
                    menuOpen ? Menu.Texture.GetNativeTexturePtr() : IntPtr.Zero, ref meta);
                GL.IssuePluginEvent(renderEvent, EventSubmit);
                Mirror();
                float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                if (wantShot) { wantShot = false; SaveEyes(); }
                float capMs = xCapWait(vi.periodMs > 0f ? 1000f / vi.periodMs : 0f);
                cpuSum += ms; if (ms > cpuMax) cpuMax = ms;
                capSum += capMs; if (capMs > capMax) capMax = capMs;
                float dt = Time.unscaledDeltaTime * 1000f;
                dtSum += dt; if (dt > dtMax) dtMax = dt;
                n++;

                float now = Time.realtimeSinceStartup;
                if (now >= nextStat)
                {
                    Stats s;
                    xStats(out s);
                    float span = now - (nextStat - 5f);
                    Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "PERF {0:F1} fps (headset {1:F1} Hz, state {2}); frame avg {3:F2} max {4:F2} ms; mod eyes+HUD CPU avg {5:F2} max {6:F2} ms; cap wait avg {7:F2} max {8:F2} ms; " +
                        "GPU eyes avg {9:F2} max {10:F2} ms, GPU frame avg {11:F2} max {12:F2} ms; XR thread: wait avg {13:F2} max {14:F2}, end avg {15:F2} max {16:F2}, copy avg {17:F2} max {18:F2} ms; " +
                        "headset frames {19}, repeated {20}, skipped (no free slot) {21}, dropped as older {22}, handover failed {26}; eye {23}x{24}, rig {25}",
                        (Time.frameCount - framesAtStat) / span, vi.periodMs > 0f ? 1000f / vi.periodMs : 0f, vi.sessionState, n > 0 ? dtSum / n : 0f, dtMax,
                        n > 0 ? cpuSum / n : 0f, cpuMax, n > 0 ? capSum / n : 0f, capMax, s.gpuEyeAvg, s.gpuEyeMax, s.gpuFrameAvg, s.gpuFrameMax,
                        s.waitAvg, s.waitMax, s.endAvg, s.endMax, s.copyAvg, s.copyMax, s.submitted, s.repeats, s.skipped, s.replaced, EyeWidth, EyeHeight, lastRig, s.acquireFail));
                    nextStat = now + 5f;
                    framesAtStat = Time.frameCount;
                    cpuSum = cpuMax = capSum = capMax = dtSum = dtMax = 0f;
                    n = 0;
                }
            }
        }

        // OpenXR is right handed (x right, y up, looking down -z); Unity is left handed (looking down +z).
        static Vector3 Pos(EyeView e) { return new Vector3(e.px, e.py, -e.pz); }
        static Quaternion Rot(EyeView e) { return new Quaternion(-e.qx, -e.qy, e.qz, e.qw); }
        static Vector3 XrPos(EyeView e) { return new Vector3(e.px, e.py, e.pz); }
        static Quaternion XrRot(EyeView e) { return new Quaternion(e.qx, e.qy, e.qz, e.qw); }
        static Quaternion XrYaw(Quaternion q)
        {
            var f = q * new Vector3(0f, 0f, -1f);
            return Quaternion.AngleAxis(Mathf.Atan2(-f.x, -f.z) * Mathf.Rad2Deg, Vector3.up);
        }

        void TakeView(ViewInfo vi)
        {
            viewPoint = (Pos(vi.eye0) + Pos(vi.eye1)) * 0.5f;
            var fwd = Rot(vi.eye0) * Vector3.forward;
            viewTurn = Quaternion.Euler(0f, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg, 0f);
            xrViewPoint = (XrPos(vi.eye0) + XrPos(vi.eye1)) * 0.5f;
            xrTurn = XrYaw(XrRot(vi.eye0));
            PlaceMenu(vi);
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "RECENTER view ({0:F2},{1:F2},{2:F2}), turned {3:F0} degrees, eye gap {4:F4} m",
                xrViewPoint.x, xrViewPoint.y, xrViewPoint.z, viewTurn.eulerAngles.y, (Pos(vi.eye0) - Pos(vi.eye1)).magnitude));
        }

        internal Camera GameCamera()
        {
            var m = CameraMotor.Instance;
            var cine = CinematicCamera();
            if (cine != null) return cine;
            if (m != null && m.MyCamera != null && m.MyCamera.isActiveAndEnabled) return m.MyCamera;
            // Cutscene cameras inside levels carry the game's post-processing; the title and video cameras do not.
            var main = Camera.main;
            return main != null && main.isActiveAndEnabled && main.GetComponent<Playful.SuperLucky.PostProcess.PostProcess>() != null ? main : null;
        }

        // Level intros and some cutscenes are Cinemachine shots: they move the flat camera (kept running, drawing nothing)
        // while the VR camera stays with Lucky. While a cutscene runs and the brain shows anything but the gameplay
        // free-look camera, the eyes are drawn from the flat camera's pose.
        bool cineLogged;
        Camera CinematicCamera()
        {
            var cm = CameraManager.Instance;
            if (cm == null || !Plugin.WantVrRig || !Playful.SuperLucky.Sequences.CutsceneSequence.HasActiveCutscene) { if (cineLogged) { cineLogged = false; Plugin.Log.LogInfo("CINEMATIC over: back to the VR camera"); } return null; }
            var brain = cm.CinemachineBrain;
            if (brain == null || !brain.isActiveAndEnabled) return null;
            var vcam = brain.ActiveVirtualCamera;
            if (vcam == null || ReferenceEquals(vcam, cm.CinemachineFreeLook)) return null;
            var flat = cm.FlatScreenCamera != null ? cm.FlatScreenCamera.GetComponent<Camera>() : null;
            if (flat == null || !flat.gameObject.activeInHierarchy) return null;
            if (!cineLogged) { cineLogged = true; Plugin.Log.LogInfo("CINEMATIC cutscene shot " + vcam.Name + ": eyes follow the game's cutscene camera"); }
            return flat;
        }

        void Origin(Camera cam, out Vector3 pos, out Quaternion rot, out float scale)
        {
            var t = cam.transform;
            pos = t.position;
            if (Plugin.WantVrRig)
            {
                rot = t.rotation;
                scale = t.lossyScale.x;
                lastRig = "VR";
            }
            else
            {
                var f = t.forward;
                rot = Quaternion.Euler(0f, Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, 0f);
                scale = 1f;
                lastRig = "Flat";
            }
            if (IpdOverride > 0f) scale = IpdOverride;
            if (Mathf.Abs(scale - lastScale) > 0.0005f)
            {
                lastScale = scale;
                var ct = CameraMotor.Instance as CurveTrackingCamera;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "SCALE eye scale {0:F3} game units per metre ({1} rig, camera {2}{3})",
                    scale, lastRig, cam.name, ct != null && ct.VrCameraActiveSettings != null ? ", zone settings " + ct.VrCameraActiveSettings.name + " IPDScale " + ct.VrCameraActiveSettings.IPDScale.ToString("F3", CultureInfo.InvariantCulture) : ""));
            }
        }

        static Matrix4x4 Projection(EyeView e, float near, float far)
        {
            float l = Mathf.Tan(e.left), r = Mathf.Tan(e.right), u = Mathf.Tan(e.up), d = Mathf.Tan(e.down);
            var m = new Matrix4x4();
            m[0, 0] = 2f / (r - l); m[0, 2] = (r + l) / (r - l);
            m[1, 1] = 2f / (u - d); m[1, 2] = (u + d) / (u - d);
            m[2, 2] = -(far + near) / (far - near); m[2, 3] = -2f * far * near / (far - near);
            m[3, 2] = -1f;
            return m;
        }

        // Returns false when there is no game camera (title, loading, video): the eyes are black and the screen shows on a panel.
        bool RenderEyes(ViewInfo info)
        {
            if (info.viewsValid != 0 && !loggedView)
            {
                loggedView = true;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "XRVIEW left pos ({0:F3},{1:F3},{2:F3}) fov L {3:F3} R {4:F3} U {5:F3} D {6:F3}; right pos ({7:F3},{8:F3},{9:F3}) fov L {10:F3} R {11:F3}",
                    info.eye0.px, info.eye0.py, info.eye0.pz, info.eye0.left, info.eye0.right, info.eye0.up, info.eye0.down, info.eye1.px, info.eye1.py, info.eye1.pz, info.eye1.left, info.eye1.right));
            }
            var cam = GameCamera();
            if (cam == null)
            {
                for (int i = 0; i < 2; i++) { var old = RenderTexture.active; RenderTexture.active = eyeOut[i]; GL.Clear(true, true, Color.black); RenderTexture.active = old; }
                return false;
            }
            bool checking = eyeCheckFrames > 0;
            bool compareNow = eyeCheckFrames == 1;
            if (eyeCheckFrames > 0) eyeCheckFrames--;
            Vector3 opos; Quaternion orot; float scale;
            Origin(cam, out opos, out orot, out scale);
            var t = cam.transform;
            Vector3 savedPos = t.position;
            Quaternion savedRot = t.rotation;
            var savedTarget = cam.targetTexture;
            var inv = Quaternion.Inverse(viewTurn);
            var headNow = info.viewsValid != 0 ? inv * ((Pos(info.eye0) + Pos(info.eye1)) * 0.5f - viewPoint) : Vector3.zero;
            for (int eye = 0; eye < 2; eye++)
            {
                var e = eye == 0 ? info.eye0 : info.eye1;
                Vector3 local;
                Quaternion localRot;
                if (info.viewsValid != 0 && !checking)
                {
                    local = inv * (Pos(e) - viewPoint) + LabHead;
                    localRot = inv * Rot(e);
                }
                else
                {
                    local = checking ? headNow : new Vector3(eye == 0 ? -0.032f : 0.032f, 0f, 0f);
                    localRot = info.viewsValid != 0 ? inv * Rot(info.eye0) : Quaternion.identity;
                }
                var pos = opos + orot * (local * scale);
                var rot = orot * localRot;
                t.SetPositionAndRotation(pos, rot);
                cam.worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(pos, rot, Vector3.one).inverse;
                cam.projectionMatrix = Projection(checking ? info.eye0 : e, cam.nearClipPlane, cam.farClipPlane);
                cam.targetTexture = eyeRender[eye];
                cam.Render();
                Finish(eye);
            }
            cam.targetTexture = savedTarget;
            cam.ResetWorldToCameraMatrix();
            cam.ResetProjectionMatrix();
            t.SetPositionAndRotation(savedPos, savedRot);
            if (compareNow) CompareEyes();
            return true;
        }

        // Copies an image upside down (as the runtime wants it), with the frame number stamped in the corner when Stamp is on.
        void Flip(RenderTexture src, RenderTexture dst, bool stamp)
        {
            var old = RenderTexture.active;
            RenderTexture.active = dst;
            copyMat.mainTexture = src;
            GL.PushMatrix();
            GL.LoadOrtho();
            copyMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0f, 1f); GL.Vertex3(0f, 0f, 0f);
            GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 1f, 0f);
            GL.TexCoord2(1f, 0f); GL.Vertex3(1f, 1f, 0f);
            GL.TexCoord2(1f, 1f); GL.Vertex3(1f, 0f, 0f);
            GL.End();
            if (stamp)
            {
                int frame = Time.frameCount;
                for (int b = 0; b < 32; b++) stampTex.SetPixel(b, 0, ((frame >> b) & 1) != 0 ? Color.white : Color.black);
                stampTex.Apply();
                copyMat.mainTexture = stampTex;
                copyMat.SetPass(0);
                float w = 32f * 12f / dst.width, h = 12f / dst.height;
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(0f, h, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3(w, h, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3(w, 0f, 0f);
                GL.End();
            }
            GL.PopMatrix();
            RenderTexture.active = old;
        }

        void Finish(int i)
        {
            Flip(eyeRender[i], eyeOut[i], Stamp);
            if (fadeA > 0.001f) Darken(eyeOut[i], fadeColor, fadeA);
        }

        // The game fades to black through an overlay canvas image (GlobalFadeController._overlayImage), which only reaches the
        // window. Without it, the first level of each launch showed Lucky frozen while the game was still placing him
        // (Steve, build 4). The same fade is laid over both eye images.
        static readonly AccessTools.FieldRef<Playful.SuperLucky.Global.GlobalFadeController, UnityEngine.UI.Image> fadeImage =
            AccessTools.FieldRefAccess<Playful.SuperLucky.Global.GlobalFadeController, UnityEngine.UI.Image>("_overlayImage");
        Color fadeColor = Color.black;
        float fadeA;
        Material fadeMat;
        bool fadeLogged, stuckLogged;
        float blackSince = -1f;

        void ReadFade()
        {
            fadeA = 0f;
            var g = Playful.SuperLucky.Global.GlobalFadeController.Instance;
            if (g == null) return;
            var img = fadeImage(g);
            if (img == null || !img.isActiveAndEnabled) return;
            fadeColor = img.color;
            fadeA = img.color.a;
            // Safety: never keep the view black while Lucky can be controlled (a fade left on by a VR-only path must not
            // blind the player). Logged once with the fade's holders.
            var p = Playful.SuperLucky.Gameplay.Player.PlayerController.Instance;
            bool playable = p != null && p.Input != null && p.Input.InputEnabled && !Playful.SuperLucky.Sequences.CutsceneSequence.HasActiveCutscene;
            if (fadeA > 0.95f && playable) { if (blackSince < 0f) blackSince = Time.realtimeSinceStartup; } else blackSince = -1f;
            if (blackSince > 0f && Time.realtimeSinceStartup - blackSince > 3f)
            {
                if (!stuckLogged) { stuckLogged = true; Plugin.Log.LogWarning("FADE stuck black for 3 s while Lucky is playable: not drawn over the eyes"); try { g.LogLocks(); } catch (Exception) { } }
                fadeA = 0f;
            }
            if (!fadeLogged && fadeA > 0.5f) { fadeLogged = true; Plugin.Log.LogInfo("FADE the game's fade image is shown over the eyes (alpha " + fadeA.ToString("F2", CultureInfo.InvariantCulture) + ")"); }
        }

        void Darken(RenderTexture dst, Color c, float a)
        {
            if (fadeMat == null)
            {
                fadeMat = new Material(Shader.Find("Hidden/Internal-Colored"));
                fadeMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                fadeMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                fadeMat.SetInt("_Cull", 0);
                fadeMat.SetInt("_ZWrite", 0);
                fadeMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            var old = RenderTexture.active;
            RenderTexture.active = dst;
            GL.PushMatrix();
            GL.LoadOrtho();
            fadeMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(new Color(c.r, c.g, c.b, a));
            GL.Vertex3(0f, 0f, 0f); GL.Vertex3(0f, 1f, 0f); GL.Vertex3(1f, 1f, 0f); GL.Vertex3(1f, 0f, 0f);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = old;
        }

        // In-level cutscenes render from their own timeline camera, but the game's audio listener in VR mode is the VR camera,
        // which stays with Lucky; the flat game's listener follows the cutscene camera instead (CameraManager.Update). During a
        // cutscene the VR camera (and so the listener) is moved to the cutscene camera, and put back after.
        bool listenerMoved;

        void LateUpdate()
        {
            if (!active) return;
            var m = CameraMotor.Instance;
            var vr = m != null ? m.MyCamera : null;
            if (vr == null || !Plugin.WantVrRig) return;
            var game = GameCamera();
            bool cutscene = game != null && game != vr && Playful.SuperLucky.Sequences.CutsceneSequence.HasActiveCutscene;
            if (cutscene)
            {
                vr.transform.SetPositionAndRotation(game.transform.position, game.transform.rotation);
                if (!listenerMoved) { listenerMoved = true; Plugin.Log.LogInfo("AUDIO cutscene camera " + game.name + ": the listener follows it"); }
            }
            else if (listenerMoved)
            {
                listenerMoved = false;
                vr.transform.localPosition = Vector3.zero;
                vr.transform.localRotation = Quaternion.identity;
                Plugin.Log.LogInfo("AUDIO cutscene over: the listener is back on the VR camera");
            }
        }

        // ---- HUD ----

        // In gameplay the game's own HUD camera draws into the HUD texture instead of the window (transparent background).
        // With no game camera (title, loading, videos) every camera that draws to the window is drawn into it instead, opaque,
        // and it shows as a big screen in front of the player.
        bool screenMode;
        int hudErrors;
        int hudLogFrame;

        bool UpdateHud(bool world)
        {
            var cm = CameraManager.Instance;
            var hc = cm != null && cm.HudCamera != null ? cm.HudCamera.GetComponent<Camera>() : null;
            if (hc != hudCam)
            {
                ReleaseHudCamera();
                hudCam = hc;
                if (hudCam != null)
                {
                    hudClear = hudCam.clearFlags;
                    hudBg = hudCam.backgroundColor;
                    hudCam.targetTexture = hudRt;  // keeps its own depth-only clear: the mod's UI camera clears the texture first
                    Plugin.Log.LogInfo("HUD the game's HUD camera " + Probes.PathOf(hudCam.transform) + " now draws into the HUD panel (" + HudW + "x" + HudH + ")");
                }
            }
            if (hudCam != null && hudCam.targetTexture != hudRt) { hudCam.targetTexture = hudRt; if (Time.frameCount > hudLogFrame) { hudLogFrame = Time.frameCount + 600; Plugin.Log.LogInfo("HUD target was reset by the game, set again"); } }
            bool wasScreen = screenMode;
            screenMode = !world;
            if (screenMode != wasScreen) Plugin.Log.LogInfo(screenMode ? "SCREEN no game camera: the game's screen shows on a panel" : "SCREEN game camera back: HUD panel");
            if (screenMode)
            {
                ReleaseUiCams();
                DrawScreen();
                Flip(hudRt, hudOut, false);
                return true;
            }
            bool hudOn = hudCam != null && hudCam.isActiveAndEnabled;
            if (!DrawGameUi(hudOn && HudMode != 2)) return false;
            Flip(hudRt, hudOut, false);
            return true;
        }

        // The game's pause menu (a 3D book seen by its own UI camera, UIPawsMenu/CameraRoot/Camera, plus an overlay canvas)
        // and its dialogue boxes (an overlay canvas) draw straight to the window, so they never reached the headset
        // (Steve, build 2). UI-only cameras are drawn into the HUD texture after the HUD, and overlay canvases on the UI layer
        // are moved onto the mod's own UI camera, which draws into the same texture.
        Camera uiCam;
        int uiScanFrame, uiLayerMask = -1;
        readonly System.Collections.Generic.List<Canvas> movedCanvases = new System.Collections.Generic.List<Canvas>();
        readonly System.Collections.Generic.HashSet<int> loggedUiCams = new System.Collections.Generic.HashSet<int>();

        // Everything renders inside the frame, in camera depth order, into the HUD texture: the mod's UI camera first
        // (clears to transparent and draws the moved overlay canvases), then the game's UI-only cameras, then the HUD camera
        // (which only clears depth). Rendering the pause menu's camera by hand at the end of the frame drew its page text
        // upside down (RigB, 2026-11-20), so the game's cameras are retargeted instead of rendered by the mod.
        readonly System.Collections.Generic.List<Camera> uiCams = new System.Collections.Generic.List<Camera>();

        bool DrawGameUi(bool hudShown)
        {
            if (uiLayerMask < 0) uiLayerMask = LayerMask.GetMask("UI", "HUD");
            if (uiCam == null)
            {
                var go = new GameObject("NSLTVR UI camera");
                DontDestroyOnLoad(go);
                go.transform.position = new Vector3(0f, 60000f, -10f);
                uiCam = go.AddComponent<Camera>();
                uiCam.cullingMask = LayerMask.GetMask("UI");
                uiCam.clearFlags = CameraClearFlags.SolidColor;
                uiCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                uiCam.depth = -999f;
                uiCam.nearClipPlane = 0.1f;
                uiCam.farClipPlane = 1000f;
                uiCam.targetTexture = hudRt;
            }
            uiCam.enabled = true;
            var game = GameCamera();
            if (Time.frameCount >= uiScanFrame)
            {
                uiScanFrame = Time.frameCount + 60;
                foreach (var c in FindObjectsOfType<Canvas>())
                {
                    if (!c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceOverlay || c.gameObject.layer != 5 || movedCanvases.Contains(c)) continue;
                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    c.worldCamera = uiCam;
                    c.planeDistance = 10f;
                    movedCanvases.Add(c);
                    Plugin.Log.LogInfo("UI overlay canvas " + Probes.PathOf(c.transform) + " now draws into the HUD panel");
                }
                movedCanvases.RemoveAll(c => c == null);
                float gameDepth = game != null ? game.depth : -1000f;
                foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (c == null || !c.gameObject.scene.IsValid() || c == game || c == hudCam || c == uiCam || uiCams.Contains(c) || c.targetTexture != null) continue;
                    if (c.cullingMask == 0 || (c.cullingMask & ~uiLayerMask) != 0 || c.depth < gameDepth || c.name.StartsWith("NSLTVR", StringComparison.Ordinal)) continue;
                    c.targetTexture = hudRt;
                    uiCams.Add(c);
                    Plugin.Log.LogInfo("UI camera " + Probes.PathOf(c.transform) + " (depth " + c.depth + ", mask 0x" + c.cullingMask.ToString("X") + ") now draws into the HUD panel");
                }
                uiCams.RemoveAll(c => c == null);
            }
            bool any = uiCams.Any(c => c != null && c.isActiveAndEnabled) || movedCanvases.Any(c => c != null && c.isActiveAndEnabled);
            return any || hudShown;
        }

        // Back to the screen panel (title, loading): the game's UI cameras draw to the window again, so the screen copy has them.
        void ReleaseUiCams()
        {
            foreach (var c in uiCams) if (c != null) c.targetTexture = null;
            uiCams.Clear();
            if (uiCam != null) uiCam.enabled = false;
        }

        void ClearHud()
        {
            var old = RenderTexture.active;
            RenderTexture.active = hudRt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            RenderTexture.active = old;
        }

        void DrawScreen()
        {
            var old = RenderTexture.active;
            RenderTexture.active = hudRt;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = old;
            foreach (var c in Camera.allCameras.OrderBy(x => x.depth))
            {
                if (c == null || !c.enabled || c.targetTexture != null || c.name.StartsWith("NSLTVR", StringComparison.Ordinal)) continue;
                c.targetTexture = hudRt;
                c.Render();
                c.targetTexture = null;
            }
            if (uiCam != null && movedCanvases.Any(c => c != null && c.isActiveAndEnabled))
            {
                uiCam.clearFlags = CameraClearFlags.Nothing;  // on top of the screen copy
                uiCam.Render();
                uiCam.clearFlags = CameraClearFlags.SolidColor;
            }
        }

        void ReleaseHudCamera()
        {
            if (hudCam == null) return;
            hudCam.targetTexture = null;
            hudCam.clearFlags = hudClear;
            hudCam.backgroundColor = hudBg;
            hudCam = null;
        }

        static float[] PoseArray(Vector3 p, Quaternion q, float w, float h) { return new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w, w, h }; }

        void PlaceMenu(ViewInfo vi)
        {
            var head = (XrPos(vi.eye0) + XrPos(vi.eye1)) * 0.5f;
            menuTurn = XrYaw(XrRot(vi.eye0));
            menuPos = head + menuTurn * new Vector3(MenuX, MenuY, -MenuDistance);
        }

        void FillMeta(ViewInfo vi, int flags, bool world)
        {
            meta.flags = flags;
            meta.frame = Time.frameCount;
            meta.displayTime = vi.displayTime;
            meta.eye0 = vi.eye0;
            meta.eye1 = vi.eye1;
            // HUD: Fixed in the room at the recenter point, or following the head. The title screen is a big fixed screen.
            if (screenMode)
                meta.hudPose = PoseArray(xrViewPoint + xrTurn * new Vector3(0f, 0f, -2.5f), xrTurn, 2.6f, 2.6f * HudH / HudW);
            else if (HudMode == 1 && vi.viewsValid != 0)
            {
                var head = (XrPos(vi.eye0) + XrPos(vi.eye1)) * 0.5f;
                var q = XrRot(vi.eye0);
                meta.hudPose = PoseArray(head + q * new Vector3(HudX, HudY, -HudDistance), q, HudWidth, HudWidth * HudH / HudW);
            }
            else
            {
                // Fixed: in the room at the recenter point. Below eye level it tilts back to face the eyes.
                var tilt = xrTurn * Quaternion.AngleAxis(Mathf.Atan2(HudY, HudDistance) * Mathf.Rad2Deg, Vector3.right);
                meta.hudPose = PoseArray(xrViewPoint + xrTurn * new Vector3(HudX, HudY, -HudDistance), tilt, HudWidth, HudWidth * HudH / HudW);
            }
            bool open = Menu != null && Menu.IsOpen;
            if (open && !menuWasOpen && vi.viewsValid != 0) PlaceMenu(vi);  // the menu opens in front of where you face
            menuWasOpen = open;
            meta.menuPose = PoseArray(menuPos, menuTurn, MenuWidth, MenuWidth * MenuUI.TexH / MenuUI.TexW);
        }

        // Re-places the open menu after a placement change.
        internal void ReplaceMenu() { if (lastView.viewsValid != 0) PlaceMenu(lastView); }

        // The game's window shows the left eye (the VR camera rig leaves it black otherwise).
        void Mirror()
        {
            if (!made) return;
            float eyeAspect = (float)EyeWidth / EyeHeight, screenAspect = (float)Screen.width / Mathf.Max(1, Screen.height);
            float sy = Mathf.Min(1f, eyeAspect / screenAspect), sx = Mathf.Min(1f, screenAspect / eyeAspect);
            var src = screenMode ? hudRt : eyeRender[0];
            if (screenMode) { sx = sy = 1f; }
            Graphics.Blit(src, (RenderTexture)null, new Vector2(sx, sy), new Vector2((1f - sx) / 2f, (1f - sy) / 2f));
        }

        // ---- lab checks ----

        Color32[] Read(RenderTexture rt, string file)
        {
            var old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            if (file != null)
            {
                Directory.CreateDirectory(ShotDir);
                File.WriteAllBytes(Path.Combine(ShotDir, file), tex.EncodeToPNG());
            }
            var px = tex.GetPixels32();
            Destroy(tex);
            return px;
        }

        void CompareEyes()
        {
            var a = Read(eyeOut[0], null);
            var b = Read(eyeOut[1], null);
            int differing = 0, max = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                if (d > 0) differing++;
                if (d > max) max = d;
            }
            Plugin.Log.LogInfo(string.Format("EYECHECK frame {0}: both eyes at the head centre, differing pixels {1} of {2}, max {3}", Time.frameCount, differing, a.Length, max));
        }

        void SaveEyes()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var name = scene + "_" + lastRig + "_f" + Time.frameCount;
            Read(eyeOut[0], name + "_L.png");
            Read(eyeOut[1], name + "_R.png");
            Read(hudOut, name + "_HUD.png");
            if (Menu != null && Menu.Texture != null) Read(Menu.Texture, name + "_MENU.png");
            Plugin.Log.LogInfo("XRSHOT " + name + " (eye scale " + lastScale.ToString("F3", CultureInfo.InvariantCulture) + ")");
        }
    }
}
