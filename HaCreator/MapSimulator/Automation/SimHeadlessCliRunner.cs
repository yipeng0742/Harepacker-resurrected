using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HaCreator.MapEditor;
using HaCreator.MapEditor.Instance;
using HaCreator.MapEditor.Instance.Shapes;
using HaCreator.MapSimulator.Character;
using HaCreator.MapSimulator.Entities;
using HaCreator.MapSimulator.IPC;
using HaCreator.MapSimulator.Physics;
using HaCreator.MapSimulator.Pools;
using HaSharedLibrary.Render;
using HaSharedLibrary.Render.DX;
using Microsoft.Xna.Framework;
using MapleLib.WzLib.WzStructure;
using MapleLib.WzLib.WzStructure.Data;

namespace HaCreator.MapSimulator.Automation
{
    internal static class SimHeadlessCliRunner
    {
        private sealed class NullDxObject : IDXObject
        {
            public void DrawObject(Microsoft.Xna.Framework.Graphics.SpriteBatch sprite, Spine.SkeletonMeshRenderer meshRenderer, GameTime gameTime, int mapShiftX, int mapShiftY, bool flip, ReflectionDrawableBoundary drawReflectionInfo)
            {
            }

            public void DrawBackground(Microsoft.Xna.Framework.Graphics.SpriteBatch sprite, Spine.SkeletonMeshRenderer meshRenderer, GameTime gameTime, int x, int y, Color color, bool flip, ReflectionDrawableBoundary drawReflectionInfo)
            {
            }

            public int Delay => 100;
            public int X => 0;
            public int Y => 0;
            public int Width => 1;
            public int Height => 1;
            public object Tag { get; set; }
        }

        private sealed class HeadlessSimWorld : IDisposable
        {
            private readonly Board _board;
            private readonly string _spawnPortalName;
            private readonly GymServer _gymServer;
            private readonly PlayerManager _playerManager;
            private readonly PortalPool _portalPool = new PortalPool();
            private readonly MobPool _mobPool = new MobPool();
            private readonly DropPool _dropPool = new DropPool();
            private readonly NullDxObject _nullDx = new NullDxObject();
            private readonly List<MobItem> _mobItems = new List<MobItem>();
            private readonly List<PortalItem> _portalItems = new List<PortalItem>();
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

            private int _tick;
            private bool _prevPickup;
            private bool _prevReset;
            private int _prevSkillSlot = -1;
            private string _prevSkillToken = "";
            private bool _running;
            private bool _inputLeft;
            private bool _inputRight;
            private bool _inputUp;
            private bool _inputDown;
            private FootholdLine _pendingLadderExitFoothold;
            private float _pendingLadderExitY;
            private int _pendingLadderExitTicks;
            private int _edgeDropCooldownTicks;
            private FootholdLine _edgeDropSourceFoothold;
            private FootholdLine _edgeDropArmFoothold;
            private int _edgeDropArmDir;
            private int _edgeDropArmTicks;
            private FootholdLine _recentJumpAssistFoothold;
            private int _recentJumpAssistDir;
            private int _recentJumpAssistTicks;

            public HeadlessSimWorld(Board board, int gymPort, string spawnPortalName)
            {
                _board = board ?? throw new ArgumentNullException(nameof(board));
                _spawnPortalName = spawnPortalName ?? "";
                _gymServer = new GymServer();
                _gymServer.Start(gymPort);

                _playerManager = new PlayerManager(device: null, texturePool: null);
                _playerManager.SetFootholdLookup(FindFoothold);
                _playerManager.SetLadderLookup(FindLadder);
                _playerManager.SetMobPool(_mobPool);
                _playerManager.SetDropPool(_dropPool);
                _playerManager.IsGymControlled = true;

                BuildPortalPool();
                BuildMobPool();
                InitializePlayer();
            }

            public void Run()
            {
                _running = true;
                var last = _stopwatch.Elapsed;
                while (_running)
                {
                    var now = _stopwatch.Elapsed;
                    float deltaTime = (float)Math.Max(0.005, Math.Min(0.05, (now - last).TotalSeconds));
                    last = now;
                    int currentTick = (int)now.TotalMilliseconds;

                    Step(currentTick, deltaTime);
                    Thread.Sleep(16);
                }
            }

            public void Dispose()
            {
                _running = false;
                _gymServer?.Dispose();
            }

            private void Step(int currentTick, float deltaTime)
            {
                _tick++;
                ApplyGymAction(currentTick);

                bool skipPlayerUpdate = PrepareTopExitObservation();
                if (!skipPlayerUpdate)
                {
                    _playerManager.Update(currentTick, deltaTime, chatIsActive: false);
                }
                NormalizePlayerState();

                var playerPos = _playerManager.GetPlayerPosition();
                float px = playerPos.X;
                float py = playerPos.Y;
                int deltaMs = Math.Max(1, (int)Math.Round(deltaTime * 1000.0));

                foreach (var mob in _mobItems)
                {
                    mob?.UpdateMovement(deltaMs, currentTick, px, py);
                }

                _mobPool.Update(currentTick, CreateMobFromSpawnPoint);
                NormalizeMobStates();
                _portalPool.Update(px, py, currentTick, deltaTime);
                _dropPool.Update(currentTick, deltaTime);

                PublishState();
            }

            private bool PrepareTopExitObservation()
            {
                var player = _playerManager.Player;
                var physics = player?.Physics;
                if (player == null || physics == null || !physics.IsOnLadderOrRope)
                {
                    return false;
                }

                if (!_inputUp || _inputDown)
                {
                    return false;
                }

                var landingFh = FindLadderTopLandingFoothold(player.X, physics.LadderTop);
                if (landingFh == null)
                {
                    return false;
                }

                float landingY = Board.CalculateYOnFoothold(landingFh, player.X);
                float topResolveMargin = GetLadderTopResolveMargin(physics);
                float landingResolveMargin = Math.Max(10f, topResolveMargin - 8f);
                bool nearTop = player.Y <= physics.LadderTop + topResolveMargin || player.Y <= landingY + landingResolveMargin;
                if (!nearTop)
                {
                    return false;
                }

                return BeginPendingLadderExit(player, physics, landingFh);
            }

            private void InitializePlayer()
            {
                var spawn = ResolveSpawnPoint();
                _playerManager.SetSpawnPoint(spawn.X, spawn.Y);
                if (!_playerManager.CreatePlaceholderPlayer())
                {
                    throw new InvalidOperationException("headless player 创建失败");
                }

                _playerManager.TeleportTo(spawn.X, spawn.Y);
                _playerManager.ForceStand();
            }

