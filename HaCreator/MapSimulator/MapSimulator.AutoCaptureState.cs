using HaCreator.MapSimulator.Automation;
using HaCreator.MapSimulator.Animation;
using HaCreator.MapSimulator.Character.Skills;
using HaCreator.MapSimulator.Entities;
using HaCreator.MapSimulator.Effects;
using HaCreator.MapSimulator.Fields;
using HaCreator.MapSimulator.Managers;
using HaSharedLibrary;
using HaSharedLibrary.Render;
using HaSharedLibrary.Render.DX;
using MapleLib.WzLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private readonly AutoCaptureRunOptions _autoCaptureOptions = AutoCaptureRuntime.Current;
        private bool _autoCaptureStarted = false;
        private List<Point> _autoCaptureScanPath;
        private int _autoCaptureCurrentPointIndex = -1;
        private int _autoCaptureTotalPointCount = 0;
        private int _autoCaptureExpectedFrameCount = 0;
        private int _autoCaptureWarmupFramesRemaining = 0;
        private int _autoCaptureSettleFramesRemaining = 0;
        private int _autoCaptureSampledFramesAtPoint = 0;
        private int _autoCaptureSampleFramesPerPoint = 4;
        private Random _autoCaptureRandom;
        private AutoCaptureDataBucket _autoCaptureCurrentBucket = AutoCaptureDataBucket.CleanBaseline;
        private AutoCaptureProfile _autoCaptureCurrentProfile = AutoCaptureProfile.NormalMove;
        private int _autoCaptureProfileSwitchTick = 0;
        private Dictionary<AutoCaptureProfile, int> _autoCaptureProfileMix = AutoCaptureRunOptions.CreateDefaultProfileMix();
        private AutoCaptureBucketMix _autoCaptureBucketMix = AutoCaptureBucketMix.CreateDefault();
        private AutoCaptureBucketPolicy _autoCaptureBucketPolicy = AutoCaptureBucketPolicy.CreateDefault();
        private AutoCaptureDamageNumberControl _autoCaptureDamageNumberControl = AutoCaptureDamageNumberControl.CreateDefault();
        private AutoCaptureHitEffectControl _autoCaptureHitEffectControl = AutoCaptureHitEffectControl.CreateDefault();
        private AutoCaptureRealSkillEffectControl _autoCaptureRealSkillEffectControl = AutoCaptureRealSkillEffectControl.CreateDefault();
        private AutoCaptureSkillCatalogControl _autoCaptureSkillCatalog = AutoCaptureSkillCatalogControl.CreateDefault();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketAttempted = new Dictionary<AutoCaptureDataBucket, int>();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketSaved = new Dictionary<AutoCaptureDataBucket, int>();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketAttemptedSnapshot = new Dictionary<AutoCaptureDataBucket, int>();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketSavedSnapshot = new Dictionary<AutoCaptureDataBucket, int>();
        private bool _autoCaptureLastFrameHasForcedHitState = false;
        private bool _autoCaptureLastFrameDamageEventTriggered = false;
        private string _autoCaptureBucketManifestPath;
        private int _autoCaptureLastProfileLogFrame = -1;
        private AutoCaptureCameraPlan _autoCaptureCameraPlan = AutoCaptureCameraPlan.CreateDefault();
        private AutoCaptureCameraPhase _autoCaptureCameraPhase = AutoCaptureCameraPhase.Init;
        private readonly Dictionary<int, int> _autoCaptureDmgLastTickByMob = new Dictionary<int, int>();
        private int _autoCaptureDmgLastGlobalTick = int.MinValue / 2;
        private int _autoCaptureDmgFrameMarker = -1;
        private int _autoCaptureDmgEventsUsedOnCaptureFrame = 0;
        private int _autoCaptureDmgAttempted = 0;
        private int _autoCaptureDmgFired = 0;
        private int _autoCaptureDmgSkippedCooldown = 0;
        private int _autoCaptureDmgSegmentsEmitted = 0;
        private int _autoCaptureDmgMobsHit = 0;
        private int _autoCaptureDmgMobsHitCurrentFrame = 0;
        private int _autoCaptureDmgMobsHitPeakSinceLastLog = 0;
        private readonly List<AutoCapNativeDamageSkillEntry> _autoCaptureNativeDamageSkillPool = new List<AutoCapNativeDamageSkillEntry>();
        private readonly List<AutoCapNativeDamageSkillEntry> _autoCapturePointSkillPool = new List<AutoCapNativeDamageSkillEntry>();
        private int _autoCapturePointRecipeSeed = 0;
        private AutoCapDamageTemplate _autoCapturePointDamageTemplate = AutoCapDamageTemplate.Single;
        private static readonly JsonSerializerOptions AutoCaptureSkillCatalogJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private sealed class AutoCapNativeDamageSkillEntry
        {
            public int SkillId { get; set; }
            public string Name { get; set; }
            public int Job { get; set; }
            public int AttackCount { get; set; }
            public int DamagePercent { get; set; }
            public int CriticalRatePercent { get; set; }
            public string VisualFamily { get; set; }
            public string OcclusionLevel { get; set; }
            public int[] SegmentOffsetsMs { get; set; } = Array.Empty<int>();
            public SkillAnimation CachedHitEffect { get; set; }
        }

        private sealed class AutoCaptureSkillCatalogDocument
        {
            public int Version { get; set; } = 1;
            public List<AutoCaptureSkillCatalogEntry> Skills { get; set; } = new List<AutoCaptureSkillCatalogEntry>();
        }

        private sealed class AutoCaptureSkillCatalogEntry
        {
            public int SkillId { get; set; }
            public string Name { get; set; }
            public bool Enabled { get; set; }
            public int Job { get; set; }
        }

        private int _autoCaptureDmgAttemptedSnapshot = 0;
        private int _autoCaptureDmgFiredSnapshot = 0;
        private int _autoCaptureDmgSkippedCooldownSnapshot = 0;
        private int _autoCaptureDmgSegmentsEmittedSnapshot = 0;
        private int _autoCaptureCaptureAttempted = 0;
        private int _autoCaptureCaptureSaved = 0;
        private int _autoCaptureCaptureSkippedEmpty = 0;
        private int _autoCaptureBoundsRawCount = 0;
        private int _autoCaptureBoundsUsableCount = 0;
        private int _autoCaptureCaptureAttemptedSnapshot = 0;
        private int _autoCaptureCaptureSavedSnapshot = 0;
        private int _autoCaptureCaptureSkippedEmptySnapshot = 0;
        private int _autoCaptureBoundsRawSnapshot = 0;
        private int _autoCaptureBoundsUsableSnapshot = 0;
        private int _autoCaptureSaveFailCount = 0;
        private int _autoCaptureSaveFailCountSnapshot = 0;
        private readonly Dictionary<string, int> _autoCaptureSaveFailByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const int AutoCapViewSafeMarginPx = 80;
        private const int AutoCapClassMobDead = 0;
        private const int AutoCapClassMobActive = 1;
        private static readonly Color[] AutoCapHitEffectTintPaletteBasic = new[]
        {
            new Color(255, 255, 255),
            new Color(255, 210, 120),
            new Color(140, 235, 255),
            new Color(255, 170, 245),
            new Color(170, 255, 170),
            new Color(255, 150, 150)
        };
        private static readonly Color[] AutoCapHitEffectTintPaletteExtended = new[]
        {
            new Color(255, 255, 255),
            new Color(255, 220, 120),
            new Color(255, 170, 110),
            new Color(255, 120, 170),
            new Color(245, 165, 255),
            new Color(180, 145, 255),
            new Color(140, 220, 255),
            new Color(110, 255, 240),
            new Color(135, 255, 170),
            new Color(220, 255, 145)
        };

        private enum AutoCapDamageTemplate
        {
            Single,
            DoubleTap,
            RapidCombo,
            StaggerCombo,
            Finisher
        }

        private enum AutoCaptureCameraPhase
        {
            Init,
            MoveToPoint,
            Settling,
            Sampling,
            Complete
        }

        private sealed class AutoCaptureBucketRuntimeTuning
        {
            public AutoCaptureProfile Profile { get; set; } = AutoCaptureProfile.NormalMove;
            public bool DisableDamageNumbers { get; set; }
            public bool DisableHitEffects { get; set; }
            public bool SuppressMobLabels { get; set; }
            public double DamageLagProbOverride { get; set; } = -1d;
            public double HitDamageMinProbOverride { get; set; } = -1d;
            public int HitExtraLayerMaxClamp { get; set; } = -1;
        }

        private bool IsAutoCaptureEnabled => _autoCaptureOptions != null;
        private bool IsAutoCaptureAudioMuted => IsAutoCaptureEnabled && (_autoCaptureOptions?.MuteAudio ?? true);

        private void InitializeAutoCaptureIfNeeded()
        {
            try
            {
                InitializeAutoCaptureIfNeededInternal();
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap][FATAL] Initialization failed: {ex.GetType().Name}: {ex.Message}");
                System.Console.WriteLine(ex.StackTrace);
                throw;
            }
        }

        private void InitializeAutoCaptureIfNeededInternal()
        {
            if (!IsAutoCaptureEnabled || _autoCaptureStarted)
                return;

            if (!string.IsNullOrWhiteSpace(_autoCaptureOptions.OutputDir))
            {
                _datasetGenerator.ConfigureOutputDirectory(_autoCaptureOptions.OutputDir);
            }
            _datasetGenerator.ConfigureWriter(
                Math.Max(1, _autoCaptureOptions.WriterThreads),
                Math.Max(16, _autoCaptureOptions.WriterQueueCapacity));
            _gameState.HideUIMode = true;
            _gameState.PlayerControlEnabled = false;
            _gameState.MobMovementEnabled = true;
            _gameState.UseSmoothCamera = false;

            _autoCaptureProfileMix = _autoCaptureOptions.GetNormalizedProfileMix();
            _autoCaptureBucketMix = _autoCaptureOptions.GetNormalizedBucketMix();
            _autoCaptureBucketPolicy = _autoCaptureOptions.GetNormalizedBucketPolicy();
            _autoCaptureDamageNumberControl = _autoCaptureOptions.GetNormalizedDamageNumberControl();
            _autoCaptureHitEffectControl = _autoCaptureOptions.GetNormalizedHitEffectControl();
            _autoCaptureRealSkillEffectControl = _autoCaptureOptions.GetNormalizedRealSkillEffectControl();
            _autoCaptureSkillCatalog = _autoCaptureOptions.GetNormalizedSkillCatalog();
            _autoCaptureCameraPlan = _autoCaptureOptions.GetNormalizedCameraPlan();
            _autoCaptureWarmupFramesRemaining = _autoCaptureCameraPlan.StartupWarmupFrames;
            _autoCaptureSettleFramesRemaining = 0;
            _autoCaptureSampleFramesPerPoint = _autoCaptureCameraPlan.SampleFramesPerPoint;
            _autoCaptureSampledFramesAtPoint = 0;

            _datasetGenerator.ConfigureRecoverBackoff(new[] { 100, 300, 500 });
            _datasetGenerator.StartGeneration();
            int runtimeSeed = _autoCaptureOptions.Seed ^ _autoCaptureOptions.MapId ^ (_autoCaptureOptions.ResolutionName?.GetHashCode() ?? 0);
            _autoCaptureRandom = new Random(runtimeSeed);
            _autoCaptureCurrentBucket = SelectBucketByGlobalDeficit();
            _autoCaptureCurrentProfile = SelectProfileForBucket(_autoCaptureCurrentBucket);
            _autoCaptureProfileSwitchTick = Environment.TickCount;
            _autoCaptureDmgLastTickByMob.Clear();
            _autoCapturePointSkillPool.Clear();
            _autoCaptureBucketAttempted.Clear();
            _autoCaptureBucketSaved.Clear();
            _autoCaptureBucketAttemptedSnapshot.Clear();
            _autoCaptureBucketSavedSnapshot.Clear();
            _autoCaptureLastFrameHasForcedHitState = false;
            _autoCaptureLastFrameDamageEventTriggered = false;
            _autoCaptureDmgLastGlobalTick = int.MinValue / 2;
            _autoCaptureDmgFrameMarker = -1;
            _autoCaptureDmgEventsUsedOnCaptureFrame = 0;
            _autoCaptureDmgAttempted = 0;
            _autoCaptureDmgFired = 0;
            _autoCaptureDmgSkippedCooldown = 0;
            _autoCaptureDmgSegmentsEmitted = 0;
            _autoCaptureDmgMobsHit = 0;
            _autoCaptureDmgMobsHitCurrentFrame = 0;
            _autoCaptureDmgMobsHitPeakSinceLastLog = 0;
            _autoCaptureDmgAttemptedSnapshot = 0;
            _autoCaptureDmgFiredSnapshot = 0;
            _autoCaptureDmgSkippedCooldownSnapshot = 0;
            _autoCaptureDmgSegmentsEmittedSnapshot = 0;
            _autoCaptureCaptureAttempted = 0;
            _autoCaptureCaptureSaved = 0;
            _autoCaptureCaptureSkippedEmpty = 0;
            _autoCaptureCaptureAttemptedSnapshot = 0;
            _autoCaptureCaptureSavedSnapshot = 0;
            _autoCaptureCaptureSkippedEmptySnapshot = 0;
            _autoCaptureBoundsRawCount = 0;
            _autoCaptureBoundsUsableCount = 0;
            _autoCaptureBoundsRawSnapshot = 0;
            _autoCaptureBoundsUsableSnapshot = 0;
            _autoCaptureSaveFailCount = 0;
            _autoCaptureSaveFailCountSnapshot = 0;
            _autoCaptureSaveFailByReason.Clear();
            _autoCaptureCurrentPointIndex = -1;
            _autoCaptureTotalPointCount = 0;
            _autoCaptureExpectedFrameCount = 0;
            _autoCaptureCameraPhase = AutoCaptureCameraPhase.Init;
            _autoCaptureBucketManifestPath = null;
            if (!string.IsNullOrWhiteSpace(_autoCaptureOptions.OutputDir))
            {
                _autoCaptureBucketManifestPath = Path.Combine(_autoCaptureOptions.OutputDir, "bucket_manifest.csv");
                try
                {
                    File.WriteAllText(
                        _autoCaptureBucketManifestPath,
                        "frame,bucket,profile,saved,raw,usable,forced_hit,damage_event" + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch
                {
                    _autoCaptureBucketManifestPath = null;
                }
            }
            BuildAutoCaptureNativeDamageSkillPool();
            BuildAutoCaptureScanPath();
            _autoCaptureTotalPointCount = _autoCaptureScanPath?.Count ?? 0;
            _autoCaptureExpectedFrameCount = checked(_autoCaptureTotalPointCount * Math.Max(1, _autoCaptureSampleFramesPerPoint));
            if (_autoCaptureTotalPointCount <= 0)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PATH_INVALID: scan path is empty.");
            }
            _autoCaptureStarted = true;

            System.Console.WriteLine(
                $"[AutoCap] map={_autoCaptureOptions.MapId:D9} res={_autoCaptureOptions.ResolutionName} total_points={_autoCaptureTotalPointCount} total_frames={_autoCaptureExpectedFrameCount} seed={runtimeSeed}");
            System.Console.WriteLine(
                $"[AutoCap] camera_plan mode={_autoCaptureCameraPlan.Mode} step_mode={_autoCaptureCameraPlan.Traversal} warmup_frames={_autoCaptureCameraPlan.StartupWarmupFrames} settle_frames={_autoCaptureCameraPlan.SettleFrames} sample_frames_per_point={_autoCaptureCameraPlan.SampleFramesPerPoint}");
            System.Console.WriteLine(
                $"[AutoCap] dmg_num_ctrl global_cd={_autoCaptureDamageNumberControl.GlobalCooldownMs}ms per_mob_cd={_autoCaptureDamageNumberControl.PerMobCooldownMs}ms per_capture_frame={_autoCaptureDamageNumberControl.MaxEventsPerCaptureFrame} max_active_numbers={_autoCaptureDamageNumberControl.MaxActiveNumbers}");
            System.Console.WriteLine(
                $"[AutoCap] hit_effect_ctrl enabled={_autoCaptureHitEffectControl.Enabled} palette={_autoCaptureHitEffectControl.PaletteMode} alpha={_autoCaptureHitEffectControl.AlphaMin:0.##}-{_autoCaptureHitEffectControl.AlphaMax:0.##} scale={_autoCaptureHitEffectControl.ScaleMin:0.##}-{_autoCaptureHitEffectControl.ScaleMax:0.##} lifetime={_autoCaptureHitEffectControl.LifetimeMsMin}-{_autoCaptureHitEffectControl.LifetimeMsMax}ms layers={_autoCaptureHitEffectControl.ExtraLayersMin}-{_autoCaptureHitEffectControl.ExtraLayersMax} jitter={_autoCaptureHitEffectControl.JitterPxX}x{_autoCaptureHitEffectControl.JitterPxY} variations=[{string.Join(",", _autoCaptureHitEffectControl.VariationPool)}]");
            System.Console.WriteLine("[AutoCap] labels class0=mob_dead class1=mob_active");
            System.Console.WriteLine(
                $"[AutoCap] writer_config requested={_autoCaptureOptions.WriterThreads}/{_autoCaptureOptions.WriterQueueCapacity} effective={_datasetGenerator.WriterThreadsEffective}/{_datasetGenerator.WriterQueueCapacityEffective}");
        }
    }
}
