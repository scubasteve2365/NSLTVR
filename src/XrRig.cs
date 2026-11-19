using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Playful.SuperLucky.Camera;
using UnityEngine;

namespace NSLTVR
{
    // Renders the game's camera once per eye from one game tick and hands both images to NSLTVRNative, which sends them to
    // OpenXR as one projection layer ("World" mode). OpenXR plumbing ported from CupheadVR's XrRig (MIT, scubasteve2365).
    //
    // Each eye: the game's camera transform is the tracking origin (what Lucky's Tale VR's CurveTrackingCamera drives), the
    // head pose since the last recenter is added on top, scaled by the camera's world scale (the rig's CameraScale, which the
    // game sets from the zone's IPDScale, so a bigger scale means wider eyes and a smaller looking world). The eye's own
    // off-axis lens comes from OpenXR. With the flat rig the game camera's yaw is kept and its pitch dropped, so the world
    // stays level, and FlatScale sets the eye gap.
    internal class XrRig : MonoBehaviour
    {
        const string Dll = "NSLTVRNative";
        const int EventInit = 1, EventSubmit = 2, EventShutdown = 3, EventResize = 4;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct EyeView { public float px, py, pz, qx, qy, qz, qw, left, right, up, down; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct FrameInfo
        {
            public int running, shouldRender, viewsValid, sessionState;
            public float periodMs;
            public EyeView eye0, eye1;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)] static extern IntPtr LoadLibraryA(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetRenderEventFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetSizeFn(int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetIntFn(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetFloatFn(float value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StartFn(IntPtr left, IntPtr right, int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BeginFrameFn(out FrameInfo info, IntPtr left, IntPtr right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PopLogFn(StringBuilder buffer, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RuntimeInfoFn(StringBuilder runtime, int runtimeSize, StringBuilder system, int systemSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StatsFn(out uint frames, out uint layers, out uint skipped);

        GetRenderEventFn xGetRenderEvent;
        IntFn xState, xTakeRecenter, xRecW, xRecH, xLastError;
        SetSizeFn xSetSize;
        SetIntFn xSetLayerMode;
        SetFloatFn xSetSharpen;
        StartFn xStart;
        BeginFrameFn xBeginFrame;
        PopLogFn xPopLog;
        StatsFn xStats;
        RuntimeInfoFn xRuntimeInfo;

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            var p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        internal string PluginDir, ShotDir;
        internal float Resolution = 1f, FlatScale = 1f, IpdOverride;
        internal Vector3 LabHead;
        internal bool Stamp = true;
        internal float PeriodMs;
        internal int EyeWidth = 1024, EyeHeight = 1024;

        bool nativeLoaded, active, sized, wantShot, wantRecenter = true, loggedView, runtimeRead;
        IntPtr renderEvent;
        readonly RenderTexture[] eyeRender = new RenderTexture[2];
        readonly RenderTexture[] eyeOut = new RenderTexture[2];
        Material copyMat;
        Texture2D stampTex;
        readonly StringBuilder logBuf = new StringBuilder(512);
        uint renderedPairs;
        int lastState = -1, eyeCheckFrames;
        Vector3 viewPoint;
        Quaternion viewTurn = Quaternion.identity;
        float lastScale;
        string lastRig = "";

        internal bool Active { get { return active; } }

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
            xLastError = Fn<IntFn>(module, "NslXR_LastError");
            xSetSize = Fn<SetSizeFn>(module, "NslXR_SetSize");
            xSetLayerMode = Fn<SetIntFn>(module, "NslXR_SetLayerMode");
            xSetSharpen = Fn<SetFloatFn>(module, "NslXR_SetSharpen");
            xStart = Fn<StartFn>(module, "NslXR_Start");
            xBeginFrame = Fn<BeginFrameFn>(module, "NslXR_BeginFrame");
            xPopLog = Fn<PopLogFn>(module, "NslXR_PopLog");
            xStats = Fn<StatsFn>(module, "NslXR_Stats");
            xRuntimeInfo = Fn<RuntimeInfoFn>(module, "NslXR_RuntimeInfo");
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
            MakeTextures(EyeWidth, EyeHeight);
            stampTex = new Texture2D(32, 1, TextureFormat.RGB24, false) { filterMode = FilterMode.Point };
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;  // xrWaitFrame paces the game
            xSetSharpen(0f);
            xSetLayerMode(2);
            xStart(eyeOut[0].GetNativeTexturePtr(), eyeOut[1].GetNativeTexturePtr(), EyeWidth, EyeHeight);
            GL.IssuePluginEvent(renderEvent, EventInit);
            active = true;
            sized = false;
            renderedPairs = 0;
            Plugin.Log.LogInfo("VR starting, color space " + QualitySettings.activeColorSpace + ", camera rig " + Plugin.CameraRig.Value);
            StartCoroutine(Loop());
        }

        internal void End()
        {
            if (!active) return;
            active = false;
            GL.IssuePluginEvent(renderEvent, EventShutdown);
            Plugin.Log.LogInfo("VR stopped after " + renderedPairs + " eye pairs");
        }

        void MakeTextures(int width, int height)
        {
            for (int i = 0; i < 2; i++)
            {
                var r = eyeRender[i];
                var o = eyeOut[i];
                eyeRender[i] = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR eye render " + i };
                eyeRender[i].Create();
                eyeOut[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR eye out " + i };
                eyeOut[i].Create();
                if (r != null) Destroy(r);
                if (o != null) Destroy(o);
            }
        }

        internal void Shot() { wantShot = true; }
        internal void Recenter() { wantRecenter = true; }
        internal void EyeCheck() { eyeCheckFrames = 4; }
        internal void ZeroDiff() { StartCoroutine(ZeroDiffSteps()); }

        void ReadRuntime()
        {
            var runtime = new StringBuilder(128);
            var headset = new StringBuilder(256);
            xRuntimeInfo(runtime, runtime.Capacity, headset, headset.Capacity);
            runtimeRead = true;
            Plugin.Log.LogInfo("XR runtime \"" + runtime + "\", headset \"" + headset + "\", layer World (projection, one image per eye)");
        }

        // The eye size the runtime recommends, times Resolution, applied once the runtime has said it.
        void SizeEyes()
        {
            int rw = xRecW(), rh = xRecH();
            if (rw <= 0 || rh <= 0) return;
            int w = Mathf.RoundToInt(rw * Resolution) / 4 * 4, h = Mathf.RoundToInt(rh * Resolution) / 4 * 4;
            sized = true;
            if (w == EyeWidth && h == EyeHeight) return;
            MakeTextures(w, h);
            EyeWidth = w;
            EyeHeight = h;
            xSetSize(w, h);
            GL.IssuePluginEvent(renderEvent, EventResize);
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "RESOLUTION runtime recommends {0}x{1}, Resolution {2:F2}, eye images {3}x{4}", rw, rh, Resolution, w, h));
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

        IEnumerator Loop()
        {
            var wait = new WaitForEndOfFrame();
            float nextStat = Time.realtimeSinceStartup + 5f;
            int framesAtStat = Time.frameCount;
            float cpuSum = 0f, cpuMax = 0f;
            int cpuCount = 0;
            while (active)
            {
                yield return wait;
                if (!active) break;
                int state = xState();
                if (state != lastState) { lastState = state; Plugin.Log.LogInfo("XR native state " + state); }
                if (state == 3)
                {
                    Plugin.Log.LogWarning("VR DID NOT START: OpenXR error " + xLastError() + " (details in the XR lines above)");
                    End();
                    yield break;
                }
                if (state != 2) continue;
                if (Application.targetFrameRate != -1 || QualitySettings.vSyncCount != 0)
                {
                    Plugin.Log.LogInfo("GAMESET the game set targetFrameRate " + Application.targetFrameRate + " vsync " + QualitySettings.vSyncCount + " during VR, turned off again: the headset paces the game");
                    Application.targetFrameRate = -1;
                    QualitySettings.vSyncCount = 0;
                }
                if (!runtimeRead) ReadRuntime();
                if (!sized) SizeEyes();
                for (int i = 0; i < 2; i++)
                {
                    if (!eyeRender[i].IsCreated()) eyeRender[i].Create();
                    if (!eyeOut[i].IsCreated()) eyeOut[i].Create();
                }
                FrameInfo info;
                if (xBeginFrame(out info, eyeOut[0].GetNativeTexturePtr(), eyeOut[1].GetNativeTexturePtr()) == 0) continue;
                if (xTakeRecenter() != 0) wantRecenter = true;
                PeriodMs = info.periodMs;
                if (info.viewsValid != 0 && wantRecenter) { wantRecenter = false; TakeView(info); }
                float t0 = Time.realtimeSinceStartup;
                if (info.shouldRender != 0) RenderEyes(info);
                float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                cpuSum += ms; cpuCount++; if (ms > cpuMax) cpuMax = ms;
                GL.IssuePluginEvent(renderEvent, EventSubmit);

                float now = Time.realtimeSinceStartup;
                if (now >= nextStat)
                {
                    uint frames, layers, skipped;
                    xStats(out frames, out layers, out skipped);
                    float span = now - (nextStat - 5f);
                    Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "XRSTAT state {0} period {1:F2} ms, unity {2:F1} fps, submitted {3}, with images {4}, skipped {5}, pairs {6}, eye {7}x{8}, eye render CPU avg {9:F2} ms max {10:F2} ms, rig {11}",
                        info.sessionState, info.periodMs, (Time.frameCount - framesAtStat) / span, frames, layers, skipped, renderedPairs, EyeWidth, EyeHeight,
                        cpuCount > 0 ? cpuSum / cpuCount : 0f, cpuMax, lastRig));
                    nextStat = now + 5f;
                    framesAtStat = Time.frameCount;
                    cpuSum = cpuMax = 0f; cpuCount = 0;
                }
            }
        }

        // OpenXR is right handed (x right, y up, looking down -z); Unity is left handed (looking down +z).
        static Vector3 Pos(EyeView e) { return new Vector3(e.px, e.py, -e.pz); }
        static Quaternion Rot(EyeView e) { return new Quaternion(-e.qx, -e.qy, e.qz, e.qw); }

        void TakeView(FrameInfo info)
        {
            viewPoint = (Pos(info.eye0) + Pos(info.eye1)) * 0.5f;
            var fwd = Rot(info.eye0) * Vector3.forward;
            viewTurn = Quaternion.Euler(0f, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg, 0f);
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "RECENTER view {0}, turned {1:F0} degrees, eye gap {2:F4} m", viewPoint, viewTurn.eulerAngles.y,
                (Pos(info.eye0) - Pos(info.eye1)).magnitude));
        }

        Camera GameCamera()
        {
            var m = CameraMotor.Instance;
            if (m != null && m.MyCamera != null && m.MyCamera.isActiveAndEnabled) return m.MyCamera;
            return Camera.main;
        }

        // Where the eyes sit in the game world: origin, turn and how many game units one metre of head movement is.
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
                scale = FlatScale;
                lastRig = "Flat";
            }
            if (IpdOverride > 0f) scale = IpdOverride;
            if (Mathf.Abs(scale - lastScale) > 0.0005f)
            {
                lastScale = scale;
                var ct = CameraMotor.Instance as CurveTrackingCamera;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "SCALE eye scale {0:F3} game units per metre ({1} rig{2}), camera pitch {3:F1}",
                    scale, lastRig, ct != null && ct.VrCameraActiveSettings != null ? ", zone settings " + ct.VrCameraActiveSettings.name + " IPDScale " + ct.VrCameraActiveSettings.IPDScale.ToString("F3", CultureInfo.InvariantCulture) + " CameraScale " + ct.CameraScale.ToString("F3", CultureInfo.InvariantCulture) : "",
                    t.eulerAngles.x));
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

        void RenderEyes(FrameInfo info)
        {
            if (info.viewsValid != 0 && !loggedView)
            {
                loggedView = true;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "XRVIEW left pos ({0:F3},{1:F3},{2:F3}) fov L {3:F3} R {4:F3} U {5:F3} D {6:F3}; right pos ({7:F3},{8:F3},{9:F3}) fov L {10:F3} R {11:F3}",
                    info.eye0.px, info.eye0.py, info.eye0.pz, info.eye0.left, info.eye0.right, info.eye0.up, info.eye0.down, info.eye1.px, info.eye1.py, info.eye1.pz, info.eye1.left, info.eye1.right));
            }
            var cam = GameCamera();
            if (cam == null) return;
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
                    local = checking ? Vector3.zero : new Vector3(eye == 0 ? -0.032f : 0.032f, 0f, 0f);
                    localRot = Quaternion.identity;
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
            renderedPairs++;
            if (wantShot) { wantShot = false; SaveEyes(); }
            if (compareNow) CompareEyes();
        }

