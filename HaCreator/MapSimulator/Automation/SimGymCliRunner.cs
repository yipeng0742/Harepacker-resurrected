using HaCreator.MapEditor;
using HaCreator.MapEditor.Info;
using HaCreator.Wz;
using HaSharedLibrary.Render.DX;
using HaSharedLibrary.Wz;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzStructure;
using SharpDX;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HaCreator.MapSimulator.Automation
{
    internal static class SimGymCliRunner
    {
        internal static bool IsSimGymMode(string[] args)
        {
            return args != null && args.Any(a => string.Equals(a, "--sim-gym", StringComparison.OrdinalIgnoreCase));
        }

        internal static int Run(string[] args)
        {
            try
            {
                if (!TryParseArgs(args, out int mapId, out int gymPort, out string versionPath, out string spawnPortal, out string resolutionName, out string error))
                {
                    Console.Error.WriteLine("[SimGym] " + error);
                    PrintUsage();
                    return 2;
                }

                if (string.IsNullOrWhiteSpace(versionPath) || !Directory.Exists(versionPath) || !File.Exists(Path.Combine(versionPath, "manifest.json")))
                {
                    Console.Error.WriteLine($"[SimGym] version_path 无效: {versionPath}");
                    return 2;
                }

                InitializeDataSource(versionPath);
                ExtractInfoIndex();

                if (!MapImgFileExists(mapId))
                {
                    Console.Error.WriteLine($"[SimGym] 地图文件不存在: {mapId:D9}");
                    return 2;
                }

                if (!TryResolveResolution(resolutionName, out RenderResolution resolution))
                {
                    Console.Error.WriteLine($"[SimGym] 不支持的分辨率: {resolutionName}");
                    return 2;
                }

                UserSettings.SimulateResolution = resolution;
                Board board = LoadBoardForMap(mapId);
                if (board == null)
                {
                    Console.Error.WriteLine($"[SimGym] 构建地图失败: {mapId:D9}");
                    return 2;
                }

                Console.WriteLine($"[SimGym] 启动 map={mapId:D9} port={gymPort} resolution={resolutionName}");
                SimGymRuntime.Current = new SimGymRunOptions
                {
                    UseCompatibleGraphics = true,
                    EnableGraphicsDiagnostics = true,
                    MuteAudio = HasFlag(args, "--mute-audio"),
                    DisableLocalHotkeys = HasFlag(args, "--disable-local-hotkeys"),
                };
                WriteEnvironmentDiagnostics(versionPath);
                return SimHeadlessCliRunner.Run(board, gymPort, string.IsNullOrWhiteSpace(spawnPortal) ? null : spawnPortal);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SimGym] 启动失败: " + ex);
                return 1;
            }
            finally
            {
                try { SimGymRuntime.Current = null; } catch { }
                try { Program.DataSource?.Dispose(); Program.DataSource = null; } catch { }
            }
        }

        private static bool TryParseArgs(
            string[] args,
            out int mapId,
            out int gymPort,
            out string versionPath,
            out string spawnPortal,
            out string resolutionName,
            out string error)
        {
            mapId = 0;
            gymPort = 18765;
            versionPath = "";
            spawnPortal = "";
            resolutionName = "1366x768";
            error = "";

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--sim-gym", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out mapId) || mapId <= 0)
                    {
                        error = "--sim-gym 需要合法 map_id";
                        return false;
                    }
                    continue;
                }
                if (arg.Equals("--gym-port", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out gymPort) || gymPort <= 0)
                    {
                        error = "--gym-port 需要合法端口";
                        return false;
                    }
                    continue;
                }
                if (arg.Equals("--version-path", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "--version-path 缺少路径";
                        return false;
                    }
                    versionPath = args[++i];
                    continue;
                }
                if (arg.Equals("--spawn-portal", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "--spawn-portal 缺少传送门名";
                        return false;
                    }
                    spawnPortal = args[++i];
                    continue;
                }
                if (arg.Equals("--resolution", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "--resolution 缺少分辨率";
                        return false;
                    }
                    resolutionName = args[++i];
                }
            }

            if (mapId <= 0)
            {
                error = "缺少 --sim-gym <map_id>";
                return false;
            }
            if (string.IsNullOrWhiteSpace(versionPath))
            {
                error = "缺少 --version-path <img_fs_dir>";
                return false;
            }
            return true;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HaCreator --sim-gym <map_id> --gym-port <port> --version-path <img_fs_dir> [--spawn-portal sp] [--resolution 1366x768] [--mute-audio] [--disable-local-hotkeys]");
        }

        private static bool HasFlag(string[] args, string flag)
        {
            return args != null && args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }

        private static void WriteEnvironmentDiagnostics(string versionPath)
        {
            if (!SimGymRuntime.EnableGraphicsDiagnostics)
            {
                return;
            }

            try
            {
                Console.WriteLine($"[SimGym][diag] cwd={Environment.CurrentDirectory}");
                Console.WriteLine($"[SimGym][diag] version_path={versionPath}");
                Console.WriteLine($"[SimGym][diag] appdata_root={HaCreatorPaths.AppDataRoot}");
                Console.WriteLine($"[SimGym][diag] appdata_env={Environment.GetEnvironmentVariable(HaCreatorPaths.AppDataRootEnvName) ?? ""}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimGym][diag] env_failed={ex.GetType().Name}:{ex.Message}");
            }

            try
            {
                using var factory = new Factory1();
                var adapters = factory.Adapters1;
                Console.WriteLine($"[SimGym][diag] dxgi_adapter_count={adapters.Length}");
                for (int i = 0; i < adapters.Length; i++)
                {
                    using var adapter = adapters[i];
                    var desc = adapter.Description1;
                    Console.WriteLine(
                        $"[SimGym][diag] adapter[{i}] desc={desc.Description} vendor={desc.VendorId} device={desc.DeviceId} flags={desc.Flags}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimGym][diag] dxgi_enum_failed={ex.GetType().Name}:{ex.Message}");
            }
        }

        private static void ValidateSimGraphicsEnvironment()
        {
            try
            {
                using var factory = new Factory1();
                var adapters = factory.Adapters1;
                if (adapters == null || adapters.Length <= 0)
                {
                    throw new InvalidOperationException("DXGI 未枚举到任何适配器。");
                }

                bool anyOutput = false;
                for (int i = 0; i < adapters.Length; i++)
                {
                    using var adapter = adapters[i];
                    try
                    {
                        using var output = adapter.GetOutput(0);
                        if (output != null)
                        {
                            anyOutput = true;
                            break;
                        }
                    }
                    catch (SharpDXException ex) when ((uint)ex.HResult == 0x887A0002u)
                    {
                        // DXGI_ERROR_NOT_FOUND: 当前会话下该 adapter 没有关联显示输出。
                    }
                }

                if (!anyOutput)
                {
                    throw new InvalidOperationException(
                        "DXGI 适配器存在，但当前桌面会话下没有任何可用输出；MonoGame GraphicsAdapter 会在此环境下初始化失败。");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("SimGym 图形环境预检查失败: " + ex.Message, ex);
            }
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
                Console.WriteLine("[SimGym] map image diagnostics:");
                Console.WriteLine("  nested=" + imgDs.GetImageDiagnostics("Map", relPathNested));
                Console.WriteLine("  flat=" + imgDs.GetImageDiagnostics("Map", relPathFlat));
            }
            return image;
        }

        private static Board LoadBoardForMap(int mapId)
        {
            string key = mapId.ToString("D9");
            WzImage mapImage = null;
            string mapName = key;
            string streetName = key;
            string categoryName = "SimGym";
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
                Console.WriteLine($"[SimGym] map={mapId:D9} 运行时加载失败（文件存在={MapImgFileExists(mapId)}）");
                return null;
            }
            if (!mapImage.Parsed)
            {
                mapImage.ParseImage();
            }

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
                case "1366x768":
                    resolution = RenderResolution.Res_1366x768;
                    return true;
                case "1280x720":
                    resolution = RenderResolution.Res_1280x720;
                    return true;
                case "1024x768":
                    resolution = RenderResolution.Res_1024x768;
                    return true;
                default:
                    resolution = RenderResolution.Res_1024x768;
                    return false;
            }
        }
    }
}
