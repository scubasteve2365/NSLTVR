using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

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
        const int MaxRows = 6;
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
        internal KeyCode MenuKey = KeyCode.Insert;
        internal bool IsOpen { get; private set; }
        internal RenderTexture Texture { get { return IsOpen ? outRt : null; } }

        RenderTexture rt, outRt;  // outRt is upside down, as the headset wants it
        Camera menuCam;
        GameObject root;
        UnityEngine.UI.Text[] tabText, labels, values;
        Image[] tabLine, rowBar;
        UnityEngine.UI.Text help, footer, version;
        int tab, selected;
        float bothSince = -1f, nextRepeat;
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

        // The game's own font: the one most of its loaded UI text uses.
        static Font GameFont()
        {
            var counts = new Dictionary<Font, int>();
            foreach (var t in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>())
                if (t.font != null) { int c; counts.TryGetValue(t.font, out c); counts[t.font] = c + 1; }
            var best = counts.Where(kv => kv.Key.name != "Arial").OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            Plugin.Log.LogInfo("MENU fonts in use: " + string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key.name + " x" + kv.Value).ToArray())
                + "; menu uses " + (best != null ? best.name : "Arial"));
            return best ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
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
            Font font = GameFont();

            // The card: a gold rim, a purple card, a darker band for the help text.
            Img(Edge, 1012, 788, 0, 0);
            Img(Card, 996, 772, 0, 0);
            version = Txt(font, 22, TextSoft, TextAnchor.MiddleRight, 400, 30, 280, 350);
            int n = Math.Max(1, Tabs.Count);
            tabText = new UnityEngine.UI.Text[n];
            tabLine = new Image[n];
            float tabW = 900f / n;
            for (int i = 0; i < n; i++)
            {
                float x = -450f + tabW * (i + 0.5f);
                tabText[i] = Txt(font, 40, Text, TextAnchor.MiddleCenter, tabW, 56, x, 296);
                tabLine[i] = Img(Teal, tabW - 40, 6, x, 262);
            }
            Img(TextSoft, 900, 2, 0, 250);
            labels = new UnityEngine.UI.Text[MaxRows];
            values = new UnityEngine.UI.Text[MaxRows];
            rowBar = new Image[MaxRows];
            for (int i = 0; i < MaxRows; i++)
            {
                float y = RowY(i);
                rowBar[i] = Img(CardDark, 900, 56, 0, y);
                labels[i] = Txt(font, 34, Text, TextAnchor.MiddleLeft, 560, 56, -150, y);
                values[i] = Txt(font, 34, Text, TextAnchor.MiddleRight, 420, 56, 225, y);
            }
            Img(CardDark, 900, 124, 0, -258);
            Img(Gold, 6, 124, -447, -258);
            help = Txt(font, 26, Text, TextAnchor.UpperLeft, 860, 114, 8, -262);
            help.horizontalOverflow = HorizontalWrapMode.Wrap;
            help.verticalOverflow = VerticalWrapMode.Truncate;
            help.resizeTextForBestFit = true;
            help.resizeTextMinSize = 18;
            help.resizeTextMaxSize = 26;
            footer = Txt(font, 21, TextSoft, TextAnchor.MiddleCenter, 960, 30, 0, -350);
            SetLayer(root);
            root.SetActive(false);
            return true;
        }

        static float RowY(int row) { return 200f - row * 62f; }

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

        UnityEngine.UI.Text Txt(Font font, int size, Color c, TextAnchor anchor, float w, float h, float x, float y)
        {
            var go = new GameObject("text");
            go.transform.SetParent(root.transform, false);
            var t = go.AddComponent<UnityEngine.UI.Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
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
            Plugin.Log.LogInfo("MENU open");
        }

        internal void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (root != null) root.SetActive(false);
            waitRelease = true;  // the game stays deaf until every button is up
            Plugin.Log.LogInfo("MENU close");
            if (Closed != null) Closed();
        }

        static void SetGameInput(bool on)
        {
            try { InControl.InputManager.Enabled = on; }
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
                if (btn == 0 && !lt && !rtrig && Mathf.Abs(lx) < 8000 && Mathf.Abs(ly) < 8000 && !Input.anyKey)
                {
                    waitRelease = false;
                    SetGameInput(true);
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
        void OnGUI()
        {
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
            Refresh();
        }

        void PressRow()
        {
            var rows = Tabs[tab].Rows;
            if (selected >= rows.Count) return;
            var r = rows[selected];
            if (r.Press != null) r.Press();
            else if (r.Change != null) r.Change(1);
            Refresh();
        }

        void Step(int x, int y)
        {
            var rows = Tabs[tab].Rows;
            if (y != 0) selected = (selected - y + rows.Count) % rows.Count;
            else if (x != 0 && selected < rows.Count && rows[selected].Change != null) rows[selected].Change(x);
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