            private Vector2 ResolveSpawnPoint()
            {
                PortalInstance targetPortal = null;
                if (!string.IsNullOrWhiteSpace(_spawnPortalName))
                {
                    targetPortal = _board.BoardItems.Portals.FirstOrDefault(p =>
                        string.Equals(p.pn, _spawnPortalName, StringComparison.OrdinalIgnoreCase));
                }

                targetPortal ??= _board.BoardItems.Portals.FirstOrDefault(p => p.pt == PortalType.StartPoint);
                targetPortal ??= _board.BoardItems.Portals.FirstOrDefault();

                if (targetPortal != null)
                {
                    return new Vector2(targetPortal.X, targetPortal.Y);
                }

                if (_board.VRRectangle != null)
                {
                    return new Vector2(_board.VRRectangle.X + _board.VRRectangle.Width / 2f, _board.VRRectangle.Y + _board.VRRectangle.Height / 2f);
                }

                return new Vector2(_board.CenterPoint.X, _board.CenterPoint.Y);
            }

            private void BuildPortalPool()
            {
                _portalItems.Clear();
                foreach (var portal in _board.BoardItems.Portals)
                {
                    if (portal == null)
                    {
                        continue;
                    }

                    _portalItems.Add(new PortalItem(portal, _nullDx));
                }

                _portalPool.Initialize(_portalItems.ToArray());
            }

            private void BuildMobPool()
            {
                _mobItems.Clear();
                foreach (var mob in _board.BoardItems.Mobs)
                {
                    if (mob == null)
                    {
                        continue;
                    }

                    var item = new MobItem(mob, _nullDx, null);
                    ConfigureMob(item);
                    _mobItems.Add(item);
                }

                _mobPool.Initialize(_mobItems.ToArray());
            }

            private void ConfigureMob(MobItem mob)
            {
                if (mob?.MovementInfo == null)
                {
                    return;
                }

                mob.SetMapBoundaries(
                    _board.VRRectangle?.X ?? (_board.CenterPoint.X - 5000),
                    (_board.VRRectangle?.X ?? (_board.CenterPoint.X - 5000)) + (_board.VRRectangle?.Width ?? 10000),
                    _board.VRRectangle?.Y ?? (_board.CenterPoint.Y - 5000),
                    (_board.VRRectangle?.Y ?? (_board.CenterPoint.Y - 5000)) + (_board.VRRectangle?.Height ?? 10000));

                var fh = FindNearestStandableFoothold(mob.MovementInfo.X, mob.MovementInfo.Y, 120f, 20f)
                    ?? FindFoothold(mob.MovementInfo.X, mob.MovementInfo.Y, 120f);
                if (fh != null)
                {
                    mob.MovementInfo.CurrentFoothold = fh;
                    mob.MovementInfo.Y = Board.CalculateYOnFoothold(fh, mob.MovementInfo.X);
                }
            }

            private MobItem CreateMobFromSpawnPoint(MobSpawnPoint spawnPoint)
            {
                if (spawnPoint == null)
                {
                    return null;
                }

                var baseMob = _board.BoardItems.Mobs.FirstOrDefault(m =>
                    string.Equals(m.MobInfo?.ID, spawnPoint.MobId, StringComparison.OrdinalIgnoreCase));
                if (baseMob == null)
                {
                    return null;
                }

                var instance = new MobInstance(
                    baseMob.MobInfo,
                    _board,
                    (int)Math.Round(spawnPoint.X),
                    (int)Math.Round(spawnPoint.Y),
                    spawnPoint.Rx0Shift,
                    spawnPoint.Rx1Shift,
                    baseMob.yShift,
                    baseMob.LimitedName,
                    baseMob.MobTime,
                    spawnPoint.Flip ? MapleBool.True : MapleBool.False,
                    baseMob.Hide,
                    baseMob.Info,
                    baseMob.Team);
                var item = new MobItem(instance, _nullDx, null);
                ConfigureMob(item);
                _mobItems.Add(item);
                return item;
            }

            private void ApplyGymAction(int currentTick)
            {
                var action = _gymServer.SnapshotAction();
                var momentary = _gymServer.ConsumeMomentaryAction();
                var player = _playerManager.Player;
                var physics = player?.Physics;
                if (action == null || player == null)
                {
                    player?.ClearInput();
                    return;
                }

                bool left = action.Left && !action.Right;
                bool right = action.Right && !action.Left;
                bool up = action.Up;
                bool down = action.Down;
                bool jump = (momentary?.Jump ?? false) || action.Jump;
                bool attack = (momentary?.Attack ?? false) || action.Attack;
                bool pickup = (momentary?.Pickup ?? false) || action.Pickup;
                _inputLeft = left;
                _inputRight = right;
                _inputUp = up;
                _inputDown = down;

                AssistLadderGrab(player, jump);
                if (jump && physics != null && !physics.IsOnLadderOrRope)
                {
                    int jumpDir = right ? 1 : (left ? -1 : 0);
                    if (jumpDir != 0)
                    {
                        var jumpSourceFh = physics.CurrentFoothold
                            ?? FindNearestStandableFoothold(player.X, player.Y, 28f, 20f)
                            ?? FindFoothold(player.X, player.Y, 32f);
                        RegisterRecentJumpAssist(jumpSourceFh, jumpDir);
                    }
                }
                player.SetInput(left, right, up, down, jump, attack, pickup);

                bool reset = (momentary?.Reset ?? false) || action.Reset;
                if (reset && !_prevReset)
                {
                    bool usedTargetRespawn = false;
                    float tx = action.TargetX;
                    float ty = action.TargetY;
                    if (!float.IsNaN(tx) && !float.IsInfinity(tx) && !float.IsNaN(ty) && !float.IsInfinity(ty))
                    {
                        _playerManager.RespawnAt(tx, ty);
                        _playerManager.ForceStand();
                        usedTargetRespawn = true;
                    }
                    if (!usedTargetRespawn)
                    {
                        _playerManager.Respawn();
                        _playerManager.ForceStand();
                    }
                }
                _prevReset = reset;

                if (pickup && !_prevPickup)
                {
                    _playerManager.Combat?.TryPickupDrop(_dropPool, currentTick);
                }
                _prevPickup = pickup;

                int skillSlot = (momentary?.SkillSlot ?? -1) >= 0 ? momentary.SkillSlot : action.SkillSlot;
                string skillToken = !string.IsNullOrWhiteSpace(momentary?.SkillToken) ? momentary.SkillToken : (action.SkillToken ?? "");
                bool skillChanged = skillSlot != _prevSkillSlot || !string.Equals(skillToken, _prevSkillToken, StringComparison.Ordinal);
                if (skillChanged && _playerManager.Skills != null)
                {
                    if (skillSlot >= 0)
                    {
                        _playerManager.Skills.TryCastHotkey(skillSlot, currentTick);
                    }
                    else if (!string.IsNullOrWhiteSpace(skillToken) && int.TryParse(skillToken, out int skillId))
                    {
                        _playerManager.Skills.TryCastSkill(skillId, currentTick);
                    }
                }
                _prevSkillSlot = skillSlot;
                _prevSkillToken = skillToken;
            }

