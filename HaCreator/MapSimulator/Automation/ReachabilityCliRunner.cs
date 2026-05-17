using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HaCreator.MapEditor;
using HaCreator.MapEditor.Instance.Shapes;
using HaCreator.MapSimulator.Character;
using HaCreator.MapSimulator.Core;
using Microsoft.Xna.Framework;
using MapleBool = MapleLib.WzLib.WzStructure.MapleBool;
using ItemTypes = MapleLib.WzLib.WzStructure.Data.ItemTypes;

namespace HaCreator.MapSimulator.Automation
{
    internal static class ReachabilityCliRunner
    {
        private const string ModeArg = "--mapsim-reachability";

        internal static bool IsReachabilityMode(string[] args)
        {
            return args != null && args.Any(a => string.Equals(a, ModeArg, StringComparison.OrdinalIgnoreCase));
        }

        internal static int Run(string[] args)
        {
            try
            {
                if (!TryParseArgs(args, out string inputPath, out string outputPath, out string parseError))
                {
                    Console.Error.WriteLine(parseError);
                    PrintUsage();
                    return 2;
                }

                string raw = File.ReadAllText(inputPath, Encoding.UTF8);
                var request = JsonSerializer.Deserialize<ReachabilityRequest>(raw, JsonOptions());
                if (request == null)
                {
                    Console.Error.WriteLine("reachability input 为空或格式非法。");
                    return 2;
                }

                IReachabilityVerifier verifier = string.Equals(request.Engine, "lightweight", StringComparison.OrdinalIgnoreCase)
                    ? new PhysicsReachabilityVerifier(request)
                    : new PlayerCharacterReachabilityVerifier(request);
                var result = verifier.VerifyAll();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
                File.WriteAllText(outputPath, JsonSerializer.Serialize(result, JsonOptions()), Encoding.UTF8);
                Console.WriteLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "[MapSimulator物理] map={0} rows={1} success={2} stable={3}",
                        result.MapId,
                        result.Results.Count,
                        result.Results.Count(r => r.Success),
                        result.Results.Count(r => r.Stable)));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[MapSimulator物理] 验证失败: " + ex);
                return 1;
            }
        }

        private static bool TryParseArgs(string[] args, out string inputPath, out string outputPath, out string error)
        {
            inputPath = "";
            outputPath = "";
            error = "";
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals(ModeArg, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = $"{ModeArg} 缺少输入 JSON 路径。";
                        return false;
                    }
                    inputPath = args[++i];
                    continue;
                }
                if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "--output 缺少输出 JSON 路径。";
                        return false;
                    }
                    outputPath = args[++i];
                }
            }

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                error = $"缺少 {ModeArg} <input.json> 参数。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = Path.ChangeExtension(inputPath, ".result.json");
            }
            return true;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HaCreator --mapsim-reachability <input.json> --output <result.json>");
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
            };
        }
    }

    internal interface IReachabilityVerifier
    {
        ReachabilityResponse VerifyAll();
    }

    internal sealed class PlayerCharacterReachabilityVerifier : IReachabilityVerifier
    {
        private readonly ReachabilityRequest _request;
        private readonly Dictionary<int, FhSpec> _nodes;
        private readonly Dictionary<int, FootholdLine> _footholds;
        private readonly PhysicsReachabilityVerifier _fallback;

        public PlayerCharacterReachabilityVerifier(ReachabilityRequest request)
        {
            _request = request;
            _nodes = request.Nodes.ToDictionary(n => n.FhId, n => n);
            _footholds = BuildFootholds(request.Nodes);
            _fallback = new PhysicsReachabilityVerifier(request);
        }

        public ReachabilityResponse VerifyAll()
        {
            var response = new ReachabilityResponse { MapId = _request.MapId };
            foreach (var edge in _request.Edges)
            {
                response.Results.Add(VerifyEdge(edge));
            }
            return response;
        }

        private EdgeVerificationResult VerifyEdge(EdgeSpec edge)
        {
            if (!_nodes.TryGetValue(edge.SrcFh, out var src) || !_nodes.TryGetValue(edge.DstFh, out var dst))
            {
                return Fail(edge, "missing_foothold", "unknown");
            }

            string mode = NormalizeMode(edge.MovementMode);
            if (mode == "walk" || mode == "fragment_bridge" || mode == "step_bridge")
            {
                var simple = SimulateWalk(edge, src, dst);
                return ToEdgeResult(edge, simple, mode);
            }
            if (mode == "jump")
            {
                var jump = SimulateJump(edge, src, dst);
                return ToEdgeResult(edge, jump, mode);
            }
            if (mode == "edge_drop" || mode == "edge_jump_drop")
            {
                var drop = SimulateEdgeDrop(edge, src, dst);
                return ToEdgeResult(edge, drop, "edge_drop");
            }
            if (mode == "down_jump")
            {
                if (src.CantThrough)
                {
                    return Fail(edge, "blocked_by_cant_through", "high");
                }
                var downJump = SimulateDownJump(edge, src, dst);
                return ToEdgeResult(edge, downJump, "down_jump");
            }

            return _fallback.VerifyEdgePublic(edge);
        }

        private CandidateResult SimulateWalk(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            double targetX = edge.TargetX ?? dst.Cx;
            double direction = Math.Sign(targetX - src.Cx);
            if (Math.Abs(direction) < 0.001)
            {
                direction = DstCenterX(src, dst) >= SrcCenterX(src) ? 1.0 : -1.0;
            }
            var candidates = BuildStartCandidates(edge, src, direction, null);
            CandidateResult best = CandidateResult.Fail("no_candidate");
            foreach (double startX in candidates)
            {
                var script = new InputScript
                {
                    Direction = direction,
                    JumpPressMs = 0,
                    DirectionHoldMs = Math.Min(_request.MaxSimMs, Math.Max(500.0, Math.Abs(targetX - startX) * 12.0)),
                    StopAfterLanding = true,
                };
                var result = SimulatePlayer(edge, src, startX, script);
                result.PreAlignX = startX;
                best = CandidateResult.Better(best, result);
                if (result.Success && result.Stable)
                {
                    break;
                }
            }
            return best;
        }

        private CandidateResult SimulateJump(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            double direction = DstCenterX(src, dst) >= SrcCenterX(src) ? 1.0 : -1.0;
            var candidates = BuildStartCandidates(edge, src, direction, edge.TargetX);
            CandidateResult best = CandidateResult.Fail("no_candidate");
            foreach (double startX in candidates)
            {
                foreach (double holdMs in new[] { 180.0, 260.0, 360.0, 520.0, 700.0 })
                {
                    var script = new InputScript
                    {
                        Direction = direction,
                        JumpPressMs = 80.0,
                        DirectionHoldMs = holdMs,
                        StopAfterLanding = true,
                    };
                    var result = SimulatePlayer(edge, src, startX, script);
                    result.PreAlignX = startX;
                    best = CandidateResult.Better(best, result);
                    if (result.Success && result.Stable)
                    {
                        return best;
                    }
                }
            }
            return best.Success ? best : CandidateResult.Better(best, _fallback.VerifyCandidate(edge, "jump"));
        }

        private CandidateResult SimulateEdgeDrop(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            if (src.ForbidFallDown)
            {
                return CandidateResult.Fail("blocked_by_forbid_fall_down");
            }
            double direction = DstCenterX(src, dst) >= SrcCenterX(src) ? 1.0 : -1.0;
            var candidates = BuildStartCandidates(edge, src, direction, edge.TargetX);
            CandidateResult best = CandidateResult.Fail("no_candidate");
            foreach (double startX in candidates)
            {
                var script = new InputScript
                {
                    Direction = direction,
                    JumpPressMs = 0,
                    DirectionHoldMs = Math.Min(_request.MaxSimMs, 1200.0),
                    StopAfterLanding = true,
                };
                var result = SimulatePlayer(edge, src, startX, script);
                result.PreAlignX = startX;
                best = CandidateResult.Better(best, result);
                if (result.Success && result.Stable)
                {
                    break;
                }
            }
            return best.Success ? best : CandidateResult.Better(best, _fallback.VerifyCandidate(edge, "edge_drop"));
        }

        private CandidateResult SimulateDownJump(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            var candidates = BuildStartCandidates(edge, src, 0.0, edge.TargetX);
            CandidateResult best = CandidateResult.Fail("no_candidate");
            foreach (double startX in candidates)
            {
                var script = new InputScript
                {
                    Direction = 0.0,
                    Down = true,
                    JumpPressMs = 90.0,
                    DirectionHoldMs = 0.0,
                    StopAfterLanding = true,
                };
                var result = SimulatePlayer(edge, src, startX, script);
                result.PreAlignX = startX;
                best = CandidateResult.Better(best, result);
                if (result.Success && result.Stable)
                {
                    break;
                }
            }
            return best.Success ? best : CandidateResult.Better(best, _fallback.VerifyCandidate(edge, "down_jump"));
        }

        private CandidateResult SimulatePlayer(EdgeSpec edge, FhSpec src, double startX, InputScript script)
        {
            if (!_footholds.TryGetValue(src.FhId, out var srcFh))
            {
                return CandidateResult.Fail("missing_source_foothold");
            }

            var build = new CharacterBuild
            {
                Speed = (float)Math.Max(10.0, _request.WalkSpeedPercent),
                JumpPower = (float)Math.Max(10.0, _request.JumpPowerPercent),
            };
            var player = new PlayerCharacter(null, null, build);
            player.SetFootholdLookup(FindFoothold);
            double y = src.YAt(startX);
            player.SetPosition((float)startX, (float)y);
            player.Physics.CurrentFoothold = srcFh;
            player.Physics.FallStartFoothold = null;
            player.Physics.VelocityX = 0;
            player.Physics.VelocityY = 0;
            player.FacingRight = script.Direction >= 0;

            double maxMs = Math.Max(400.0, _request.MaxSimMs);
            double dtMs = Math.Max(5.0, _request.DtMs);
            int stableTicks = 0;
            int firstTargetMs = -1;
            double lastMoveX = player.X;
            double lastMoveY = player.Y;
            int stillTicks = 0;

            for (double elapsed = 0.0; elapsed <= maxMs; elapsed += dtMs)
            {
                bool holdDir = script.Direction != 0.0 && elapsed <= script.DirectionHoldMs;
                bool left = holdDir && script.Direction < 0.0;
                bool right = holdDir && script.Direction > 0.0;
                bool jump = elapsed <= script.JumpPressMs;
                bool down = script.Down && elapsed <= script.JumpPressMs;

                if (script.StopAfterLanding && firstTargetMs >= 0)
                {
                    left = false;
                    right = false;
                    jump = false;
                    down = false;
                }

                player.SetInput(left, right, false, down, jump, false, false);
                player.Update((int)Math.Round(elapsed), (float)(dtMs / 1000.0));

                int? currentFh = player.Physics.CurrentFoothold?.num;
                if (currentFh == edge.DstFh)
                {
                    if (firstTargetMs < 0)
                    {
                        firstTargetMs = (int)Math.Round(elapsed);
                    }
                    stableTicks++;
                    if (stableTicks >= StableFrameCount())
                    {
                        player.ClearInput();
                        return new CandidateResult
                        {
                            Success = true,
                            Stable = true,
                            LandingFh = edge.DstFh,
                            EndX = player.X,
                            EndY = player.Y,
                            ElapsedMs = elapsed,
                            ErrorX = Math.Abs(player.X - Math.Clamp(player.X, _nodes[edge.DstFh].MinX, _nodes[edge.DstFh].MaxX)),
                            ErrorY = Math.Abs(player.Y - _nodes[edge.DstFh].YAt(Math.Clamp(player.X, _nodes[edge.DstFh].MinX, _nodes[edge.DstFh].MaxX))),
                            Reason = "player_character_landed_on_target",
                        };
                    }
                }
                else
                {
                    stableTicks = 0;
                }

                if (Math.Abs(player.X - lastMoveX) + Math.Abs(player.Y - lastMoveY) < 0.02)
                {
                    stillTicks++;
                }
                else
                {
                    stillTicks = 0;
                    lastMoveX = player.X;
                    lastMoveY = player.Y;
                }
                if (stillTicks > 90 && elapsed > 600.0)
                {
                    return CandidateResult.Fail("player_character_stalled", player.X, player.Y, elapsed / 1000.0);
                }
            }

            int? landingFh = player.Physics.CurrentFoothold?.num;
            return new CandidateResult
            {
                Success = false,
                Stable = false,
                LandingFh = landingFh,
                EndX = player.X,
                EndY = player.Y,
                ElapsedMs = maxMs,
                ErrorX = landingFh == edge.DstFh ? 0 : null,
                ErrorY = landingFh == edge.DstFh ? 0 : null,
                Reason = landingFh.HasValue ? "player_character_landed_on_other_fh" : "player_character_timeout_airborne",
            };
        }

        private FootholdLine FindFoothold(float x, float y, float searchRange)
        {
            FootholdLine best = null;
            double bestDelta = double.MaxValue;
            foreach (var pair in _footholds)
            {
                var spec = _nodes[pair.Key];
                if (spec.IsWall || x < spec.MinX - 2.0 || x > spec.MaxX + 2.0)
                {
                    continue;
                }
                double fhY = spec.YAt(Math.Clamp(x, spec.MinX, spec.MaxX));
                double delta = fhY - y;
                if (delta >= -10.0 && delta <= Math.Max(20.0, searchRange) && delta < bestDelta)
                {
                    best = pair.Value;
                    bestDelta = delta;
                }
            }
            return best;
        }

        private static Dictionary<int, FootholdLine> BuildFootholds(IEnumerable<FhSpec> nodes)
        {
            var board = new Board(new Point(4000, 4000), new Point(0, 0), null, false, null, ItemTypes.None, ItemTypes.None);
            var result = new Dictionary<int, FootholdLine>();
            foreach (var node in nodes)
            {
                var first = new FootholdAnchor(board, node.X1, node.Y1, node.Layer, node.Platform, false);
                var second = new FootholdAnchor(board, node.X2, node.Y2, node.Layer, node.Platform, false);
                var line = new FootholdLine(
                    board,
                    first,
                    second,
                    node.ForbidFallDown ? MapleBool.True : MapleBool.False,
                    node.CantThrough ? MapleBool.True : MapleBool.False,
                    node.Piece,
                    node.Force)
                {
                    num = node.FhId,
                    prev = node.PrevFh ?? 0,
                    next = node.NextFh ?? 0,
                };
                result[node.FhId] = line;
            }
            foreach (var line in result.Values)
            {
                if (line.prev != 0 && result.TryGetValue(line.prev, out var prev))
                {
                    line.prevOverride = prev;
                }
                if (line.next != 0 && result.TryGetValue(line.next, out var next))
                {
                    line.nextOverride = next;
                }
                board.BoardItems.FootholdLines.Add(line);
            }
            return result;
        }

        private EdgeVerificationResult ToEdgeResult(EdgeSpec edge, CandidateResult result, string mode)
        {
            string risk = result.Success && result.Stable ? RiskFor(edge, result, mode) : "high";
            double holdMs = mode == "jump"
                ? Math.Min(800.0, Math.Max(180.0, result.ElapsedMs * 0.45))
                : mode == "edge_drop"
                    ? Math.Min(900.0, Math.Max(180.0, result.ElapsedMs * 0.65))
                    : 0.0;
            return new EdgeVerificationResult(edge)
            {
                Success = result.Success,
                Stable = result.Stable,
                Risk = risk,
                LandingFh = result.LandingFh,
                Cost = Math.Max(1.0, edge.Cost + (risk == "low" ? 0.0 : risk == "medium" ? 500.0 : 5000.0)),
                Reason = result.Reason,
                Source = "mapsim_physics",
                EndX = result.EndX,
                EndY = result.EndY,
                ElapsedMs = result.ElapsedMs,
                ErrorX = result.ErrorX,
                ErrorY = result.ErrorY,
                PreAlignX = result.PreAlignX,
                Recommended = new RecommendedTiming
                {
                    PreAlignX = result.PreAlignX,
                    PressMs = mode == "down_jump" ? 90.0 : mode == "jump" ? 80.0 : 0.0,
                    HoldMs = holdMs,
                    ReleaseMs = 40.0,
                    LandingWaitMs = LandingWaitFor(mode, result),
                    VerifyTolerance = Math.Max(_request.LandingTolerancePx, mode == "edge_drop" || mode == "down_jump" ? 18.0 : 12.0),
                },
            };
        }

        private double LandingWaitFor(string mode, CandidateResult result)
        {
            if (!result.Success)
            {
                return 250.0;
            }
            if (mode == "edge_drop" || mode == "down_jump")
            {
                return Math.Min(900.0, Math.Max(260.0, result.ElapsedMs * 0.28));
            }
            if (mode == "jump")
            {
                return Math.Min(650.0, Math.Max(160.0, result.ElapsedMs * 0.22));
            }
            return 80.0;
        }

        private string RiskFor(EdgeSpec edge, CandidateResult result, string mode)
        {
            if (!_nodes.TryGetValue(edge.SrcFh, out var src) || !_nodes.TryGetValue(edge.DstFh, out var dst))
            {
                return "unknown";
            }
            double dy = Math.Abs(dst.Cy - src.Cy);
            double dx = Math.Abs(dst.Cx - src.Cx);
            if ((mode == "walk" || mode == "fragment_bridge" || mode == "step_bridge") && result.Success)
            {
                return "low";
            }
            if ((mode == "jump" || mode == "edge_drop" || mode == "down_jump") && dy <= _request.LowRiskMaxDyPx && dx <= _request.LowRiskMaxDxPx)
            {
                return "low";
            }
            return "medium";
        }

        private EdgeVerificationResult Fail(EdgeSpec edge, string reason, string risk)
        {
            return new EdgeVerificationResult(edge)
            {
                Success = false,
                Stable = false,
                Risk = risk,
                LandingFh = null,
                Cost = Math.Max(1.0, edge.Cost + 5000.0),
                Reason = reason,
                Source = "mapsim_physics",
            };
        }

        private List<double> BuildStartCandidates(EdgeSpec edge, FhSpec src, double direction, double? preferred)
        {
            var values = new List<double>();
            if (preferred.HasValue)
            {
                values.Add(Math.Clamp(preferred.Value, src.MinX + 2.0, src.MaxX - 2.0));
            }
            if (direction > 0)
            {
                values.Add(src.MaxX - 3.0);
                values.Add(src.MaxX - 12.0);
                values.Add(src.MaxX - 28.0);
            }
            else if (direction < 0)
            {
                values.Add(src.MinX + 3.0);
                values.Add(src.MinX + 12.0);
                values.Add(src.MinX + 28.0);
            }
            values.Add(SrcCenterX(src));
            return values
                .Select(v => Math.Clamp(v, src.MinX + 1.0, src.MaxX - 1.0))
                .Distinct()
                .ToList();
        }

        private int StableFrameCount() => Math.Max(3, (int)Math.Ceiling(100.0 / Math.Max(5.0, _request.DtMs)));
        private static string NormalizeMode(string mode) => string.Equals((mode ?? "").Trim(), "jump_vertical", StringComparison.OrdinalIgnoreCase) ? "jump" : (mode ?? "").Trim().ToLowerInvariant();
        private static double SrcCenterX(FhSpec fh) => fh.Cx;
        private static double DstCenterX(FhSpec src, FhSpec dst) => dst.Cx;

        private sealed class InputScript
        {
            public double Direction { get; set; }
            public bool Down { get; set; }
            public double JumpPressMs { get; set; }
            public double DirectionHoldMs { get; set; }
            public bool StopAfterLanding { get; set; }
        }
    }

    internal sealed class PhysicsReachabilityVerifier : IReachabilityVerifier
    {
        private readonly ReachabilityRequest _request;
        private readonly Dictionary<int, FhSpec> _nodes;
        public EdgeVerificationResult VerifyEdgePublic(EdgeSpec edge)
        {
            return VerifyEdge(edge);
        }

        public CandidateResult VerifyCandidate(EdgeSpec edge, string mode)
        {
            if (!_nodes.TryGetValue(edge.SrcFh, out var src) || !_nodes.TryGetValue(edge.DstFh, out var dst))
            {
                return CandidateResult.Fail("fallback_missing_foothold");
            }
            if (mode == "jump")
            {
                return VerifyJumpCandidate(edge, src, dst);
            }
            if (mode == "edge_drop")
            {
                return VerifyEdgeDropCandidate(edge, src, dst);
            }
            if (mode == "down_jump")
            {
                return VerifyDownJumpCandidate(edge, src, dst);
            }
            var result = VerifyEdge(edge);
            return new CandidateResult
            {
                Success = result.Success,
                Stable = result.Stable,
                LandingFh = result.LandingFh,
                EndX = result.EndX,
                EndY = result.EndY,
                ElapsedMs = result.ElapsedMs ?? 0.0,
                ErrorX = result.ErrorX,
                ErrorY = result.ErrorY,
                PreAlignX = result.PreAlignX,
                Reason = "fallback_" + result.Reason,
            };
        }

        public PhysicsReachabilityVerifier(ReachabilityRequest request)
        {
            _request = request;
            _nodes = request.Nodes.ToDictionary(n => n.FhId, n => n);
        }

        public ReachabilityResponse VerifyAll()
        {
            var response = new ReachabilityResponse { MapId = _request.MapId };
            foreach (var edge in _request.Edges)
            {
                response.Results.Add(VerifyEdge(edge));
            }
            return response;
        }

        private EdgeVerificationResult VerifyEdge(EdgeSpec edge)
        {
            if (!_nodes.TryGetValue(edge.SrcFh, out var src) || !_nodes.TryGetValue(edge.DstFh, out var dst))
            {
                return Fail(edge, "missing_foothold", "unknown");
            }

            string mode = (edge.MovementMode ?? "").Trim().ToLowerInvariant();
            if (mode == "jump_vertical")
            {
                mode = "jump";
            }

            if (mode == "walk" || mode == "fragment_bridge" || mode == "step_bridge")
            {
                return SimpleSuccess(edge, dst, "simple_reachable", "low", 1.0);
            }

            if (mode == "jump")
            {
                return VerifyJump(edge, src, dst);
            }
            if (mode == "edge_drop" || mode == "edge_jump_drop")
            {
                return VerifyEdgeDrop(edge, src, dst);
            }
            if (mode == "down_jump")
            {
                return VerifyDownJump(edge, src, dst);
            }

            return new EdgeVerificationResult(edge)
            {
                Success = true,
                Stable = true,
                Risk = "medium",
                LandingFh = edge.DstFh,
                Cost = Math.Max(1.0, edge.Cost),
                Reason = "complex_migration_unverified",
                Source = "mapsim_physics",
            };
        }

        private EdgeVerificationResult VerifyJump(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            return ToEdgeResult(edge, VerifyJumpCandidate(edge, src, dst), "jump");
        }

        private CandidateResult VerifyJumpCandidate(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            double direction = DstCenterX(src, dst) >= SrcCenterX(src) ? 1.0 : -1.0;
            var candidates = BuildStartCandidates(edge, src, direction, edge.TargetX);
            CandidateResult best = CandidateResult.Fail("no_candidate");
            foreach (double startX in candidates)
            {
                double startY = src.YAt(startX);
                double jumpPower = Math.Max(0.5, _request.JumpPowerPercent / 100.0);
                double vx = direction * Math.Max(100.0, _request.WalkSpeedPercent);
                double vy = -PhysicsConstants.Instance.JumpSpeed * jumpPower;
                CandidateResult result = SimulateAir(edge, startX, startY, vx, vy, skipSrcUntilBelow: false);
                best = CandidateResult.Better(best, result);
                if (result.Success && result.Stable)
                {
                    break;
                }
            }
            best.Reason = "fallback_" + best.Reason;
            return best;
        }

        private EdgeVerificationResult VerifyEdgeDrop(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            return ToEdgeResult(edge, VerifyEdgeDropCandidate(edge, src, dst), "edge_drop");
        }

        private CandidateResult VerifyEdgeDropCandidate(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            double direction = DstCenterX(src, dst) >= SrcCenterX(src) ? 1.0 : -1.0;
            var candidates = BuildStartCandidates(edge, src, direction, edge.TargetX);
            CandidateResult best = CandidateResult.Fail("no_candidate");
            foreach (double startX in candidates)
            {
                double edgeX = direction > 0 ? src.MaxX + 1.0 : src.MinX - 1.0;
                double startY = src.YAt(Math.Clamp(startX, src.MinX, src.MaxX));
                double vx = direction * Math.Max(80.0, _request.WalkSpeedPercent);
                CandidateResult result = SimulateAir(edge, edgeX, startY, vx, 0.0, skipSrcUntilBelow: true);
                result.PreAlignX = startX;
                best = CandidateResult.Better(best, result);
                if (result.Success && result.Stable)
                {
                    break;
                }
            }
            best.Reason = "fallback_" + best.Reason;
            return best;
        }

        private EdgeVerificationResult VerifyDownJump(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            if (src.CantThrough)
            {
                return Fail(edge, "blocked_by_cant_through", "high");
            }
            return ToEdgeResult(edge, VerifyDownJumpCandidate(edge, src, dst), "down_jump");
        }

        private CandidateResult VerifyDownJumpCandidate(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            var candidates = BuildStartCandidates(edge, src, 0.0, edge.TargetX);
            CandidateResult best = CandidateResult.Fail("no_candidate");
            foreach (double startX in candidates)
            {
                double startY = src.YAt(startX);
                CandidateResult result = SimulateAir(edge, startX, startY + 2.0, 0.0, 50.0, skipSrcUntilBelow: true);
                result.PreAlignX = startX;
                best = CandidateResult.Better(best, result);
                if (result.Success && result.Stable)
                {
                    break;
                }
            }
            best.Reason = "fallback_" + best.Reason;
            return best;
        }

        private CandidateResult SimulateAir(EdgeSpec edge, double startX, double startY, double vx, double vy, bool skipSrcUntilBelow)
        {
            double x = startX;
            double y = startY;
            double lastX = x;
            double lastY = y;
            double maxT = Math.Max(0.4, _request.MaxSimMs / 1000.0);
            double dt = Math.Max(0.005, _request.DtMs / 1000.0);
            int stillTicks = 0;
            double belowSrcY = _nodes.TryGetValue(edge.SrcFh, out var src) ? src.YAt(Math.Clamp(x, src.MinX, src.MaxX)) + 30.0 : startY + 30.0;

            for (double t = 0.0; t <= maxT; t += dt)
            {
                vy += PhysicsConstants.Instance.GravityAcc * dt;
                if (vy > PhysicsConstants.Instance.FallSpeed)
                {
                    vy = PhysicsConstants.Instance.FallSpeed;
                }
                x += vx * dt;
                y += vy * dt;

                if (Math.Abs(x - lastX) + Math.Abs(y - lastY) < 0.05)
                {
                    stillTicks++;
                }
                else
                {
                    stillTicks = 0;
                    lastX = x;
                    lastY = y;
                }
                if (stillTicks > 20)
                {
                    return CandidateResult.Fail("stalled", x, y, t);
                }

                foreach (var fh in _nodes.Values)
                {
                    if (fh.IsWall || x < fh.MinX || x > fh.MaxX)
                    {
                        continue;
                    }
                    if (skipSrcUntilBelow && fh.FhId == edge.SrcFh && y < belowSrcY)
                    {
                        continue;
                    }
                    double fhY = fh.YAt(x);
                    if (vy >= 0 && Math.Abs(y - fhY) <= _request.LandingTolerancePx)
                    {
                        bool success = fh.FhId == edge.DstFh;
                        double xErr = Math.Abs(x - Math.Clamp(x, fh.MinX, fh.MaxX));
                        double yErr = Math.Abs(y - fhY);
                        return new CandidateResult
                        {
                            Success = success,
                            Stable = success && Math.Abs(vy) <= PhysicsConstants.Instance.FallSpeed,
                            LandingFh = fh.FhId,
                            EndX = x,
                            EndY = fhY,
                            ElapsedMs = t * 1000.0,
                            ErrorX = xErr,
                            ErrorY = yErr,
                            Reason = success ? "landed_on_target" : "landed_on_other_fh",
                        };
                    }
                }
            }

            return CandidateResult.Fail("timeout", x, y, maxT);
        }

        private List<double> BuildStartCandidates(EdgeSpec edge, FhSpec src, double direction, double? preferred)
        {
            var values = new List<double>();
            if (preferred.HasValue)
            {
                values.Add(Math.Clamp(preferred.Value, src.MinX + 2.0, src.MaxX - 2.0));
            }
            if (direction > 0)
            {
                values.Add(src.MaxX - 2.0);
                values.Add(src.MaxX - 10.0);
            }
            else if (direction < 0)
            {
                values.Add(src.MinX + 2.0);
                values.Add(src.MinX + 10.0);
            }
            values.Add(SrcCenterX(src));
            return values
                .Select(v => Math.Clamp(v, src.MinX + 1.0, src.MaxX - 1.0))
                .Distinct()
                .ToList();
        }

        private EdgeVerificationResult ToEdgeResult(EdgeSpec edge, CandidateResult result, string mode)
        {
            string risk = result.Success && result.Stable ? RiskFor(edge, result, mode) : "high";
            return new EdgeVerificationResult(edge)
            {
                Success = result.Success,
                Stable = result.Stable,
                Risk = risk,
                LandingFh = result.LandingFh,
                Cost = Math.Max(1.0, edge.Cost + (risk == "low" ? 0.0 : risk == "medium" ? 500.0 : 5000.0)),
                Reason = result.Reason,
                Source = "mapsim_physics",
                EndX = result.EndX,
                EndY = result.EndY,
                ElapsedMs = result.ElapsedMs,
                ErrorX = result.ErrorX,
                ErrorY = result.ErrorY,
                PreAlignX = result.PreAlignX,
                Recommended = new RecommendedTiming
                {
                    PreAlignX = result.PreAlignX,
                    PressMs = mode == "down_jump" ? 70 : 0,
                    HoldMs = mode == "edge_drop" ? Math.Min(650, Math.Max(120, result.ElapsedMs * 0.35)) : 0,
                    ReleaseMs = 30,
                    LandingWaitMs = Math.Min(500, Math.Max(80, result.ElapsedMs * 0.2)),
                    VerifyTolerance = _request.LandingTolerancePx,
                },
            };
        }

        private string RiskFor(EdgeSpec edge, CandidateResult result, string mode)
        {
            if (!_nodes.TryGetValue(edge.SrcFh, out var src) || !_nodes.TryGetValue(edge.DstFh, out var dst))
            {
                return "unknown";
            }
            double dy = Math.Abs(dst.Cy - src.Cy);
            double dx = Math.Abs(dst.Cx - src.Cx);
            if (mode == "jump" && dy <= _request.LowRiskMaxDyPx && dx <= _request.LowRiskMaxDxPx)
            {
                return "low";
            }
            if ((mode == "edge_drop" || mode == "down_jump") && dy <= _request.LowRiskMaxDyPx && dx <= _request.LowRiskMaxDxPx)
            {
                return "low";
            }
            return "medium";
        }

        private EdgeVerificationResult SimpleSuccess(EdgeSpec edge, FhSpec dst, string reason, string risk, double costScale)
        {
            return new EdgeVerificationResult(edge)
            {
                Success = true,
                Stable = true,
                Risk = risk,
                LandingFh = dst.FhId,
                Cost = Math.Max(1.0, edge.Cost * costScale),
                Reason = reason,
                Source = "mapsim_physics",
                EndX = dst.Cx,
                EndY = dst.Cy,
                ElapsedMs = 0,
                Recommended = new RecommendedTiming(),
            };
        }

        private EdgeVerificationResult Fail(EdgeSpec edge, string reason, string risk)
        {
            return new EdgeVerificationResult(edge)
            {
                Success = false,
                Stable = false,
                Risk = risk,
                LandingFh = null,
                Cost = Math.Max(1.0, edge.Cost + 5000.0),
                Reason = reason,
                Source = "mapsim_physics",
            };
        }

        private static double SrcCenterX(FhSpec fh) => fh.Cx;
        private static double DstCenterX(FhSpec src, FhSpec dst) => dst.Cx;
    }

    internal sealed class ReachabilityRequest
    {
        public int MapId { get; set; }
        public string Engine { get; set; } = "player_character";
        public double WalkSpeedPercent { get; set; } = 100.0;
        public double JumpPowerPercent { get; set; } = 100.0;
        public double DtMs { get; set; } = 16.6667;
        public double MaxSimMs { get; set; } = 2500.0;
        public double LandingTolerancePx { get; set; } = 12.0;
        public double LowRiskMaxDyPx { get; set; } = 100.0;
        public double LowRiskMaxDxPx { get; set; } = 120.0;
        public List<FhSpec> Nodes { get; set; } = new List<FhSpec>();
        public List<EdgeSpec> Edges { get; set; } = new List<EdgeSpec>();
    }

    internal sealed class FhSpec
    {
        public int FhId { get; set; }
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int X2 { get; set; }
        public int Y2 { get; set; }
        public bool CantThrough { get; set; }
        public bool ForbidFallDown { get; set; }
        public int? PrevFh { get; set; }
        public int? NextFh { get; set; }
        public int? Piece { get; set; }
        public int? Force { get; set; }
        public int Layer { get; set; }
        public int Platform { get; set; }
        public bool IsWall => X1 == X2;
        public double MinX => Math.Min(X1, X2);
        public double MaxX => Math.Max(X1, X2);
        public double Cx => (X1 + X2) / 2.0;
        public double Cy => (Y1 + Y2) / 2.0;

        public double YAt(double x)
        {
            if (X1 == X2)
            {
                return Math.Min(Y1, Y2);
            }
            double t = (x - X1) / (double)(X2 - X1);
            return Y1 + t * (Y2 - Y1);
        }
    }

    internal sealed class EdgeSpec
    {
        public int SrcFh { get; set; }
        public int DstFh { get; set; }
        public string Action { get; set; } = "";
        public string MovementMode { get; set; } = "";
        public double Cost { get; set; }
        public double? TargetX { get; set; }
    }

    internal sealed class ReachabilityResponse
    {
        public int MapId { get; set; }
        public List<EdgeVerificationResult> Results { get; set; } = new List<EdgeVerificationResult>();
    }

    internal sealed class EdgeVerificationResult
    {
        public EdgeVerificationResult()
        {
        }

        public EdgeVerificationResult(EdgeSpec edge)
        {
            SrcFh = edge.SrcFh;
            DstFh = edge.DstFh;
            Action = edge.Action;
            MovementMode = edge.MovementMode;
        }

        public int SrcFh { get; set; }
        public int DstFh { get; set; }
        public string Action { get; set; } = "";
        public string MovementMode { get; set; } = "";
        public bool Success { get; set; }
        public bool Stable { get; set; }
        public string Risk { get; set; } = "unknown";
        public int? LandingFh { get; set; }
        public double Cost { get; set; }
        public string Reason { get; set; } = "";
        public string Source { get; set; } = "mapsim_physics";
        public double? EndX { get; set; }
        public double? EndY { get; set; }
        public double? ElapsedMs { get; set; }
        public double? ErrorX { get; set; }
        public double? ErrorY { get; set; }
        public double? PreAlignX { get; set; }
        public RecommendedTiming Recommended { get; set; } = new RecommendedTiming();
    }

    internal sealed class RecommendedTiming
    {
        public double? PreAlignX { get; set; }
        public double PressMs { get; set; }
        public double HoldMs { get; set; }
        public double ReleaseMs { get; set; }
        public double LandingWaitMs { get; set; }
        public double VerifyTolerance { get; set; }
    }

    internal sealed class CandidateResult
    {
        public bool Success { get; set; }
        public bool Stable { get; set; }
        public int? LandingFh { get; set; }
        public double? EndX { get; set; }
        public double? EndY { get; set; }
        public double ElapsedMs { get; set; }
        public double? ErrorX { get; set; }
        public double? ErrorY { get; set; }
        public double? PreAlignX { get; set; }
        public string Reason { get; set; } = "";

        public static CandidateResult Fail(string reason, double? x = null, double? y = null, double elapsedS = 0.0)
        {
            return new CandidateResult
            {
                Success = false,
                Stable = false,
                EndX = x,
                EndY = y,
                ElapsedMs = elapsedS * 1000.0,
                Reason = reason,
            };
        }

        public static CandidateResult Better(CandidateResult current, CandidateResult next)
        {
            if (next.Success && !current.Success)
            {
                return next;
            }
            if (next.Success == current.Success && next.Stable && !current.Stable)
            {
                return next;
            }
            if (next.Success == current.Success && next.ElapsedMs < current.ElapsedMs)
            {
                return next;
            }
            return current;
        }
    }
}
