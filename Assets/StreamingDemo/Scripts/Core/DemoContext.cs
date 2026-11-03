using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// Services shared by every demo mode. A mode never creates its own disk or
    /// memory budget: they all compete for the same simulated hardware, so the
    /// comparison between strategies is fair.
    /// </summary>
    public sealed class DemoContext
    {
        public DemoApp App;
        public SimulatedIO Disk;
        public MemoryBudget Memory;
        public Metrics Metrics;
        public PlayerAvatar Player;
        public CameraRig Rig;
        public Camera Cam;
        public DemoAssets Assets;
        public Light Sun;

        public struct LogEntry
        {
            public float Time;
            public string Text;
            public Color Color;
        }

        public readonly List<LogEntry> LogEntries = new List<LogEntry>();
        public const int MaxLog = 40;

        public string BannerText;
        public Color BannerColor;
        public float BannerUntil;

        /// <summary>Global "use fog to hide the edge of the loaded world" toggle.</summary>
        public bool FogEnabled = true;
        public float FogStart = 100f, FogEnd = 400f;

        public void Log(string text, Color color)
        {
            LogEntries.Add(new LogEntry { Time = Time.time, Text = text, Color = color });
            if (LogEntries.Count > MaxLog) LogEntries.RemoveAt(0);
        }

        /// <summary>Big centred message for key moments ("CELL BOUNDARY CROSSED").</summary>
        public void Banner(string text, Color color, float seconds = 2.2f)
        {
            BannerText = text;
            BannerColor = color;
            BannerUntil = Time.time + seconds;
        }

        public void SetFog(float start, float end)
        {
            FogStart = start;
            FogEnd = end;
            ApplyFog();
        }

        public void ApplyFog()
        {
            RenderSettings.fog = FogEnabled;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Palette.Background;
            RenderSettings.fogStartDistance = FogStart;
            RenderSettings.fogEndDistance = FogEnd;
        }
    }
}
