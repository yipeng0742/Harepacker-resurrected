using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace HaCreator.MapSimulator.Automation
{
    internal static class WzImgExportCliRunner
    {
        private const string ModeArg = "--wz-img-export";

        internal static bool IsWzImgExportMode(string[] args)
        {
            return args != null && args.Any(a => string.Equals(a, ModeArg, StringComparison.OrdinalIgnoreCase));
        }

        internal static int Run(string[] args)
        {
            try
            {
                if (!TryParseArgs(args, out var options, out string parseError))
                {
                    Console.Error.WriteLine(parseError);
                    PrintUsage();
                    return 2;
                }

                var exporter = new WzImgResourceExporter(options);
                var summary = exporter.Run();
                Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions()));
                return summary.FailedImages == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WZ IMG导出] 失败: " + ex);
                return 1;
            }
        }

        private static bool TryParseArgs(string[] args, out WzImgExportOptions options, out string error)
        {
            options = new WzImgExportOptions();
            error = "";
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals(ModeArg, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (arg.Equals("--wz-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.WzRoot = args[++i];
                    continue;
                }
                if (arg.Equals("--output-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.OutputRoot = args[++i];
                    continue;
                }
                if (arg.Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Mode = args[++i];
                    continue;
                }
                if (arg.Equals("--map-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
                    {
                        options.MapIds.Add(mapId);
                    }
                    continue;
                }
                if (arg.Equals("--overwrite", StringComparison.OrdinalIgnoreCase))
                {
                    options.Overwrite = true;
                    continue;
                }
                if (arg.Equals("--resume", StringComparison.OrdinalIgnoreCase))
                {
                    options.Resume = true;
                    continue;
                }
                if (arg.Equals("--no-assets", StringComparison.OrdinalIgnoreCase))
                {
                    options.ExportAssets = false;
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(options.WzRoot))
            {
                error = "缺少 --wz-root。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(options.OutputRoot))
            {
                error = "缺少 --output-root。";
                return false;
            }
            if (!Directory.Exists(options.WzRoot))
            {
                error = "WZ IMG根目录不存在: " + options.WzRoot;
                return false;
            }
            return true;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HaCreator --wz-img-export --wz-root <DataDir> --output-root <ExportDir> [--mode full|maps] [--map-id 100020000] [--overwrite|--resume] [--no-assets]");
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
                PropertyNameCaseInsensitive = true,
            };
        }
    }

    internal sealed class WzImgExportOptions
    {
        public string WzRoot { get; set; } = "";
        public string OutputRoot { get; set; } = "";
        public string Mode { get; set; } = "full";
        public bool Overwrite { get; set; }
        public bool Resume { get; set; }
        public bool ExportAssets { get; set; } = true;
        public List<int> MapIds { get; } = new List<int>();
    }

    internal sealed class WzImgExportSummary
    {
        public int TotalImages { get; set; }
        public int ParsedImages { get; set; }
        public int SkippedImages { get; set; }
        public int FailedImages { get; set; }
        public int Maps { get; set; }
        public int Footholds { get; set; }
        public int Spawns { get; set; }
        public int Portals { get; set; }
        public int LadderRopes { get; set; }
        public int CanvasAssets { get; set; }
        public int SoundAssets { get; set; }
        public int RawAssets { get; set; }
    }

    internal sealed class WzImgResourceExporter
    {
        private readonly WzImgExportOptions _options;
        private readonly string _rawRoot;
        private readonly string _propertyRoot;
        private readonly string _assetRoot;
        private readonly WzImgExportSummary _summary = new WzImgExportSummary();

        private StreamWriter _resources;
        private StreamWriter _maps;
        private StreamWriter _footholds;
        private StreamWriter _spawns;
        private StreamWriter _portals;
        private StreamWriter _ladderRopes;
        private StreamWriter _layers;
        private StreamWriter _strings;
        private StreamWriter _audit;

        public WzImgResourceExporter(WzImgExportOptions options)
        {
            _options = options;
            _rawRoot = Path.Combine(options.OutputRoot, "raw_resources");
            _propertyRoot = Path.Combine(_rawRoot, "properties");
            _assetRoot = Path.Combine(_rawRoot, "assets");
        }

        public WzImgExportSummary Run()
        {
            PrepareOutput();
            using var dataSource = new ImgFileSystemDataSource(_options.WzRoot);
            OpenWriters();
            try
            {
                foreach (string imgPath in EnumerateImgFiles())
                {
                    ExportImage(dataSource, imgPath);
                }
            }
            finally
            {
                CloseWriters();
            }
            return _summary;
        }

        private void PrepareOutput()
        {
            if (_options.Overwrite && Directory.Exists(_options.OutputRoot) && !_options.Resume)
            {
                Directory.Delete(_options.OutputRoot, true);
            }
            Directory.CreateDirectory(_options.OutputRoot);
            Directory.CreateDirectory(_rawRoot);
            Directory.CreateDirectory(_propertyRoot);
            Directory.CreateDirectory(_assetRoot);
        }

        private void OpenWriters()
        {
            _resources = OpenJsonl("resources_manifest.jsonl");
            _maps = OpenJsonl("maps_manifest.jsonl");
            _footholds = OpenJsonl("map_footholds.jsonl");
            _spawns = OpenJsonl("map_spawns.jsonl");
            _portals = OpenJsonl("map_portals.jsonl");
            _ladderRopes = OpenJsonl("map_ladder_ropes.jsonl");
            _layers = OpenJsonl("map_layers.jsonl");
            _strings = OpenJsonl("string_maps.jsonl");
            _audit = OpenJsonl("audit_xml_compare.jsonl");
        }

        private StreamWriter OpenJsonl(string name)
        {
            string path = Path.Combine(_options.OutputRoot, name);
            return new StreamWriter(path, append: _options.Resume, new UTF8Encoding(false));
        }

        private void CloseWriters()
        {
            foreach (var writer in new[] { _resources, _maps, _footholds, _spawns, _portals, _ladderRopes, _layers, _strings, _audit })
            {
                writer?.Flush();
                writer?.Dispose();
            }
        }

        private IEnumerable<string> EnumerateImgFiles()
        {
            IEnumerable<string> files = Directory.EnumerateFiles(_options.WzRoot, "*.img", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".img.xml", StringComparison.OrdinalIgnoreCase));

            if (string.Equals(_options.Mode, "maps", StringComparison.OrdinalIgnoreCase))
            {
                files = files.Where(IsMapImagePath);
            }
            if (_options.MapIds.Count > 0)
            {
                var ids = new HashSet<string>(_options.MapIds.Select(id => id.ToString(CultureInfo.InvariantCulture) + ".img"), StringComparer.OrdinalIgnoreCase);
                files = files.Where(p => ids.Contains(Path.GetFileName(p)) || IsStringMapImagePath(p));
            }
            return files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsMapImagePath(string path)
        {
            string normalized = path.Replace('/', '\\');
            return normalized.IndexOf("\\Map\\Map\\Map", StringComparison.OrdinalIgnoreCase) >= 0
                && int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        private static bool IsStringMapImagePath(string path)
        {
            string normalized = path.Replace('/', '\\');
            return normalized.EndsWith("\\String\\Map.img", StringComparison.OrdinalIgnoreCase);
        }

        private void ExportImage(ImgFileSystemDataSource dataSource, string imgPath)
        {
            _summary.TotalImages++;
            string relativePath = Path.GetRelativePath(_options.WzRoot, imgPath).Replace('\\', '/');
            string[] parts = relativePath.Split(new[] { '/' }, 2);
            if (parts.Length < 2)
            {
                _summary.FailedImages++;
                Write(_resources, ErrorRecord(relativePath, "invalid_relative_path"));
                return;
            }

            string category = parts[0];
            string categoryRelativePath = parts[1];
            string propertyPath = Path.Combine(_propertyRoot, SafeRelativePath(relativePath) + ".jsonl");
            if (_options.Resume && File.Exists(propertyPath))
            {
                _summary.SkippedImages++;
                return;
            }

            try
            {
                WzImage image = dataSource.GetImage(category, categoryRelativePath);
                if (image == null)
                {
                    _summary.FailedImages++;
                    Write(_resources, ErrorRecord(relativePath, "maplelib_parse_failed"));
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(propertyPath) ?? _propertyRoot);
                using (var propWriter = new StreamWriter(propertyPath, false, new UTF8Encoding(false)))
                {
                    foreach (WzImageProperty prop in image.WzProperties)
                    {
                        ExportPropertyRecursive(propWriter, category, relativePath, prop, prop.Name);
                    }
                }

                string hash = Sha256File(imgPath);
                Write(_resources, new Dictionary<string, object>
                {
                    ["kind"] = "image",
                    ["category"] = category,
                    ["relative_path"] = relativePath,
                    ["source_path"] = imgPath,
                    ["property_tree_path"] = Path.GetRelativePath(_options.OutputRoot, propertyPath).Replace('\\', '/'),
                    ["sha256"] = hash,
                    ["bytes"] = new FileInfo(imgPath).Length,
                    ["status"] = "ok",
                });
                _summary.ParsedImages++;

                if (IsMapImagePath(imgPath))
                {
                    ExportMapImage(relativePath, image);
                    AuditXml(relativePath, imgPath);
                }
                if (string.Equals(relativePath.Replace('\\', '/'), "String/Map.img", StringComparison.OrdinalIgnoreCase))
                {
                    ExportStringMap(image);
                }

                if (_summary.ParsedImages % 100 == 0)
                {
                    dataSource.ClearCache();
                    Console.WriteLine($"[WZ IMG导出] parsed={_summary.ParsedImages} failed={_summary.FailedImages} current={relativePath}");
                }
            }
            catch (Exception ex)
            {
                _summary.FailedImages++;
                Write(_resources, ErrorRecord(relativePath, ex.GetType().Name + ":" + ex.Message));
            }
        }

        private Dictionary<string, object> ErrorRecord(string relativePath, string reason)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = "image",
                ["relative_path"] = relativePath,
                ["status"] = "error",
                ["reason"] = reason,
            };
        }

        private void ExportPropertyRecursive(StreamWriter propWriter, string category, string imageRelativePath, WzImageProperty prop, string propPath)
        {
            var record = PropertyRecord(category, imageRelativePath, prop, propPath);

            if (_options.ExportAssets)
            {
                TryExportAsset(category, imageRelativePath, prop, propPath, record);
            }
            Write(propWriter, record);

            if (prop.WzProperties != null)
            {
                foreach (WzImageProperty child in prop.WzProperties)
                {
                    ExportPropertyRecursive(propWriter, category, imageRelativePath, child, propPath + "/" + child.Name);
                }
            }
        }

        private Dictionary<string, object> PropertyRecord(string category, string imageRelativePath, WzImageProperty prop, string propPath)
        {
            var record = new Dictionary<string, object>
            {
                ["category"] = category,
                ["image_relative_path"] = imageRelativePath,
                ["path"] = propPath,
                ["name"] = prop.Name ?? "",
                ["type"] = prop.PropertyType.ToString(),
            };

            switch (prop)
            {
                case WzStringProperty p:
                    record["value"] = p.Value ?? "";
                    break;
                case WzIntProperty p:
                    record["value"] = p.Value;
                    break;
                case WzShortProperty p:
                    record["value"] = p.Value;
                    break;
                case WzLongProperty p:
                    record["value"] = p.Value;
                    break;
                case WzFloatProperty p:
                    record["value"] = p.Value;
                    break;
                case WzDoubleProperty p:
                    record["value"] = p.Value;
                    break;
                case WzVectorProperty p:
                    record["x"] = p.X.Value;
                    record["y"] = p.Y.Value;
                    break;
                case WzUOLProperty p:
                    record["value"] = p.Value ?? "";
                    record["uol_resolved"] = SafeResolveUol(p);
                    break;
                case WzCanvasProperty p:
                    record["width"] = p.PngProperty?.Width ?? 0;
                    record["height"] = p.PngProperty?.Height ?? 0;
                    break;
                case WzBinaryProperty p:
                    record["length_ms"] = p.Length;
                    record["frequency"] = p.Frequency;
                    break;
                case WzRawDataProperty p:
                    byte[] bytes = SafeGetRawBytes(p);
                    record["bytes"] = bytes?.Length ?? 0;
                    break;
            }
            return record;
        }

        private string SafeResolveUol(WzUOLProperty prop)
        {
            try
            {
                return prop.LinkValue?.FullPath ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static byte[] SafeGetRawBytes(WzRawDataProperty prop)
        {
            try
            {
                return prop.GetBytes(false);
            }
            catch
            {
                return null;
            }
        }

        private void TryExportAsset(string category, string imageRelativePath, WzImageProperty prop, string propPath, Dictionary<string, object> record)
        {
            try
            {
                if (prop is WzCanvasProperty canvas)
                {
                    string assetPath = Path.Combine(_assetRoot, SafeRelativePath(imageRelativePath), SafeRelativePath(propPath) + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? _assetRoot);
                    using var bitmap = canvas.GetLinkedWzCanvasBitmap();
                    bitmap.Save(assetPath, ImageFormat.Png);
                    record["asset_path"] = Path.GetRelativePath(_options.OutputRoot, assetPath).Replace('\\', '/');
                    Write(_resources, AssetRecord("canvas", category, imageRelativePath, propPath, assetPath));
                    _summary.CanvasAssets++;
                }
                else if (prop is WzBinaryProperty sound)
                {
                    string assetPath = Path.Combine(_assetRoot, SafeRelativePath(imageRelativePath), SafeRelativePath(propPath) + ".mp3");
                    Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? _assetRoot);
                    sound.SaveToFile(assetPath);
                    record["asset_path"] = Path.GetRelativePath(_options.OutputRoot, assetPath).Replace('\\', '/');
                    Write(_resources, AssetRecord("sound", category, imageRelativePath, propPath, assetPath));
                    _summary.SoundAssets++;
                }
                else if (prop is WzRawDataProperty raw)
                {
                    byte[] bytes = SafeGetRawBytes(raw);
                    if (bytes != null)
                    {
                        string assetPath = Path.Combine(_assetRoot, SafeRelativePath(imageRelativePath), SafeRelativePath(propPath) + ".bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? _assetRoot);
                        File.WriteAllBytes(assetPath, bytes);
                        record["asset_path"] = Path.GetRelativePath(_options.OutputRoot, assetPath).Replace('\\', '/');
                        Write(_resources, AssetRecord("raw", category, imageRelativePath, propPath, assetPath));
                        _summary.RawAssets++;
                    }
                }
            }
            catch (Exception ex)
            {
                record["asset_error"] = ex.GetType().Name + ":" + ex.Message;
            }
        }

        private Dictionary<string, object> AssetRecord(string kind, string category, string imageRelativePath, string propPath, string assetPath)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["category"] = category,
                ["image_relative_path"] = imageRelativePath,
                ["property_path"] = propPath,
                ["asset_path"] = Path.GetRelativePath(_options.OutputRoot, assetPath).Replace('\\', '/'),
                ["sha256"] = Sha256File(assetPath),
                ["bytes"] = new FileInfo(assetPath).Length,
                ["status"] = "ok",
            };
        }

        private void ExportMapImage(string relativePath, WzImage image)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(relativePath), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
            {
                return;
            }

            var miniMap = image["miniMap"] as WzImageProperty;
            int centerX = GetInt(miniMap, "centerX");
            int centerY = GetInt(miniMap, "centerY");
            int mag = GetInt(miniMap, "mag", 4);
            int width = GetInt(miniMap, "width");
            int height = GetInt(miniMap, "height");
            string miniMapPath = "";
            if (miniMap?["canvas"] is WzCanvasProperty canvas)
            {
                string outPath = Path.Combine(_options.OutputRoot, "templates", "maps", mapId.ToString(CultureInfo.InvariantCulture) + ".png");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? _options.OutputRoot);
                    using var bitmap = canvas.GetLinkedWzCanvasBitmap();
                    bitmap.Save(outPath, ImageFormat.Png);
                    miniMapPath = Path.GetRelativePath(_options.OutputRoot, outPath).Replace('\\', '/');
                    if (width == 0) width = bitmap.Width;
                    if (height == 0) height = bitmap.Height;
                }
                catch
                {
                    string assetPath = Path.Combine(_assetRoot, SafeRelativePath(relativePath), SafeRelativePath("miniMap/canvas") + ".png");
                    if (File.Exists(assetPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? _options.OutputRoot);
                        File.Copy(assetPath, outPath, true);
                        miniMapPath = Path.GetRelativePath(_options.OutputRoot, outPath).Replace('\\', '/');
                    }
                    else
                    {
                        miniMapPath = "";
                    }
                }
            }

            Write(_maps, new Dictionary<string, object>
            {
                ["map_id"] = mapId,
                ["relative_path"] = relativePath,
                ["source"] = "maplelib_img",
                ["center_x"] = centerX,
                ["center_y"] = centerY,
                ["mag"] = mag,
                ["minimap_width"] = width,
                ["minimap_height"] = height,
                ["minimap_path"] = miniMapPath,
                ["has_foothold"] = image["foothold"] != null,
                ["has_life"] = image["life"] != null,
            });
            _summary.Maps++;

            ExportFootholds(mapId, image["foothold"] as WzImageProperty);
            ExportPortals(mapId, image["portal"] as WzImageProperty);
            ExportLadderRopes(mapId, image["ladderRope"] as WzImageProperty);
            ExportSpawns(mapId, image["life"] as WzImageProperty);
            ExportLayers(mapId, image);
        }

        private void ExportFootholds(int mapId, WzImageProperty foothold)
        {
            if (foothold?.WzProperties == null) return;
            foreach (WzImageProperty layer in foothold.WzProperties)
            {
                foreach (WzImageProperty platform in layer.WzProperties ?? Enumerable.Empty<WzImageProperty>())
                {
                    foreach (WzImageProperty fh in platform.WzProperties ?? Enumerable.Empty<WzImageProperty>())
                    {
                        Write(_footholds, new Dictionary<string, object>
                        {
                            ["map_id"] = mapId,
                            ["id"] = ParseInt(fh.Name),
                            ["x1"] = GetInt(fh, "x1"),
                            ["y1"] = GetInt(fh, "y1"),
                            ["x2"] = GetInt(fh, "x2"),
                            ["y2"] = GetInt(fh, "y2"),
                            ["prev_fh"] = GetInt(fh, "prev"),
                            ["next_fh"] = GetInt(fh, "next"),
                            ["cant_through"] = BoolInt(fh, "cantThrough"),
                            ["forbid_fall_down"] = BoolInt(fh, "forbidFallDown"),
                            ["piece"] = NullableInt(fh, "piece"),
                            ["force"] = NullableInt(fh, "force"),
                            ["layer"] = ParseInt(layer.Name),
                            ["platform"] = ParseInt(platform.Name),
                            ["source"] = "maplelib_img",
                        });
                        _summary.Footholds++;
                    }
                }
            }
        }

        private void ExportPortals(int mapId, WzImageProperty portal)
        {
            if (portal?.WzProperties == null) return;
            foreach (WzImageProperty p in portal.WzProperties)
            {
                Write(_portals, new Dictionary<string, object>
                {
                    ["map_id"] = mapId,
                    ["id"] = ParseInt(p.Name),
                    ["portal_name"] = GetString(p, "pn"),
                    ["target_map_id"] = GetInt(p, "tm", 999999999),
                    ["target_node"] = GetString(p, "tn"),
                    ["x"] = GetInt(p, "x"),
                    ["y"] = GetInt(p, "y"),
                    ["portal_type"] = GetInt(p, "pt"),
                    ["source"] = "maplelib_img",
                });
                _summary.Portals++;
            }
        }

        private void ExportLadderRopes(int mapId, WzImageProperty ladderRope)
        {
            if (ladderRope?.WzProperties == null) return;
            foreach (WzImageProperty lr in ladderRope.WzProperties)
            {
                Write(_ladderRopes, new Dictionary<string, object>
                {
                    ["map_id"] = mapId,
                    ["id"] = ParseInt(lr.Name),
                    ["l"] = GetInt(lr, "l"),
                    ["uf"] = GetInt(lr, "uf"),
                    ["x"] = GetInt(lr, "x"),
                    ["y1"] = GetInt(lr, "y1"),
                    ["y2"] = GetInt(lr, "y2"),
                    ["source"] = "maplelib_img",
                });
                _summary.LadderRopes++;
            }
        }

        private void ExportSpawns(int mapId, WzImageProperty life)
        {
            if (life?.WzProperties == null) return;
            foreach (WzImageProperty item in life.WzProperties)
            {
                string type = GetString(item, "type");
                if (!string.Equals(type, "m", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int x = GetInt(item, "x");
                int y = GetInt(item, "y");
                Write(_spawns, new Dictionary<string, object>
                {
                    ["map_id"] = mapId,
                    ["id"] = ParseInt(item.Name),
                    ["mob_id"] = ParseInt(GetString(item, "id")),
                    ["fh"] = GetInt(item, "fh"),
                    ["x"] = x,
                    ["cy"] = GetInt(item, "cy", y),
                    ["rx0"] = GetInt(item, "rx0", x - 50),
                    ["rx1"] = GetInt(item, "rx1", x + 50),
                    ["source"] = "maplelib_img",
                });
                _summary.Spawns++;
            }
        }

        private void ExportLayers(int mapId, WzImage image)
        {
            foreach (WzImageProperty top in image.WzProperties)
            {
                if (top.WzProperties == null)
                {
                    continue;
                }
                bool isNumericLayer = int.TryParse(top.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerNo);
                bool isBack = string.Equals(top.Name, "back", StringComparison.OrdinalIgnoreCase);
                if (!isNumericLayer && !isBack)
                {
                    continue;
                }
                foreach (WzImageProperty section in top.WzProperties)
                {
                    if (section.WzProperties == null)
                    {
                        continue;
                    }
                    foreach (WzImageProperty entry in section.WzProperties)
                    {
                        Write(_layers, new Dictionary<string, object>
                        {
                            ["map_id"] = mapId,
                            ["layer"] = isNumericLayer ? layerNo : -1,
                            ["section"] = section.Name,
                            ["entry_id"] = entry.Name,
                            ["x"] = GetInt(entry, "x"),
                            ["y"] = GetInt(entry, "y"),
                            ["z"] = GetInt(entry, "z"),
                            ["name"] = GetString(entry, "name"),
                            ["o_s"] = GetString(entry, "oS"),
                            ["t_s"] = GetString(entry, "tS"),
                            ["b_s"] = GetString(entry, "bS"),
                            ["path"] = top.Name + "/" + section.Name + "/" + entry.Name,
                            ["source"] = "maplelib_img",
                        });
                    }
                }
            }
        }

        private void ExportStringMap(WzImage image)
        {
            foreach (WzImageProperty root in image.WzProperties)
            {
                ExportStringMapRecursive(root, root.Name);
            }
        }

        private void ExportStringMapRecursive(WzImageProperty node, string path)
        {
            if (int.TryParse(node.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
            {
                string street = GetString(node, "streetName");
                string name = GetString(node, "mapName");
                if (!string.IsNullOrWhiteSpace(street) || !string.IsNullOrWhiteSpace(name))
                {
                    Write(_strings, new Dictionary<string, object>
                    {
                        ["map_id"] = mapId,
                        ["street_name"] = street,
                        ["map_name"] = name,
                        ["path"] = path,
                        ["source"] = "maplelib_img",
                    });
                }
            }
            if (node.WzProperties == null) return;
            foreach (WzImageProperty child in node.WzProperties)
            {
                ExportStringMapRecursive(child, path + "/" + child.Name);
            }
        }

        private void AuditXml(string relativePath, string imgPath)
        {
            string xmlPath = imgPath + ".xml";
            if (!File.Exists(xmlPath))
            {
                Write(_audit, new Dictionary<string, object>
                {
                    ["relative_path"] = relativePath,
                    ["status"] = "xml_missing",
                    ["source"] = "maplelib_img",
                });
                return;
            }
            Write(_audit, new Dictionary<string, object>
            {
                ["relative_path"] = relativePath,
                ["xml_path"] = xmlPath,
                ["status"] = "xml_present",
                ["source"] = "maplelib_img",
            });
        }

        private static int GetInt(WzImageProperty parent, string name, int defaultValue = 0)
        {
            if (parent == null) return defaultValue;
            try
            {
                return parent[name] switch
                {
                    WzIntProperty p => p.Value,
                    WzShortProperty p => p.Value,
                    WzLongProperty p => (int)p.Value,
                    WzStringProperty p when int.TryParse(p.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) => value,
                    _ => defaultValue,
                };
            }
            catch
            {
                return defaultValue;
            }
        }

        private static int? NullableInt(WzImageProperty parent, string name)
        {
            if (parent == null || parent[name] == null) return null;
            return GetInt(parent, name);
        }

        private static string GetString(WzImageProperty parent, string name)
        {
            if (parent == null) return "";
            try
            {
                return parent[name] switch
                {
                    WzStringProperty p => p.Value ?? "",
                    WzIntProperty p => p.Value.ToString(CultureInfo.InvariantCulture),
                    WzShortProperty p => p.Value.ToString(CultureInfo.InvariantCulture),
                    WzLongProperty p => p.Value.ToString(CultureInfo.InvariantCulture),
                    _ => "",
                };
            }
            catch
            {
                return "";
            }
        }

        private static int BoolInt(WzImageProperty parent, string name)
        {
            return GetInt(parent, name) != 0 ? 1 : 0;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
        }

        private static string SafeRelativePath(string value)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToArray();
            var sb = new StringBuilder(value.Length);
            foreach (char c in value.Replace('\\', '/'))
            {
                sb.Append(c == '/' ? Path.DirectorySeparatorChar : invalid.Contains(c) ? '_' : c);
            }
            return sb.ToString();
        }

        private static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static void Write(StreamWriter writer, object record)
        {
            writer.WriteLine(JsonSerializer.Serialize(record, WzImgExportCliRunnerJson.Options));
        }
    }

    internal static class WzImgExportCliRunnerJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
        };
    }
}
