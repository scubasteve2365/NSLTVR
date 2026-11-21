using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace NSLTVR
{
    // The in-headset mod menu, styled after the game's own bright menus (deep purple card, white rounded text in the game's
    // own font, gold selection, teal tab line). Ported from CupheadVR's MenuUI (MIT, scubasteve2365); click sounds are a
    // Phase 2 item and left out. Hold both sticks for half a second, or press Insert, to open or close it.
    // LB/RB or LT/RT change tab, up and down choose, left and right change, A selects, B closes. Keyboard: arrows, Enter,
    // Esc or Backspace, Page Up/Down. While it is open the game gets no input (InControl switched off), and after it
    // closes the game gets nothing until every button and key is up. It is drawn into a texture that goes to the headset
    // as its own panel.
    internal class MenuUI : MonoBehaviour
    {
        internal class Row
        {
            public string Label;
            public Func<string> Value;      // null for a button row
            public Action<int> Change;      // left/right; null if the row has no value to change
            public Action Press;            // A; null to make A step the value up
            public Func<string> Help;
        }

        internal class Tab
        {
            public string Name;
            public List<Row> Rows = new List<Row>();
        }

        const int Layer = 31;
        const float HoldToOpen = 0.5f;
        internal const int TexW = 1024, TexH = 800;
        const int MaxRows = 7;
        static readonly Color Card = new Color(0.25f, 0.16f, 0.45f, 1f);
        static readonly Color CardDark = new Color(0.17f, 0.10f, 0.33f, 1f);
        static readonly Color Edge = new Color(0.98f, 0.80f, 0.20f, 1f);
        static readonly Color Text = Color.white;
        static readonly Color TextSoft = new Color(0.78f, 0.74f, 0.90f, 1f);
        static readonly Color Gold = new Color(1f, 0.82f, 0.22f, 1f);
        static readonly Color Teal = new Color(0.20f, 0.85f, 0.90f, 1f);

        internal List<Tab> Tabs = new List<Tab>();
        internal string Version = "";
        internal Func<string> Problem = () => null;
        internal Func<bool> VrActive = () => false;
        internal Action Closed;
        internal Func<int> Volume = () => 2;  // 0 off .. 3 high
        Click clickMove, clickChange, clickTab, clickOpen, clickClose, clickRefuse;

        // Short generated tones (no game audio files), ported from CupheadVR, pitched a little higher and rounder for NSLT.
        // The game plays all its sound through Wwise and Unity's own audio is silent in this game (Steve, build 15: no clicks
        // through an AudioSource), so the clicks go straight to Windows (winmm PlaySound from memory, asynchronous).
        void MakeSounds()
        {
            clickMove = new Click(990f, 1050f, 0.035f);
            clickChange = new Click(1320f, 1480f, 0.045f);
            clickTab = new Click(740f, 1110f, 0.07f);
            clickOpen = new Click(590f, 1180f, 0.12f);
            clickClose = new Click(1180f, 590f, 0.12f);
            clickRefuse = new Click(220f, 190f, 0.14f);
        }

        sealed class Click
        {
            const int Rate = 44100;
            readonly float[] data;
            readonly IntPtr[] wav = new IntPtr[4];  // per volume, unmanaged so PlaySound can read it after the call returns

            internal Click(float f0, float f1, float seconds)
            {
                int n = (int)(Rate * seconds);
                data = new float[n];
                double phase = 0;
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / n;
                    phase += 2 * Math.PI * Mathf.Lerp(f0, f1, t) / Rate;
                    float env = Mathf.Min(1f, i / (Rate * 0.004f)) * (1f - t) * (1f - t);
                    data[i] = (float)(Math.Sin(phase) + 0.25 * Math.Sin(2 * phase)) * env * 0.4f;
                }
            }

            internal IntPtr Wav(int volume)
            {
                if (wav[volume] != IntPtr.Zero) return wav[volume];
                float gain = volume == 1 ? 0.25f : volume == 2 ? 0.5f : 1f;
                int bytes = data.Length * 2;
                var b = new byte[44 + bytes];
                Action<int, string> str = (o, x) => { for (int i = 0; i < 4; i++) b[o + i] = (byte)x[i]; };
                Action<int, int> i32 = (o, x) => { b[o] = (byte)x; b[o + 1] = (byte)(x >> 8); b[o + 2] = (byte)(x >> 16); b[o + 3] = (byte)(x >> 24); };
                str(0, "RIFF"); i32(4, 36 + bytes); str(8, "WAVE"); str(12, "fmt "); i32(16, 16);
                b[20] = 1; b[22] = 1; i32(24, Rate); i32(28, Rate * 2); b[32] = 2; b[34] = 16;
                str(36, "data"); i32(40, bytes);
                for (int i = 0; i < data.Length; i++)
                {
                    short v = (short)Mathf.Clamp(data[i] * gain * 32767f, -32768f, 32767f);
                    b[44 + i * 2] = (byte)v; b[45 + i * 2] = (byte)(v >> 8);
                }
                wav[volume] = Marshal.AllocHGlobal(b.Length);
                Marshal.Copy(b, 0, wav[volume], b.Length);
                return wav[volume];
            }
        }

        [DllImport("winmm.dll", SetLastError = false)]
        static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
        const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;
        int soundErrors, soundLogs;

        void Play(Click clip)
        {
            int v = Volume != null ? Volume() : 2;
            if (clip == null || v <= 0) return;
            try { bool ok = PlaySound(clip.Wav(Mathf.Clamp(v, 1, 3)), IntPtr.Zero, SndAsync | SndNoDefault | SndMemory); if (soundLogs++ < 3) Plugin.Log.LogInfo("MENU click sound played by Windows: " + ok + " (Unity audio " + (AudioSettings.GetConfiguration().sampleRate) + " Hz, listeners " + FindObjectsOfType<AudioListener>().Length + ")"); }
            catch (Exception e) { if (soundErrors++ == 0) Plugin.Log.LogWarning("MENU click sound failed: " + e.Message); }
        }
        internal KeyCode MenuKey = KeyCode.Insert;
        internal bool IsOpen { get; private set; }
        internal RenderTexture Texture { get { return IsOpen ? outRt : null; } }

        RenderTexture rt, outRt;  // outRt is upside down, as the headset wants it
        Camera menuCam;
        GameObject root;
        TextMeshProUGUI[] tabText, labels, values;
        Image[] tabLine, rowBar;
        TextMeshProUGUI help, footer, version;
        int tab, selected;
        float bothSince = -1f, nextRepeat, closedAt;
        bool bothFired, waitRelease, aWas, bWas, tabLWas, tabRWas, dirty;
        int heldX, heldY;

        [StructLayout(LayoutKind.Sequential)]
        struct XState { public uint packet; public ushort buttons; public byte lt, rt; public short lx, ly, rx, ry; }
        [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint index, out XState state);
        const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, LStick = 0x40, RStick = 0x80, LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000;

        // Only connected pads are polled every frame; empty slots are checked about once a second (CPU hygiene).
        readonly bool[] padOn = new bool[4];
        float nextPadScan;

        ushort Buttons(out short lx, out short ly, out bool lt, out bool rtrig)
        {
            ushort all = 0;
            lx = ly = 0;
            lt = rtrig = false;
            bool scan = Time.realtimeSinceStartup >= nextPadScan;
            if (scan) nextPadScan = Time.realtimeSinceStartup + 1f;
            for (uint i = 0; i < 4; i++)
            {
                if (!padOn[i] && !scan) continue;
                XState s;
                bool was = padOn[i];
                padOn[i] = XInputGetState(i, out s) == 0;
                if (padOn[i] != was) Plugin.Log.LogInfo("PAD Xbox pad " + i + (padOn[i] ? " connected" : " disconnected"));
                if (!padOn[i]) continue;
                all |= s.buttons;
                lt |= s.lt > 128;
                rtrig |= s.rt > 128;
                if (Math.Abs((int)s.lx) > Math.Abs((int)lx)) lx = s.lx;
                if (Math.Abs((int)s.ly) > Math.Abs((int)ly)) ly = s.ly;
            }
            return all;
        }

        // The game's own font: the TextMesh Pro font most of its loaded UI text uses (the game draws its text with TextMesh Pro).
        static TMP_FontAsset GameFont()
        {
            var counts = new Dictionary<TMP_FontAsset, int>();
            foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                if (t.font != null) { int c; counts.TryGetValue(t.font, out c); counts[t.font] = c + 1; }
            var best = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            Plugin.Log.LogInfo("MENU fonts in use: " + string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key.name + " x" + kv.Value).ToArray())
                + "; menu uses " + (best != null ? best.name : "TextMesh Pro default"));
            return best ?? TMP_Settings.defaultFontAsset;
        }

        bool Build()
        {
            rt = new RenderTexture(TexW, TexH, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR menu" };
            rt.Create();
            outRt = new RenderTexture(TexW, TexH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "NSLTVR menu out" };
            outRt.Create();
            var camGo = new GameObject("NSLTVR menu camera");
            DontDestroyOnLoad(camGo);
            camGo.transform.position = new Vector3(0f, 50000f, -10f);
            menuCam = camGo.AddComponent<Camera>();
            menuCam.enabled = false;
            menuCam.orthographic = true;
            menuCam.orthographicSize = TexH / 2f;
            menuCam.cullingMask = 1 << Layer;
            menuCam.clearFlags = CameraClearFlags.SolidColor;
            menuCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            menuCam.nearClipPlane = 0.1f;
            menuCam.farClipPlane = 100f;
            menuCam.targetTexture = rt;

            root = new GameObject("NSLTVR menu");
            DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = menuCam;
            canvas.planeDistance = 5f;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            TMP_FontAsset font = GameFont();

            // The card: a gold rim, a purple card, a darker band for the help text.
            Img(Edge, 1012, 788, 0, 0);
            Img(Card, 996, 772, 0, 0);
            version = Txt(font, 22, TextSoft, TextAnchor.MiddleRight, 400, 30, 280, 350);
            int n = Math.Max(1, Tabs.Count);
            tabText = new TextMeshProUGUI[n];
            tabLine = new Image[n];
            float tabW = 900f / n;
            for (int i = 0; i < n; i++)
            {
                float x = -450f + tabW * (i + 0.5f);
                tabText[i] = Txt(font, 40, Text, TextAnchor.MiddleCenter, tabW, 56, x, 296);
                tabLine[i] = Img(Teal, tabW - 40, 6, x, 262);
            }
            Img(TextSoft, 900, 2, 0, 250);
            labels = new TextMeshProUGUI[MaxRows];
            values = new TextMeshProUGUI[MaxRows];
            rowBar = new Image[MaxRows];
            for (int i = 0; i < MaxRows; i++)
            {
                float y = RowY(i);
                rowBar[i] = Img(CardDark, 900, 52, 0, y);
                labels[i] = Txt(font, 32, Text, TextAnchor.MiddleLeft, 560, 52, -150, y);
                values[i] = Txt(font, 32, Text, TextAnchor.MiddleRight, 420, 52, 225, y);
            }
            Img(CardDark, 900, 124, 0, -258);
            Img(Gold, 6, 124, -447, -258);
            help = Txt(font, 26, Text, TextAnchor.UpperLeft, 860, 114, 8, -262);
            help.enableWordWrapping = true;
            help.overflowMode = TextOverflowModes.Truncate;
            help.enableAutoSizing = true;
            help.fontSizeMin = 18;
            help.fontSizeMax = 26;
            footer = Txt(font, 21, TextSoft, TextAnchor.MiddleCenter, 960, 30, 0, -350);
            SetLayer(root);
            root.SetActive(false);
            MakeSounds();
            return true;
        }

        static float RowY(int row) { return 205f - row * 56f; }

        Image Img(Color c, float w, float h, float x, float y)
        {
            var go = new GameObject("image");
            go.transform.SetParent(root.transform, false);
            var img = go.AddComponent<Image>();
            img.color = c;
            img.rectTransform.sizeDelta = new Vector2(w, h);
            img.rectTransform.anchoredPosition = new Vector2(x, y);
            return img;
        }

        static TextAlignmentOptions Align(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                default: return TextAlignmentOptions.Center;
            }
        }

        TextMeshProUGUI Txt(TMP_FontAsset font, int size, Color c, TextAnchor anchor, float w, float h, float x, float y)
        {
            var go = new GameObject("text");
            go.transform.SetParent(root.transform, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = size;
            t.alignment = Align(anchor);
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.color = c;
            t.rectTransform.sizeDelta = new Vector2(w, h);
            t.rectTransform.anchoredPosition = new Vector2(x, y);
            return t;
        }

        static void SetLayer(GameObject go)
        {
            go.layer = Layer;
            foreach (Transform t in go.transform) SetLayer(t.gameObject);
        }

        // ---- behaviour ----

        internal void Open()
        {
            if (IsOpen || waitRelease || Tabs.Count == 0) return;
            if (root == null && !Build()) return;
            root.SetActive(true);
            IsOpen = true;
            selected = 0;
            SetGameInput(false);
            aWas = bWas = tabLWas = tabRWas = true;  // ignore presses already held
            Refresh();
            Play(clickOpen);
            Plugin.Log.LogInfo("MENU open");
        }

        internal void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (root != null) root.SetActive(false);
            waitRelease = true;  // the game stays deaf until every button is up
            Play(clickClose);
            closedAt = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo("MENU close");
            if (Closed != null) Closed();
        }

        static void SetGameInput(bool on)
        {
            try { InControl.InputManager.Enabled = on; Plugin.Log.LogInfo("MENU game input " + (on ? "on" : "off") + " (InControl enabled " + InControl.InputManager.Enabled + ")"); }
            catch (Exception e) { Plugin.Log.LogWarning("menu: could not switch game input: " + e.Message); }
        }

        internal void Refresh() { dirty = true; }

        void Redraw()
        {
            dirty = false;
            string problem = Problem();
            version.text = problem != null ? "VR DID NOT START" : Version;
            version.color = problem != null ? Gold : TextSoft;
            for (int i = 0; i < tabText.Length; i++)
            {
                bool on = i == tab;
                tabText[i].text = i < Tabs.Count ? Tabs[i].Name.ToUpperInvariant() : "";
                tabText[i].color = on ? Gold : TextSoft;
                tabLine[i].enabled = on;
            }
            var rows = Tabs[tab].Rows;
            for (int i = 0; i < MaxRows; i++)
            {
                bool used = i < rows.Count;
                bool sel = used && i == selected;
                labels[i].text = used ? rows[i].Label : "";
                values[i].text = used && rows[i].Value != null ? rows[i].Value() : "";
                labels[i].color = sel ? Gold : Text;
                values[i].color = sel ? Gold : Text;
                rowBar[i].enabled = sel;
            }
            string h = selected < rows.Count && rows[selected].Help != null ? rows[selected].Help() : "";
            help.text = problem ?? h;
            footer.text = "LB / RB: tab     Up / down: choose     Left / right: change     A: select     B: close";
            menuCam.Render();
            Graphics.Blit(rt, outRt, new Vector2(1f, -1f), new Vector2(0f, 1f));
        }

        void Update()
        {
            short lx, ly;
            bool lt, rtrig;
            ushort btn = Buttons(out lx, out ly, out lt, out rtrig);
            bool both = (btn & LStick) != 0 && (btn & RStick) != 0;
            float now = Time.realtimeSinceStartup;
            if (both)
            {
                if (bothSince < 0f) bothSince = now;
                if (!bothFired && now - bothSince >= HoldToOpen)
                {
                    bothFired = true;
                    if (IsOpen) Close(); else Open();
                }
            }
            else
            {
                bothSince = -1f;
                bothFired = false;
            }
            if (Input.GetKeyDown(MenuKey)) { if (IsOpen) Close(); else Open(); }

            if (waitRelease)
            {
                // Nothing reaches the game until the pad's buttons and the menu's keys are up. Only those: Unity's
                // Input.anyKey also counts joystick buttons from other devices, which kept the game deaf on Steve's PC (build 1).
                bool keys = Input.GetKey(MenuKey) || Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.Escape) || Input.GetKey(KeyCode.Backspace)
                    || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow)
                    || Input.GetKey(KeyCode.PageUp) || Input.GetKey(KeyCode.PageDown);
                bool idle = btn == 0 && !lt && !rtrig && !keys;
                if (idle || now - closedAt > 2f)
                {
                    waitRelease = false;
                    SetGameInput(true);
                    Plugin.Log.LogInfo("MENU game input back on (" + (idle ? "every button up" : "after 2 s, buttons still reported held: 0x" + btn.ToString("X4")) + ")");
                }
                return;
            }
            if (!IsOpen) return;

            bool a = (btn & A) != 0 || Input.GetKey(KeyCode.Return);
            bool b = (btn & B) != 0 || Input.GetKey(KeyCode.Escape) || Input.GetKey(KeyCode.Backspace);
            bool tabL = (btn & LB) != 0 || lt || Input.GetKey(KeyCode.PageUp);
            bool tabR = (btn & RB) != 0 || rtrig || Input.GetKey(KeyCode.PageDown);
            if (b && !bWas) { bWas = true; aWas = a; Close(); return; }
            if (a && !aWas) PressRow();
            if (tabL && !tabLWas) ChangeTab(-1);
            if (tabR && !tabRWas) ChangeTab(1);
            aWas = a;
            bWas = b;
            tabLWas = tabL;
            tabRWas = tabR;

            int y = (btn & Up) != 0 || ly > 16000 || Input.GetKey(KeyCode.UpArrow) ? 1 : (btn & Down) != 0 || ly < -16000 || Input.GetKey(KeyCode.DownArrow) ? -1 : 0;
            int x = (btn & Right) != 0 || lx > 16000 || Input.GetKey(KeyCode.RightArrow) ? 1 : (btn & Left) != 0 || lx < -16000 || Input.GetKey(KeyCode.LeftArrow) ? -1 : 0;
            if (y != heldY || x != heldX)
            {
                nextRepeat = now + 0.35f;
                Step(x, y);
            }
            else if ((x != 0 || y != 0) && now >= nextRepeat)
            {
                nextRepeat = now + 0.12f;
                Step(x, y);
            }
            heldX = x;
            heldY = y;
        }

        // With VR off the menu is drawn on the flat game window instead.
        GUIStyle bannerStyle;
        internal Func<float> ProblemTime = () => 0f;

        void OnGUI()
        {
            string problem = Problem();
            // VR did not start: say so on the flat screen for 30 s, so a player never sits in front of a silent flat game
            // (LESSONS_LEARNED.md "VR fails and the player sees nothing"). The menu (Insert) shows the reason too.
            if (!VrActive() && !IsOpen && problem != null && Time.realtimeSinceStartup - ProblemTime() < 30f)
            {
                if (bannerStyle == null)
                {
                    bannerStyle = new GUIStyle(GUI.skin.box) { fontSize = Math.Max(12, Screen.height / 60), wordWrap = true, alignment = TextAnchor.MiddleLeft };
                    bannerStyle.normal.textColor = Color.white;
                    bannerStyle.padding = new RectOffset(16, 16, 10, 10);
                }
                GUI.Box(new Rect(Screen.width * 0.08f, Screen.height * 0.03f, Screen.width * 0.84f, Screen.height * 0.22f),
                    "New Super Lucky's Tale VR: VR DID NOT START. " + problem + " Press " + MenuKey + " for the mod menu.", bannerStyle);
                return;
            }
            if (VrActive() || !IsOpen || rt == null) return;
            float h = Screen.height * 0.8f, w = h * rt.width / rt.height;
            GUI.DrawTexture(new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h), rt);
        }

        void LateUpdate()
        {
            if (IsOpen && dirty) Redraw();
        }

        void ChangeTab(int dir)
        {
            tab = (tab + dir + Tabs.Count) % Tabs.Count;
            selected = 0;
            Play(clickTab);
            Refresh();
        }

        void PressRow()
        {
            var rows = Tabs[tab].Rows;
            if (selected >= rows.Count) return;
            var r = rows[selected];
            if (r.Press != null) { r.Press(); Play(clickChange); }
            else if (r.Change != null) ChangeRow(r, 1);
            else Play(clickRefuse);
            Refresh();
        }

        void ChangeRow(Row r, int dir)
        {
            if (r.Change == null) { Play(clickRefuse); return; }
            string before = r.Value != null ? r.Value() : null;
            r.Change(dir);
            Play(r.Value != null && r.Value() == before ? clickRefuse : clickChange);  // at a limit nothing changes
        }

        void Step(int x, int y)
        {
            var rows = Tabs[tab].Rows;
            if (y != 0) { selected = (selected - y + rows.Count) % rows.Count; Play(clickMove); }
            else if (x != 0 && selected < rows.Count) ChangeRow(rows[selected], x);
            else return;
            Refresh();
        }

        // Lab: drive the menu from the command file.
        internal void LabKey(string key)
        {
            if (key == "open") { Open(); return; }
            if (!IsOpen) return;
            if (key == "up") Step(0, 1);
            else if (key == "down") Step(0, -1);
            else if (key == "left") Step(-1, 0);
            else if (key == "right") Step(1, 0);
            else if (key == "a") PressRow();
            else if (key == "b") Close();
            else if (key == "lb") ChangeTab(-1);
            else if (key == "rb") ChangeTab(1);
        }
    }
}
