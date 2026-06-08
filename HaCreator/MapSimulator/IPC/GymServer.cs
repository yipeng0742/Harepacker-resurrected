using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HaCreator.MapSimulator.IPC
{
    public sealed class GymAction
    {
        public bool Left { get; set; }
        public bool Right { get; set; }
        public bool Up { get; set; }
        public bool Down { get; set; }
        public bool Jump { get; set; }
        public bool Attack { get; set; }
        public bool Pickup { get; set; }
        public bool Reset { get; set; }
        public int SkillSlot { get; set; } = -1;
        public string SkillToken { get; set; } = "";
        public float TargetX { get; set; }
        public float TargetY { get; set; }
    }

    public sealed class GymMobState
    {
        public int MobId { get; set; }
        public int PoolId { get; set; }
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public bool Alive { get; set; }
        public int CurrentHp { get; set; }
        public int MaxHp { get; set; }
        public int FhId { get; set; }
        public bool FacingRight { get; set; }
        public bool CanAttack { get; set; }
        public float DistanceToPlayer { get; set; }
        public float AttackRangePx { get; set; }
    }

    public sealed class GymState
    {
        public int MapId { get; set; }
        public int Tick { get; set; }
        public int Frame { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float VX { get; set; }
        public float VY { get; set; }
        public bool Alive { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Mp { get; set; }
        public int MaxMp { get; set; }
        public bool IsGrounded { get; set; }
        public bool FacingRight { get; set; }
        public int CurrentFhId { get; set; }
        public int FallStartFhId { get; set; }
        public bool IsOnLadder { get; set; }
        public bool IsOnPortal { get; set; }
        public float DistLeftEdge { get; set; }
        public float DistRightEdge { get; set; }
        public float PlatformMinX { get; set; }
        public float PlatformMaxX { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public bool IsDone { get; set; }
        public float NearestLadderX { get; set; }
        public float NearestLadderTop { get; set; }
        public float NearestLadderBottom { get; set; }
        public bool LadderOverlap { get; set; }
        public bool IsOverlappingLadder { get; set; }
        public float NearestPortalX { get; set; }
        public float NearestPortalY { get; set; }
        public bool PortalOverlap { get; set; }
        public bool IsOverlappingPortal { get; set; }
        public bool IsInSwimArea { get; set; }
        public bool IsJumpingDown { get; set; }
        public bool CurrentFhCantThrough { get; set; }
        public string PhysicsMoveAction { get; set; } = "";
        public string PhysicsJumpState { get; set; } = "";
        public string PlayerState { get; set; } = "";
        public string CharacterAction { get; set; } = "";
        public GymMobState[] Mobs { get; set; } = Array.Empty<GymMobState>();
    }

    /// <summary>
    /// 轻量 TCP JSON 行协议：
    /// 客户端发送 GymAction JSON，每行一条；
    /// 服务端回写 GymState JSON，每行一条。
    /// </summary>
    public sealed class GymServer : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly object _sync = new object();
        private readonly ConcurrentQueue<string> _pendingStateLines = new ConcurrentQueue<string>();
        private int _flushInProgress;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoopTask;
        private TcpClient _activeClient;
        private StreamWriter _writer;
        private DateTime _lastMomentaryJumpUtc = DateTime.MinValue;
        private GymAction _latchedJumpAction;

        public GymAction PendingAction { get; private set; }

        public void Start(int port)
        {
            if (_listener != null)
                return;

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Console.WriteLine($"[GymServer] started at 127.0.0.1:{port}");
        }

        public void ClearAction()
        {
            lock (_sync)
            {
                PendingAction = null;
                _latchedJumpAction = null;
            }
        }

        public GymAction SnapshotAction()
        {
            lock (_sync)
            {
                return PendingAction == null ? null : CloneAction(PendingAction);
            }
        }

        public GymAction ConsumeMomentaryAction()
        {
            lock (_sync)
            {
                if (PendingAction == null)
                {
                    return null;
                }

                bool hasMomentary =
                    PendingAction.Jump ||
                    PendingAction.Attack ||
                    PendingAction.Pickup ||
                    PendingAction.Reset ||
                    PendingAction.SkillSlot >= 0 ||
                    !string.IsNullOrWhiteSpace(PendingAction.SkillToken);
                if (!hasMomentary)
                {
                    return null;
                }

                var consumed = new GymAction
                {
                    Jump = PendingAction.Jump,
                    Attack = PendingAction.Attack,
                    Pickup = PendingAction.Pickup,
                    Reset = PendingAction.Reset,
                    SkillSlot = PendingAction.SkillSlot,
                    SkillToken = PendingAction.SkillToken ?? "",
                    TargetX = PendingAction.TargetX,
                    TargetY = PendingAction.TargetY,
                };

                if (consumed.Jump)
                {
                    if (_latchedJumpAction != null)
                    {
                        consumed.Left = _latchedJumpAction.Left;
                        consumed.Right = _latchedJumpAction.Right;
                        consumed.Up = _latchedJumpAction.Up;
                        consumed.Down = _latchedJumpAction.Down;
                    }
                    _lastMomentaryJumpUtc = DateTime.UtcNow;
                    _latchedJumpAction = null;
                }

                PendingAction.Jump = false;
                PendingAction.Attack = false;
                PendingAction.Pickup = false;
                PendingAction.Reset = false;
                PendingAction.SkillSlot = -1;
                PendingAction.SkillToken = "";
                return consumed;
            }
        }

        public bool HasRecentMomentaryJump(double windowMs = 220.0)
        {
            lock (_sync)
            {
                if (_lastMomentaryJumpUtc == DateTime.MinValue)
                {
                    return false;
                }

                return (DateTime.UtcNow - _lastMomentaryJumpUtc).TotalMilliseconds <= Math.Max(1.0, windowMs);
            }
        }

        public void ClearRecentMomentaryJump()
        {
            lock (_sync)
            {
                _lastMomentaryJumpUtc = DateTime.MinValue;
            }
        }

        public void SendState(GymState state)
        {
            if (state == null)
                return;

            string line = JsonSerializer.Serialize(state, JsonOptions);
            _pendingStateLines.Enqueue(line);
            TryScheduleFlush();
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    AttachClient(client);
                    await ReadActionsAsync(client, ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GymServer] accept/read error: {ex.Message}");
                }
                finally
                {
                    DetachClient(client);
                }
            }
        }

        private void AttachClient(TcpClient client)
        {
            lock (_sync)
            {
                DetachClient(_activeClient);
                _activeClient = client;
                _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            }
            Console.WriteLine("[GymServer] client connected.");
        }

        private void DetachClient(TcpClient client)
        {
            lock (_sync)
            {
                if (client == null)
                    return;

                if (ReferenceEquals(_activeClient, client))
                {
                    _writer?.Dispose();
                    _writer = null;
                    _activeClient = null;
                    ClearPendingStateLines();
                }
            }

            try { client.Close(); } catch { }
        }

        private async Task ReadActionsAsync(TcpClient client, CancellationToken ct)
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            while (!ct.IsCancellationRequested && client.Connected)
            {
                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;

                try
                {
                    var action = JsonSerializer.Deserialize<GymAction>(line, JsonOptions);
                    if (action != null)
                    {
                        lock (_sync)
                        {
                            MergeAction(action);
                        }
                    }
                }
                catch
                {
                    // Ignore malformed messages.
                }
            }
        }

        private void TryScheduleFlush()
        {
            if (Interlocked.CompareExchange(ref _flushInProgress, 1, 0) != 0)
            {
                return;
            }

            _ = FlushStatesAsync();
        }

        private async Task FlushStatesAsync()
        {
            try
            {
                while (true)
                {
                    StreamWriter writer;
                    lock (_sync)
                    {
                        writer = _writer;
                    }
                    if (writer == null)
                    {
                        ClearPendingStateLines();
                        return;
                    }

                    bool wroteAny = false;
                    while (_pendingStateLines.TryDequeue(out var line))
                    {
                        wroteAny = true;
                        try
                        {
                            await writer.WriteLineAsync(line).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Break on socket write error; next connection can continue.
                            return;
                        }
                    }

                    if (!wroteAny)
                    {
                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _flushInProgress, 0);
                if (!_pendingStateLines.IsEmpty)
                {
                    _ = Task.Run(() => TryScheduleFlush());
                }
            }
        }

        private void ClearPendingStateLines()
        {
            while (_pendingStateLines.TryDequeue(out _))
            {
            }
        }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
            }
            catch { }

            try
            {
                _listener?.Stop();
            }
            catch { }

            try
            {
                _acceptLoopTask?.Wait(500);
            }
            catch { }

            lock (_sync)
            {
                _writer?.Dispose();
                _writer = null;
                _activeClient?.Close();
                _activeClient = null;
                PendingAction = null;
                _latchedJumpAction = null;
            }
        }

        private void MergeAction(GymAction action)
        {
            if (action == null)
            {
                return;
            }

            PendingAction ??= new GymAction();

            PendingAction.Left = action.Left;
            PendingAction.Right = action.Right;
            PendingAction.Up = action.Up;
            PendingAction.Down = action.Down;
            PendingAction.TargetX = action.TargetX;
            PendingAction.TargetY = action.TargetY;

            bool jumpWasPending = PendingAction.Jump;
            PendingAction.Jump = PendingAction.Jump || action.Jump;
            PendingAction.Attack = PendingAction.Attack || action.Attack;
            PendingAction.Pickup = PendingAction.Pickup || action.Pickup;
            PendingAction.Reset = PendingAction.Reset || action.Reset;

            // Jump 为 momentary 信号；只在“本次 jump 首次进入 pending”时锁存方向，
            // 避免后续 key_up(down/right/left) 报文把组合键语义覆盖掉。
            if (action.Jump && !jumpWasPending)
            {
                _latchedJumpAction = CloneAction(action);
            }

            if (action.SkillSlot >= 0)
            {
                PendingAction.SkillSlot = action.SkillSlot;
            }
            if (!string.IsNullOrWhiteSpace(action.SkillToken))
            {
                PendingAction.SkillToken = action.SkillToken;
            }
        }

        private static GymAction CloneAction(GymAction action)
        {
            if (action == null)
            {
                return null;
            }

            return new GymAction
            {
                Left = action.Left,
                Right = action.Right,
                Up = action.Up,
                Down = action.Down,
                Jump = action.Jump,
                Attack = action.Attack,
                Pickup = action.Pickup,
                Reset = action.Reset,
                SkillSlot = action.SkillSlot,
                SkillToken = action.SkillToken ?? "",
                TargetX = action.TargetX,
                TargetY = action.TargetY,
            };
        }
    }
}
