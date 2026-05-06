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
        public AutoCaptureHitEffectControl HitEffectControl { get; set; } =
            AutoCaptureHitEffectControl.CreateDefault();

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

        public AutoCaptureHitEffectControl GetNormalizedHitEffectControl()
        {
            return (HitEffectControl ?? AutoCaptureHitEffectControl.CreateDefault()).Normalize();
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

    internal enum AutoCaptureHitEffectPaletteMode
    {
        Basic,
        Extended
    }

    internal sealed class AutoCaptureHitEffectControl
    {
        public bool Enabled { get; set; } = true;
        public AutoCaptureHitEffectPaletteMode PaletteMode { get; set; } = AutoCaptureHitEffectPaletteMode.Extended;
        public double AlphaMin { get; set; } = 0.45d;
        public double AlphaMax { get; set; } = 0.90d;
        public double ScaleMin { get; set; } = 0.70d;
        public double ScaleMax { get; set; } = 1.50d;
        public int LifetimeMsMin { get; set; } = 120;
        public int LifetimeMsMax { get; set; } = 360;
        public int ExtraLayersMin { get; set; } = 0;
        public int ExtraLayersMax { get; set; } = 2;
        public int JitterPxX { get; set; } = 48;
        public int JitterPxY { get; set; } = 28;
        public List<int> VariationPool { get; set; } = new List<int> { 0, 1, 2, 3 };

        public static AutoCaptureHitEffectControl CreateDefault()
        {
            return new AutoCaptureHitEffectControl();
        }

        public AutoCaptureHitEffectControl Normalize()
        {
            var normalized = new AutoCaptureHitEffectControl
            {
                Enabled = Enabled,
                PaletteMode = PaletteMode,
                AlphaMin = Math.Clamp(AlphaMin, 0.05d, 1.00d),
                AlphaMax = Math.Clamp(AlphaMax, 0.05d, 1.00d),
                ScaleMin = Math.Clamp(ScaleMin, 0.30d, 2.50d),
                ScaleMax = Math.Clamp(ScaleMax, 0.30d, 2.50d),
                LifetimeMsMin = Math.Clamp(LifetimeMsMin, 60, 2000),
                LifetimeMsMax = Math.Clamp(LifetimeMsMax, 60, 2000),
                ExtraLayersMin = Math.Clamp(ExtraLayersMin, 0, 6),
                ExtraLayersMax = Math.Clamp(ExtraLayersMax, 0, 6),
                JitterPxX = Math.Clamp(JitterPxX, 0, 300),
                JitterPxY = Math.Clamp(JitterPxY, 0, 300),
                VariationPool = new List<int>()
            };

            if (normalized.AlphaMin > normalized.AlphaMax)
            {
                (normalized.AlphaMin, normalized.AlphaMax) = (normalized.AlphaMax, normalized.AlphaMin);
            }
            if (normalized.ScaleMin > normalized.ScaleMax)
            {
                (normalized.ScaleMin, normalized.ScaleMax) = (normalized.ScaleMax, normalized.ScaleMin);
            }
            if (normalized.LifetimeMsMin > normalized.LifetimeMsMax)
            {
                (normalized.LifetimeMsMin, normalized.LifetimeMsMax) = (normalized.LifetimeMsMax, normalized.LifetimeMsMin);
            }
            if (normalized.ExtraLayersMin > normalized.ExtraLayersMax)
            {
                (normalized.ExtraLayersMin, normalized.ExtraLayersMax) = (normalized.ExtraLayersMax, normalized.ExtraLayersMin);
            }

            if (VariationPool != null)
            {
                foreach (int v in VariationPool)
                {
                    if (v >= 0 && v <= 16)
                    {
                        normalized.VariationPool.Add(v);
                    }
                }
            }
            if (normalized.VariationPool.Count == 0)
            {
                normalized.VariationPool.AddRange(new[] { 0, 1, 2, 3 });
            }
            normalized.VariationPool = normalized.VariationPool.Distinct().ToList();
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
