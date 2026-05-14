using HaCreator.MapSimulator.Automation;
using HaCreator.MapSimulator.Character.Skills;
using HaSharedLibrary;
using HaSharedLibrary.Render.DX;
using HaSharedLibrary.Util;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
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
            {
                return;
            }

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
            _autoCaptureSkillRejectRecords.Clear();
            _autoCaptureSkillDuplicateRecords.Clear();
            _autoCaptureSkillScannedCount = 0;
            _autoCaptureSkillParseErrorCount = 0;
            _autoCaptureSkillUniqueNodeCount = 0;
            _autoCaptureSkillDuplicateNodeCount = 0;
            _autoCaptureSkillBuiltCount = 0;
            _autoCaptureSkillWithEffectCount = 0;
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
            _autoCaptureLoadedRealSkillEffectCount = 0;
            _autoCaptureRealSkillEffectTriggerCount = 0;
            _autoCaptureLastCompleteLogFrame = -1;
            _autoCaptureCompletionHandled = false;

            BuildAutoCaptureNativeDamageSkillPool();
            _autoCaptureLoadedRealSkillEffectCount = LoadAutoCaptureRealSkillEffects();
            BuildAutoCaptureScanPath();
            _autoCaptureTotalPointCount = _autoCaptureScanPath?.Count ?? 0;
            _autoCaptureExpectedFrameCount = checked(_autoCaptureTotalPointCount * Math.Max(1, _autoCaptureSampleFramesPerPoint));
            if (_autoCaptureTotalPointCount <= 0)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PATH_INVALID: scan path is empty.");
            }

            _autoCaptureStarted = true;

            System.Console.WriteLine($"[AutoCap] map={_autoCaptureOptions.MapId:D9} res={_autoCaptureOptions.ResolutionName} total_points={_autoCaptureTotalPointCount} total_frames={_autoCaptureExpectedFrameCount} seed={runtimeSeed}");
            System.Console.WriteLine($"[AutoCap] camera_plan mode={_autoCaptureCameraPlan.Mode} step_mode={_autoCaptureCameraPlan.Traversal} warmup_frames={_autoCaptureCameraPlan.StartupWarmupFrames} settle_frames={_autoCaptureCameraPlan.SettleFrames} sample_frames_per_point={_autoCaptureCameraPlan.SampleFramesPerPoint}");
            System.Console.WriteLine($"[AutoCap] dmg_num_ctrl global_cd={_autoCaptureDamageNumberControl.GlobalCooldownMs}ms per_mob_cd={_autoCaptureDamageNumberControl.PerMobCooldownMs}ms per_capture_frame={_autoCaptureDamageNumberControl.MaxEventsPerCaptureFrame} max_active_numbers={_autoCaptureDamageNumberControl.MaxActiveNumbers} enable_miss={_autoCaptureDamageNumberControl.EnableMiss} damage_range={_autoCaptureDamageNumberControl.MinDamage}-{_autoCaptureDamageNumberControl.MaxDamage} distribution={_autoCaptureDamageNumberControl.DamageDistributionMode}");
            System.Console.WriteLine($"[AutoCap] real_skill_fx enabled={_autoCaptureRealSkillEffectControl.Enabled} source={_autoCaptureRealSkillEffectControl.Source} kind={_autoCaptureRealSkillEffectControl.Kind} loaded_framesets={_autoCaptureLoadedRealSkillEffectCount}");
            System.Console.WriteLine("[AutoCap] labels class0=mob_dead class1=mob_active");
            System.Console.WriteLine($"[AutoCap] writer_config requested={_autoCaptureOptions.WriterThreads}/{_autoCaptureOptions.WriterQueueCapacity} effective={_datasetGenerator.WriterThreadsEffective}/{_datasetGenerator.WriterQueueCapacityEffective}");
        }

        private void BuildAutoCaptureNativeDamageSkillPool()
        {
            System.Console.WriteLine("[AutoCap] Building native damage skill pool...");
            _autoCaptureNativeDamageSkillPool.Clear();

            if (!IsAutoCaptureEnabled)
            {
                return;
            }

            var allSkills = LoadAutoCapSkillsForSampling(
                out string skillSource,
                out int scannedSkillNodes,
                out int parseErrors);
            _autoCaptureSkillScannedCount = scannedSkillNodes;
            _autoCaptureSkillParseErrorCount = parseErrors;

            if (allSkills.Count == 0)
            {
                throw new InvalidOperationException($"[AutoCap][native_dmg_pool] skills_unavailable source={skillSource} scanned={scannedSkillNodes} parse_errors={parseErrors}. Aborting per configuration.");
            }

            int rejectedAttack = 0;
            int rejectedNoLevels = 0;
            int rejectedAttackCount = 0;
            int rejectedDamage = 0;
            int rejectedTimings = 0;

            foreach (var skill in allSkills)
            {
                if (!TryBuildAutoCapNativeDamageSkill(skill, false, out AutoCapNativeDamageSkillEntry entry, out string reason))
                {
                    AppendAutoCaptureSkillReject(skill, reason);
                    switch (reason)
                    {
                        case "not_attack":
                            rejectedAttack++;
                            break;
                        case "no_levels":
                            rejectedNoLevels++;
                            break;
                        case "attack_count":
                            rejectedAttackCount++;
                            break;
                        case "damage":
                            rejectedDamage++;
                            break;
                        case "timings":
                            rejectedTimings++;
                            break;
                    }
                    continue;
                }

                entry.CachedHitEffect = skill.HitEffect;
                _autoCaptureNativeDamageSkillPool.Add(entry);
            }

            int builtCount = _autoCaptureNativeDamageSkillPool.Count;
            int withEffectCount = _autoCaptureNativeDamageSkillPool.Count(s => s.CachedHitEffect != null);
            _autoCaptureSkillBuiltCount = builtCount;
            _autoCaptureSkillWithEffectCount = withEffectCount;
            string catalogPath = ResolveAutoCaptureSkillCatalogPath();
            ExportAutoCaptureSkillManifest();
            ExportAutoCaptureSkillRejectSummary();
            ExportAutoCaptureAcceptedSkillList();
            ExportAutoCaptureRejectedSkillList();
            ExportAutoCaptureRejectedSkillMarkdown();
            ExportOrUpdateAutoCaptureSkillCatalog(catalogPath);
            System.Console.WriteLine($"[AutoCap][native_dmg_pool] source={skillSource} scanned={scannedSkillNodes} built={builtCount} with_hit_effect={withEffectCount} total_skills={allSkills.Count} timing_source=flexible catalog_path={catalogPath} reject_not_attack={rejectedAttack} reject_no_levels={rejectedNoLevels} reject_attack_count={rejectedAttackCount} reject_damage={rejectedDamage} reject_timings={rejectedTimings}");

            if (builtCount <= 0)
            {
                throw new InvalidOperationException("[AutoCap][native_dmg_pool] built=0 after skill catalog filtering. Aborting per configuration.");
            }
        }

        private bool TryBuildAutoCapNativeDamageSkill(
            SkillData skill,
            bool unused_param,
            out AutoCapNativeDamageSkillEntry entry,
            out string reason)
        {
            entry = null;
            reason = null;

            if (skill == null || !skill.IsAttack)
            {
                reason = "not_attack";
                return false;
            }

            if (skill.Levels == null || skill.Levels.Count == 0)
            {
                reason = "no_levels";
                return false;
            }

            SkillLevelData levelData = skill.Levels.Values
                .OrderByDescending(l => l?.Level ?? 0)
                .FirstOrDefault(l => l != null);
            if (levelData == null)
            {
                reason = "no_levels";
                return false;
            }

            if (levelData.AttackCount <= 0)
            {
                reason = "attack_count";
                return false;
            }

            if (levelData.Damage <= 0)
            {
                reason = "damage";
                return false;
            }

            int[] timings = TryResolveNativeSkillSegmentOffsets(skill, levelData.AttackCount);
            if (timings == null || timings.Length != levelData.AttackCount)
            {
                reason = "timings";
                return false;
            }

            entry = new AutoCapNativeDamageSkillEntry
            {
                SkillId = skill.SkillId,
                Name = skill.Name,
                Job = skill.Job,
                AttackCount = levelData.AttackCount,
                DamagePercent = levelData.Damage,
                CriticalRatePercent = Math.Max(0, levelData.CriticalRate),
                VisualFamily = InferAutoCaptureSkillVisualFamily(skill, levelData),
                OcclusionLevel = InferAutoCaptureSkillOcclusionLevel(skill, levelData),
                SegmentOffsetsMs = timings
            };
            return true;
        }

        private string ResolveAutoCaptureSkillCatalogPath()
        {
            string relativePath = _autoCaptureSkillCatalog?.Path;
            string baseDir = !string.IsNullOrWhiteSpace(_autoCaptureOptions.JobDir)
                ? _autoCaptureOptions.JobDir
                : _autoCaptureOptions.OutputDir;

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Environment.CurrentDirectory;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = "AutoCapSkillCatalog.json";
            }

            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            return Path.Combine(baseDir, relativePath);
        }

        private void ExportAutoCaptureSkillManifest()
        {
            try
            {
                string manifestPath = Path.Combine(GetAutoCaptureSummaryRootDir(), "AutoCapSkillManifest.md");
                if (File.Exists(manifestPath))
                {
                    return;
                }

                int parsedSkillCount = _autoCaptureSkillUniqueNodeCount - _autoCaptureSkillParseErrorCount;
                int rejectedNotAttack = _autoCaptureSkillRejectRecords.Count(r => string.Equals(r.ReasonCode, "not_attack", StringComparison.Ordinal));
                int parsedAttackCount = parsedSkillCount - rejectedNotAttack;
                int acceptedWithoutHit = Math.Max(0, _autoCaptureSkillBuiltCount - _autoCaptureSkillWithEffectCount);

                var lines = new List<string>
                {
                    "## \u6280\u80fd\u6c60\u603b\u89c8",
                    "",
                    $"- \u626b\u63cf\u6280\u80fd\u8282\u70b9\uff1a{_autoCaptureSkillScannedCount}",
                    $"- \u53bb\u91cd\u540e\u552f\u4e00 skillId\uff1a{_autoCaptureSkillUniqueNodeCount}",
                    $"- \u91cd\u590d\u8282\u70b9\u6570\uff1a{_autoCaptureSkillDuplicateNodeCount}",
                    $"- \u89e3\u6790\u6210\u529f\u6280\u80fd\uff1a{parsedSkillCount}",
                    $"- \u89e3\u6790\u5f02\u5e38\u6570\uff1a{_autoCaptureSkillParseErrorCount}",
                    $"- \u975e\u653b\u51fb\u6280\u80fd\u6392\u9664\uff1a{rejectedNotAttack}",
                    $"- \u653b\u51fb\u6280\u80fd\u5019\u9009\uff1a{parsedAttackCount}",
                    $"- \u6700\u7ec8\u5165\u6c60\u6280\u80fd\u6570\uff1a{_autoCaptureSkillBuiltCount}",
                    $"- \u5176\u4e2d\u5e26 hit \u7279\u6548\u6280\u80fd\u6570\uff1a{_autoCaptureSkillWithEffectCount}",
                    $"- \u65e0 hit \u7279\u6548\u4f46\u4ecd\u53ef\u7528\u6280\u80fd\u6570\uff1a{acceptedWithoutHit}",
                    "",
                    "## \u5165\u6c60\u6280\u80fd\u6e05\u5355",
                    "",
                    "| Skill ID | \u6280\u80fd\u540d | Job | \u6bb5\u6570 | \u4f24\u5bb3 | \u66b4\u51fb\u7387 | \u547d\u4e2d\u7279\u6548 | \u89c6\u89c9\u65cf | \u906e\u6321\u7ea7\u522b |",
                    "| ---: | :--- | ---: | ---: | ---: | ---: | :---: | :--- | :--- |"
                };

                foreach (var entry in _autoCaptureNativeDamageSkillPool.OrderBy(s => s.SkillId))
                {
                    string skillName = GetAutoCaptureSkillDisplayName(entry.SkillId, entry.Name);
                    lines.Add($"| {entry.SkillId} | {skillName} | {entry.Job} | {entry.AttackCount} | {entry.DamagePercent} | {entry.CriticalRatePercent} | {(entry.CachedHitEffect != null ? "\u6709" : "\u65e0")} | {entry.VisualFamily} | {entry.OcclusionLevel} |");
                }

                var noHitSkills = _autoCaptureNativeDamageSkillPool.Where(s => s.CachedHitEffect == null).OrderBy(s => s.SkillId).ToList();
                if (noHitSkills.Count > 0)
                {
                    lines.Add("");
                    lines.Add("## \u65e0 hit \u7279\u6548\u4f46\u4ecd\u4fdd\u7559");
                    lines.Add("");
                    foreach (var entry in noHitSkills)
                    {
                        string skillName = GetAutoCaptureSkillDisplayName(entry.SkillId, entry.Name);
                        lines.Add($"- {entry.SkillId} {skillName} | job={entry.Job} | \u4ecd\u4fdd\u7559\u539f\u56e0\uff1a\u653b\u51fb\u6bb5\u6570\u3001\u4f24\u5bb3\u503c\u3001\u6bb5\u65f6\u5e8f\u5747\u6709\u6548\uff0c\u4ec5\u7f3a\u5c11 hit \u7279\u6548\u8d44\u6e90\u3002");
                    }
                }

                var duplicateGroups = _autoCaptureSkillDuplicateRecords.GroupBy(r => r.SkillId).OrderBy(g => g.Key).ToList();
                if (duplicateGroups.Count > 0)
                {
                    lines.Add("");
                    lines.Add("## \u91cd\u590d skill \u8282\u70b9");
                    lines.Add("");
                    lines.Add("| Skill ID | \u6280\u80fd\u540d | \u9996\u6b21\u6765\u6e90 Job | \u91cd\u590d\u6765\u6e90 Job |");
                    lines.Add("| ---: | :--- | ---: | :--- |");
                    foreach (var group in duplicateGroups)
                    {
                        AutoCaptureSkillDuplicateRecord first = group.First();
                        string skillName = GetAutoCaptureSkillDisplayName(first.SkillId, first.Name);
                        string duplicateJobs = string.Join(" / ", group.Select(r => r.DuplicateJob).Distinct().OrderBy(v => v));
                        lines.Add($"| {first.SkillId} | {skillName} | {first.FirstJob} | {duplicateJobs} |");
                    }
                }

                File.WriteAllLines(manifestPath, BuildMarkdownDocument("AutoCap \u6280\u80fd\u6c60\u603b\u8868", lines), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to export skill manifest: {ex.Message}");
            }
        }



        private void ExportAutoCaptureSkillRejects()
        {
            try
            {
                AppendAutoCaptureCsvSummary(
                    "AutoCapSkillRejects.csv",
                    "map_id,resolution,skill_id,name,job,is_attack,level_count,attack_count,damage,reject_reason",
                    _autoCaptureSkillRejectRows);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to export skill rejects: {ex.Message}");
            }
        }

        private void AppendAutoCaptureSkillReject(SkillData skill, string reason)
        {
            if (skill == null)
            {
                return;
            }

            int levelCount = skill.Levels?.Count ?? 0;
            SkillLevelData levelData = skill.Levels?.Values?
                .Where(l => l != null)
                .OrderByDescending(l => l.Level)
                .FirstOrDefault();
            _autoCaptureSkillRejectRecords.Add(new AutoCaptureSkillRejectRecord
            {
                SkillId = skill.SkillId,
                Name = skill.Name,
                Job = skill.Job,
                IsAttack = skill.IsAttack,
                LevelCount = levelCount,
                AttackCount = levelData?.AttackCount ?? 0,
                Damage = levelData?.Damage ?? 0,
                ReasonCode = reason,
                ReasonDetail = BuildAutoCaptureSkillRejectRecordDetail(skill, reason, levelData),
                HasHitEffect = skill.HitEffect != null,
                HasActionNode = skill.AutoCapHasActionNode,
                HasBallNode = skill.AutoCapHasBallNode
            });
        }

        private void ExportOrUpdateAutoCaptureSkillCatalog(string catalogPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(catalogPath) ?? Environment.CurrentDirectory);

                var catalog = LoadAutoCaptureSkillCatalog(catalogPath) ?? new AutoCaptureSkillCatalogDocument();
                var bySkillId = catalog.Skills?.ToDictionary(item => item.SkillId) ?? new Dictionary<int, AutoCaptureSkillCatalogEntry>();

                foreach (var skill in _autoCaptureNativeDamageSkillPool.OrderBy(s => s.SkillId))
                {
                    if (bySkillId.TryGetValue(skill.SkillId, out AutoCaptureSkillCatalogEntry existing))
                    {
                        existing.Name = skill.Name;
                        existing.Job = skill.Job;
                        existing.Enabled = true;
                    }
                    else
                    {
                        bySkillId[skill.SkillId] = new AutoCaptureSkillCatalogEntry
                        {
                            SkillId = skill.SkillId,
                            Name = skill.Name,
                            Enabled = true,
                            Job = skill.Job
                        };
                    }
                }

                catalog.Version = 1;
                catalog.Skills = bySkillId.Values.OrderBy(item => item.SkillId).ToList();

                string json = JsonSerializer.Serialize(catalog, AutoCaptureSkillCatalogJsonOptions);
                File.WriteAllText(catalogPath, json, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"E_AUTOCAP_CAMERA_PLAN_INVALID: failed to export skill catalog ({catalogPath}): {ex.Message}");
            }
        }

        private AutoCaptureSkillCatalogDocument LoadAutoCaptureSkillCatalog(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(catalogPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<AutoCaptureSkillCatalogDocument>(json, AutoCaptureSkillCatalogJsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"E_AUTOCAP_CAMERA_PLAN_INVALID: failed to load skill catalog ({catalogPath}): {ex.Message}");
            }
        }

        private string GetAutoCaptureSummaryRootDir()
        {
            if (!string.IsNullOrWhiteSpace(_autoCaptureOptions?.JobDir))
            {
                return _autoCaptureOptions.JobDir;
            }

            if (!string.IsNullOrWhiteSpace(_autoCaptureOptions?.OutputRootDir))
            {
                return _autoCaptureOptions.OutputRootDir;
            }

            if (!string.IsNullOrWhiteSpace(_autoCaptureOptions?.OutputDir))
            {
                return _autoCaptureOptions.OutputDir;
            }

            return Environment.CurrentDirectory;
        }

        private void ExportAutoCaptureCaptureSummary()
        {
            try
            {
                string mapId = _autoCaptureOptions?.MapId.ToString("D9") ?? "unknown";
                string resolutionName = _autoCaptureOptions?.ResolutionName ?? "unknown";
                int attemptedA = GetBucketCount(_autoCaptureBucketAttempted, AutoCaptureDataBucket.CleanBaseline);
                int attemptedB = GetBucketCount(_autoCaptureBucketAttempted, AutoCaptureDataBucket.AnchorDecoupling);
                int attemptedC = GetBucketCount(_autoCaptureBucketAttempted, AutoCaptureDataBucket.ChaosOcclusion);
                int savedA = GetBucketCount(_autoCaptureBucketSaved, AutoCaptureDataBucket.CleanBaseline);
                int savedB = GetBucketCount(_autoCaptureBucketSaved, AutoCaptureDataBucket.AnchorDecoupling);
                int savedC = GetBucketCount(_autoCaptureBucketSaved, AutoCaptureDataBucket.ChaosOcclusion);
                double saveRate = _autoCaptureCaptureAttempted > 0
                    ? (double)_autoCaptureCaptureSaved / _autoCaptureCaptureAttempted
                    : 0d;
                double avgRaw = _autoCaptureCaptureAttempted > 0
                    ? (double)_autoCaptureBoundsRawCount / _autoCaptureCaptureAttempted
                    : 0d;
                double avgUsable = _autoCaptureCaptureAttempted > 0
                    ? (double)_autoCaptureBoundsUsableCount / _autoCaptureCaptureAttempted
                    : 0d;

                var lines = new List<string>
                {
                    $"## 地图 {mapId} / 分辨率 {resolutionName}",
                    "",
                    $"- 期望帧数：{_autoCaptureExpectedFrameCount}",
                    $"- 实际采样帧数：{_datasetGenerator?.CapturedFrameCount ?? 0}",
                    $"- 尝试保存帧数：{_autoCaptureCaptureAttempted}",
                    $"- 成功保存帧数：{_autoCaptureCaptureSaved}",
                    $"- 保存失败次数：{_autoCaptureSaveFailCount}",
                    $"- 保存成功率：{saveRate:0.000}",
                    $"- 平均原始框数：{avgRaw:0.00}",
                    $"- 平均可用框数：{avgUsable:0.00}",
                    $"- 伤害事件触发次数：{_autoCaptureDmgFired}",
                    $"- 伤害段发射数：{_autoCaptureDmgSegmentsEmitted}",
                    $"- real skill hit 特效触发次数：{_autoCaptureRealSkillEffectTriggerCount}",
                    ""
                };

                lines.Add("### Bucket 分布");
                lines.Add("");
                lines.Add("| Bucket | 尝试帧数 | 保存帧数 |");
                lines.Add("| :--- | ---: | ---: |");
                lines.Add($"| A / CleanBaseline | {attemptedA} | {savedA} |");
                lines.Add($"| B / AnchorDecoupling | {attemptedB} | {savedB} |");
                lines.Add($"| C / ChaosOcclusion | {attemptedC} | {savedC} |");

                string saveFailByReason = FormatSaveFailReasonStats();
                if (!string.Equals(saveFailByReason, "none", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add("");
                    lines.Add($"### 保存失败原因");
                    lines.Add("");
                    lines.Add($"- {saveFailByReason}");
                }

                AppendAutoCaptureMarkdownSummary(
                    "AutoCapCaptureSummary.md",
                    "# AutoCap Capture Summary",
                    string.Join(Environment.NewLine, lines));
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to export capture summary: {ex.Message}");
            }
        }

        private void AppendAutoCaptureMarkdownSummary(string fileName, string title, string body)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            try
            {
                string rootDir = GetAutoCaptureSummaryRootDir();
                Directory.CreateDirectory(rootDir);
                string path = Path.Combine(rootDir, fileName);
                bool fileExists = File.Exists(path);
                using (var writer = new StreamWriter(path, true, new UTF8Encoding(true)))
                {
                    if (!fileExists)
                    {
                        writer.WriteLine(title);
                        writer.WriteLine();
                    }
                    else if (new FileInfo(path).Length > 0)
                    {
                        writer.WriteLine();
                    }

                    writer.WriteLine(body);
                    writer.WriteLine();
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to append summary {fileName}: {ex.Message}");
            }
        }

        private void AppendAutoCaptureCsvSummary(string fileName, string header, IEnumerable<string> rows)
        {
            if (string.IsNullOrWhiteSpace(fileName) || rows == null)
            {
                return;
            }

            try
            {
                string rootDir = GetAutoCaptureSummaryRootDir();
                Directory.CreateDirectory(rootDir);
                string path = Path.Combine(rootDir, fileName);
                bool fileExists = File.Exists(path);
                using (var writer = new StreamWriter(path, true, new UTF8Encoding(true)))
                {
                    if (!fileExists)
                    {
                        writer.WriteLine(header);
                    }

                    foreach (string row in rows)
                    {
                        if (!string.IsNullOrWhiteSpace(row))
                        {
                            writer.WriteLine(row);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to append summary {fileName}: {ex.Message}");
            }
        }

        private static string GetAutoCaptureSkillRejectReasonText(string reason)
        {
            switch (reason)
            {
                case "not_attack":
                    return "非攻击技能";
                case "no_levels":
                    return "缺少等级数据";
                case "attack_count":
                    return "攻击段数无效";
                case "damage":
                    return "伤害值无效";
                case "timings":
                    return "段时序缺失";
                default:
                    return string.IsNullOrWhiteSpace(reason) ? "未说明" : reason;
            }
        }

        private static string InferAutoCaptureSkillVisualFamily(SkillData skill, SkillLevelData levelData)
        {
            string name = $"{skill?.Name} {skill?.SkillId}".ToLowerInvariant();
            int attackCount = Math.Max(1, levelData?.AttackCount ?? 1);
            if (name.Contains("laser") || name.Contains("beam"))
            {
                return "beam";
            }
            if (name.Contains("arrow") || name.Contains("bullet") || name.Contains("shot"))
            {
                return "projectile";
            }
            if (name.Contains("slash") || name.Contains("swing") || name.Contains("blade"))
            {
                return "slash";
            }
            if (name.Contains("bomb") || name.Contains("blast") || name.Contains("explosion"))
            {
                return "burst";
            }
            if (attackCount >= 5)
            {
                return "combo";
            }
            return "impact";
        }

        private void ExportAutoCaptureSkillRejectSummary()
        {
            try
            {
                string summaryPath = Path.Combine(GetAutoCaptureSummaryRootDir(), "AutoCapSkillRejectSummary.md");
                if (File.Exists(summaryPath))
                {
                    return;
                }

                int parsedSkillCount = _autoCaptureSkillUniqueNodeCount - _autoCaptureSkillParseErrorCount;
                int rejectedNotAttack = _autoCaptureSkillRejectRecords.Count(r => string.Equals(r.ReasonCode, "not_attack", StringComparison.Ordinal));
                int rejectedNoLevels = _autoCaptureSkillRejectRecords.Count(r => string.Equals(r.ReasonCode, "no_levels", StringComparison.Ordinal));
                int rejectedAttackCount = _autoCaptureSkillRejectRecords.Count(r => string.Equals(r.ReasonCode, "attack_count", StringComparison.Ordinal));
                int rejectedDamage = _autoCaptureSkillRejectRecords.Count(r => string.Equals(r.ReasonCode, "damage", StringComparison.Ordinal));
                int rejectedTimings = _autoCaptureSkillRejectRecords.Count(r => string.Equals(r.ReasonCode, "timings", StringComparison.Ordinal));
                int parsedAttackCount = parsedSkillCount - rejectedNotAttack;

                var lines = new List<string>
                {
                    "## \u6f0f\u6597\u7edf\u8ba1",
                    "",
                    $"- \u626b\u63cf\u6280\u80fd\u8282\u70b9\uff1a{_autoCaptureSkillScannedCount}",
                    $"- \u53bb\u91cd\u540e\u552f\u4e00 skillId\uff1a{_autoCaptureSkillUniqueNodeCount}",
                    $"- \u91cd\u590d\u8282\u70b9\u6570\uff1a{_autoCaptureSkillDuplicateNodeCount}",
                    $"- \u89e3\u6790\u6210\u529f\u6280\u80fd\uff1a{parsedSkillCount}",
                    $"- \u89e3\u6790\u5f02\u5e38\u6570\uff1a{_autoCaptureSkillParseErrorCount}",
                    $"- \u975e\u653b\u51fb\u6280\u80fd\u6392\u9664\uff1a{rejectedNotAttack}",
                    $"- \u653b\u51fb\u6280\u80fd\u5019\u9009\uff1a{parsedAttackCount}",
                    $"- \u56e0\u7f3a\u5c11\u7b49\u7ea7\u6570\u636e\u6392\u9664\uff1a{rejectedNoLevels}",
                    $"- \u56e0\u653b\u51fb\u6bb5\u6570\u65e0\u6548\u6392\u9664\uff1a{rejectedAttackCount}",
                    $"- \u56e0\u4f24\u5bb3\u503c\u65e0\u6548\u6392\u9664\uff1a{rejectedDamage}",
                    $"- \u56e0\u6bb5\u65f6\u5e8f\u7f3a\u5931\u6392\u9664\uff1a{rejectedTimings}",
                    $"- \u6700\u7ec8\u5165\u6c60\u6280\u80fd\u6570\uff1a{_autoCaptureSkillBuiltCount}",
                    $"- \u5176\u4e2d\u5e26 hit \u7279\u6548\u6280\u80fd\u6570\uff1a{_autoCaptureSkillWithEffectCount}",
                    $"- \u65e0 hit \u7279\u6548\u4f46\u4ecd\u53ef\u7528\u6280\u80fd\u6570\uff1a{Math.Max(0, _autoCaptureSkillBuiltCount - _autoCaptureSkillWithEffectCount)}",
                    ""
                };

                if (_autoCaptureSkillDuplicateRecords.Count > 0)
                {
                    lines.Add("## 607 \u5230 590\uff1a\u91cd\u590d skill \u8282\u70b9");
                    lines.Add("");
                    lines.Add("| Skill ID | \u6280\u80fd\u540d | \u9996\u6b21\u6765\u6e90 Job | \u91cd\u590d\u6765\u6e90 Job |");
                    lines.Add("| ---: | :--- | ---: | :--- |");
                    foreach (var group in _autoCaptureSkillDuplicateRecords.GroupBy(r => r.SkillId).OrderBy(g => g.Key))
                    {
                        AutoCaptureSkillDuplicateRecord first = group.First();
                        string skillName = GetAutoCaptureSkillDisplayName(first.SkillId, first.Name);
                        string duplicateJobs = string.Join(" / ", group.Select(r => r.DuplicateJob).Distinct().OrderBy(v => v));
                        lines.Add($"| {first.SkillId} | {skillName} | {first.FirstJob} | {duplicateJobs} |");
                    }
                    lines.Add("");
                }

                if (_autoCaptureSkillRejectRecords.Count == 0)
                {
                    lines.Add("## 590 \u5230 52\uff1a\u65e0\u7b5b\u9664\u9879");
                    lines.Add("");
                    lines.Add("- \u672c\u6b21\u6ca1\u6709\u6280\u80fd\u88ab\u7b5b\u9664\u3002");
                }
                else
                {
                    lines.Add("## 590 \u5230 52\uff1a\u7b5b\u9664\u539f\u56e0\u5206\u7ec4");
                    lines.Add("");
                    lines.Add("| \u539f\u56e0 | \u6570\u91cf |");
                    lines.Add("| :--- | ---: |");
                    foreach (var group in _autoCaptureSkillRejectRecords
                        .GroupBy(r => GetAutoCaptureSkillRejectReasonTextSafe(r.ReasonCode))
                        .OrderByDescending(g => g.Count())
                        .ThenBy(g => g.Key, StringComparer.Ordinal))
                    {
                        lines.Add($"| {group.Key} | {group.Count()} |");
                    }
                }

                File.WriteAllLines(summaryPath, BuildMarkdownDocument("AutoCap \u6280\u80fd\u7b5b\u9009\u6458\u8981", lines), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to export skill reject summary: {ex.Message}");
            }
        }



        private static string GetAutoCaptureSkillRejectReasonTextSafe(string reason)
        {
            if (string.Equals(reason, "not_attack", StringComparison.Ordinal))
            {
                return "\u4e0d\u662f\u653b\u51fb\u6280\u80fd";
            }
            if (string.Equals(reason, "no_levels", StringComparison.Ordinal))
            {
                return "\u7f3a\u5c11\u7b49\u7ea7\u6570\u636e";
            }
            if (string.Equals(reason, "attack_count", StringComparison.Ordinal))
            {
                return "\u653b\u51fb\u6bb5\u6570\u65e0\u6548";
            }
            if (string.Equals(reason, "damage", StringComparison.Ordinal))
            {
                return "\u4f24\u5bb3\u503c\u65e0\u6548";
            }
            if (string.Equals(reason, "timings", StringComparison.Ordinal))
            {
                return "\u6bb5\u65f6\u5e8f\u7f3a\u5931";
            }

            return string.IsNullOrWhiteSpace(reason) ? "\u672a\u8bf4\u660e\u539f\u56e0" : reason;
        }



        private static string BuildAutoCaptureSkillRejectDetail(AutoCaptureSkillRejectRecord record)
        {
            if (record == null)
            {
                return "\u65e0";
            }

            if (!string.IsNullOrWhiteSpace(record.ReasonDetail))
            {
                return record.ReasonDetail;
            }

            return "\u65e0";
        }



        private void ExportAutoCaptureAcceptedSkillList()
        {
            try
            {
                string path = Path.Combine(GetAutoCaptureSummaryRootDir(), "AutoCapAcceptedSkills.csv");
                if (File.Exists(path))
                {
                    return;
                }

                var lines = new List<string>
                {
                    "\u6280\u80fdID,\u6280\u80fd\u540d,Job,\u653b\u51fb\u6bb5\u6570,\u4f24\u5bb3\u503c,\u66b4\u51fb\u7387,\u547d\u4e2d\u7279\u6548,\u89c6\u89c9\u65cf,\u906e\u6321\u7ea7\u522b"
                };

                foreach (var entry in _autoCaptureNativeDamageSkillPool.OrderBy(s => s.SkillId))
                {
                    string skillName = GetAutoCaptureSkillDisplayName(entry.SkillId, entry.Name);
                    lines.Add(string.Join(",",
                        entry.SkillId,
                        EscapeCsvField(skillName),
                        entry.Job,
                        entry.AttackCount,
                        entry.DamagePercent,
                        entry.CriticalRatePercent,
                        EscapeCsvField(entry.CachedHitEffect != null ? "\u6709" : "\u65e0"),
                        EscapeCsvField(entry.VisualFamily),
                        EscapeCsvField(entry.OcclusionLevel)));
                }

                File.WriteAllLines(path, lines, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to export accepted skill list: {ex.Message}");
            }
        }



        private void ExportAutoCaptureRejectedSkillList()
        {
            try
            {
                string path = Path.Combine(GetAutoCaptureSummaryRootDir(), "AutoCapRejectedSkills.csv");
                if (File.Exists(path))
                {
                    return;
                }

                var lines = new List<string>
                {
                    "\u6280\u80fdID,\u6280\u80fd\u540d,Job,\u662f\u5426\u653b\u51fb\u6280\u80fd,\u7b49\u7ea7\u6570,\u653b\u51fb\u6bb5\u6570,\u4f24\u5bb3\u503c,\u547d\u4e2d\u7279\u6548,\u7b5b\u9664\u539f\u56e0,\u539f\u56e0\u8bf4\u660e"
                };

                foreach (var record in _autoCaptureSkillRejectRecords.OrderBy(r => r.SkillId))
                {
                    string skillName = string.IsNullOrWhiteSpace(record.Name)
                        ? GetAutoCaptureSkillDisplayName(record.SkillId, null)
                        : record.Name;
                    lines.Add(string.Join(",",
                        record.SkillId,
                        EscapeCsvField(skillName),
                        record.Job,
                        EscapeCsvField(record.IsAttack ? "\u662f" : "\u5426"),
                        record.LevelCount,
                        record.AttackCount,
                        record.Damage,
                        EscapeCsvField(record.HasHitEffect ? "\u6709" : "\u65e0"),
                        EscapeCsvField(GetAutoCaptureSkillRejectReasonTextSafe(record.ReasonCode)),
                        EscapeCsvField(BuildAutoCaptureSkillRejectDetail(record))));
                }

                File.WriteAllLines(path, lines, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to export rejected skill list: {ex.Message}");
            }
        }



        private void ExportAutoCaptureRejectedSkillMarkdown()
        {
            try
            {
                string path = Path.Combine(GetAutoCaptureSummaryRootDir(), "AutoCapRejectedSkillsByReason.md");
                if (File.Exists(path))
                {
                    return;
                }

                var lines = new List<string>();
                if (_autoCaptureSkillRejectRecords.Count == 0)
                {
                    lines.Add("## \u672c\u6b21\u6ca1\u6709\u88ab\u7b5b\u9664\u7684\u6280\u80fd");
                    lines.Add("");
                    lines.Add("- \u6240\u6709\u8fdb\u5165\u653b\u51fb\u5019\u9009\u7684\u6280\u80fd\u90fd\u901a\u8fc7\u4e86\u540e\u7eed\u6821\u9a8c\u3002");
                }
                else
                {
                    foreach (var group in _autoCaptureSkillRejectRecords
                        .GroupBy(r => GetAutoCaptureSkillRejectReasonTextSafe(r.ReasonCode))
                        .OrderByDescending(g => g.Count())
                        .ThenBy(g => g.Key, StringComparer.Ordinal))
                    {
                        lines.Add($"## {group.Key}\uff08{group.Count()}\uff09");
                        lines.Add("");
                        lines.Add("| Skill ID | \u6280\u80fd\u540d | Job | \u547d\u4e2d\u7279\u6548 | \u539f\u56e0\u8bf4\u660e |");
                        lines.Add("| ---: | :--- | ---: | :---: | :--- |");
                        foreach (var record in group.OrderBy(r => r.SkillId))
                        {
                            string skillName = string.IsNullOrWhiteSpace(record.Name)
                                ? GetAutoCaptureSkillDisplayName(record.SkillId, null)
                                : record.Name;
                            lines.Add($"| {record.SkillId} | {skillName} | {record.Job} | {(record.HasHitEffect ? "\u6709" : "\u65e0")} | {BuildAutoCaptureSkillRejectDetail(record)} |");
                        }
                        lines.Add("");
                    }
                }

                File.WriteAllLines(path, BuildMarkdownDocument("AutoCap \u6280\u80fd\u7b5b\u9664\u660e\u7ec6", lines), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap] Failed to export rejected skill markdown: {ex.Message}");
            }
        }



        private string GetAutoCaptureSkillDisplayName(int skillId, string fallbackName)
        {
            if (!string.IsNullOrWhiteSpace(fallbackName))
            {
                return fallbackName;
            }

            if (Program.InfoManager.SkillNameCache.TryGetValue(skillId.ToString(), out var nameTuple) &&
                !string.IsNullOrWhiteSpace(nameTuple.Item1))
            {
                return nameTuple.Item1;
            }

            return "Unknown Name";
        }

        private static string BuildAutoCaptureSkillRejectRecordDetail(SkillData skill, string reason, SkillLevelData levelData)
        {
            if (skill == null)
            {
                return "无";
            }

            if (string.Equals(reason, "not_attack", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(skill.AutoCapRejectHintDetail))
                {
                    return skill.AutoCapRejectHintDetail;
                }

                if (!string.IsNullOrWhiteSpace(skill.AutoCapRejectHintCode))
                {
                    return skill.AutoCapRejectHintCode;
                }
            }

            switch (reason)
            {
                case "no_levels":
                    return $"等级数据为空，level_count={skill.Levels?.Count ?? 0}";
                case "attack_count":
                    return $"攻击段数无效，attack_count={levelData?.AttackCount ?? 0}";
                case "damage":
                    return $"伤害值无效，damage={levelData?.Damage ?? 0}";
                case "timings":
                    return $"攻击段数={levelData?.AttackCount ?? 0}，但未解析到完整段时序";
                default:
                    return "无";
            }
        }

        private static List<string> BuildMarkdownDocument(string title, IEnumerable<string> bodyLines)
        {
            var lines = new List<string> { $"# {title}", "" };
            if (bodyLines != null)
            {
                lines.AddRange(bodyLines);
            }
            return lines;
        }

        private static string EscapeCsvField(string value)
        {
            string text = value ?? string.Empty;
            if (text.Contains(",") || text.Contains("\"") || text.Contains("\r") || text.Contains("\n"))
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            return text;
        }

        private static string InferAutoCaptureSkillOcclusionLevel(SkillData skill, SkillLevelData levelData)
        {
            int attackCount = Math.Max(1, levelData?.AttackCount ?? 1);
            int damage = Math.Max(0, levelData?.Damage ?? 0);
            bool hasHitEffect = skill?.HitEffect != null && skill.HitEffect.Frames != null && skill.HitEffect.Frames.Count > 0;

            if (attackCount >= 5 || damage >= 260 || (hasHitEffect && attackCount >= 4))
            {
                return "high";
            }
            if (attackCount >= 3 || damage >= 160)
            {
                return "medium";
            }
            return "low";
        }

        private static int[] TryResolveNativeSkillSegmentOffsets(SkillData skill, int attackCount)
        {
            if (skill == null || attackCount <= 0)
            {
                return null;
            }

            int[] offsets = TryExtractOffsetsFromAnimation(skill.Effect, attackCount);
            if (offsets == null)
            {
                offsets = TryExtractOffsetsFromAnimation(skill.HitEffect, attackCount);
            }

            if (offsets == null)
            {
                offsets = new int[attackCount];
                for (int i = 0; i < attackCount; i++)
                {
                    offsets[i] = i * 120;
                }
            }

            return offsets;
        }

        private List<SkillData> LoadAutoCapSkillsForSampling(
            out string source,
            out int scannedSkillNodes,
            out int parseErrors)
        {
            source = "none";
            scannedSkillNodes = 0;
            parseErrors = 0;

            if (_playerManager?.SkillLoader != null)
            {
                try
                {
                    var loaded = _playerManager.SkillLoader.LoadAllSkills();
                    if (loaded != null && loaded.Count > 0)
                    {
                        source = "skill_loader";
                        return loaded;
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[AutoCap] SkillLoader.LoadAllSkills failed: {ex.Message}");
                }
            }

            source = "manual_wz_scan";
            var result = new List<SkillData>();
            var skillObj = Program.FindWzObject("Skill", string.Empty);
            WzDirectory skillDir = null;
            if (skillObj is WzFile f) skillDir = f.WzDirectory;
            else if (skillObj is WzDirectory d) skillDir = d;

            if (skillDir == null)
            {
                return result;
            }

            var seenSkillIds = new HashSet<int>();
            var firstSeenJobBySkillId = new Dictionary<int, int>();
            foreach (var jobImg in skillDir.WzImages)
            {
                if (jobImg == null || !jobImg.Name.EndsWith(".img"))
                {
                    continue;
                }
                if (!jobImg.Parsed)
                {
                    jobImg.ParseImage();
                }
                string jobName = jobImg.Name.Replace(".img", "");
                var skillRoot = jobImg["skill"];
                var propertiesToScan = skillRoot != null ? skillRoot.WzProperties : jobImg.WzProperties;

                foreach (var skillNode in propertiesToScan)
                {
                    if (skillNode == null || !int.TryParse(skillNode.Name, out int skillId))
                    {
                        continue;
                    }
                    scannedSkillNodes++;
                    if (seenSkillIds.Contains(skillId))
                    {
                        _autoCaptureSkillDuplicateNodeCount++;
                        _autoCaptureSkillDuplicateRecords.Add(new AutoCaptureSkillDuplicateRecord
                        {
                            SkillId = skillId,
                            Name = GetAutoCaptureSkillDisplayName(skillId, null),
                            FirstJob = firstSeenJobBySkillId.TryGetValue(skillId, out int firstJob) ? firstJob : 0,
                            DuplicateJob = int.TryParse(jobName, out int duplicateJob) ? duplicateJob : 0
                        });
                        continue;
                    }

                    var skill = BuildAutoCapSkillDataFromNode(skillId, skillNode as WzImageProperty, jobName);
                    if (skill == null)
                    {
                        parseErrors++;
                        continue;
                    }
                    result.Add(skill);
                    seenSkillIds.Add(skillId);
                    firstSeenJobBySkillId[skillId] = skill.Job;
                    _autoCaptureSkillUniqueNodeCount++;
                }
            }

            return result;
        }

        private SkillData BuildAutoCapSkillDataFromNode(int skillId, WzImageProperty skillNode, string jobImgName)
        {
            if (skillNode == null) return null;
            int jobId = 0;
            int.TryParse(jobImgName, out jobId);

            var skill = new SkillData { SkillId = skillId, Job = jobId };
            var effectNode = skillNode["effect"];
            if (effectNode != null) skill.Effect = BuildAutoCapSkillAnimationFromNode(effectNode, "effect");
            var hitNode = skillNode["hit"];
            if (hitNode != null)
            {
                skill.HitEffect = BuildAutoCapSkillAnimationFromNode(hitNode, "hit");
            }
            var levelNode = skillNode["level"];
            if (levelNode != null && levelNode.WzProperties != null)
            {
                foreach (var child in levelNode.WzProperties)
                {
                    if (child == null || !int.TryParse(child.Name, out int level)) continue;
                    skill.Levels[level] = new SkillLevelData
                    {
                        Level = level,
                        Damage = Math.Max(0, TryReadWzInt(child, "damage", TryReadWzInt(child, "dam", TryReadWzInt(child, "p1", TryReadWzInt(child, "p2", 0))))),
                        AttackCount = Math.Max(0, TryReadWzInt(child, "attackCount", 1)),
                        MobCount = Math.Max(0, TryReadWzInt(child, "mobCount", 1)),
                        CriticalRate = Math.Max(0, TryReadWzInt(child, "cr", 0))
                    };
                }
            }
            var commonNode = skillNode["common"];
            if (skill.Levels.Count == 0 && commonNode != null)
            {
                skill.Levels[1] = new SkillLevelData
                {
                    Level = 1,
                    Damage = Math.Max(0, TryReadWzInt(commonNode, "damage", TryReadWzInt(commonNode, "dam", TryReadWzInt(commonNode, "p1", TryReadWzInt(commonNode, "p2", 0))))),
                    AttackCount = Math.Max(0, TryReadWzInt(commonNode, "attackCount", 1)),
                    MobCount = Math.Max(0, TryReadWzInt(commonNode, "mobCount", 1)),
                    CriticalRate = Math.Max(0, TryReadWzInt(commonNode, "cr", 0))
                };
            }

            bool hasHit = skillNode["hit"] != null;
            bool hasBall = skillNode["ball"] != null;
            bool hasAction = skillNode["action"] != null;
            bool isInvisible = TryReadWzInt(skillNode, "invisible", 0) > 0;
            string skillName = "";
            if (Program.InfoManager.SkillNameCache.TryGetValue(skillId.ToString(), out var nameTuple))
            {
                skillName = nameTuple.Item1;
            }

            string[] blacklist = { "被动", "强化", "恢复", "祝福", "护盾", "治疗", "隐身", "复活" };
            bool isBlacklisted = blacklist.Any(word => skillName.Contains(word));
            skill.Name = skillName;
            skill.AutoCapHasActionNode = hasAction;
            skill.AutoCapHasBallNode = hasBall;
            skill.IsAttack = hasAction && (hasHit || hasBall) && !isInvisible && (jobId >= 100 && jobId < 8000) && !isBlacklisted;
            skill.AutoCapRejectHintCode = null;
            skill.AutoCapRejectHintDetail = null;

            if (!skill.IsAttack)
            {
                if (!hasAction)
                {
                    skill.AutoCapRejectHintCode = "missing_action";
                    skill.AutoCapRejectHintDetail = "缺少 action 节点，无法作为攻击动作播放。";
                }
                else if (!hasHit && !hasBall)
                {
                    skill.AutoCapRejectHintCode = "missing_hit_or_ball";
                    skill.AutoCapRejectHintDetail = "同时缺少 hit 和 ball 节点，没有可用攻击表现。";
                }
                else if (isInvisible)
                {
                    skill.AutoCapRejectHintCode = "invisible";
                    skill.AutoCapRejectHintDetail = "技能被标记为 invisible，采集时不作为可见攻击技能。";
                }
                else if (jobId < 100 || jobId >= 8000)
                {
                    skill.AutoCapRejectHintCode = "job_out_of_range";
                    skill.AutoCapRejectHintDetail = $"Job={jobId} 不在角色主动攻击技能扫描范围内。";
                }
                else if (isBlacklisted)
                {
                    skill.AutoCapRejectHintCode = "name_blacklist";
                    skill.AutoCapRejectHintDetail = "技能名命中被动/强化/恢复/祝福/护盾/治疗/隐身/复活黑名单。";
                }
            }

            if (skill.IsAttack)
            {
                bool actuallyHasDamage = skill.Levels.Values.Any(v => v != null && v.Damage > 0);
                if (!actuallyHasDamage)
                {
                    skill.IsAttack = false;
                    skill.AutoCapRejectHintCode = "no_positive_damage";
                    skill.AutoCapRejectHintDetail = "虽然有攻击表现，但所有等级伤害值都小于等于 0。";
                }
            }

            if (skill.IsAttack)
            {
                foreach (var lvl in skill.Levels.Values)
                {
                    if (lvl != null && lvl.Damage <= 0) lvl.Damage = 100;
                }
            }

            return skill;
        }

        private SkillAnimation BuildAutoCapSkillAnimationFromNode(WzImageProperty node, string name)
        {
            if (node == null) return null;
            if (GraphicsDevice == null) return null;

            WzImageProperty frameContainer = node;
            if (node.WzProperties != null && node.WzProperties.Count > 0)
            {
                var firstChild = node.WzProperties[0];
                if (firstChild != null && int.TryParse(firstChild.Name, out _) && firstChild is not WzCanvasProperty)
                {
                    frameContainer = firstChild as WzImageProperty;
                }
            }

            if (frameContainer == null || frameContainer.WzProperties == null) return null;

            var anim = new SkillAnimation { Name = name };
            var properties = frameContainer.WzProperties;
            int count = properties.Count;

            for (int i = 0; i < count; i++)
            {
                var frameNode = properties[i];
                if (frameNode == null || !int.TryParse(frameNode.Name, out _)) continue;

                var actualNode = frameNode.GetLinkedWzImageProperty();
                if (actualNode is not WzCanvasProperty canvas) continue;

                try
                {
                    var bitmap = canvas.GetLinkedWzCanvasBitmap();
                    if (bitmap == null) continue;
                    var texture = bitmap.ToTexture2D(GraphicsDevice);
                    if (texture == null) continue;

                    var origin = canvas["origin"] as WzVectorProperty;
                    int ox = origin?.X?.Value ?? texture.Width / 2;
                    int oy = origin?.Y?.Value ?? texture.Height;
                    int delay = Math.Max(10, TryReadWzInt(frameNode, "delay", 100));

                    anim.Frames.Add(new SkillFrame
                    {
                        Texture = new DXObject(-ox, -oy, texture, delay),
                        Delay = delay,
                        Origin = new Point(ox, oy)
                    });
                }
                catch
                {
                }
            }

            return anim.Frames.Count > 0 ? anim : null;
        }

        private static int TryReadWzInt(WzImageProperty node, string name, int defaultValue)
        {
            if (node == null) return defaultValue;
            var p = node[name];
            if (p == null) return defaultValue;
            if (p is WzIntProperty i) return i.Value;
            if (p is WzDoubleProperty d) return (int)d.Value;
            if (p is WzFloatProperty f) return (int)f.Value;
            if (p is WzLongProperty l) return (int)l.Value;
            if (p is WzShortProperty s) return s.Value;
            if (p is WzStringProperty str && int.TryParse(str.Value, out int res)) return res;
            return defaultValue;
        }

        private static int[] TryExtractOffsetsFromAnimation(SkillAnimation source, int attackCount)
        {
            if (source == null || source.Frames == null || source.Frames.Count == 0)
            {
                return null;
            }

            var frameDelays = new List<int>(source.Frames.Count);
            foreach (var frame in source.Frames)
            {
                if (frame?.Texture == null)
                {
                    continue;
                }
                int delay = frame.Delay;
                if (delay <= 0)
                {
                    return null;
                }
                frameDelays.Add(delay);
            }

            if (frameDelays.Count == 0)
            {
                return null;
            }

            var offsets = new int[attackCount];
            int elapsed = 0;
            for (int i = 0; i < attackCount; i++)
            {
                offsets[i] = elapsed;
                elapsed += frameDelays[i % frameDelays.Count];
            }

            return offsets;
        }
    }
}
