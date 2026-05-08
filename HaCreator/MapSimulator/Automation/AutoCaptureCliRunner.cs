using HaCreator.MapEditor;
using HaCreator.Wz;
using HaSharedLibrary.Render.DX;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzStructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HaCreator.MapSimulator.Automation
{
    internal static class AutoCaptureCliRunner
    {
        private sealed class AutoCaptureJob
        {
            public string map_list_path { get; set; }
            public string output_root { get; set; }
            public string version_path { get; set; }
            public float time_scale { get; set; } = 20f;
            public string[] resolutions { get; set; }
            public ScanStep scan_step_px { get; set; } = new ScanStep();
            public CaptureProfileMix capture_profile_mix { get; set; } = new CaptureProfileMix();
            public BucketMix bucket_mix { get; set; } = new BucketMix();
            public BucketPolicy bucket_policy { get; set; } = new BucketPolicy();
            public HpBarControl hp_bar_control { get; set; } = new HpBarControl();
            public DamageNumberControl damage_number_control { get; set; } = new DamageNumberControl();
            public HitEffectControl hit_effect_control { get; set; } = new HitEffectControl();
            public CaptureGuard capture_guard { get; set; } = new CaptureGuard();
            public int seed { get; set; } = 20260505;
            public int target_frames { get; set; } = 180;
            public int capture_warmup_ms { get; set; } = 5000;
            public int camera_tick_budget { get; set; } = 0;
            public bool mute_audio { get; set; } = true;
            public WriterControl writer { get; set; } = new WriterControl();
        }

        private sealed class ScanStep
        {
            public int x { get; set; } = 96;
            public int y { get; set; } = 96;
        }

        private sealed class CaptureProfileMix
        {
            public int normal_move { get; set; } = 30;
            public int attack_heavy { get; set; } = 30;
            public int hit_occlusion_heavy { get; set; } = 25;
            public int death_heavy { get; set; } = 15;
        }

        private sealed class BucketMix
        {
            public int clean_baseline { get; set; } = 20;
            public int anchor_decoupling { get; set; } = 20;
            public int chaos_occlusion { get; set; } = 40;
            public int pure_noise { get; set; } = 20;
        }

        private sealed class BucketPolicy
        {
            public bool enforce_dead_mutual_exclusion { get; set; } = true;
            public double stand_move_damage_lag_prob { get; set; } = 0.03d;
            public double hit_damage_min_prob { get; set; } = 0.90d;
            public string global_ratio_scope { get; set; } = "global";
        }

        private sealed class HpBarControl
        {
            public int hp_event_global_cooldown_ms { get; set; } = 180;
            public int hp_event_per_mob_cooldown_ms { get; set; } = 650;
            public int max_hp_events_per_capture_frame { get; set; } = 6;
            public int max_hp_active_mobs { get; set; } = 6;
            public HpEventProbByProfile hp_event_prob_by_profile { get; set; } = new HpEventProbByProfile();
        }

        private sealed class HpEventProbByProfile
        {
            public double normal { get; set; } = 0.10d;
            public double attack { get; set; } = 0.18d;
            public double hit { get; set; } = 0.28d;
            public double death { get; set; } = 0.06d;
        }

        private sealed class DamageNumberControl
        {
            public bool use_mob_ratio_cap { get; set; } = true;
            public double mob_ratio { get; set; } = 0.30d;
            public int min_events_per_capture_frame { get; set; } = 1;
            public int max_events_per_capture_frame_cap { get; set; } = 6;
            public int global_cooldown_ms { get; set; } = 220;
            public int per_mob_cooldown_ms { get; set; } = 900;
            public int max_events_per_capture_frame { get; set; } = 6;
            public int max_active_numbers { get; set; } = 36;
            public string template_style { get; set; } = "realistic";
            public DamageTemplateWeights template_weights { get; set; } = new DamageTemplateWeights();
            public DamageProbByProfile prob_by_profile { get; set; } = new DamageProbByProfile();
        }

        private sealed class DamageTemplateWeights
        {
            public int single { get; set; } = 35;
            public int double_tap { get; set; } = 30;
            public int rapid_combo { get; set; } = 20;
            public int stagger_combo { get; set; } = 10;
            public int finisher { get; set; } = 5;
        }

        private sealed class DamageProbByProfile
        {
            public double normal { get; set; } = 0.08d;
            public double attack { get; set; } = 0.14d;
            public double hit { get; set; } = 0.20d;
            public double death { get; set; } = 0.05d;
        }

        private sealed class HitEffectControl
        {
            public bool enabled { get; set; } = true;
            public string palette_mode { get; set; } = "extended";
            public double[] alpha_range { get; set; } = new[] { 0.45d, 0.90d };
            public double[] scale_range { get; set; } = new[] { 0.70d, 1.50d };
            public int[] lifetime_ms_range { get; set; } = new[] { 120, 360 };
            public int[] extra_layers_range { get; set; } = new[] { 0, 2 };
            public JitterPx jitter_px { get; set; } = new JitterPx();
            public int[] variation_pool { get; set; } = new[] { 0, 1, 2, 3 };
        }

        private sealed class JitterPx
        {
            public int x { get; set; } = 48;
            public int y { get; set; } = 28;
        }

        private sealed class CaptureGuard
        {
            public int throughput_floor_per_10m { get; set; } = 120;
            public int throughput_floor_per_5m { get; set; } = 100;
            public int hp_label_cap_per_frame { get; set; } = 1;
            public double hp_unpaired_fallback_prob { get; set; } = 0.02d;
            public double death_label_prob_in_death_profile { get; set; } = 0.25d;
            public int point_max_attempts { get; set; } = 8;
            public int[] offscreen_recover_backoff_ms { get; set; } = new[] { 100, 300, 500 };
            public int max_consecutive_capture_failures_per_map { get; set; } = 24;
        }

        private sealed class WriterControl
        {
            public int threads { get; set; } = 8;
            public int queue_capacity { get; set; } = 128;
        }

        internal static bool IsAutoCaptureMode(string[] args)
        {
            return args != null && args.Any(a => string.Equals(a, "--autocap", StringComparison.OrdinalIgnoreCase));
        }

        internal static int Run(string[] args)
        {
            try
            {
                if (!TryParseArgs(args, out string jobPath, out string resumePath, out bool dryRun, out string parseError))
                {
                    Console.WriteLine($"[AutoCap][错误] {parseError}");
                    PrintUsage();
                    return 2;
                }

                if (!File.Exists(jobPath))
                {
                    Console.WriteLine($"[AutoCap][错误] job.json 不存在: {jobPath}");
                    return 2;
                }

                AutoCaptureJob job = LoadJob(jobPath);
                if (job == null)
                {
                    Console.WriteLine("[AutoCap][错误] job.json 解析失败。");
                    return 2;
                }

                string jobDir = Path.GetDirectoryName(jobPath) ?? Environment.CurrentDirectory;
                string mapListPath = ResolveJobPath(jobDir, job.map_list_path);
                string outputRoot = ResolveJobPath(jobDir, job.output_root);
                string versionPath = job.version_path;

                if (string.IsNullOrWhiteSpace(versionPath))
                {
                    Console.WriteLine("[AutoCap][错误] job.json 缺少 version_path。");
                    return 2;
                }
                if (!Directory.Exists(versionPath))
                {
                    Console.WriteLine($"[AutoCap][错误] version_path 目录不存在: {versionPath}");
                    return 2;
                }
                if (!File.Exists(Path.Combine(versionPath, "manifest.json")))
                {
                    Console.WriteLine($"[AutoCap][错误] version_path 缺少 manifest.json: {versionPath}");
                    return 2;
                }
                if (!File.Exists(mapListPath))
                {
                    Console.WriteLine($"[AutoCap][错误] map_list_path 文件不存在: {mapListPath}");
                    return 2;
                }

                List<int> mapIds = LoadMapList(mapListPath);
                if (mapIds.Count == 0)
                {
                    Console.WriteLine("[AutoCap][错误] 地图清单为空。");
                    return 2;
                }

                string[] jobResolutions = job.resolutions == null || job.resolutions.Length == 0
                    ? new[] { "1920x1080", "1600x900", "1366x768", "1280x720" }
                    : job.resolutions;

                Console.WriteLine("[AutoCap] 作业参数：");
                Console.WriteLine($"  job_path     : {jobPath}");
                Console.WriteLine($"  map_count    : {mapIds.Count}");
                Console.WriteLine($"  output_root  : {outputRoot}");
                Console.WriteLine($"  version_path : {versionPath}");
                Console.WriteLine($"  time_scale   : {job.time_scale}");
                Console.WriteLine($"  resolutions  : {string.Join(", ", jobResolutions)}");
                Console.WriteLine($"  scan_step_px : {job.scan_step_px?.x ?? 96}, {job.scan_step_px?.y ?? 96}");
                Console.WriteLine($"  target_frames: {Math.Max(1, job.target_frames)}");
                Console.WriteLine($"  warmup_ms    : {Math.Max(0, job.capture_warmup_ms)}");
                Console.WriteLine($"  camera_budget: {Math.Max(0, job.camera_tick_budget)}");
                Console.WriteLine($"  seed         : {job.seed}");
                Console.WriteLine($"  mute_audio   : {job.mute_audio}");
                Console.WriteLine($"  profile_mix  : normal_move={job.capture_profile_mix?.normal_move ?? 30}, attack_heavy={job.capture_profile_mix?.attack_heavy ?? 30}, hit_occlusion_heavy={job.capture_profile_mix?.hit_occlusion_heavy ?? 25}, death_heavy={job.capture_profile_mix?.death_heavy ?? 15}");
                AutoCaptureBucketMix bucketMix = BuildBucketMix(job.bucket_mix);
                AutoCaptureBucketPolicy bucketPolicy = BuildBucketPolicy(job.bucket_policy);
                Console.WriteLine($"  bucket_mix   : clean_baseline={bucketMix.CleanBaseline}, anchor_decoupling={bucketMix.AnchorDecoupling}, chaos_occlusion={bucketMix.ChaosOcclusion}, pure_noise={bucketMix.PureNoise}");
                Console.WriteLine($"  bucket_policy: enforce_dead_mutual_exclusion={bucketPolicy.EnforceDeadMutualExclusion}, stand_move_damage_lag_prob={bucketPolicy.StandMoveDamageLagProb:0.###}, hit_damage_min_prob={bucketPolicy.HitDamageMinProb:0.###}, global_ratio_scope={bucketPolicy.GlobalRatioScope}");
                AutoCaptureHpBarControl hpBarControl = BuildHpBarControl(job.hp_bar_control);
                AutoCaptureDamageNumberControl damageControl = BuildDamageNumberControl(job.damage_number_control);
                AutoCaptureHitEffectControl hitEffectControl = BuildHitEffectControl(job.hit_effect_control);
                AutoCaptureCaptureGuardControl captureGuard = BuildCaptureGuard(job.capture_guard);
                Console.WriteLine($"  hp_bar_ctrl  : global_cd={hpBarControl.HpEventGlobalCooldownMs}ms, per_mob_cd={hpBarControl.HpEventPerMobCooldownMs}ms, per_frame={hpBarControl.MaxHpEventsPerCaptureFrame}, active_mobs={hpBarControl.MaxHpActiveMobs}");
                Console.WriteLine($"                 probs(normal/attack/hit/death)={hpBarControl.GetProbability(AutoCaptureProfile.NormalMove):0.###}/{hpBarControl.GetProbability(AutoCaptureProfile.AttackHeavy):0.###}/{hpBarControl.GetProbability(AutoCaptureProfile.HitOcclusionHeavy):0.###}/{hpBarControl.GetProbability(AutoCaptureProfile.DeathHeavy):0.###}");
                Console.WriteLine($"  dmg_num_ctrl : global_cd={damageControl.GlobalCooldownMs}ms, per_mob_cd={damageControl.PerMobCooldownMs}ms, per_frame={damageControl.MaxEventsPerCaptureFrame}, active_nums={damageControl.MaxActiveNumbers}, ratio_cap={damageControl.UseMobRatioCap}, mob_ratio={damageControl.MobRatio:0.###}, frame_cap={damageControl.MinEventsPerCaptureFrame}-{damageControl.MaxEventsPerCaptureFrameCap}");
                Console.WriteLine($"                 probs(normal/attack/hit/death)={damageControl.GetProbability(AutoCaptureProfile.NormalMove):0.###}/{damageControl.GetProbability(AutoCaptureProfile.AttackHeavy):0.###}/{damageControl.GetProbability(AutoCaptureProfile.HitOcclusionHeavy):0.###}/{damageControl.GetProbability(AutoCaptureProfile.DeathHeavy):0.###}");
                Console.WriteLine($"                 template_style={damageControl.TemplateStyle}, template_weights=single:{damageControl.TemplateWeights.GetValueOrDefault(AutoCaptureDamageTemplateKind.Single, 0)},double_tap:{damageControl.TemplateWeights.GetValueOrDefault(AutoCaptureDamageTemplateKind.DoubleTap, 0)},rapid_combo:{damageControl.TemplateWeights.GetValueOrDefault(AutoCaptureDamageTemplateKind.RapidCombo, 0)},stagger_combo:{damageControl.TemplateWeights.GetValueOrDefault(AutoCaptureDamageTemplateKind.StaggerCombo, 0)},finisher:{damageControl.TemplateWeights.GetValueOrDefault(AutoCaptureDamageTemplateKind.Finisher, 0)}");
                Console.WriteLine($"  hit_effect_ctrl: enabled={hitEffectControl.Enabled}, palette={hitEffectControl.PaletteMode}, alpha={hitEffectControl.AlphaMin:0.##}-{hitEffectControl.AlphaMax:0.##}, scale={hitEffectControl.ScaleMin:0.##}-{hitEffectControl.ScaleMax:0.##}, lifetime={hitEffectControl.LifetimeMsMin}-{hitEffectControl.LifetimeMsMax}ms, layers={hitEffectControl.ExtraLayersMin}-{hitEffectControl.ExtraLayersMax}, jitter={hitEffectControl.JitterPxX}x{hitEffectControl.JitterPxY}, variations=[{string.Join(",", hitEffectControl.VariationPool)}]");
                Console.WriteLine($"  capture_guard: throughput_floor_per_10m={captureGuard.ThroughputFloorPer10Minutes}, throughput_floor_per_5m={captureGuard.ThroughputFloorPer5Minutes}, hp_label_cap_per_frame={captureGuard.HpLabelCapPerFrame}, hp_unpaired_fallback_prob={captureGuard.HpUnpairedFallbackProb:0.###}, death_label_prob_in_death_profile={captureGuard.DeathLabelProbInDeathProfile:0.###}, point_max_attempts={captureGuard.PointMaxAttempts}, offscreen_recover_backoff_ms=[{string.Join(",", captureGuard.OffscreenRecoverBackoffMs)}], max_consecutive_capture_failures_per_map={captureGuard.MaxConsecutiveCaptureFailuresPerMap}");
                var writer = job.writer ?? new WriterControl();
                int writerThreads = Math.Max(1, writer.threads);
                int writerQueueCapacity = Math.Max(16, writer.queue_capacity);
                Console.WriteLine($"  writer       : threads={writerThreads}, queue_capacity={writerQueueCapacity}");
                if (!string.IsNullOrWhiteSpace(resumePath))
                {
                    Console.WriteLine($"  resume       : {resumePath}");
                }

                InitializeDataSource(versionPath);
                ExtractInfoIndex();
                var missingMaps = ValidateMapsExist(mapIds);
                if (missingMaps.Count > 0)
                {
                    Console.WriteLine($"[AutoCap][错误] 共 {missingMaps.Count} 张地图无法加载，前 20 项：");
                    foreach (int mapId in missingMaps.Take(20))
                    {
                        Console.WriteLine($"  - {mapId:D9}");
                    }
                    return 2;
                }

                Directory.CreateDirectory(outputRoot);
                Console.WriteLine("[AutoCap] 数据源与地图校验通过。");

                if (dryRun)
                {
                    Console.WriteLine("[AutoCap] dry-run 完成，不执行采集。");
                    return 0;
                }

                int failCount = 0;
                foreach (string resolutionName in jobResolutions)
                {
                    if (!TryResolveResolution(resolutionName, out RenderResolution resolution))
                    {
                        Console.WriteLine($"[AutoCap][警告] 跳过不支持的分辨率: {resolutionName}");
                        continue;
                    }

                    UserSettings.SimulateResolution = resolution;
                    string resolutionOutputDir = Path.Combine(outputRoot, resolutionName);
                    Directory.CreateDirectory(resolutionOutputDir);

                    foreach (int mapId in mapIds)
                    {
                        try
                        {
                            Board board = LoadBoardForMap(mapId);
                            if (board == null)
                            {
                                Console.WriteLine($"[AutoCap][错误] 构建地图失败: {mapId:D9}");
                                failCount++;
                                continue;
                            }

                            string mapOutDir = Path.Combine(resolutionOutputDir, mapId.ToString("D9"));
                            Directory.CreateDirectory(mapOutDir);

                            AutoCaptureRuntime.Current = new AutoCaptureRunOptions
                            {
                                MapId = mapId,
                                ResolutionName = resolutionName,
                                OutputDir = mapOutDir,
                                StepX = Math.Max(16, job.scan_step_px?.x ?? 96),
                                StepY = Math.Max(16, job.scan_step_px?.y ?? 96),
                                TargetFrames = Math.Max(1, job.target_frames),
                                CaptureWarmupMs = Math.Max(0, job.capture_warmup_ms),
                                CameraTickBudgetOverride = Math.Max(0, job.camera_tick_budget),
                                TimeScale = Math.Max(1f, job.time_scale),
                                Seed = job.seed,
                                MuteAudio = job.mute_audio,
                                WriterThreads = writerThreads,
                                WriterQueueCapacity = writerQueueCapacity,
                                CaptureProfileMix = BuildProfileMix(job.capture_profile_mix),
                                BucketMix = bucketMix,
                                BucketPolicy = bucketPolicy,
                                HpBarControl = hpBarControl,
                                DamageNumberControl = damageControl,
                                HitEffectControl = hitEffectControl,
                                CaptureGuard = captureGuard
                            };

                            Console.WriteLine($"[AutoCap] 开始采集 map={mapId:D9} res={resolutionName}");
                            using (var simulator = new MapSimulator(board, $"AutoCap-{mapId:D9}-{resolutionName}"))
                            {
                                simulator.Run();
                            }
                            AutoCaptureRuntime.Current = null;
                        }
                        catch (Exception ex)
                        {
                            AutoCaptureRuntime.Current = null;
                            failCount++;
                            Console.WriteLine($"[AutoCap][错误] map={mapId:D9} res={resolutionName} 采集失败: {ex.Message}");
                        }
                    }
                }

                if (failCount > 0)
                {
                    Console.WriteLine($"[AutoCap] 完成，但存在失败项: {failCount}");
                    return 4;
                }

                Console.WriteLine("[AutoCap] 全部分辨率与地图采集完成。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoCap][异常] {ex.Message}");
                return 1;
            }
            finally
            {
                try { AutoCaptureRuntime.Current = null; } catch { }
                try { Program.DataSource?.Dispose(); Program.DataSource = null; } catch { }
            }
        }

        private static bool TryParseArgs(string[] args, out string jobPath, out string resumePath, out bool dryRun, out string error)
        {
            jobPath = null;
            resumePath = null;
            dryRun = false;
            error = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--autocap", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "--autocap 缺少 job.json 路径参数。";
                        return false;
                    }
                    jobPath = args[++i];
                }
                else if (arg.Equals("--resume", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "--resume 缺少 checkpoint 路径参数。";
                        return false;
                    }
                    resumePath = args[++i];
                }
                else if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
                {
                    dryRun = true;
                }
            }

            if (string.IsNullOrWhiteSpace(jobPath))
            {
                error = "缺少 --autocap <job.json> 参数。";
                return false;
            }

            jobPath = Path.GetFullPath(jobPath);
            if (!string.IsNullOrWhiteSpace(resumePath))
            {
                resumePath = Path.GetFullPath(resumePath);
            }
            return true;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  HaCreator --autocap <job.json> [--resume <checkpoint.json>] [--dry-run]");
        }

        private static AutoCaptureJob LoadJob(string jobPath)
        {
            string json = File.ReadAllText(jobPath);
            return JsonSerializer.Deserialize<AutoCaptureJob>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        private static string ResolveJobPath(string jobDir, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return jobDir;
            return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(jobDir, value));
        }

        private static List<int> LoadMapList(string mapListPath)
        {
            var list = new List<int>();
            foreach (var line in File.ReadAllLines(mapListPath))
            {
                string s = line.Trim();
                if (string.IsNullOrWhiteSpace(s) || s.StartsWith("#"))
                    continue;
                if (int.TryParse(s, out int mapId))
                {
                    list.Add(mapId);
                }
            }
            return list;
        }

        private static void InitializeDataSource(string versionPath)
        {
            Program.InfoManager ??= new WzInformationManager();
            Program.InfoManager.Clear();
            Program.StartupManager ??= new StartupManager();
            Program.StartupManager.SetDataSourceMode(DataSourceMode.ImgFileSystem);

            Program.DataSource?.Dispose();
            Program.DataSource = Program.StartupManager.CreateDataSourceFromConfig(versionPath);
        }

        private static void ExtractInfoIndex()
        {
            var extractor = new ImgDataExtractor(Program.DataSource, Program.InfoManager);
            extractor.ExtractAll();
        }

        private static List<int> ValidateMapsExist(List<int> mapIds)
        {
            var missing = new List<int>();
            foreach (int id in mapIds)
            {
                bool fileExists = MapImgFileExists(id);
                if (!fileExists)
                {
                    missing.Add(id);
                    continue;
                }

                // 仅做可加载性预热，不作为 hard gate（避免解密/解析差异导致 dry-run 被误拦截）
                if (TryLoadMapImage(id) == null)
                {
                    Console.WriteLine($"[AutoCap][警告] map={id:D9} 文件存在但预加载失败，继续执行（运行时再尝试加载）。");
                }
            }
            return missing;
        }

        private static bool MapImgFileExists(int mapId)
        {
            string padded = mapId.ToString("D9");
            string folder = padded.Substring(0, 1);
            string relPathFlat = $"Map{folder}/{padded}.img";
            string relPathNested = $"Map/Map{folder}/{padded}.img";

            var dataSource = Program.DataSource;
            if (dataSource is ImgFileSystemDataSource imgDs)
            {
                string basePath = imgDs.Manager?.VersionPath;
                if (!string.IsNullOrWhiteSpace(basePath))
                {
                    string nestedPath = Path.Combine(basePath, "Map", relPathNested.Replace('/', Path.DirectorySeparatorChar));
                    string flatPath = Path.Combine(basePath, "Map", relPathFlat.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(nestedPath) || File.Exists(flatPath))
                    {
                        return true;
                    }
                }
            }

            return dataSource?.ImageExists("Map", relPathNested) == true
                || dataSource?.ImageExists("Map", relPathFlat) == true;
        }

        private static WzImage TryLoadMapImage(int mapId)
        {
            string padded = mapId.ToString("D9");
            string folder = padded.Substring(0, 1);
            string relPathFlat = $"Map{folder}/{padded}.img";
            string relPathNested = $"Map/Map{folder}/{padded}.img";

            // IMG filesystem layouts differ by extraction/version:
            // 1) Map/Map1/100020000.img
            // 2) Map/Map/Map1/100020000.img
            // Try both absolute and category-relative styles.
            var dataSource = Program.DataSource;
            if (dataSource == null)
            {
                return null;
            }

            WzImage image =
                dataSource.GetImage("Map", relPathNested)
                ?? dataSource.GetImage("Map", relPathFlat)
                ?? dataSource.GetImageByPath($"Map/{relPathNested}")
                ?? dataSource.GetImageByPath($"Map/{relPathFlat}");

            if (image == null && dataSource is ImgFileSystemDataSource imgDs)
            {
                string diagNested = imgDs.GetImageDiagnostics("Map", relPathNested);
                string diagFlat = imgDs.GetImageDiagnostics("Map", relPathFlat);
                Console.WriteLine($"[AutoCap][诊断] map={padded} load failed. nested=\"{relPathNested}\", flat=\"{relPathFlat}\"");
                Console.WriteLine($"[AutoCap][诊断] nested:\n{diagNested}");
                Console.WriteLine($"[AutoCap][诊断] flat:\n{diagFlat}");
            }

            return image;
        }

        private static Board LoadBoardForMap(int mapId)
        {
            string key = mapId.ToString("D9");
            WzImage mapImage = null;
            string mapName = key;
            string streetName = key;
            string categoryName = "AutoCap";
            MapInfo info = null;

            if (Program.InfoManager.MapsCache.TryGetValue(key, out var loaded))
            {
                mapImage = loaded.Item1;
                mapName = string.IsNullOrWhiteSpace(loaded.Item2) ? mapName : loaded.Item2;
                streetName = string.IsNullOrWhiteSpace(loaded.Item3) ? streetName : loaded.Item3;
                categoryName = string.IsNullOrWhiteSpace(loaded.Item4) ? categoryName : loaded.Item4;
                info = loaded.Item5;
            }

            mapImage ??= TryLoadMapImage(mapId);
            if (mapImage == null)
            {
                Console.WriteLine($"[AutoCap][错误] map={mapId:D9} 运行时加载失败（文件存在={MapImgFileExists(mapId)}）。");
                return null;
            }
            if (!mapImage.Parsed)
                mapImage.ParseImage();

            info ??= new MapInfo(mapImage, streetName, mapName, categoryName);

            var tabs = new System.Windows.Controls.TabControl();
            var multiBoard = new MultiBoard();
            System.Windows.RoutedEventHandler noop = (_, __) => { };
            var handlers = new[] { noop, noop, noop, noop };

            MapLoader.CreateMapFromImage(mapId, mapImage, info, mapName, streetName, categoryName, tabs, multiBoard, handlers);
            return multiBoard.SelectedBoard;
        }

        private static bool TryResolveResolution(string resolutionName, out RenderResolution resolution)
        {
            switch (resolutionName?.Trim())
            {
                case "1920x1080":
                    resolution = RenderResolution.Res_1920x1080;
                    return true;
                case "1600x900":
                    resolution = RenderResolution.Res_1366x768;
                    return true;
                case "1366x768":
                    resolution = RenderResolution.Res_1366x768;
                    return true;
                case "1280x720":
                    resolution = RenderResolution.Res_1280x720;
                    return true;
                default:
                    resolution = RenderResolution.Res_1024x768;
                    return false;
            }
        }

        private static Dictionary<AutoCaptureProfile, int> BuildProfileMix(CaptureProfileMix mix)
        {
            mix ??= new CaptureProfileMix();
            var result = new Dictionary<AutoCaptureProfile, int>
            {
                [AutoCaptureProfile.NormalMove] = Math.Max(0, mix.normal_move),
                [AutoCaptureProfile.AttackHeavy] = Math.Max(0, mix.attack_heavy),
                [AutoCaptureProfile.HitOcclusionHeavy] = Math.Max(0, mix.hit_occlusion_heavy),
                [AutoCaptureProfile.DeathHeavy] = Math.Max(0, mix.death_heavy)
            };

            if (result.Values.Sum() <= 0)
            {
                return AutoCaptureRunOptions.CreateDefaultProfileMix();
            }

            return result;
        }

        private static AutoCaptureBucketMix BuildBucketMix(BucketMix mix)
        {
            mix ??= new BucketMix();
            return new AutoCaptureBucketMix
            {
                CleanBaseline = Math.Max(0, mix.clean_baseline),
                AnchorDecoupling = Math.Max(0, mix.anchor_decoupling),
                ChaosOcclusion = Math.Max(0, mix.chaos_occlusion),
                PureNoise = Math.Max(0, mix.pure_noise)
            }.Normalize();
        }

        private static AutoCaptureBucketPolicy BuildBucketPolicy(BucketPolicy policy)
        {
            policy ??= new BucketPolicy();
            return new AutoCaptureBucketPolicy
            {
                EnforceDeadMutualExclusion = policy.enforce_dead_mutual_exclusion,
                StandMoveDamageLagProb = Math.Clamp(policy.stand_move_damage_lag_prob, 0d, 1d),
                HitDamageMinProb = Math.Clamp(policy.hit_damage_min_prob, 0d, 1d),
                GlobalRatioScope = policy.global_ratio_scope
            }.Normalize();
        }

        private static AutoCaptureHpBarControl BuildHpBarControl(HpBarControl control)
        {
            control ??= new HpBarControl();
            var prob = control.hp_event_prob_by_profile ?? new HpEventProbByProfile();

            return new AutoCaptureHpBarControl
            {
                HpEventGlobalCooldownMs = Math.Max(0, control.hp_event_global_cooldown_ms),
                HpEventPerMobCooldownMs = Math.Max(0, control.hp_event_per_mob_cooldown_ms),
                MaxHpEventsPerCaptureFrame = Math.Max(0, control.max_hp_events_per_capture_frame),
                MaxHpActiveMobs = Math.Max(1, control.max_hp_active_mobs),
                HpEventProbByProfile = new Dictionary<AutoCaptureProfile, double>
                {
                    [AutoCaptureProfile.NormalMove] = Math.Clamp(prob.normal, 0d, 1d),
                    [AutoCaptureProfile.AttackHeavy] = Math.Clamp(prob.attack, 0d, 1d),
                    [AutoCaptureProfile.HitOcclusionHeavy] = Math.Clamp(prob.hit, 0d, 1d),
                    [AutoCaptureProfile.DeathHeavy] = Math.Clamp(prob.death, 0d, 1d)
                }
            }.Normalize();
        }

        private static AutoCaptureDamageNumberControl BuildDamageNumberControl(DamageNumberControl control)
        {
            control ??= new DamageNumberControl();
            var prob = control.prob_by_profile ?? new DamageProbByProfile();
            var templateWeights = control.template_weights ?? new DamageTemplateWeights();
            AutoCaptureDamageTemplateStyle templateStyle =
                string.Equals(control.template_style, "robust", StringComparison.OrdinalIgnoreCase)
                    ? AutoCaptureDamageTemplateStyle.Robust
                    : AutoCaptureDamageTemplateStyle.Realistic;

            return new AutoCaptureDamageNumberControl
            {
                UseMobRatioCap = control.use_mob_ratio_cap,
                MobRatio = Math.Clamp(control.mob_ratio, 0d, 1d),
                MinEventsPerCaptureFrame = Math.Max(0, control.min_events_per_capture_frame),
                MaxEventsPerCaptureFrameCap = Math.Max(1, control.max_events_per_capture_frame_cap),
                GlobalCooldownMs = Math.Max(0, control.global_cooldown_ms),
                PerMobCooldownMs = Math.Max(0, control.per_mob_cooldown_ms),
                MaxEventsPerCaptureFrame = Math.Max(0, control.max_events_per_capture_frame),
                MaxActiveNumbers = Math.Max(1, control.max_active_numbers),
                TemplateStyle = templateStyle,
                TemplateWeights = new Dictionary<AutoCaptureDamageTemplateKind, int>
                {
                    [AutoCaptureDamageTemplateKind.Single] = Math.Max(0, templateWeights.single),
                    [AutoCaptureDamageTemplateKind.DoubleTap] = Math.Max(0, templateWeights.double_tap),
                    [AutoCaptureDamageTemplateKind.RapidCombo] = Math.Max(0, templateWeights.rapid_combo),
                    [AutoCaptureDamageTemplateKind.StaggerCombo] = Math.Max(0, templateWeights.stagger_combo),
                    [AutoCaptureDamageTemplateKind.Finisher] = Math.Max(0, templateWeights.finisher)
                },
                ProbByProfile = new Dictionary<AutoCaptureProfile, double>
                {
                    [AutoCaptureProfile.NormalMove] = Math.Clamp(prob.normal, 0d, 1d),
                    [AutoCaptureProfile.AttackHeavy] = Math.Clamp(prob.attack, 0d, 1d),
                    [AutoCaptureProfile.HitOcclusionHeavy] = Math.Clamp(prob.hit, 0d, 1d),
                    [AutoCaptureProfile.DeathHeavy] = Math.Clamp(prob.death, 0d, 1d)
                }
            }.Normalize();
        }

        private static AutoCaptureHitEffectControl BuildHitEffectControl(HitEffectControl control)
        {
            control ??= new HitEffectControl();
            AutoCaptureHitEffectPaletteMode paletteMode =
                string.Equals(control.palette_mode, "basic", StringComparison.OrdinalIgnoreCase)
                    ? AutoCaptureHitEffectPaletteMode.Basic
                    : AutoCaptureHitEffectPaletteMode.Extended;

            double alphaMin = control.alpha_range != null && control.alpha_range.Length > 0 ? control.alpha_range[0] : 0.45d;
            double alphaMax = control.alpha_range != null && control.alpha_range.Length > 1 ? control.alpha_range[1] : 0.90d;
            double scaleMin = control.scale_range != null && control.scale_range.Length > 0 ? control.scale_range[0] : 0.70d;
            double scaleMax = control.scale_range != null && control.scale_range.Length > 1 ? control.scale_range[1] : 1.50d;
            int lifeMin = control.lifetime_ms_range != null && control.lifetime_ms_range.Length > 0 ? control.lifetime_ms_range[0] : 120;
            int lifeMax = control.lifetime_ms_range != null && control.lifetime_ms_range.Length > 1 ? control.lifetime_ms_range[1] : 360;
            int layerMin = control.extra_layers_range != null && control.extra_layers_range.Length > 0 ? control.extra_layers_range[0] : 0;
            int layerMax = control.extra_layers_range != null && control.extra_layers_range.Length > 1 ? control.extra_layers_range[1] : 2;
            int jitterX = control.jitter_px?.x ?? 48;
            int jitterY = control.jitter_px?.y ?? 28;

            return new AutoCaptureHitEffectControl
            {
                Enabled = control.enabled,
                PaletteMode = paletteMode,
                AlphaMin = alphaMin,
                AlphaMax = alphaMax,
                ScaleMin = scaleMin,
                ScaleMax = scaleMax,
                LifetimeMsMin = lifeMin,
                LifetimeMsMax = lifeMax,
                ExtraLayersMin = layerMin,
                ExtraLayersMax = layerMax,
                JitterPxX = jitterX,
                JitterPxY = jitterY,
                VariationPool = (control.variation_pool ?? new[] { 0, 1, 2, 3 }).ToList()
            }.Normalize();
        }

        private static AutoCaptureCaptureGuardControl BuildCaptureGuard(CaptureGuard guard)
        {
            guard ??= new CaptureGuard();
            return new AutoCaptureCaptureGuardControl
            {
                ThroughputFloorPer10Minutes = Math.Max(1, guard.throughput_floor_per_10m),
                ThroughputFloorPer5Minutes = Math.Max(1, guard.throughput_floor_per_5m),
                HpLabelCapPerFrame = Math.Clamp(guard.hp_label_cap_per_frame, 1, 2),
                HpUnpairedFallbackProb = Math.Clamp(guard.hp_unpaired_fallback_prob, 0d, 1d),
                DeathLabelProbInDeathProfile = Math.Clamp(guard.death_label_prob_in_death_profile, 0d, 1d),
                PointMaxAttempts = Math.Clamp(guard.point_max_attempts, 1, 200),
                OffscreenRecoverBackoffMs = guard.offscreen_recover_backoff_ms ?? new[] { 100, 300, 500 },
                MaxConsecutiveCaptureFailuresPerMap = Math.Clamp(guard.max_consecutive_capture_failures_per_map, 1, 10000)
            }.Normalize();
        }
    }
}
