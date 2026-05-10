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

    internal enum AutoCaptureDataBucket
    {
        CleanBaseline,
        AnchorDecoupling,
        ChaosOcclusion,
        PureNoise
    }

    internal enum AutoCaptureDamageTemplateStyle
    {
        Realistic,
        Robust
    }

    internal enum AutoCaptureDamageTemplateKind
    {
        Single,
        DoubleTap,
        RapidCombo,
        StaggerCombo,
        Finisher
    }

    internal sealed class AutoCaptureRunOptions
    {
        public int MapId { get; set; }
        public string ResolutionName { get; set; }
        public string OutputDir { get; set; }
        public string OutputRootDir { get; set; }
        public string JobName { get; set; }
        public string JobDir { get; set; }
        public float TimeScale { get; set; } = 20f;
        public int Seed { get; set; } = 20260505;
        public bool MuteAudio { get; set; } = true;
        public int WriterThreads { get; set; } = 4;
        public int WriterQueueCapacity { get; set; } = 128;
        public AutoCaptureCameraPlan CameraPlan { get; set; } =
            AutoCaptureCameraPlan.CreateDefault();
        public Dictionary<AutoCaptureProfile, int> CaptureProfileMix { get; set; } =
            CreateDefaultProfileMix();
        public AutoCaptureDamageNumberControl DamageNumberControl { get; set; } =
            AutoCaptureDamageNumberControl.CreateDefault();
        public AutoCaptureHitEffectControl HitEffectControl { get; set; } =
            AutoCaptureHitEffectControl.CreateDefault();
        public AutoCaptureRealSkillEffectControl RealSkillEffect { get; set; } =
            AutoCaptureRealSkillEffectControl.CreateDefault();
        public AutoCaptureBucketMix BucketMix { get; set; } =
            AutoCaptureBucketMix.CreateDefault();
        public AutoCaptureBucketPolicy BucketPolicy { get; set; } =
            AutoCaptureBucketPolicy.CreateDefault();

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

        public AutoCaptureDamageNumberControl GetNormalizedDamageNumberControl()
        {
            return (DamageNumberControl ?? AutoCaptureDamageNumberControl.CreateDefault()).Normalize();
        }

        public AutoCaptureHitEffectControl GetNormalizedHitEffectControl()
        {
            return (HitEffectControl ?? AutoCaptureHitEffectControl.CreateDefault()).Normalize();
        }

        public AutoCaptureRealSkillEffectControl GetNormalizedRealSkillEffectControl()
        {
            return (RealSkillEffect ?? AutoCaptureRealSkillEffectControl.CreateDefault()).Normalize();
        }

        public AutoCaptureCameraPlan GetNormalizedCameraPlan()
        {
            return (CameraPlan ?? AutoCaptureCameraPlan.CreateDefault()).Normalize();
        }

        public AutoCaptureBucketMix GetNormalizedBucketMix()
        {
            return (BucketMix ?? AutoCaptureBucketMix.CreateDefault()).Normalize();
        }

        public AutoCaptureBucketPolicy GetNormalizedBucketPolicy()
        {
            return (BucketPolicy ?? AutoCaptureBucketPolicy.CreateDefault()).Normalize();
        }
    }

    internal sealed class AutoCaptureCameraPlan
    {
        public string Mode { get; set; } = "fixed_grid_once";
        public double GridOverlapRatioX { get; set; } = 0.2d;
        public double GridOverlapRatioY { get; set; } = 0.2d;
        public string StartCorner { get; set; } = "top_left";
        public string Traversal { get; set; } = "snake_rows";
        public int StartupWarmupFrames { get; set; } = 6;
        public int SettleFrames { get; set; } = 2;
        public int SampleFramesPerPoint { get; set; } = 4;

        public static AutoCaptureCameraPlan CreateDefault()
        {
            return new AutoCaptureCameraPlan();
        }

        public AutoCaptureCameraPlan Normalize()
        {
            string mode = string.IsNullOrWhiteSpace(Mode)
                ? "fixed_grid_once"
                : Mode.Trim().ToLowerInvariant();
            if (!string.Equals(mode, "fixed_grid_once", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PLAN_INVALID: camera_plan.mode must be fixed_grid_once.");
            }

            string startCorner = string.IsNullOrWhiteSpace(StartCorner)
                ? "top_left"
                : StartCorner.Trim().ToLowerInvariant();
            if (!string.Equals(startCorner, "top_left", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PLAN_INVALID: camera_plan.start_corner must be top_left.");
            }

            string traversal = string.IsNullOrWhiteSpace(Traversal)
                ? "snake_rows"
                : Traversal.Trim().ToLowerInvariant();
            if (!string.Equals(traversal, "snake_rows", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PLAN_INVALID: camera_plan.traversal must be snake_rows.");
            }

            double overlapX = Math.Clamp(GridOverlapRatioX, 0d, 0.95d);
            double overlapY = Math.Clamp(GridOverlapRatioY, 0d, 0.95d);
            if (Math.Abs(overlapX - GridOverlapRatioX) > 1e-9 || Math.Abs(overlapY - GridOverlapRatioY) > 1e-9)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PLAN_INVALID: camera_plan overlap ratios must be within [0, 0.95].");
            }

            int startupWarmupFrames = Math.Max(0, StartupWarmupFrames);
            int settleFrames = Math.Max(0, SettleFrames);
            int sampleFramesPerPoint = Math.Max(1, SampleFramesPerPoint);

            return new AutoCaptureCameraPlan
            {
                Mode = mode,
                GridOverlapRatioX = overlapX,
                GridOverlapRatioY = overlapY,
                StartCorner = startCorner,
                Traversal = traversal,
                StartupWarmupFrames = startupWarmupFrames,
                SettleFrames = settleFrames,
                SampleFramesPerPoint = sampleFramesPerPoint
            };
        }
    }

    internal sealed class AutoCaptureBucketMix
    {
        public int CleanBaseline { get; set; } = 20;
        public int AnchorDecoupling { get; set; } = 20;
        public int ChaosOcclusion { get; set; } = 40;
        public int PureNoise { get; set; } = 20;

        public static AutoCaptureBucketMix CreateDefault()
        {
            return new AutoCaptureBucketMix();
        }

        public AutoCaptureBucketMix Normalize()
        {
            int clean = Math.Max(0, CleanBaseline);
            int anchor = Math.Max(0, AnchorDecoupling);
            int chaos = Math.Max(0, ChaosOcclusion);
            int noise = Math.Max(0, PureNoise);
            int total = clean + anchor + chaos + noise;
            if (total <= 0)
            {
                return CreateDefault();
            }

            return new AutoCaptureBucketMix
            {
                CleanBaseline = clean,
                AnchorDecoupling = anchor,
                ChaosOcclusion = chaos,
                PureNoise = noise
            };
        }

        public int GetWeight(AutoCaptureDataBucket bucket)
        {
            return bucket switch
            {
                AutoCaptureDataBucket.CleanBaseline => Math.Max(0, CleanBaseline),
                AutoCaptureDataBucket.AnchorDecoupling => Math.Max(0, AnchorDecoupling),
                AutoCaptureDataBucket.ChaosOcclusion => Math.Max(0, ChaosOcclusion),
                AutoCaptureDataBucket.PureNoise => Math.Max(0, PureNoise),
                _ => 0
            };
        }
    }

    internal sealed class AutoCaptureBucketPolicy
    {
        public bool EnforceDeadMutualExclusion { get; set; } = true;
        public double StandMoveDamageLagProb { get; set; } = 0.03d;
        public double HitDamageMinProb { get; set; } = 0.90d;
        public string GlobalRatioScope { get; set; } = "global";

        public static AutoCaptureBucketPolicy CreateDefault()
        {
            return new AutoCaptureBucketPolicy();
        }

        public AutoCaptureBucketPolicy Normalize()
        {
            string scope = string.IsNullOrWhiteSpace(GlobalRatioScope)
                ? "global"
                : GlobalRatioScope.Trim().ToLowerInvariant();
            if (!string.Equals(scope, "global", StringComparison.Ordinal))
            {
                scope = "global";
            }

            return new AutoCaptureBucketPolicy
            {
                EnforceDeadMutualExclusion = EnforceDeadMutualExclusion,
                StandMoveDamageLagProb = Math.Clamp(StandMoveDamageLagProb, 0d, 1d),
                HitDamageMinProb = Math.Clamp(HitDamageMinProb, 0d, 1d),
                GlobalRatioScope = scope
            };
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
        public AutoCaptureDamageTemplateStyle TemplateStyle { get; set; } = AutoCaptureDamageTemplateStyle.Realistic;
        public Dictionary<AutoCaptureDamageTemplateKind, int> TemplateWeights { get; set; } =
            CreateDefaultTemplateWeights(AutoCaptureDamageTemplateStyle.Realistic);
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

        public static Dictionary<AutoCaptureDamageTemplateKind, int> CreateDefaultTemplateWeights(AutoCaptureDamageTemplateStyle style)
        {
            if (style == AutoCaptureDamageTemplateStyle.Robust)
            {
                return new Dictionary<AutoCaptureDamageTemplateKind, int>
                {
                    [AutoCaptureDamageTemplateKind.Single] = 15,
                    [AutoCaptureDamageTemplateKind.DoubleTap] = 20,
                    [AutoCaptureDamageTemplateKind.RapidCombo] = 30,
                    [AutoCaptureDamageTemplateKind.StaggerCombo] = 20,
                    [AutoCaptureDamageTemplateKind.Finisher] = 15
                };
            }

            return new Dictionary<AutoCaptureDamageTemplateKind, int>
            {
                [AutoCaptureDamageTemplateKind.Single] = 35,
                [AutoCaptureDamageTemplateKind.DoubleTap] = 30,
                [AutoCaptureDamageTemplateKind.RapidCombo] = 20,
                [AutoCaptureDamageTemplateKind.StaggerCombo] = 10,
                [AutoCaptureDamageTemplateKind.Finisher] = 5
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
                TemplateStyle = TemplateStyle,
                TemplateWeights = new Dictionary<AutoCaptureDamageTemplateKind, int>(),
                ProbByProfile = new Dictionary<AutoCaptureProfile, double>()
            };
            if (normalized.MinEventsPerCaptureFrame > normalized.MaxEventsPerCaptureFrameCap)
            {
                normalized.MinEventsPerCaptureFrame = normalized.MaxEventsPerCaptureFrameCap;
            }

            var sourceTemplateWeights = TemplateWeights ?? CreateDefaultTemplateWeights(normalized.TemplateStyle);
            foreach (AutoCaptureDamageTemplateKind kind in Enum.GetValues(typeof(AutoCaptureDamageTemplateKind)))
            {
                int value = sourceTemplateWeights.TryGetValue(kind, out int w) ? w : 0;
                normalized.TemplateWeights[kind] = Math.Max(0, value);
            }
            if (normalized.TemplateWeights.Values.Sum() <= 0)
            {
                normalized.TemplateWeights = CreateDefaultTemplateWeights(normalized.TemplateStyle);
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
        public double AlphaMin { get; set; } = 0.35d;
        public double AlphaMax { get; set; } = 0.75d;
        public double ScaleMin { get; set; } = 0.65d;
        public double ScaleMax { get; set; } = 1.25d;
        public int LifetimeMsMin { get; set; } = 100;
        public int LifetimeMsMax { get; set; } = 260;
        public int ExtraLayersMin { get; set; } = 0;
        public int ExtraLayersMax { get; set; } = 1;
        public int JitterPxX { get; set; } = 32;
        public int JitterPxY { get; set; } = 20;
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

    internal sealed class AutoCaptureRealSkillEffectControl
    {
        public bool Enabled { get; set; } = true;
        public string Source { get; set; } = "all_skills";
        public string Kind { get; set; } = "hit_only";

        public static AutoCaptureRealSkillEffectControl CreateDefault()
        {
            return new AutoCaptureRealSkillEffectControl();
        }

        public AutoCaptureRealSkillEffectControl Normalize()
        {
            string source = string.IsNullOrWhiteSpace(Source)
                ? "all_skills"
                : Source.Trim().ToLowerInvariant();
            if (!string.Equals(source, "all_skills", StringComparison.Ordinal))
            {
                source = "all_skills";
            }

            string kind = string.IsNullOrWhiteSpace(Kind)
                ? "hit_only"
                : Kind.Trim().ToLowerInvariant();
            if (!string.Equals(kind, "hit_only", StringComparison.Ordinal))
            {
                kind = "hit_only";
            }

            return new AutoCaptureRealSkillEffectControl
            {
                Enabled = Enabled,
                Source = source,
                Kind = kind
            };
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
