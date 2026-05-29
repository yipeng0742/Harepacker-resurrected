using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        public GymMobState[] Mobs { get; set; } = Array.Empty<GymMobState>();
    }

    /// <summary>
    /// 轻量 TCP JSON 行协议：
    /// 客户端发送 GymAction JSON，每行一条；
    /// 服务端回写 GymState JSON，每行一条。
    /// </summary>
    public sealed class GymServer : IDisposable
    {
        private readonly object _sync = new object();
        private readonly ConcurrentQueue<string> _pendingStateLines = new ConcurrentQueue<string>();
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoopTask;
        private TcpClient _activeClient;
        private StreamWriter _writer;

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
            }
        }

        public void SendState(GymState state)
        {
            if (state == null)
                return;

            string line = JsonSerializer.Serialize(state);
            _pendingStateLines.Enqueue(line);
            _ = FlushStatesAsync();
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
                    var action = JsonSerializer.Deserialize<GymAction>(line);
                    if (action != null)
                    {
                        lock (_sync)
                        {
                            PendingAction = action;
                        }
                    }
                }
                catch
                {
                    // Ignore malformed messages.
                }
            }
        }

        private async Task FlushStatesAsync()
        {
            StreamWriter writer;
            lock (_sync)
            {
                writer = _writer;
            }
            if (writer == null)
                return;

            while (_pendingStateLines.TryDequeue(out var line))
            {
                try
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                catch
                {
                    // Break on socket write error; next connection can continue.
                    break;
                }
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
            }
        }
    }
}
