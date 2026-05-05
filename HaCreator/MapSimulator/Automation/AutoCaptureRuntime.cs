using System;
using System.Collections.Generic;
using System.Linq;

namespace HaCreator.MapSimulator.Automation
{
    internal enum AutoCaptureProfile
    {
        NormalMove,
        AttackHeavy,
        HitOcclusionHeavy,
        DeathHeavy
    }

    internal sealed class AutoCaptureRunOptions
    {
        public int MapId { get; set; }
        public string ResolutionName { get; set; }
        public string OutputDir { get; set; }
        public int StepX { get; set; } = 96;
        public int StepY { get; set; } = 96;
        public int TargetFrames { get; set; } = 120;
        public float TimeScale { get; set; } = 20f;
        public int Seed { get; set; } = 20260505;
        public bool MuteAudio { get; set; } = true;
        public Dictionary<AutoCaptureProfile, int> CaptureProfileMix { get; set; } =
            CreateDefaultProfileMix();

        public static Dictionary<AutoCaptureProfile, int> CreateDefaultProfileMix()
        {
            return new Dictionary<AutoCaptureProfile, int>
            {
                [AutoCaptureProfile.NormalMove] = 30,
                [AutoCaptureProfile.AttackHeavy] = 30,
                [AutoCaptureProfile.HitOcclusionHeavy] = 25,
                [AutoCaptureProfile.DeathHeavy] = 15
            };
        }

        public Dictionary<AutoCaptureProfile, int> GetNormalizedProfileMix()
        {
            var src = CaptureProfileMix ?? CreateDefaultProfileMix();
            var normalized = new Dictionary<AutoCaptureProfile, int>();

            foreach (AutoCaptureProfile profile in Enum.GetValues(typeof(AutoCaptureProfile)))
            {
                int weight = src.TryGetValue(profile, out int w) ? w : 0;
                if (weight > 0)
                {
                    normalized[profile] = weight;
                }
            }

            if (normalized.Count == 0 || normalized.Values.Sum() <= 0)
            {
                return CreateDefaultProfileMix();
            }

            return normalized;
        }
    }

    internal static class AutoCaptureRuntime
    {
        [ThreadStatic]
        private static AutoCaptureRunOptions _current;

        internal static AutoCaptureRunOptions Current
        {
            get => _current;
            set => _current = value;
        }

        internal static bool IsAudioMuted => _current?.MuteAudio == true;
    }
}
