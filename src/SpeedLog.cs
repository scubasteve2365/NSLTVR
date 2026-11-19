using System.Globalization;
using UnityEngine;

namespace NSLTVR
{
    // Game speed (standards: every mod logs it). Every window of real time: game seconds per real second from Unity's clock
    // (Time.time), the same for fixed ticks, frames and ticks per second, tick length, time scale, the headset rate and the
    // game's VSync and frame cap. From CupheadVR's SpeedLog.
    internal class SpeedLog : MonoBehaviour
    {
        internal XrRig Rig;
        internal bool On;
        internal float Window = 5f;
        float startReal, startGame, startUnscaled;
        int startFrame, ticks;

        internal void Begin(float seconds)
        {
            Window = seconds;
            On = true;
            Restart();
            Plugin.Log.LogInfo("SPEEDLOG on, window " + seconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
        }

        void Restart()
        {
            startReal = Time.realtimeSinceStartup;
            startGame = Time.time;
            startUnscaled = Time.unscaledTime;
            startFrame = Time.frameCount;
            ticks = 0;
        }

        void FixedUpdate() { if (On) ticks++; }

        void Update()
        {
            if (!On) return;
            float real = Time.realtimeSinceStartup - startReal;
            if (real < Window) return;
            float game = Time.time - startGame;
            float unscaled = Time.unscaledTime - startUnscaled;
            int frames = Time.frameCount - startFrame;
            float step = Time.fixedDeltaTime;
            float ts = Time.timeScale > 0f ? Time.timeScale : 1f;
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "GAMESPEED {0:F3} game s per real s (unscaled {1:F3}, per time scale {2:F3}), ticks {3:F3}, {4:F1} fps, {5:F1} ticks/s, tick {6:F5} s, timeScale {7:F2}, headset {8:F1} Hz, vsync {9}, frame cap {10}, over {11:F2} s",
                game / real, unscaled / real, game / real / ts, ticks * step / real / ts, frames / real, ticks / real, step, Time.timeScale,
                Rig != null && Rig.PeriodMs > 0f ? 1000f / Rig.PeriodMs : 0f, QualitySettings.vSyncCount, Application.targetFrameRate, real));
            Restart();
        }
    }
}
