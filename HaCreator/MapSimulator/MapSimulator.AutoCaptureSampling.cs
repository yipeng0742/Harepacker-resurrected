using HaCreator.MapSimulator.Automation;
using HaCreator.MapSimulator.Entities;
using HaCreator.MapSimulator.Effects;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private static AutoCaptureProfile SelectProfileByWeight(Random random, Dictionary<AutoCaptureProfile, int> mix)
        {
            if (random == null || mix == null || mix.Count == 0)
            {
                return AutoCaptureProfile.NormalMove;
            }

            int total = mix.Values.Where(v => v > 0).Sum();
            if (total <= 0)
            {
                return AutoCaptureProfile.NormalMove;
            }

            int roll = random.Next(total);
            int acc = 0;
            foreach (var kv in mix)
            {
                if (kv.Value <= 0)
                    continue;

                acc += kv.Value;
                if (roll < acc)
                {
                    return kv.Key;
                }
            }

            return AutoCaptureProfile.NormalMove;
        }

        private static IEnumerable<AutoCaptureDataBucket> EnumerateAllBuckets()
        {
            return (AutoCaptureDataBucket[])Enum.GetValues(typeof(AutoCaptureDataBucket));
        }

        private static string GetBucketCode(AutoCaptureDataBucket bucket)
        {
            return bucket switch
            {
                AutoCaptureDataBucket.CleanBaseline => "A",
                AutoCaptureDataBucket.AnchorDecoupling => "B",
                AutoCaptureDataBucket.ChaosOcclusion => "C",
                _ => "A"
            };
        }

        private static bool IsHitLikeAction(string action)
        {
            if (string.IsNullOrEmpty(action))
            {
                return false;
            }

            string s = action.ToLowerInvariant();
            return s.StartsWith("hit", StringComparison.Ordinal) ||
                   s.StartsWith("damage", StringComparison.Ordinal) ||
                   s.StartsWith("dam", StringComparison.Ordinal);
        }

        private static bool IsStandMoveLikeAction(string action)
        {
            if (string.IsNullOrEmpty(action))
            {
                return false;
            }

            string s = action.ToLowerInvariant();
            return s.StartsWith("stand", StringComparison.Ordinal) ||
                   s.StartsWith("move", StringComparison.Ordinal) ||
                   s.StartsWith("walk", StringComparison.Ordinal) ||
                   s.StartsWith("fly", StringComparison.Ordinal) ||
                   s.StartsWith("jump", StringComparison.Ordinal);
        }

        private AutoCaptureDataBucket SelectBucketByGlobalDeficit()
        {
            var mix = _autoCaptureBucketMix ?? AutoCaptureBucketMix.CreateDefault();
            int totalSaved = 0;
            foreach (var bucket in EnumerateAllBuckets())
            {
                totalSaved += _autoCaptureBucketSaved.TryGetValue(bucket, out int v) ? Math.Max(0, v) : 0;
            }

            if (totalSaved <= 0)
            {
                AutoCaptureDataBucket fallback = AutoCaptureDataBucket.ChaosOcclusion;
                int bestWeight = -1;
                foreach (var bucket in EnumerateAllBuckets())
                {
                    int w = mix.GetWeight(bucket);
                    if (w > bestWeight)
                    {
                        bestWeight = w;
                        fallback = bucket;
                    }
                }
                return fallback;
            }

            AutoCaptureDataBucket selected = AutoCaptureDataBucket.ChaosOcclusion;
            double bestGap = double.MinValue;
            int bestWeightTie = -1;
            foreach (var bucket in EnumerateAllBuckets())
            {
                int targetWeight = mix.GetWeight(bucket);
                if (targetWeight <= 0)
                {
                    continue;
                }

                int saved = _autoCaptureBucketSaved.TryGetValue(bucket, out int cnt) ? Math.Max(0, cnt) : 0;
                double expected = totalSaved * (targetWeight / 100.0d);
                double gap = expected - saved;
                if (gap > bestGap + 1e-6 ||
                    (Math.Abs(gap - bestGap) <= 1e-6 && targetWeight > bestWeightTie))
                {
                    bestGap = gap;
                    bestWeightTie = targetWeight;
                    selected = bucket;
                }
            }

            return selected;
        }

        private AutoCaptureProfile SelectProfileForBucket(AutoCaptureDataBucket bucket)
        {
            switch (bucket)
            {
                case AutoCaptureDataBucket.CleanBaseline:
                    return AutoCaptureProfile.NormalMove;
                case AutoCaptureDataBucket.AnchorDecoupling:
                {
                    int normal = _autoCaptureProfileMix != null && _autoCaptureProfileMix.TryGetValue(AutoCaptureProfile.NormalMove, out int wNormal)
                        ? Math.Max(0, wNormal)
                        : 1;
                    int attack = _autoCaptureProfileMix != null && _autoCaptureProfileMix.TryGetValue(AutoCaptureProfile.AttackHeavy, out int wAttack)
                        ? Math.Max(0, wAttack)
                        : 1;
                    int total = normal + attack;
                    if (total <= 0 || _autoCaptureRandom == null)
                    {
                        return AutoCaptureProfile.NormalMove;
                    }
                    int roll = _autoCaptureRandom.Next(total);
                    return roll < normal ? AutoCaptureProfile.NormalMove : AutoCaptureProfile.AttackHeavy;
                }
                case AutoCaptureDataBucket.ChaosOcclusion:
                {
                    int hit = _autoCaptureProfileMix != null && _autoCaptureProfileMix.TryGetValue(AutoCaptureProfile.HitOcclusionHeavy, out int wHit)
                        ? Math.Max(0, wHit)
                        : 4;
                    int attack = _autoCaptureProfileMix != null && _autoCaptureProfileMix.TryGetValue(AutoCaptureProfile.AttackHeavy, out int wAttack)
                        ? Math.Max(0, wAttack)
                        : 2;
                    int death = _autoCaptureProfileMix != null && _autoCaptureProfileMix.TryGetValue(AutoCaptureProfile.DeathHeavy, out int wDeath)
                        ? Math.Max(0, wDeath)
                        : 1;
                    int total = hit + attack + death;
                    if (total <= 0 || _autoCaptureRandom == null)
                    {
                        return AutoCaptureProfile.HitOcclusionHeavy;
                    }
                    int roll = _autoCaptureRandom.Next(total);
                    if (roll < hit)
                    {
                        return AutoCaptureProfile.HitOcclusionHeavy;
                    }
                    roll -= hit;
                    if (roll < attack)
                    {
                        return AutoCaptureProfile.AttackHeavy;
                    }
                    return AutoCaptureProfile.DeathHeavy;
                }
                default:
                    return AutoCaptureProfile.HitOcclusionHeavy;
            }
        }

        private AutoCaptureBucketRuntimeTuning BuildBucketTuning()
        {
            var tuning = new AutoCaptureBucketRuntimeTuning { Profile = _autoCaptureCurrentProfile };
            switch (_autoCaptureCurrentBucket)
            {
                case AutoCaptureDataBucket.CleanBaseline:
                    tuning.DisableDamageNumbers = true;
                    tuning.DamageLagProbOverride = 0d;
                    break;
                case AutoCaptureDataBucket.AnchorDecoupling:
                    tuning.DamageLagProbOverride = Math.Min(0.08d, _autoCaptureBucketPolicy?.StandMoveDamageLagProb ?? 0.03d);
                    break;
                case AutoCaptureDataBucket.ChaosOcclusion:
                    tuning.HitDamageMinProbOverride = Math.Max(0.90d, _autoCaptureBucketPolicy?.HitDamageMinProb ?? 0.90d);
                    break;
            }
            return tuning;
        }

        private void PrepareAutoCaptureBucketForSampling()
        {
            if (!IsAutoCaptureEnabled || _autoCaptureRandom == null)
            {
                return;
            }

            _autoCaptureCurrentBucket = SelectBucketByGlobalDeficit();
            _autoCaptureCurrentProfile = SelectProfileForBucket(_autoCaptureCurrentBucket);
            _autoCaptureProfileSwitchTick = Environment.TickCount;
            _autoCapturePointRecipeSeed = _autoCaptureRandom.Next();
            _autoCapturePointDamageTemplate = PickAutoCapDamageTemplate(_autoCaptureCurrentProfile);
            RebuildAutoCapturePointSkillPool();
        }

        private void RebuildAutoCapturePointSkillPool()
        {
            _autoCapturePointSkillPool.Clear();
            if (_autoCaptureNativeDamageSkillPool.Count <= 0)
            {
                return;
            }

            string targetOcclusion = ResolvePointOcclusionLevel(_autoCaptureCurrentProfile, _autoCaptureCurrentBucket);
            IEnumerable<AutoCapNativeDamageSkillEntry> filtered = _autoCaptureNativeDamageSkillPool
                .Where(skill => string.Equals(skill.OcclusionLevel, targetOcclusion, StringComparison.OrdinalIgnoreCase));

            if (!filtered.Any())
            {
                filtered = _autoCaptureNativeDamageSkillPool;
            }

            int takeCount = _autoCaptureCurrentProfile switch
            {
                AutoCaptureProfile.NormalMove => 2,
                AutoCaptureProfile.AttackHeavy => 3,
                AutoCaptureProfile.HitOcclusionHeavy => 4,
                AutoCaptureProfile.DeathHeavy => 2,
                _ => 2
            };

            foreach (var skill in filtered.OrderBy(_ => _autoCaptureRandom.Next()).Take(takeCount))
            {
                _autoCapturePointSkillPool.Add(skill);
            }

            if (_autoCapturePointSkillPool.Count == 0)
            {
                _autoCapturePointSkillPool.Add(_autoCaptureNativeDamageSkillPool[_autoCaptureRandom.Next(_autoCaptureNativeDamageSkillPool.Count)]);
            }
        }

        private static string ResolvePointOcclusionLevel(AutoCaptureProfile profile, AutoCaptureDataBucket bucket)
        {
            if (profile == AutoCaptureProfile.NormalMove) return "low";
            if (profile == AutoCaptureProfile.AttackHeavy) return "medium";
            if (profile == AutoCaptureProfile.HitOcclusionHeavy || bucket == AutoCaptureDataBucket.ChaosOcclusion) return "high";
            return "medium";
        }
    }
}