            private void NormalizePlayerState()
            {
                var player = _playerManager.Player;
                var physics = player?.Physics;
                if (player == null || physics == null)
                {
                    return;
                }

                if (_recentJumpAssistTicks > 0)
                {
                    _recentJumpAssistTicks--;
                    if (_recentJumpAssistTicks <= 0)
                    {
                        ResetRecentJumpAssist();
                    }
                }

                if (ResolvePendingLadderExit(player, physics))
                {
                    return;
                }

                if (TryResolveRecentTopExit(player, physics))
                {
                    return;
                }

                if (physics.IsOnLadderOrRope)
                {
                    ResetEdgeDropArm();
                    if (TryResolveLadderReach(player, physics))
                    {
                        return;
                    }

                    if (TryResolveLadderTopExit(player, physics))
                    {
                        return;
                    }

                    var ladder = FindLadder(player.X, player.Y, 8f);
                    if (!ladder.HasValue)
                    {
                        physics.ReleaseLadder();
                    }
                    return;
                }

                var currentFh = physics.CurrentFoothold;
                if (_edgeDropCooldownTicks > 0 &&
                    currentFh != null &&
                    _edgeDropSourceFoothold != null &&
                    ShouldSuppressEdgeDropLanding(player, currentFh) &&
                    !HasClearedEdgeDropSource(player, currentFh, requiredDropPx: 28f))
                {
                    ForceEdgeDropContinuation(player, physics, currentFh);
                    currentFh = null;
                }

                if (currentFh != null)
                {
                    float currentY = Board.CalculateYOnFoothold(currentFh, player.X);
                    if (TryExposeRecentLadderLanding(player, physics, currentFh, currentY))
                    {
                        return;
                    }

                    if (TryResolveUpperJumpAssist(player, physics, currentFh, currentY))
                    {
                        return;
                    }

                    float minX = Math.Min(currentFh.FirstDot.X, currentFh.SecondDot.X);
                    float maxX = Math.Max(currentFh.FirstDot.X, currentFh.SecondDot.X);
                    bool insideBounds = player.X >= minX - 10f && player.X <= maxX + 10f;

                    if (!insideBounds && Math.Abs((float)physics.VelocityY) < 20f)
                    {
                        int detachDir = player.X >= (minX + maxX) * 0.5f ? 1 : -1;
                        ArmEdgeDropFromFoothold(player, physics, currentFh, currentY, detachDir, forceOutside: true);
                        currentFh = null;
                    }
                    else if (Math.Abs(player.Y - currentY) > 48f)
                    {
                        var betterFh = FindNearestStandableFoothold(player.X, player.Y, 96f, 36f)
                            ?? FindFoothold(player.X, player.Y, 96f);
                        if (betterFh != null && betterFh != currentFh)
                        {
                            float betterY = Board.CalculateYOnFoothold(betterFh, player.X);
                            if (Math.Abs(player.Y - betterY) + 8f < Math.Abs(player.Y - currentY))
                            {
                                physics.LandOnFoothold(betterFh);
                                player.SetPosition(player.X, betterY);
                                currentFh = betterFh;
                            }
                        }
                    }

                    if (currentFh != null && TryResolveEdgeDrop(player, physics, currentFh, currentY))
                    {
                        return;
                    }
                }
                else
                {
                    ResetEdgeDropArm();
                }

                if (physics.CurrentFoothold == null)
                {
                    ResetEdgeDropArm();
                    if (_edgeDropCooldownTicks > 0)
                    {
                        if (_edgeDropSourceFoothold == null)
                        {
                            _edgeDropCooldownTicks = 0;
                        }
                        else if (!HasClearedEdgeDropSource(player, _edgeDropSourceFoothold, requiredDropPx: 48f))
                        {
                            _edgeDropCooldownTicks = Math.Max(_edgeDropCooldownTicks, 4);
                        }
                        if (_edgeDropCooldownTicks > 0)
                        {
                            _edgeDropCooldownTicks--;
                            if ((float)physics.VelocityY < 116f)
                            {
                                physics.VelocityY = 116f;
                            }
                            return;
                        }
                    }

                    if (TryResolveLatchedUpperJumpAssist(player, physics))
                    {
                        return;
                    }

                    TryResolveNearbyLanding(player, physics);
                }
            }

