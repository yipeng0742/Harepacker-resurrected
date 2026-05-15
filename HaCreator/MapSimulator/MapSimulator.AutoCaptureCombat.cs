using HaCreator.MapSimulator.Automation;
using HaCreator.MapSimulator.Entities;
using HaCreator.MapSimulator.Effects;
using HaCreator.MapSimulator.Loaders;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private static bool IsDeathLikeAction(string action)
        {
            return action == "die1" || action == "die2" || action == "die";
        }

        private bool IsDeadMutualExclusionEnabled()
        {
            return _autoCaptureBucketPolicy?.EnforceDeadMutualExclusion ?? true;
        }

        private bool IsDeadLikeMob(MobItem mob)
        {
            if (mob == null)
            {
                return true;
            }
            if (IsDeadMutualExclusionEnabled())
            {
                if ((mob.AI?.IsDead ?? false) || mob.IsDeathAnimationComplete || IsDeathLikeAction(mob.CurrentAction))
                {
                    return true;
                }
            }
            return false;
        }

        private void IncrementBucketCount(Dictionary<AutoCaptureDataBucket, int> target, AutoCaptureDataBucket bucket)
        {
            if (target == null)
            {
                return;
            }
            if (target.TryGetValue(bucket, out int value))
            {
                target[bucket] = value + 1;
            }
            else
            {
                target[bucket] = 1;
            }
        }

        private int GetBucketCount(Dictionary<AutoCaptureDataBucket, int> source, AutoCaptureDataBucket bucket)
        {
            if (source == null)
            {
                return 0;
            }
            return source.TryGetValue(bucket, out int value) ? Math.Max(0, value) : 0;
        }

        private void AppendBucketManifest(
            int frameNo,
            AutoCaptureDataBucket bucket,
            AutoCaptureProfile profile,
            bool saved,
            int rawBoxes,
            int usableBoxes,
            int deadBoxes,
            int activeBoxes,
            bool emptyLabel,
            int passIndex,
            int sampleIndex)
        {
            try
            {
                string outputDir = _autoCaptureOptions?.OutputDir;
                if (string.IsNullOrWhiteSpace(outputDir))
                {
                    return;
                }

                Directory.CreateDirectory(outputDir);
                string path = Path.Combine(outputDir, "bucket_manifest.csv");
                bool needsHeader = !File.Exists(path);
                using (var writer = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    if (needsHeader)
                    {
                        writer.WriteLine("frame,bucket,profile,saved,raw_boxes,usable_boxes,dead_boxes,active_boxes,empty_label,pass_index,sample_index");
                    }

                    writer.WriteLine(
                        string.Join(",",
                            Math.Max(0, frameNo),
                            GetBucketCode(bucket),
                            profile,
                            saved ? "1" : "0",
                            Math.Max(0, rawBoxes),
                            Math.Max(0, usableBoxes),
                            Math.Max(0, deadBoxes),
                            Math.Max(0, activeBoxes),
                            emptyLabel ? "1" : "0",
                            Math.Max(0, passIndex),
                            Math.Max(0, sampleIndex)));
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap][manifest] write_failed: {ex.Message}");
            }
        }

        private void ApplyAutoCaptureAugmentation(int tick)
        {
            if (!_datasetGenerator.IsGenerating || _mobPool?.ActiveMobs == null)
                return;

            var bucketTuning = BuildBucketTuning();
            int capturedFrames = _datasetGenerator.CapturedFrameCount;
            if (capturedFrames > 0 && capturedFrames % 30 == 0 && capturedFrames != _autoCaptureLastProfileLogFrame)
            {
                _autoCaptureLastProfileLogFrame = capturedFrames;
                int dmgAttemptedDelta = _autoCaptureDmgAttempted - _autoCaptureDmgAttemptedSnapshot;
                int dmgFiredDelta = _autoCaptureDmgFired - _autoCaptureDmgFiredSnapshot;
                int dmgSkippedDelta = _autoCaptureDmgSkippedCooldown - _autoCaptureDmgSkippedCooldownSnapshot;
                int dmgSegmentsDelta = _autoCaptureDmgSegmentsEmitted - _autoCaptureDmgSegmentsEmittedSnapshot;
                int dmgMobsHitPeak = _autoCaptureDmgMobsHitPeakSinceLastLog;
                int capAttemptedDelta = _autoCaptureCaptureAttempted - _autoCaptureCaptureAttemptedSnapshot;
                int capSavedDelta = _autoCaptureCaptureSaved - _autoCaptureCaptureSavedSnapshot;
                int capSkippedEmptyDelta = _autoCaptureCaptureSkippedEmpty - _autoCaptureCaptureSkippedEmptySnapshot;
                int boundsRawDelta = _autoCaptureBoundsRawCount - _autoCaptureBoundsRawSnapshot;
                int boundsUsableDelta = _autoCaptureBoundsUsableCount - _autoCaptureBoundsUsableSnapshot;
                int saveFailDelta = _autoCaptureSaveFailCount - _autoCaptureSaveFailCountSnapshot;
                int bucketAttemptA = GetBucketCount(_autoCaptureBucketAttempted, AutoCaptureDataBucket.CleanBaseline) - GetBucketCount(_autoCaptureBucketAttemptedSnapshot, AutoCaptureDataBucket.CleanBaseline);
                int bucketAttemptB = GetBucketCount(_autoCaptureBucketAttempted, AutoCaptureDataBucket.AnchorDecoupling) - GetBucketCount(_autoCaptureBucketAttemptedSnapshot, AutoCaptureDataBucket.AnchorDecoupling);
                int bucketAttemptC = GetBucketCount(_autoCaptureBucketAttempted, AutoCaptureDataBucket.ChaosOcclusion) - GetBucketCount(_autoCaptureBucketAttemptedSnapshot, AutoCaptureDataBucket.ChaosOcclusion);
                int bucketSavedA = GetBucketCount(_autoCaptureBucketSaved, AutoCaptureDataBucket.CleanBaseline) - GetBucketCount(_autoCaptureBucketSavedSnapshot, AutoCaptureDataBucket.CleanBaseline);
                int bucketSavedB = GetBucketCount(_autoCaptureBucketSaved, AutoCaptureDataBucket.AnchorDecoupling) - GetBucketCount(_autoCaptureBucketSavedSnapshot, AutoCaptureDataBucket.AnchorDecoupling);
                int bucketSavedC = GetBucketCount(_autoCaptureBucketSaved, AutoCaptureDataBucket.ChaosOcclusion) - GetBucketCount(_autoCaptureBucketSavedSnapshot, AutoCaptureDataBucket.ChaosOcclusion);
                string saveFailReason = _datasetGenerator?.LastSaveFailureReason ?? "none";
                string saveFailByReason = FormatSaveFailReasonStats();
                _autoCaptureDmgAttemptedSnapshot = _autoCaptureDmgAttempted;
                _autoCaptureDmgFiredSnapshot = _autoCaptureDmgFired;
                _autoCaptureDmgSkippedCooldownSnapshot = _autoCaptureDmgSkippedCooldown;
                _autoCaptureDmgSegmentsEmittedSnapshot = _autoCaptureDmgSegmentsEmitted;
                _autoCaptureCaptureAttemptedSnapshot = _autoCaptureCaptureAttempted;
                _autoCaptureCaptureSavedSnapshot = _autoCaptureCaptureSaved;
                _autoCaptureCaptureSkippedEmptySnapshot = _autoCaptureCaptureSkippedEmpty;
                _autoCaptureBoundsRawSnapshot = _autoCaptureBoundsRawCount;
                _autoCaptureBoundsUsableSnapshot = _autoCaptureBoundsUsableCount;
                _autoCaptureSaveFailCountSnapshot = _autoCaptureSaveFailCount;
                foreach (var bucket in EnumerateAllBuckets())
                {
                    _autoCaptureBucketAttemptedSnapshot[bucket] = GetBucketCount(_autoCaptureBucketAttempted, bucket);
                    _autoCaptureBucketSavedSnapshot[bucket] = GetBucketCount(_autoCaptureBucketSaved, bucket);
                }
                _autoCaptureDmgMobsHitPeakSinceLastLog = 0;
                double saveRate = capAttemptedDelta > 0 ? ((double)capSavedDelta / capAttemptedDelta) : 0d;
                int scanIdx = _autoCaptureCurrentPointIndex + 1;
                int scanTotal = _autoCaptureScanPath?.Count ?? 0;
                System.Console.WriteLine($"[AutoCap][采集摘要] frame={capturedFrames} point_idx={scanIdx} point_total={scanTotal} pass_idx={_autoCaptureCurrentPassIndex + 1}/{Math.Max(1, _autoCapturePassesPerPoint)} sample_idx={_autoCaptureCurrentSampleIndex + 1}/{Math.Max(1, _autoCaptureSampleFramesPerPoint)} sampled_frames_at_point={_autoCaptureSampledFramesAtPoint} phase={_autoCaptureCameraPhase} bucket={GetBucketCode(_autoCaptureCurrentBucket)} profile={_autoCaptureCurrentProfile} capture_attempted={capAttemptedDelta} saved={capSavedDelta} skipped_empty={capSkippedEmptyDelta} bucket_attempted=A:{bucketAttemptA},B:{bucketAttemptB},C:{bucketAttemptC} bucket_saved=A:{bucketSavedA},B:{bucketSavedB},C:{bucketSavedC} bounds_raw={boundsRawDelta} bounds_usable={boundsUsableDelta} save_fail={saveFailDelta} save_fail_reason={saveFailReason} save_fail_by_reason={saveFailByReason} save_rate={saveRate:0.000} dmg_attempted={dmgAttemptedDelta} dmg_fired={dmgFiredDelta} dmg_skipped_cooldown={dmgSkippedDelta} dmg_active={_effectManager?.Combat?.ActiveDamageNumbers ?? 0} mobs_hit_peak_per_frame={dmgMobsHitPeak} segments_emitted={dmgSegmentsDelta}");
            }

            var combat = _effectManager?.Combat;
            var forceStateMobs = new List<MobItem>();
            var fallbackMobs = new List<MobItem>();
            foreach (var mob in _mobPool.ActiveMobs)
            {
                mob.ForceStateForDataset(null);
            }

            foreach (var mob in _mobPool.ActiveMobs)
            {
                bool hasForcedState = false;
                Random pointRandom = new Random(_autoCapturePointRecipeSeed ^ mob.PoolId ^ (_autoCaptureCurrentPointIndex + 1));
                switch (_autoCaptureCurrentProfile)
                {
                    case AutoCaptureProfile.NormalMove:
                        if (pointRandom.NextDouble() < 0.35)
                        {
                            string moveAction = mob.PickRandomActionByPrefixes(pointRandom, "move", "walk", "fly", "jump", "stand");
                            if (!string.IsNullOrEmpty(moveAction))
                            {
                                mob.ForceStateForDataset(moveAction);
                                hasForcedState = true;
                            }
                        }
                        break;
                    case AutoCaptureProfile.AttackHeavy:
                        if (pointRandom.NextDouble() < 0.90)
                        {
                            string attackAction = mob.PickRandomActionByPrefixes(pointRandom, "attack", "skill", "magic", "cast");
                            if (!string.IsNullOrEmpty(attackAction))
                            {
                                mob.ForceStateForDataset(attackAction);
                                hasForcedState = true;
                            }
                        }
                        break;
                    case AutoCaptureProfile.HitOcclusionHeavy:
                        if (pointRandom.NextDouble() < 0.45)
                        {
                            string hitAction = mob.PickRandomActionByPrefixes(pointRandom, "hit", "damage", "dam");
                            if (!string.IsNullOrEmpty(hitAction))
                            {
                                mob.ForceStateForDataset(hitAction);
                                hasForcedState = true;
                            }
                        }
                        break;
                    case AutoCaptureProfile.DeathHeavy:
                        if (pointRandom.NextDouble() < 0.80)
                        {
                            string dieAction = mob.PickRandomActionByPrefixes(pointRandom, "die", "dead", "death");
                            if (!string.IsNullOrEmpty(dieAction))
                            {
                                mob.ForceStateForDataset(dieAction);
                                hasForcedState = true;
                            }
                        }
                        break;
                }

                if (hasForcedState)
                {
                    forceStateMobs.Add(mob);
                }
                else
                {
                    fallbackMobs.Add(mob);
                }
            }

            TryTriggerAutoCaptureDamageNumbers(combat, tick, forceStateMobs, fallbackMobs, bucketTuning);
        }

        private void TryTriggerAutoCaptureDamageNumbers(CombatEffects combat, int tick, List<MobItem> forceStateMobs, List<MobItem> fallbackMobs, AutoCaptureBucketRuntimeTuning tuning)
        {
            if (combat == null || _autoCaptureCurrentProfile == AutoCaptureProfile.DeathHeavy)
            {
                return;
            }

            if (_autoCaptureCameraPhase != AutoCaptureCameraPhase.Sampling)
            {
                return;
            }

            var candidates = BuildDamageEventCandidates(forceStateMobs, fallbackMobs);
            if (candidates.Count == 0)
            {
                return;
            }

            bool standMoveOnly = !candidates.Any(m => m != null && IsHitLikeAction(m.CurrentAction));
            int visibleMobs = candidates.Count;
            int maxEvents = ResolveDynamicBoundEventFrameLimit(visibleMobs);
            int eventBudget = Math.Max(1, Math.Min(maxEvents, _autoCaptureDamageNumberControl?.MaxEventsPerCaptureFrame ?? 1));

            int emitted = 0;
            foreach (var mob in candidates)
            {
                if (emitted >= eventBudget)
                {
                    break;
                }

                if (IsDeadLikeMob(mob) || !CanFireDamageEventForMob(mob, tick))
                {
                    continue;
                }

                AutoCapNativeDamageSkillEntry selectedSkill = PickAutoCapturePointSkill();
                int segmentCount = selectedSkill?.AttackCount > 0
                    ? selectedSkill.AttackCount
                    : ResolveSegmentCountByTemplate(_autoCapturePointDamageTemplate);
                if (segmentCount <= 0)
                {
                    segmentCount = 1;
                }

                int baseDamage = RollAutoCapBaseDamageByTemplate(_autoCapturePointDamageTemplate);
                int eventTick = tick + (_autoCaptureRandom?.Next(0, 20) ?? 0);
                int xOffset = _autoCaptureRandom?.Next(-6, 7) ?? 0;
                int yOffset = _autoCaptureRandom?.Next(-8, 9) ?? 0;
                int emittedForMob = 0;

                for (int burst = 0; burst < segmentCount; burst++)
                {
                    int segmentOffset = ResolveAutoCaptureSegmentTickOffset(selectedSkill, burst);
                    bool emitMiss = ShouldEmitAutoCaptureMiss();
                    if (combat == null) break;
                    if (!IsMobInCameraView(mob))
                    {
                        break;
                    }

                    if (!emitMiss && selectedSkill?.CachedHitEffect != null && _autoCaptureRealSkillEffectControl?.Enabled == true)
                    {
                        bool flip = _autoCaptureRandom?.NextDouble() > 0.5;
                        float scale = ResolveRealSkillEffectScale(_autoCaptureCurrentProfile);
                        combat.AddSkillHitEffect(
                            mob.CurrentX + xOffset,
                            mob.CurrentY - 24f + yOffset,
                            eventTick + segmentOffset,
                            selectedSkill.CachedHitEffect,
                            flip,
                            Color.White,
                            scale);
                        _autoCaptureRealSkillEffectTriggerCount++;
                    }

                    int comboIndex = burst % 6;
                    if (emitMiss)
                    {
                        combat.AddMiss(mob.CurrentX + xOffset, mob.CurrentY - 24f + yOffset, eventTick + segmentOffset, DamageColorType.Red);
                    }
                    else
                    {
                        int damage = RollAutoCapSegmentDamage(baseDamage, _autoCapturePointDamageTemplate, burst);
                        bool isCritical = RollAutoCapSegmentCritical(_autoCapturePointDamageTemplate, burst);
                        combat.AddPlayerDamage(damage, mob.CurrentX + xOffset, mob.CurrentY - 24f + yOffset, isCritical, eventTick + segmentOffset, comboIndex);
                    }

                    _autoCaptureDmgLastGlobalTick = eventTick;
                    _autoCaptureDmgLastTickByMob[mob.PoolId] = eventTick;
                    _autoCaptureDmgFired++;
                    _autoCaptureDmgSegmentsEmitted++;
                    emittedForMob++;
                }

                if (emittedForMob > 0)
                {
                    _autoCaptureDmgLastGlobalTick = tick;
                    _autoCaptureDmgEventsUsedOnCaptureFrame++;
                    _autoCaptureDmgMobsHit++;
                    _autoCaptureDmgMobsHitCurrentFrame++;
                    if (_autoCaptureDmgMobsHitCurrentFrame > _autoCaptureDmgMobsHitPeakSinceLastLog)
                    {
                        _autoCaptureDmgMobsHitPeakSinceLastLog = _autoCaptureDmgMobsHitCurrentFrame;
                    }
                    emitted++;
                }
            }
        }

        private AutoCapNativeDamageSkillEntry PickAutoCapturePointSkill()
        {
            if (_autoCapturePointSkillPool != null && _autoCapturePointSkillPool.Count > 0 && _autoCaptureRandom != null)
            {
                return _autoCapturePointSkillPool[_autoCaptureRandom.Next(_autoCapturePointSkillPool.Count)];
            }

            if (_autoCaptureNativeDamageSkillPool != null && _autoCaptureNativeDamageSkillPool.Count > 0 && _autoCaptureRandom != null)
            {
                return _autoCaptureNativeDamageSkillPool[_autoCaptureRandom.Next(_autoCaptureNativeDamageSkillPool.Count)];
            }

            return null;
        }

        private int ResolveAutoCaptureSegmentTickOffset(AutoCapNativeDamageSkillEntry skill, int segmentIndex)
        {
            if (skill?.SegmentOffsetsMs != null &&
                segmentIndex >= 0 &&
                segmentIndex < skill.SegmentOffsetsMs.Length)
            {
                return Math.Max(0, skill.SegmentOffsetsMs[segmentIndex]);
            }

            return ResolveSegmentTickOffsetMs(_autoCapturePointDamageTemplate, segmentIndex);
        }

        private int ResolveDynamicBoundEventFrameLimit(int visibleMobCount)
        {
            if (_autoCaptureDamageNumberControl == null)
            {
                return 1;
            }

            if (!_autoCaptureDamageNumberControl.UseMobRatioCap)
            {
                return Math.Max(1, _autoCaptureDamageNumberControl.MaxEventsPerCaptureFrame);
            }

            int raw = (int)Math.Round(Math.Max(0, visibleMobCount) * _autoCaptureDamageNumberControl.MobRatio, MidpointRounding.AwayFromZero);
            int byRatio = ClampInt(raw, _autoCaptureDamageNumberControl.MinEventsPerCaptureFrame, _autoCaptureDamageNumberControl.MaxEventsPerCaptureFrameCap);
            int hardCap = Math.Max(1, _autoCaptureDamageNumberControl.MaxEventsPerCaptureFrame);
            return ClampInt(byRatio, 1, hardCap);
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private float ResolveRealSkillEffectScale(AutoCaptureProfile profile)
        {
            if (_autoCaptureRandom == null)
            {
                return 1.0f;
            }

            double min = 0.85d;
            double max = 1.20d;
            switch (profile)
            {
                case AutoCaptureProfile.NormalMove:
                    min = 0.80d;
                    max = 1.00d;
                    break;
                case AutoCaptureProfile.AttackHeavy:
                    min = 0.95d;
                    max = 1.25d;
                    break;
                case AutoCaptureProfile.HitOcclusionHeavy:
                    min = 1.00d;
                    max = 1.30d;
                    break;
            }
            if (max < min)
            {
                (min, max) = (max, min);
            }
            return (float)(min + ((max - min) * _autoCaptureRandom.NextDouble()));
        }

        private AutoCapDamageTemplate PickAutoCapDamageTemplate(AutoCaptureProfile profile)
        {
            if (_autoCaptureRandom == null)
            {
                return AutoCapDamageTemplate.Single;
            }

            var configured = _autoCaptureDamageNumberControl?.TemplateWeights;
            if (configured != null && configured.Count > 0)
            {
                var map = new Dictionary<AutoCapDamageTemplate, int>
                {
                    [AutoCapDamageTemplate.Single] = configured.TryGetValue(AutoCaptureDamageTemplateKind.Single, out int wSingle) ? wSingle : 0,
                    [AutoCapDamageTemplate.DoubleTap] = configured.TryGetValue(AutoCaptureDamageTemplateKind.DoubleTap, out int wDouble) ? wDouble : 0,
                    [AutoCapDamageTemplate.RapidCombo] = configured.TryGetValue(AutoCaptureDamageTemplateKind.RapidCombo, out int wRapid) ? wRapid : 0,
                    [AutoCapDamageTemplate.StaggerCombo] = configured.TryGetValue(AutoCaptureDamageTemplateKind.StaggerCombo, out int wStagger) ? wStagger : 0,
                    [AutoCapDamageTemplate.Finisher] = configured.TryGetValue(AutoCaptureDamageTemplateKind.Finisher, out int wFinisher) ? wFinisher : 0
                };
                int total = map.Values.Where(v => v > 0).Sum();
                if (total > 0)
                {
                    int rollByWeight = _autoCaptureRandom.Next(total);
                    int accByWeight = 0;
                    foreach (var kv in map)
                    {
                        if (kv.Value <= 0)
                        {
                            continue;
                        }
                        accByWeight += kv.Value;
                        if (rollByWeight < accByWeight)
                        {
                            return kv.Key;
                        }
                    }
                }
            }

            int roll = _autoCaptureRandom.Next(100);
            return profile switch
            {
                AutoCaptureProfile.AttackHeavy => roll switch
                {
                    < 12 => AutoCapDamageTemplate.Single,
                    < 34 => AutoCapDamageTemplate.DoubleTap,
                    < 68 => AutoCapDamageTemplate.RapidCombo,
                    < 88 => AutoCapDamageTemplate.StaggerCombo,
                    _ => AutoCapDamageTemplate.Finisher
                },
                AutoCaptureProfile.HitOcclusionHeavy => roll switch
                {
                    < 18 => AutoCapDamageTemplate.Single,
                    < 40 => AutoCapDamageTemplate.DoubleTap,
                    < 72 => AutoCapDamageTemplate.RapidCombo,
                    < 90 => AutoCapDamageTemplate.StaggerCombo,
                    _ => AutoCapDamageTemplate.Finisher
                },
                AutoCaptureProfile.DeathHeavy => roll switch
                {
                    < 48 => AutoCapDamageTemplate.Single,
                    < 78 => AutoCapDamageTemplate.DoubleTap,
                    < 90 => AutoCapDamageTemplate.RapidCombo,
                    < 97 => AutoCapDamageTemplate.StaggerCombo,
                    _ => AutoCapDamageTemplate.Finisher
                },
                _ => roll switch
                {
                    < 36 => AutoCapDamageTemplate.Single,
                    < 66 => AutoCapDamageTemplate.DoubleTap,
                    < 86 => AutoCapDamageTemplate.RapidCombo,
                    < 96 => AutoCapDamageTemplate.StaggerCombo,
                    _ => AutoCapDamageTemplate.Finisher
                }
            };
        }

        private int ResolveSegmentCountByTemplate(AutoCapDamageTemplate template)
        {
            if (_autoCaptureRandom == null)
            {
                return 1;
            }

            return template switch
            {
                AutoCapDamageTemplate.Single => 1,
                AutoCapDamageTemplate.DoubleTap => _autoCaptureRandom.Next(2, 4),
                AutoCapDamageTemplate.RapidCombo => _autoCaptureRandom.Next(3, 7),
                AutoCapDamageTemplate.StaggerCombo => _autoCaptureRandom.Next(3, 6),
                AutoCapDamageTemplate.Finisher => _autoCaptureRandom.Next(2, 5),
                _ => 1
            };
        }

        private static readonly int[] AutoCapRapidTickOffsets = { 0, 8, 16, 24, 34, 46 };
        private static readonly int[] AutoCapStaggerTickOffsets = { 0, 14, 30, 52, 80 };
        private static readonly int[] AutoCapFinisherTickOffsets = { 0, 18, 42, 84 };

        private int ResolveSegmentTickOffsetMs(AutoCapDamageTemplate template, int segmentIndex)
        {
            if (segmentIndex <= 0)
            {
                return 0;
            }

            return template switch
            {
                AutoCapDamageTemplate.Single => 0,
                AutoCapDamageTemplate.DoubleTap => 18 * segmentIndex,
                AutoCapDamageTemplate.RapidCombo => ResolveTickOffsetFromTable(AutoCapRapidTickOffsets, segmentIndex, 14),
                AutoCapDamageTemplate.StaggerCombo => ResolveTickOffsetFromTable(AutoCapStaggerTickOffsets, segmentIndex, 24),
                AutoCapDamageTemplate.Finisher => ResolveTickOffsetFromTable(AutoCapFinisherTickOffsets, segmentIndex, 30),
                _ => 14 * segmentIndex
            };
        }

        private static int ResolveTickOffsetFromTable(int[] table, int segmentIndex, int tailStep)
        {
            if (table == null || table.Length == 0)
            {
                return segmentIndex * Math.Max(1, tailStep);
            }

            if (segmentIndex < table.Length)
            {
                return table[segmentIndex];
            }

            int last = table[table.Length - 1];
            int extra = segmentIndex - (table.Length - 1);
            return last + (extra * Math.Max(1, tailStep));
        }

        private int RollAutoCapBaseDamageByTemplate(AutoCapDamageTemplate template)
        {
            return RollAutoCapBaseDamage();
        }

        private int RollAutoCapSegmentDamage(int baseDamage, AutoCapDamageTemplate template, int segmentIndex)
        {
            if (_autoCaptureRandom == null)
            {
                return Math.Max(1, baseDamage);
            }

            double jitter = 0.88d + (_autoCaptureRandom.NextDouble() * 0.28d);
            double factor = template switch
            {
                AutoCapDamageTemplate.Single => 1.00d,
                AutoCapDamageTemplate.DoubleTap => segmentIndex == 0 ? 1.00d : 0.80d,
                AutoCapDamageTemplate.RapidCombo => Math.Max(0.25d, 0.92d - (segmentIndex * 0.14d)),
                AutoCapDamageTemplate.StaggerCombo => Math.Min(1.20d, 0.60d + (segmentIndex * 0.18d)),
                AutoCapDamageTemplate.Finisher => segmentIndex == 0 ? 0.55d : Math.Min(1.35d, 0.90d + (segmentIndex * 0.15d)),
                _ => 1.00d
            };

            int value = (int)Math.Round(baseDamage * factor * jitter);
            int minDamage = Math.Max(1, _autoCaptureDamageNumberControl?.MinDamage ?? 1);
            int maxDamage = Math.Max(minDamage, _autoCaptureDamageNumberControl?.MaxDamage ?? 199999);
            return Math.Clamp(value, minDamage, maxDamage);
        }

        private bool RollAutoCapSegmentCritical(AutoCapDamageTemplate template, int segmentIndex)
        {
            if (_autoCaptureRandom == null)
            {
                return false;
            }

            double chance = template switch
            {
                AutoCapDamageTemplate.Single => 0.22d,
                AutoCapDamageTemplate.DoubleTap => segmentIndex == 0 ? 0.22d : 0.28d,
                AutoCapDamageTemplate.RapidCombo => 0.18d,
                AutoCapDamageTemplate.StaggerCombo => 0.20d + (segmentIndex * 0.03d),
                AutoCapDamageTemplate.Finisher => segmentIndex >= 1 ? 0.42d : 0.18d,
                _ => 0.22d
            };
            return _autoCaptureRandom.NextDouble() < Math.Clamp(chance, 0.05d, 0.65d);
        }

        private bool ShouldEmitAutoCaptureMiss()
        {
            if (_autoCaptureRandom == null || _autoCaptureDamageNumberControl == null || !_autoCaptureDamageNumberControl.EnableMiss)
            {
                return false;
            }

            double missProb = _autoCaptureDamageNumberControl.GetMissProbability(_autoCaptureCurrentProfile);
            return _autoCaptureRandom.NextDouble() < Math.Clamp(missProb, 0d, 1d);
        }

        private int RollAutoCapBaseDamage()
        {
            int minDamage = Math.Max(1, _autoCaptureDamageNumberControl?.MinDamage ?? 1);
            int maxDamage = Math.Max(minDamage, _autoCaptureDamageNumberControl?.MaxDamage ?? 199999);
            if (_autoCaptureRandom == null)
            {
                return minDamage;
            }

            if ((_autoCaptureDamageNumberControl?.DamageDistributionMode ?? AutoCaptureDamageDistributionMode.Quadratic) == AutoCaptureDamageDistributionMode.Bucketed)
            {
                return RollAutoCapBaseDamageBucketed(minDamage, maxDamage);
            }

            double u = _autoCaptureRandom.NextDouble();
            double scaled = u * u;
            int span = Math.Max(0, maxDamage - minDamage);
            return minDamage + (int)Math.Round(span * scaled);
        }

        private int RollAutoCapBaseDamageBucketed(int minDamage, int maxDamage)
        {
            int roll = _autoCaptureRandom?.Next(100) ?? 0;
            return roll switch
            {
                < 20 => RollAutoCapBaseDamageInBucket(minDamage, maxDamage, 1, 999),
                < 55 => RollAutoCapBaseDamageInBucket(minDamage, maxDamage, 1000, 9999),
                < 85 => RollAutoCapBaseDamageInBucket(minDamage, maxDamage, 10000, 49999),
                < 97 => RollAutoCapBaseDamageInBucket(minDamage, maxDamage, 50000, 119999),
                _ => RollAutoCapBaseDamageInBucket(minDamage, maxDamage, 120000, 199999)
            };
        }

        private int RollAutoCapBaseDamageInBucket(int globalMin, int globalMax, int bucketMin, int bucketMax)
        {
            int effectiveMin = Math.Max(globalMin, bucketMin);
            int effectiveMax = Math.Min(globalMax, bucketMax);
            if (effectiveMax < effectiveMin)
            {
                return globalMin;
            }

            if (effectiveMax == effectiveMin || _autoCaptureRandom == null)
            {
                return effectiveMin;
            }

            return _autoCaptureRandom.Next(effectiveMin, effectiveMax + 1);
        }

        private List<MobItem> BuildDamageEventCandidates(List<MobItem> forceStateMobs, List<MobItem> fallbackMobs)
        {
            var list = new List<MobItem>();
            if (forceStateMobs != null && forceStateMobs.Count > 0)
            {
                list.AddRange(forceStateMobs.Where(m => !IsDeadLikeMob(m)).Where(IsMobInCameraView).OrderBy(m => DistanceToCameraCenterSq(m)));
            }
            if (fallbackMobs != null && fallbackMobs.Count > 0)
            {
                list.AddRange(fallbackMobs.Where(m => !IsDeadLikeMob(m)).Where(IsMobInCameraView).OrderBy(m => DistanceToCameraCenterSq(m)));
            }
            return list;
        }

        private bool IsMobInCameraView(MobItem mob)
        {
            if (mob == null)
            {
                return false;
            }

            int marginX = Math.Min(AutoCapViewSafeMarginPx, Math.Max(8, _renderParams.RenderWidth / 6));
            int marginY = Math.Min(AutoCapViewSafeMarginPx, Math.Max(8, _renderParams.RenderHeight / 6));

            float worldLeft = mapShiftX - _mapCenterX + marginX;
            float worldTop = mapShiftY - _mapCenterY + marginY;
            float worldRight = mapShiftX - _mapCenterX + _renderParams.RenderWidth - marginX;
            float worldBottom = mapShiftY - _mapCenterY + _renderParams.RenderHeight - marginY;

            float x = mob.CurrentX;
            float y = mob.CurrentY;
            return x >= worldLeft && x <= worldRight && y >= worldTop && y <= worldBottom;
        }

        private double DistanceToCameraCenterSq(MobItem mob)
        {
            if (mob == null)
            {
                return double.MaxValue;
            }

            double centerX = mapShiftX;
            double centerY = mapShiftY;
            double dx = mob.CurrentX - centerX;
            double dy = mob.CurrentY - centerY;
            return dx * dx + dy * dy;
        }

        private bool CanFireDamageEventForMob(MobItem mob, int tick)
        {
            if (mob == null)
            {
                return false;
            }
            if (IsDeadLikeMob(mob))
            {
                return false;
            }

            int globalElapsed = unchecked(tick - _autoCaptureDmgLastGlobalTick);
            if (globalElapsed < _autoCaptureDamageNumberControl.GlobalCooldownMs)
            {
                return false;
            }

            if (_autoCaptureDmgLastTickByMob.TryGetValue(mob.PoolId, out int lastPerMobTick))
            {
                int perMobElapsed = unchecked(tick - lastPerMobTick);
                if (perMobElapsed < _autoCaptureDamageNumberControl.PerMobCooldownMs)
                {
                    return false;
                }
            }

            return true;
        }

        private static int ResolveMobLabelClassId(MobItem mob)
        {
            if (mob == null)
            {
                return AutoCapClassMobDead;
            }

            if ((mob.AI?.IsDead ?? false) || mob.IsDeathAnimationComplete || IsDeathLikeAction(mob.CurrentAction))
            {
                return AutoCapClassMobDead;
            }

            return AutoCapClassMobActive;
        }

        private string FormatSaveFailReasonStats()
        {
            if (_autoCaptureSaveFailByReason == null || _autoCaptureSaveFailByReason.Count == 0)
            {
                return "none";
            }

            return string.Join(";", _autoCaptureSaveFailByReason
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Take(8)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
        }

        private static List<(int classId, Rectangle bounds)> BuildScaleFallbackBounds(List<(int classId, Rectangle bounds)> boundsList, float scale)
        {
            var result = new List<(int classId, Rectangle bounds)>();
            if (boundsList == null || boundsList.Count == 0 || scale <= 1.01f)
            {
                return result;
            }

            float inv = 1f / scale;
            for (int i = 0; i < boundsList.Count; i++)
            {
                var item = boundsList[i];
                var r = item.bounds;
                int left = (int)Math.Round(r.Left * inv);
                int top = (int)Math.Round(r.Top * inv);
                int width = Math.Max(1, (int)Math.Round(r.Width * inv));
                int height = Math.Max(1, (int)Math.Round(r.Height * inv));
                result.Add((item.classId, new Rectangle(left, top, width, height)));
            }

            return result;
        }

        private static bool HasUsableClassId(List<(int classId, Rectangle bounds)> boundsList, int classId, int width, int height)
        {
            if (boundsList == null || boundsList.Count == 0)
            {
                return false;
            }

            foreach (var item in boundsList)
            {
                if (item.classId == classId && IsUsableCaptureRect(item.bounds, width, height))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
