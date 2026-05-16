using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HaCreator.MapSimulator.Core;

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

                var verifier = new PhysicsReachabilityVerifier(request);
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

    internal sealed class PhysicsReachabilityVerifier
    {
        private readonly ReachabilityRequest _request;
        private readonly Dictionary<int, FhSpec> _nodes;

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
            return ToEdgeResult(edge, best, "jump");
        }

        private EdgeVerificationResult VerifyEdgeDrop(EdgeSpec edge, FhSpec src, FhSpec dst)
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
            return ToEdgeResult(edge, best, "edge_drop");
        }

        private EdgeVerificationResult VerifyDownJump(EdgeSpec edge, FhSpec src, FhSpec dst)
        {
            if (src.CantThrough)
            {
                return Fail(edge, "blocked_by_cant_through", "high");
            }
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
            return ToEdgeResult(edge, best, "down_jump");
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