            private bool TryResolveUpperJumpAssist(PlayerCharacter player, CVecCtrl physics, FootholdLine currentFh, float currentY, bool allowAirborneLatch = false)
            {
                if (player == null || physics == null || currentFh == null)
                {
                    return false;
                }

                bool hasRecentJump = _gymServer.HasRecentMomentaryJump(windowMs: 420.0) || _recentJumpAssistTicks > 0;
                if (!hasRecentJump)
                {
                    return false;
                }

                if (_inputUp || _inputDown || physics.IsOnLadderOrRope)
                {
                    return false;
                }

                int dir = _inputRight && !_inputLeft ? 1 : (_inputLeft && !_inputRight ? -1 : 0);
                if (dir == 0 && _recentJumpAssistTicks > 0)
                {
                    bool sameSource = _recentJumpAssistFoothold == null || _recentJumpAssistFoothold == currentFh;
                    if (!sameSource && _recentJumpAssistFoothold != null)
                    {
                        float sourceY = Board.CalculateYOnFoothold(_recentJumpAssistFoothold, player.X);
                        sameSource = Math.Abs(sourceY - currentY) <= 18f;
                    }
                    if (sameSource)
                    {
                        dir = _recentJumpAssistDir;
                    }
                }
                if (dir == 0)
                {
                    return false;
                }

                if (!allowAirborneLatch && Math.Abs((float)physics.VelocityY) > 18f)
                {
                    return false;
                }

                float minX = Math.Min(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float maxX = Math.Max(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float edgeGap = dir > 0 ? (maxX - player.X) : (player.X - minX);
                if (edgeGap < -6f || edgeGap > 28f)
                {
                    return false;
                }

                float[] probeOffsets = dir > 0
                    ? new[] { 6f, 18f, 36f, 56f }
                    : new[] { -6f, -18f, -36f, -56f };
                FootholdLine upper = null;
                float upperY = 0f;
                float probeXResolved = player.X;

                foreach (float offset in probeOffsets)
                {
                    float probeX = dir > 0 ? maxX + offset : minX + offset;
                    var candidate = FindNearestStandableFoothold(probeX, currentY - 60f, 96f, 20f)
                        ?? FindFoothold(probeX, currentY - 40f, 120f);
                    if (candidate == null || candidate == currentFh)
                    {
                        continue;
                    }

                    float candidateY = Board.CalculateYOnFoothold(candidate, probeX);
                    float risePx = currentY - candidateY;
                    if (risePx < 28f || risePx > 96f)
                    {
                        continue;
                    }

                    upper = candidate;
                    upperY = candidateY;
                    probeXResolved = probeX;
                    break;
                }

                if (upper == null)
                {
                    return false;
                }

                float jumpPower = (_playerManager.Player?.Build?.JumpPower ?? 100f) / 100f;
                float upwardVelocity = CVecCtrl.JumpVelocity * jumpPower;
                float horizontalVelocity = Math.Max(70f, Math.Abs((float)physics.VelocityX));
                if (dir < 0)
                {
                    horizontalVelocity = -horizontalVelocity;
                }

                float startX = dir > 0 ? Math.Min(maxX - 2f, Math.Max(minX + 2f, player.X)) : Math.Max(minX + 2f, Math.Min(maxX - 2f, player.X));
                float startY = Board.CalculateYOnFoothold(currentFh, startX);
                player.SetPosition(startX, startY);
                player.ForceJump(upwardVelocity, horizontalVelocity);
                physics.FallStartFoothold = currentFh;
                ResetRecentJumpAssist();
                return true;
            }

            private bool TryResolveLatchedUpperJumpAssist(PlayerCharacter player, CVecCtrl physics)
            {
                if (player == null || physics == null)
                {
                    return false;
                }

                bool hasRecentJump = _gymServer.HasRecentMomentaryJump(windowMs: 420.0) || _recentJumpAssistTicks > 0;
                if (!hasRecentJump || physics.IsOnLadderOrRope)
                {
                    return false;
                }

                var sourceFh = _recentJumpAssistFoothold ?? physics.FallStartFoothold;
                int dir = _inputRight && !_inputLeft ? 1 : (_inputLeft && !_inputRight ? -1 : _recentJumpAssistDir);
                if (sourceFh == null || dir == 0)
                {
                    return false;
                }

                float sourceY = Board.CalculateYOnFoothold(sourceFh, player.X);
                bool nearSourceBand = player.Y >= sourceY - 20f && player.Y <= sourceY + 20f;
                float verticalSpeed = (float)physics.VelocityY;
                bool latchVerticalWindowOk = verticalSpeed <= 60f;
                if (!nearSourceBand || !latchVerticalWindowOk)
                {
                    return false;
                }

                return TryResolveUpperJumpAssist(player, physics, sourceFh, sourceY, allowAirborneLatch: true);
            }

            private bool ResolvePendingLadderExit(PlayerCharacter player, CVecCtrl physics)
            {
                if (player == null || physics == null || _pendingLadderExitFoothold == null)
                {
                    return false;
                }

                if (_pendingLadderExitTicks > 0)
                {
                    _pendingLadderExitTicks--;
                }

                bool wantsExit = _inputLeft || _inputRight || _pendingLadderExitTicks <= 0;
                if (wantsExit)
                {
                    float landingY = Board.CalculateYOnFoothold(_pendingLadderExitFoothold, player.X);
                    physics.LandOnFoothold(_pendingLadderExitFoothold);
                    player.SetPosition(player.X, landingY);
                    _playerManager.ForceStand();
                    _pendingLadderExitFoothold = null;
                    _pendingLadderExitY = 0f;
                    _pendingLadderExitTicks = 0;
                    return true;
                }

                physics.CurrentFoothold = null;
                physics.FallStartFoothold = null;
                physics.VelocityX = 0;
                physics.VelocityY = 0;
                player.SetPosition(player.X, _pendingLadderExitY);
                return true;
            }

            private bool TryResolveRecentTopExit(PlayerCharacter player, CVecCtrl physics)
            {
                if (player == null || physics == null)
                {
                    return false;
                }

                if (!_inputUp || _inputDown || physics.IsOnLadderOrRope || physics.CurrentFoothold != null)
                {
                    return false;
                }

                var ladder = FindLadder(player.X, player.Y, 18f);
                if (!ladder.HasValue)
                {
                    return false;
                }

                var landingFh = FindLadderTopLandingFoothold(player.X, ladder.Value.top);
                if (landingFh == null)
                {
                    return false;
                }

                float landingY = Board.CalculateYOnFoothold(landingFh, player.X);
                bool nearReleaseTop = player.Y >= ladder.Value.top - 24f && player.Y <= ladder.Value.top + 18f;
                bool nearLanding = player.Y <= landingY + 12f;
                bool lowVerticalSpeed = Math.Abs((float)physics.VelocityY) <= 18f;
                if ((!nearReleaseTop && !nearLanding) || !lowVerticalSpeed)
                {
                    return false;
                }

                return BeginPendingLadderExit(player, physics, landingFh);
            }

            private bool TryExposeRecentLadderLanding(PlayerCharacter player, CVecCtrl physics, FootholdLine currentFh, float currentY)
            {
                if (player == null || physics == null || currentFh == null)
                {
                    return false;
                }

                if (!_inputUp || _inputDown || _pendingLadderExitFoothold != null)
                {
                    return false;
                }

                var ladder = FindLadder(player.X, player.Y, 22f);
                if (!ladder.HasValue)
                {
                    return false;
                }

                bool nearTopLanding =
                    currentY >= ladder.Value.top - 24f &&
                    currentY <= ladder.Value.top + 12f &&
                    Math.Abs(player.X - ladder.Value.x) <= 24f;
                if (!nearTopLanding)
                {
                    return false;
                }

                int holdTicks = Math.Max(8, ladder.Value.bottom - ladder.Value.top >= 240 ? 14 : 12);
                float reachedY = currentY - 14f;
                physics.CurrentFoothold = null;
                physics.FallStartFoothold = null;
                physics.VelocityX = 0;
                physics.VelocityY = 0;
                player.SetPosition(player.X, reachedY);
                _playerManager.ForceStand();
                _pendingLadderExitFoothold = currentFh;
                _pendingLadderExitY = reachedY;
                _pendingLadderExitTicks = Math.Max(_pendingLadderExitTicks, holdTicks);
                return true;
            }

            private bool BeginPendingLadderExit(PlayerCharacter player, CVecCtrl physics, FootholdLine landingFh)
            {
                if (player == null || physics == null || landingFh == null)
                {
                    return false;
                }

                float landingY = Board.CalculateYOnFoothold(landingFh, player.X);
                float reachedY = Math.Min(landingY - 14f, physics.LadderTop - 8f);
                int holdTicks = GetPendingLadderExitTicks(physics);

                physics.ReleaseLadder();
                physics.CurrentFoothold = null;
                physics.FallStartFoothold = null;
                physics.VelocityX = 0;
                physics.VelocityY = 0;
                player.SetPosition(player.X, reachedY);
                _playerManager.ForceStand();
                _pendingLadderExitFoothold = landingFh;
                _pendingLadderExitY = reachedY;
                _pendingLadderExitTicks = Math.Max(_pendingLadderExitTicks, holdTicks);
                return true;
            }

            private void AssistLadderGrab(PlayerCharacter player, bool jump)
            {
                var physics = player?.Physics;
                if (player == null || physics == null)
                {
                    return;
                }

                if (!_inputUp || _inputDown || physics.IsOnLadderOrRope)
                {
                    return;
                }

                float searchRange = jump ? 72f : 56f;
                var ladder = FindLadder(player.X, player.Y, searchRange);
                if (!ladder.HasValue)
                {
                    return;
                }

                float dx = Math.Abs(player.X - ladder.Value.x);
                if (dx > 18f)
                {
                    return;
                }

                bool verticalReach =
                    player.Y >= ladder.Value.top - 32f &&
                    player.Y <= ladder.Value.bottom + 28f;
                if (!verticalReach)
                {
                    return;
                }

                float snapY = player.Y;
                if (physics.CurrentFoothold != null && player.Y > ladder.Value.bottom - 4f && player.Y <= ladder.Value.bottom + 28f)
                {
                    snapY = ladder.Value.bottom - 2f;
                }

                if (Math.Abs(player.X - ladder.Value.x) > 0.5f || Math.Abs(player.Y - snapY) > 0.5f)
                {
                    player.SetPosition(ladder.Value.x, snapY);
                }
            }

            private bool TryResolveLadderReach(PlayerCharacter player, CVecCtrl physics)
            {
                if (player == null || physics == null)
                {
                    return false;
                }

                if (!_inputUp || _inputDown)
                {
                    return false;
                }

                var landingFh = FindLadderTopLandingFoothold(player.X, physics.LadderTop);
                if (landingFh == null)
                {
                    return false;
                }

                float landingY = Board.CalculateYOnFoothold(landingFh, player.X);
                float topResolveMargin = GetLadderTopResolveMargin(physics);
                float landingResolveMargin = Math.Max(10f, topResolveMargin - 8f);
                bool nearTop = player.Y <= physics.LadderTop + topResolveMargin || player.Y <= landingY + landingResolveMargin;
                if (!nearTop)
                {
                    return false;
                }

                return BeginPendingLadderExit(player, physics, landingFh);
            }

            private bool TryResolveLadderTopExit(PlayerCharacter player, CVecCtrl physics)
            {
                if (player == null || physics == null)
                {
                    return false;
                }

                if (_inputDown)
                {
                    return false;
                }

                var landingFh = FindLadderTopLandingFoothold(player.X, physics.LadderTop)
                    ?? FindNearestStandableFoothold(player.X, player.Y - 20f, 72f, 48f)
                    ?? FindFoothold(player.X, player.Y - 20f, 56f);
                if (landingFh == null)
                {
                    return false;
                }

                float landingY = Board.CalculateYOnFoothold(landingFh, player.X);
                float topResolveMargin = GetLadderTopResolveMargin(physics) + 10f;
                float landingResolveMargin = Math.Max(24f, topResolveMargin - 8f);
                bool nearTop = player.Y <= physics.LadderTop + topResolveMargin;
                bool nearLanding = player.Y <= landingY + landingResolveMargin;
                bool wantsExit = _inputLeft || _inputRight || !_inputUp || Math.Abs((float)physics.VelocityY) < 4f;
                if (!wantsExit || (!nearTop && !nearLanding))
                {
                    return false;
                }

                return BeginPendingLadderExit(player, physics, landingFh);
            }

            private FootholdLine FindLadderTopLandingFoothold(float x, int ladderTop)
            {
                var landingFh = FindNearestStandableFoothold(x, ladderTop - 6f, 128f, 56f)
                    ?? FindFoothold(x, ladderTop - 12f, 160f)
                    ?? FindNearestStandableFoothold(x, ladderTop + 8f, 96f, 56f);
                if (landingFh == null)
                {
                    return null;
                }

                float landingY = Board.CalculateYOnFoothold(landingFh, x);
                if (landingY > ladderTop + 28f)
                {
                    return null;
                }

                return landingFh;
            }

            private float GetLadderTopResolveMargin(CVecCtrl physics)
            {
                if (physics == null)
                {
                    return 18f;
                }

                float ladderSpan = Math.Abs(physics.LadderBottom - physics.LadderTop);
                if (ladderSpan >= 320f)
                {
                    return 96f;
                }

                if (ladderSpan >= 240f)
                {
                    return 64f;
                }

                return 18f;
            }

            private int GetPendingLadderExitTicks(CVecCtrl physics)
            {
                if (physics == null)
                {
                    return 8;
                }

                float ladderSpan = Math.Abs(physics.LadderBottom - physics.LadderTop);
                if (ladderSpan >= 320f)
                {
                    return 16;
                }

                if (ladderSpan >= 240f)
                {
                    return 14;
                }

                return 12;
            }

            private void TryResolveNearbyLanding(PlayerCharacter player, CVecCtrl physics)
            {
                if (player == null || physics == null)
                {
                    return;
                }

                if ((float)physics.VelocityY < -20f)
                {
                    return;
                }

                var landingFh = FindNearestStandableFoothold(player.X, player.Y - 6f, 48f, 36f)
                    ?? FindFoothold(player.X, player.Y - 6f, 36f);
                if (landingFh == null)
                {
                    return;
                }

                if (_edgeDropCooldownTicks > 0 && ShouldSuppressEdgeDropLanding(player, landingFh))
                {
                    return;
                }

                if (_edgeDropSourceFoothold != null && landingFh == _edgeDropSourceFoothold)
                {
                    if (!HasClearedEdgeDropSource(player, landingFh, requiredDropPx: 28f))
                    {
                        return;
                    }
                }

                float landingY = Board.CalculateYOnFoothold(landingFh, player.X);
                float yError = player.Y - landingY;
                bool closeEnough = yError >= -6f && yError <= 14f;
                if (!closeEnough)
                {
                    return;
                }

                physics.LandOnFoothold(landingFh);
                player.SetPosition(player.X, landingY);
                _edgeDropSourceFoothold = null;
                _edgeDropCooldownTicks = 0;
                if (Math.Abs((float)physics.VelocityX) < 12f)
                {
                    _playerManager.ForceStand();
                }
            }

            private bool TryResolveEdgeDrop(PlayerCharacter player, CVecCtrl physics, FootholdLine currentFh, float currentY)
            {
                if (player == null || physics == null || currentFh == null)
                {
                    ResetEdgeDropArm();
                    return false;
                }

                int dir = _inputRight && !_inputLeft ? 1 : (_inputLeft && !_inputRight ? -1 : 0);
                if (dir == 0 || _inputUp || _inputDown)
                {
                    ResetEdgeDropArm();
                    return false;
                }

                float minX = Math.Min(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float maxX = Math.Max(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float edgeThreshold = 4f;
                bool nearEdge = dir > 0 ? player.X >= maxX - edgeThreshold : player.X <= minX + edgeThreshold;
                if (!nearEdge)
                {
                    ResetEdgeDropArm();
                    return false;
                }

                if (_edgeDropArmFoothold != currentFh || _edgeDropArmDir != dir)
                {
                    _edgeDropArmFoothold = currentFh;
                    _edgeDropArmDir = dir;
                    _edgeDropArmTicks = 1;
                    return false;
                }

                _edgeDropArmTicks++;
                if (_edgeDropArmTicks < 2)
                {
                    return false;
                }

                float[] probeOffsets = dir > 0
                    ? new[] { 6f, 18f, 36f, 72f }
                    : new[] { -6f, -18f, -36f, -72f };

                FootholdLine below = null;
                float selectedProbeX = 0f;
                float selectedDetachX = 0f;

                foreach (float offset in probeOffsets)
                {
                    float probeX = dir > 0 ? maxX + offset : minX + offset;
                    var sameLevel = FindNearestStandableFoothold(probeX, currentY, 12f, 10f);
                    var candidateBelow = FindFoothold(probeX, currentY + 12f, 240f);
                    if (candidateBelow == null || candidateBelow == currentFh)
                    {
                        continue;
                    }

                    float candidateBelowY = Board.CalculateYOnFoothold(candidateBelow, probeX);
                    float dropPx = candidateBelowY - currentY;
                    if (dropPx < 12f)
                    {
                        continue;
                    }

                    if (sameLevel != null && sameLevel != currentFh)
                    {
                        float sameLevelY = Board.CalculateYOnFoothold(sameLevel, probeX);
                        if (Math.Abs(sameLevelY - currentY) <= 10f)
                        {
                            continue;
                        }
                    }

                    below = candidateBelow;
                    selectedProbeX = probeX;
                    selectedDetachX = dir > 0
                        ? Math.Max(maxX + 24f, probeX + 12f)
                        : Math.Min(minX - 24f, probeX - 12f);
                    break;
                }

                if (below == null)
                {
                    return false;
                }

                player.SetPosition(selectedDetachX, Board.CalculateYOnFoothold(currentFh, selectedProbeX) + 2f);
                ArmEdgeDropFromFoothold(player, physics, currentFh, currentY, dir, forceOutside: false);
                ResetEdgeDropArm();
                return true;
            }

            private void ArmEdgeDropFromFoothold(
                PlayerCharacter player,
                CVecCtrl physics,
                FootholdLine currentFh,
                float currentY,
                int dir,
                bool forceOutside)
            {
                if (player == null || physics == null || currentFh == null)
                {
                    return;
                }

                int resolvedDir = dir >= 0 ? 1 : -1;
                float minX = Math.Min(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float maxX = Math.Max(currentFh.FirstDot.X, currentFh.SecondDot.X);
                if (forceOutside)
                {
                    float detachX = resolvedDir > 0 ? maxX + 32f : minX - 32f;
                    float detachY = Board.CalculateYOnFoothold(currentFh, detachX);
                    player.SetPosition(detachX, detachY + 4f);
                }

                physics.CurrentFoothold = null;
                physics.FallStartFoothold = currentFh;
                player.ForceFall(104f);
                if (resolvedDir > 0)
                {
                    physics.VelocityX = Math.Max(physics.VelocityX, 54.0);
                }
                else
                {
                    physics.VelocityX = Math.Min(physics.VelocityX, -54.0);
                }
                physics.VelocityY = Math.Max(physics.VelocityY, 104.0);
                _edgeDropCooldownTicks = Math.Max(_edgeDropCooldownTicks, 8);
                _edgeDropSourceFoothold = currentFh;
            }

            private void ForceEdgeDropContinuation(PlayerCharacter player, CVecCtrl physics, FootholdLine currentFh)
            {
                if (player == null || physics == null || currentFh == null)
                {
                    return;
                }

                float minX = Math.Min(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float maxX = Math.Max(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float detachX = player.X >= (minX + maxX) * 0.5f ? maxX + 36f : minX - 36f;
                float detachY = Board.CalculateYOnFoothold(currentFh, detachX);
                int detachDir = player.X >= (minX + maxX) * 0.5f ? 1 : -1;
                float forcedY = Math.Max(player.Y + 12f, detachY + 6f);

                player.SetPosition(detachX, forcedY);
                physics.CurrentFoothold = null;
                physics.FallStartFoothold = _edgeDropSourceFoothold ?? currentFh;
                player.ForceFall(132f);
                if (detachDir > 0)
                {
                    physics.VelocityX = Math.Max(physics.VelocityX, 58.0);
                }
                else
                {
                    physics.VelocityX = Math.Min(physics.VelocityX, -58.0);
                }
                physics.VelocityY = Math.Max(physics.VelocityY, 132.0);
                _edgeDropCooldownTicks = Math.Max(_edgeDropCooldownTicks, 7);
            }

            private void ResetEdgeDropArm()
            {
                _edgeDropArmFoothold = null;
                _edgeDropArmDir = 0;
                _edgeDropArmTicks = 0;
            }

            private void RegisterRecentJumpAssist(FootholdLine sourceFh, int dir)
            {
                if (sourceFh == null || dir == 0)
                {
                    return;
                }

                _recentJumpAssistFoothold = sourceFh;
                _recentJumpAssistDir = dir >= 0 ? 1 : -1;
                _recentJumpAssistTicks = Math.Max(_recentJumpAssistTicks, 24);
            }

            private void ResetRecentJumpAssist()
            {
                _gymServer.ClearRecentMomentaryJump();
                _recentJumpAssistFoothold = null;
                _recentJumpAssistDir = 0;
                _recentJumpAssistTicks = 0;
            }

            private bool ShouldSuppressEdgeDropLanding(PlayerCharacter player, FootholdLine candidateFh)
            {
                if (player == null || candidateFh == null || _edgeDropSourceFoothold == null)
                {
                    return false;
                }

                if (candidateFh == _edgeDropSourceFoothold)
                {
                    return true;
                }

                float sourceY = Board.CalculateYOnFoothold(_edgeDropSourceFoothold, player.X);
                float candidateY = Board.CalculateYOnFoothold(candidateFh, player.X);
                float sourceMinX = Math.Min(_edgeDropSourceFoothold.FirstDot.X, _edgeDropSourceFoothold.SecondDot.X);
                float sourceMaxX = Math.Max(_edgeDropSourceFoothold.FirstDot.X, _edgeDropSourceFoothold.SecondDot.X);
                float candidateMinX = Math.Min(candidateFh.FirstDot.X, candidateFh.SecondDot.X);
                float candidateMaxX = Math.Max(candidateFh.FirstDot.X, candidateFh.SecondDot.X);
                bool overlapsSourceBand = candidateMaxX >= sourceMinX - 32f && candidateMinX <= sourceMaxX + 32f;
                bool nearSourceHeight = Math.Abs(candidateY - sourceY) <= 24f;

                return overlapsSourceBand && nearSourceHeight;
            }

            private bool HasClearedEdgeDropSource(PlayerCharacter player, FootholdLine sourceFh, float requiredDropPx)
            {
                if (player == null || sourceFh == null)
                {
                    return true;
                }

                float sourceY = Board.CalculateYOnFoothold(sourceFh, player.X);
                if (player.Y >= sourceY + requiredDropPx)
                {
                    return true;
                }

                float sourceMinX = Math.Min(sourceFh.FirstDot.X, sourceFh.SecondDot.X);
                float sourceMaxX = Math.Max(sourceFh.FirstDot.X, sourceFh.SecondDot.X);
                return player.X <= sourceMinX - 48f || player.X >= sourceMaxX + 48f;
            }

            private void NormalizeMobStates()
            {
                var active = _mobPool.ActiveMobs;
                if (active == null || active.Count <= 0)
                {
                    return;
                }

                foreach (var mob in active)
                {
                    if (mob?.MovementInfo == null)
                    {
                        continue;
                    }

                    var movement = mob.MovementInfo;
                    var fh = movement.CurrentFoothold
                        ?? FindNearestStandableFoothold(movement.X, movement.Y, 160f, 32f)
                        ?? FindFoothold(movement.X, movement.Y, 160f);
                    if (fh == null)
                    {
                        continue;
                    }

                    float fhY = Board.CalculateYOnFoothold(fh, movement.X);
                    if (movement.CurrentFoothold == null || Math.Abs(movement.Y - fhY) > 160f)
                    {
                        movement.CurrentFoothold = fh;
                        movement.Y = fhY;
                    }
                }
            }

            private void PublishState()
            {
                _gymServer.SendState(BuildGymState());
            }

            private GymState BuildGymState()
            {
                var player = _playerManager.Player;
                var physics = player?.Physics;
                float px = player?.X ?? 0f;
                float py = player?.Y ?? 0f;
                bool hasPendingLadderExit = _pendingLadderExitFoothold != null;
                var currentFh = physics?.CurrentFoothold;
                FootholdLine resolvedFh = null;
                if (!hasPendingLadderExit && !(currentFh == null && _edgeDropCooldownTicks > 0))
                {
                    resolvedFh = currentFh ?? TryResolveObservedFoothold(px, py, physics);
                }
                var fallStartFh = physics?.FallStartFoothold;
                bool isGrounded = !hasPendingLadderExit && (physics?.IsOnFoothold() ?? false);
                if (!isGrounded && currentFh != null)
                {
                    float fhY = Board.CalculateYOnFoothold(currentFh, px);
                    if (Math.Abs(py - fhY) <= 8f)
                    {
                        isGrounded = true;
                    }
                }

                float platformMinX = 0f;
                float platformMaxX = 0f;
                float distLeftEdge = 0f;
                float distRightEdge = 0f;
                var platformFh = resolvedFh ?? _pendingLadderExitFoothold;
                if (platformFh != null)
                {
                    platformMinX = Math.Min(platformFh.FirstDot.X, platformFh.SecondDot.X);
                    platformMaxX = Math.Max(platformFh.FirstDot.X, platformFh.SecondDot.X);
                    distLeftEdge = px - platformMinX;
                    distRightEdge = platformMaxX - px;
                }

                var nearestLadder = FindLadder(px, py, 50f);
                bool ladderOverlap =
                    nearestLadder.HasValue &&
                    Math.Abs(px - nearestLadder.Value.x) <= 6f &&
                    py >= nearestLadder.Value.top - 8 &&
                    py <= nearestLadder.Value.bottom + 8;

                var overlapPortal = _portalPool.CheckPortalCollision(px, py);
                var nearestPortal = overlapPortal ?? _portalPool.FindPortalAtPosition(px, py, 60f);
                bool portalOverlap = overlapPortal != null;

                return new GymState
                {
                    MapId = _board.MapInfo?.id ?? 0,
                    Tick = _tick,
                    Frame = _tick,
                    X = px,
                    Y = py,
                    VX = (float)(physics?.VelocityX ?? 0.0),
                    VY = hasPendingLadderExit ? -8f : (float)(physics?.VelocityY ?? 0.0),
                    Alive = player?.IsAlive ?? false,
                    Hp = player?.HP ?? 0,
                    MaxHp = player?.MaxHP ?? 0,
                    Mp = player?.MP ?? 0,
                    MaxMp = player?.MaxMP ?? 0,
                    IsGrounded = isGrounded,
                    FacingRight = player?.FacingRight ?? true,
                    CurrentFhId = hasPendingLadderExit ? -1 : (resolvedFh?.num ?? -1),
                    FallStartFhId = hasPendingLadderExit ? -1 : (fallStartFh?.num ?? -1),
                    IsOnLadder = physics?.IsOnLadder() ?? false,
                    IsOnPortal = portalOverlap,
                    DistLeftEdge = distLeftEdge,
                    DistRightEdge = distRightEdge,
                    PlatformMinX = platformMinX,
                    PlatformMaxX = platformMaxX,
                    TargetX = 0f,
                    TargetY = 0f,
                    IsDone = false,
                    NearestLadderX = nearestLadder?.x ?? 0f,
                    NearestLadderTop = nearestLadder?.top ?? 0f,
                    NearestLadderBottom = nearestLadder?.bottom ?? 0f,
                    LadderOverlap = ladderOverlap,
                    IsOverlappingLadder = ladderOverlap,
                    NearestPortalX = nearestPortal?.PortalInstance?.X ?? 0f,
                    NearestPortalY = nearestPortal?.PortalInstance?.Y ?? 0f,
                    PortalOverlap = portalOverlap,
                    IsOverlappingPortal = portalOverlap,
                    Mobs = BuildGymMobStates(px, py),
                };
            }

            private GymMobState[] BuildGymMobStates(float playerX, float playerY)
            {
                var active = _mobPool.ActiveMobs;
                if (active == null || active.Count <= 0)
                {
                    return Array.Empty<GymMobState>();
                }

                var result = new List<GymMobState>(active.Count);
                foreach (var mob in active)
                {
                    if (mob?.MovementInfo == null)
                    {
                        continue;
                    }

                    int fhId = mob.MovementInfo.CurrentFoothold?.num ?? -1;
                    if (fhId <= 0)
                    {
                        fhId = (FindNearestStandableFoothold(mob.MovementInfo.X, mob.MovementInfo.Y, 160f, 32f)
                            ?? FindFoothold(mob.MovementInfo.X, mob.MovementInfo.Y, 120f))?.num ?? -1;
                    }

                    result.Add(new GymMobState
                    {
                        MobId = SafeParseInt(mob.MobInstance?.MobInfo?.ID),
                        PoolId = mob.PoolId,
                        Name = mob.MobInstance?.MobInfo?.Name ?? (mob.MobInstance?.MobInfo?.ID ?? "mob"),
                        X = mob.MovementInfo.X,
                        Y = mob.MovementInfo.Y,
                        Alive = mob.AI?.IsDead != true,
                        CurrentHp = mob.AI?.CurrentHp ?? 0,
                        MaxHp = mob.AI?.MaxHp ?? 0,
                        FhId = fhId,
                        FacingRight = mob.MovementInfo.FlipX,
                        CanAttack = mob.AI?.State != AI.MobAIState.Death && mob.AI?.State != AI.MobAIState.Removed,
                        DistanceToPlayer = Vector2.Distance(new Vector2(playerX, playerY), new Vector2(mob.MovementInfo.X, mob.MovementInfo.Y)),
                        AttackRangePx = mob.AI?.GetCurrentAttack()?.Range ?? 60f,
                    });
                }

                return result.ToArray();
            }

            private FootholdLine TryResolveObservedFoothold(float x, float y, CVecCtrl physics)
            {
                if (physics?.IsOnLadderOrRope == true)
                {
                    return null;
                }

                var candidate = FindNearestStandableFoothold(x, y, 48f, 48f)
                    ?? FindFoothold(x, y, 36f);
                if (candidate == null)
                {
                    return null;
                }

                float candidateY = Board.CalculateYOnFoothold(candidate, x);
                bool closeToFoothold = Math.Abs(y - candidateY) <= 18f;
                bool nearZeroVy = Math.Abs((float)(physics?.VelocityY ?? 0.0)) <= 6f;
                if (!closeToFoothold || !nearZeroVy)
                {
                    return null;
                }

                return candidate;
            }

            private FootholdLine FindFoothold(float x, float y, float searchRange)
            {
                return _board.FindFootholdBelow(x, y, searchRange, upwardTolerance: 20f);
            }

            private FootholdLine FindNearestStandableFoothold(float x, float y, float verticalRange, float horizontalMargin)
            {
                var footholds = _board?.BoardItems?.FootholdLines;
                if (footholds == null || footholds.Count <= 0)
                {
                    return null;
                }

                FootholdLine best = null;
                float bestAbsDy = float.MaxValue;
                float bestXPenalty = float.MaxValue;

                foreach (var fh in footholds)
                {
                    if (fh == null || fh.IsWall)
                    {
                        continue;
                    }

                    float minX = Math.Min(fh.FirstDot.X, fh.SecondDot.X);
                    float maxX = Math.Max(fh.FirstDot.X, fh.SecondDot.X);
                    if (x < minX - horizontalMargin || x > maxX + horizontalMargin)
                    {
                        continue;
                    }

                    float fhY = Board.CalculateYOnFoothold(fh, x);
                    float absDy = Math.Abs(fhY - y);
                    if (absDy > verticalRange)
                    {
                        continue;
                    }

                    float xPenalty = 0f;
                    if (x < minX)
                    {
                        xPenalty = minX - x;
                    }
                    else if (x > maxX)
                    {
                        xPenalty = x - maxX;
                    }

                    if (absDy < bestAbsDy - 0.01f || (Math.Abs(absDy - bestAbsDy) <= 0.01f && xPenalty < bestXPenalty))
                    {
                        best = fh;
                        bestAbsDy = absDy;
                        bestXPenalty = xPenalty;
                    }
                }

                return best;
            }

            private (int x, int top, int bottom, bool isLadder)? FindLadder(float x, float y, float searchRange)
            {
                Rope best = null;
                double bestDist = double.MaxValue;

                foreach (var rope in _board.BoardItems.Ropes)
                {
                    if (rope == null)
                    {
                        continue;
                    }

                    int rx = rope.FirstAnchor.X;
                    int top = Math.Min(rope.FirstAnchor.Y, rope.SecondAnchor.Y);
                    int bottom = Math.Max(rope.FirstAnchor.Y, rope.SecondAnchor.Y);

                    if (Math.Abs(x - rx) > searchRange)
                    {
                        continue;
                    }
                    if (y < top - searchRange || y > bottom + searchRange)
                    {
                        continue;
                    }

                    double dist = Math.Abs(x - rx);
                    if (dist < bestDist)
                    {
                        best = rope;
                        bestDist = dist;
                    }
                }

                if (best == null)
                {
                    return null;
                }

                return (
                    best.FirstAnchor.X,
                    Math.Min(best.FirstAnchor.Y, best.SecondAnchor.Y),
                    Math.Max(best.FirstAnchor.Y, best.SecondAnchor.Y),
                    best.ladder);
            }

            private static int SafeParseInt(string value)
            {
                return int.TryParse(value ?? "", out int parsed) ? parsed : -1;
            }
        }

        internal static int Run(Board board, int gymPort, string spawnPortal)
        {
            try
            {
                using var world = new HeadlessSimWorld(board, gymPort, spawnPortal);
                Console.WriteLine($"[SimHeadless] 启动 map={board?.MapInfo?.id:D9} port={gymPort}");
                world.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SimHeadless] 启动失败: " + ex);
                return 1;
            }
        }
    }
}