        // Copies the eye image to the texture the native side sends on, upside down for the runtime, with the frame number.
        void Finish(int i)
        {
            var old = RenderTexture.active;
            RenderTexture.active = eyeOut[i];
            copyMat.mainTexture = eyeRender[i];
            GL.PushMatrix();
            GL.LoadOrtho();
            copyMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0f, 1f); GL.Vertex3(0f, 0f, 0f);
            GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 1f, 0f);
            GL.TexCoord2(1f, 0f); GL.Vertex3(1f, 1f, 0f);
            GL.TexCoord2(1f, 1f); GL.Vertex3(1f, 0f, 0f);
            GL.End();
            if (Stamp)
            {
                int frame = Time.frameCount;
                for (int b = 0; b < 32; b++) stampTex.SetPixel(b, 0, ((frame >> b) & 1) != 0 ? Color.white : Color.black);
                stampTex.Apply();
                copyMat.mainTexture = stampTex;
                copyMat.SetPass(0);
                float w = 32f * 12f / eyeOut[i].width, h = 12f / eyeOut[i].height;
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

        static void Diff(Color32[] a, Color32[] b, out int differing, out int max)
        {
            differing = 0; max = 0;
            if (a.Length != b.Length) { differing = -1; return; }
            for (int i = 0; i < a.Length; i++)
            {
                int d = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                if (d > 0) differing++;
                if (d > max) max = d;
            }
        }

        void CompareEyes()
        {
            int differing, max;
            Diff(Read(eyeOut[0], "check_L.png"), Read(eyeOut[1], "check_R.png"), out differing, out max);
            Plugin.Log.LogInfo(string.Format("EYECHECK frame {0}: both eyes at the head centre, differing pixels {1} of {2}, max {3}", Time.frameCount, differing, EyeWidth * EyeHeight, max));
        }

        void SaveEyes()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var name = scene + "_" + lastRig + "_f" + Time.frameCount;
            Read(eyeOut[0], name + "_L.png");
            Read(eyeOut[1], name + "_R.png");
            Plugin.Log.LogInfo("XRSHOT " + name + " (eye scale " + lastScale.ToString("F3", CultureInfo.InvariantCulture) + ")");
        }

        // Zero-difference control. At the end of a frame: (1) the game's own frame from the back buffer, (2) the game's camera
        // rendered again with nothing changed, (3) the same camera through the eye path with the eye placed exactly at the game
        // camera with the game's own projection, (4) the camera stack (every enabled camera in depth order) for the HUD.
        IEnumerator ZeroDiffSteps()
        {
            yield return new WaitForEndOfFrame();
            var cam = GameCamera();
            if (cam == null) { Plugin.Log.LogWarning("ZERODIFF no game camera"); yield break; }
            int w = Screen.width, h = Screen.height;
            var screen = new Texture2D(w, h, TextureFormat.RGB24, false);
            screen.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            screen.Apply();
            Directory.CreateDirectory(ShotDir);
            File.WriteAllBytes(Path.Combine(ShotDir, "zd_screen.png"), screen.EncodeToPNG());
            var game = screen.GetPixels32();
            Destroy(screen);
            var a = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var b = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var c = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var s = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prev = cam.targetTexture;
            cam.targetTexture = a; cam.Render();
            cam.targetTexture = b; cam.Render();
            // Eye path at the game camera: same transform calls and matrices the eye render uses.
            var t = cam.transform;
            Vector3 p = t.position; Quaternion r = t.rotation;
            var proj = cam.projectionMatrix;
            t.SetPositionAndRotation(p, r);
            cam.worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(p, r, Vector3.one).inverse;
            cam.projectionMatrix = proj;
            cam.targetTexture = c; cam.Render();
            cam.ResetWorldToCameraMatrix();
            cam.ResetProjectionMatrix();
            cam.targetTexture = prev;
            // Camera stack, as the game draws it to the screen.
            var old = RenderTexture.active;
            RenderTexture.active = s;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = old;
            var stack = Camera.allCameras.OrderBy(x => x.depth).ToArray();
            foreach (var x in stack)
            {
                if (x == null || !x.enabled || x.targetTexture != null) continue;
                x.targetTexture = s; x.Render(); x.targetTexture = null;
            }
            int d1, m1, d2, m2, d3, m3, d4, m4;
            var pa = Read(a, "zd_render1.png");
            var pb = Read(b, null);
            var pc = Read(c, "zd_eyepath.png");
            var ps = Read(s, "zd_stack.png");
            Diff(pa, pb, out d1, out m1);
            Diff(pa, pc, out d2, out m2);
            Diff(ps, game, out d3, out m3);
            Diff(pa, game, out d4, out m4);
            Plugin.Log.LogInfo(string.Format("ZERODIFF frame {0}, {1}x{2} ({3} pixels), camera {4}, stack [{5}]: render vs render again {6} differ (max {7}); eye path at the game camera vs render {8} differ (max {9}); camera stack vs game's frame {10} differ (max {11}); camera alone vs game's frame {12} differ (max {13})",
                Time.frameCount, w, h, w * h, cam.name, string.Join(", ", stack.Select(x => x.name + "@" + x.depth).ToArray()), d1, m1, d2, m2, d3, m3, d4, m4));
            Destroy(a); Destroy(b); Destroy(c); Destroy(s);
        }
    }
}
