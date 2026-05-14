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
        private readonly DatasetGenerator _datasetGenerator = new DatasetGenerator();
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
        private AutoCaptureRealSkillEffectControl _autoCaptureRealSkillEffectControl = AutoCaptureRealSkillEffectControl.CreateDefault();
        private AutoCaptureSkillCatalogControl _autoCaptureSkillCatalog = AutoCaptureSkillCatalogControl.CreateDefault();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketAttempted = new Dictionary<AutoCaptureDataBucket, int>();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketSaved = new Dictionary<AutoCaptureDataBucket, int>();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketAttemptedSnapshot = new Dictionary<AutoCaptureDataBucket, int>();
        private readonly Dictionary<AutoCaptureDataBucket, int> _autoCaptureBucketSavedSnapshot = new Dictionary<AutoCaptureDataBucket, int>();
        private bool _autoCaptureLastFrameHasForcedHitState = false;
        private bool _autoCaptureLastFrameDamageEventTriggered = false;
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
        private readonly List<string> _autoCaptureSkillRejectRows = new List<string>();
        private readonly List<AutoCaptureSkillRejectRecord> _autoCaptureSkillRejectRecords = new List<AutoCaptureSkillRejectRecord>();
        private readonly List<AutoCaptureSkillDuplicateRecord> _autoCaptureSkillDuplicateRecords = new List<AutoCaptureSkillDuplicateRecord>();
        private int _autoCaptureSkillScannedCount = 0;
        private int _autoCaptureSkillParseErrorCount = 0;
        private int _autoCaptureSkillUniqueNodeCount = 0;
        private int _autoCaptureSkillDuplicateNodeCount = 0;
        private int _autoCaptureSkillBuiltCount = 0;
        private int _autoCaptureSkillWithEffectCount = 0;
        private int _autoCapturePointRecipeSeed = 0;
        private AutoCapDamageTemplate _autoCapturePointDamageTemplate = AutoCapDamageTemplate.Single;
        private int _autoCaptureLoadedRealSkillEffectCount = 0;
        private int _autoCaptureRealSkillEffectTriggerCount = 0;
        private int _autoCaptureLastCompleteLogFrame = -1;
        private bool _autoCaptureCompletionHandled = false;
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
            public bool HasHitNode { get; set; }
            public bool HasBallNode { get; set; }
            public bool HasActionNode { get; set; }
            public bool IsInvisible { get; set; }
            public string PoolGroup { get; set; }
            public string PoolReason { get; set; }
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

        private sealed class AutoCaptureSkillRejectRecord
        {
            public int SkillId { get; set; }
            public string Name { get; set; }
            public int Job { get; set; }
            public bool IsAttack { get; set; }
            public int LevelCount { get; set; }
            public int AttackCount { get; set; }
            public int Damage { get; set; }
            public string ReasonCode { get; set; }
            public string ReasonDetail { get; set; }
            public bool HasHitEffect { get; set; }
            public bool HasHitNode { get; set; }
            public bool HasActionNode { get; set; }
            public bool HasBallNode { get; set; }
            public bool IsInvisible { get; set; }
            public string PoolGroup { get; set; }
        }

        private sealed class AutoCaptureSkillDuplicateRecord
        {
            public int SkillId { get; set; }
            public string Name { get; set; }
            public int FirstJob { get; set; }
            public int DuplicateJob { get; set; }
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
            public bool SuppressMobLabels { get; set; }
            public double DamageLagProbOverride { get; set; } = -1d;
            public double HitDamageMinProbOverride { get; set; } = -1d;
        }

    }
}
