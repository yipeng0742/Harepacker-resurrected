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
        public AutoCaptureHpBarControl HpBarControl { get; set; } =
            AutoCaptureHpBarControl.CreateDefault();
        public AutoCaptureDamageNumberControl DamageNumberControl { get; set; } =
            AutoCaptureDamageNumberControl.CreateDefault();

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

        public AutoCaptureHpBarControl GetNormalizedHpBarControl()
        {
            return (HpBarControl ?? AutoCaptureHpBarControl.CreateDefault()).Normalize();
        }

        public AutoCaptureDamageNumberControl GetNormalizedDamageNumberControl()
        {
            return (DamageNumberControl ?? AutoCaptureDamageNumberControl.CreateDefault()).Normalize();
        }
    }

    internal sealed class AutoCaptureHpBarControl
    {
        public int HpEventGlobalCooldownMs { get; set; } = 180;
        public int HpEventPerMobCooldownMs { get; set; } = 650;
        public int MaxHpEventsPerCaptureFrame { get; set; } = 6;
        public int MaxHpActiveMobs { get; set; } = 6;
        public Dictionary<AutoCaptureProfile, double> HpEventProbByProfile { get; set; } =
            CreateDefaultProbabilities();

        public static AutoCaptureHpBarControl CreateDefault()
        {
            return new AutoCaptureHpBarControl();
        }

        public static Dictionary<AutoCaptureProfile, double> CreateDefaultProbabilities()
        {
            return new Dictionary<AutoCaptureProfile, double>
            {
                [AutoCaptureProfile.NormalMove] = 0.10d,
                [AutoCaptureProfile.AttackHeavy] = 0.18d,
                [AutoCaptureProfile.HitOcclusionHeavy] = 0.28d,
                [AutoCaptureProfile.DeathHeavy] = 0.06d
            };
        }

        public AutoCaptureHpBarControl Normalize()
        {
            var normalized = new AutoCaptureHpBarControl
            {
                HpEventGlobalCooldownMs = Math.Max(0, HpEventGlobalCooldownMs),
                HpEventPerMobCooldownMs = Math.Max(0, HpEventPerMobCooldownMs),
                MaxHpEventsPerCaptureFrame = Math.Max(0, MaxHpEventsPerCaptureFrame),
                MaxHpActiveMobs = Math.Max(1, MaxHpActiveMobs),
                HpEventProbByProfile = new Dictionary<AutoCaptureProfile, double>()
            };

            var source = HpEventProbByProfile ?? CreateDefaultProbabilities();
            foreach (AutoCaptureProfile profile in Enum.GetValues(typeof(AutoCaptureProfile)))
            {
                double value = source.TryGetValue(profile, out double p)
                    ? p
                    : CreateDefaultProbabilities()[profile];
                normalized.HpEventProbByProfile[profile] = Math.Clamp(value, 0d, 1d);
            }
            return normalized;
        }

        public double GetProbability(AutoCaptureProfile profile)
        {
            var source = HpEventProbByProfile ?? CreateDefaultProbabilities();
            if (!source.TryGetValue(profile, out double value))
            {
                value = CreateDefaultProbabilities()[profile];
            }
            return Math.Clamp(value, 0d, 1d);
        }
    }

    internal sealed class AutoCaptureDamageNumberControl
    {
        public bool UseMobRatioCap { get; set; } = true;
        public double MobRatio { get; set; } = 0.30d;
        public int MinEventsPerCaptureFrame { get; set; } = 1;
        public int MaxEventsPerCaptureFrameCap { get; set; } = 3;
        public int GlobalCooldownMs { get; set; } = 220;
        public int PerMobCooldownMs { get; set; } = 900;
        public int MaxEventsPerCaptureFrame { get; set; } = 2;
        public int MaxActiveNumbers { get; set; } = 36;
        public Dictionary<AutoCaptureProfile, double> ProbByProfile { get; set; } =
            CreateDefaultProbabilities();

        public static AutoCaptureDamageNumberControl CreateDefault()
        {
            return new AutoCaptureDamageNumberControl();
        }

        public static Dictionary<AutoCaptureProfile, double> CreateDefaultProbabilities()
        {
            return new Dictionary<AutoCaptureProfile, double>
            {
                [AutoCaptureProfile.NormalMove] = 0.08d,
                [AutoCaptureProfile.AttackHeavy] = 0.14d,
                [AutoCaptureProfile.HitOcclusionHeavy] = 0.20d,
                [AutoCaptureProfile.DeathHeavy] = 0.05d
            };
        }

        public AutoCaptureDamageNumberControl Normalize()
        {
            var normalized = new AutoCaptureDamageNumberControl
            {
                UseMobRatioCap = UseMobRatioCap,
                MobRatio = Math.Clamp(MobRatio, 0d, 1d),
                MinEventsPerCaptureFrame = Math.Max(0, MinEventsPerCaptureFrame),
                MaxEventsPerCaptureFrameCap = Math.Max(1, MaxEventsPerCaptureFrameCap),
                GlobalCooldownMs = Math.Max(0, GlobalCooldownMs),
                PerMobCooldownMs = Math.Max(0, PerMobCooldownMs),
                MaxEventsPerCaptureFrame = Math.Max(0, MaxEventsPerCaptureFrame),
                MaxActiveNumbers = Math.Max(1, MaxActiveNumbers),
                ProbByProfile = new Dictionary<AutoCaptureProfile, double>()
            };
            if (normalized.MinEventsPerCaptureFrame > normalized.MaxEventsPerCaptureFrameCap)
            {
                normalized.MinEventsPerCaptureFrame = normalized.MaxEventsPerCaptureFrameCap;
            }

            var source = ProbByProfile ?? CreateDefaultProbabilities();
            foreach (AutoCaptureProfile profile in Enum.GetValues(typeof(AutoCaptureProfile)))
            {
                double value = source.TryGetValue(profile, out double p)
                    ? p
                    : CreateDefaultProbabilities()[profile];
                normalized.ProbByProfile[profile] = Math.Clamp(value, 0d, 1d);
            }
            return normalized;
        }

        public double GetProbability(AutoCaptureProfile profile)
        {
            var source = ProbByProfile ?? CreateDefaultProbabilities();
            if (!source.TryGetValue(profile, out double value))
            {
                value = CreateDefaultProbabilities()[profile];
            }
            return Math.Clamp(value, 0d, 1d);
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
