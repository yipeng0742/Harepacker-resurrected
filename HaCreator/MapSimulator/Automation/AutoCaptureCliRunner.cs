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
            public int seed { get; set; } = 20260505;
            public int target_frames { get; set; } = 180;
            public bool mute_audio { get; set; } = true;
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
                Console.WriteLine($"  seed         : {job.seed}");
                Console.WriteLine($"  mute_audio   : {job.mute_audio}");
                Console.WriteLine($"  profile_mix  : normal_move={job.capture_profile_mix?.normal_move ?? 30}, attack_heavy={job.capture_profile_mix?.attack_heavy ?? 30}, hit_occlusion_heavy={job.capture_profile_mix?.hit_occlusion_heavy ?? 25}, death_heavy={job.capture_profile_mix?.death_heavy ?? 15}");
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
                                TimeScale = Math.Max(1f, job.time_scale),
                                Seed = job.seed,
                                MuteAudio = job.mute_audio,
                                CaptureProfileMix = BuildProfileMix(job.capture_profile_mix)
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
                if (TryLoadMapImage(id) == null)
                {
                    missing.Add(id);
                }
            }
            return missing;
        }

        private static WzImage TryLoadMapImage(int mapId)
        {
            string padded = mapId.ToString("D9");
            string folder = padded.Substring(0, 1);
            string relPath = $"Map/Map{folder}/{padded}.img";
            return Program.DataSource?.GetImageByPath($"Map/{relPath}")
                   ?? Program.DataSource?.GetImage("Map", relPath);
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
                return null;
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
    }
}
