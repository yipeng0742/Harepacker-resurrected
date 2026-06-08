using HaCreator.MapEditor.Instance.Misc;
using HaCreator.MapEditor.Instance.Shapes;
using HaCreator.MapEditor.Instance;
using HaCreator.MapSimulator.Character.Skills;
using HaCreator.MapSimulator.Entities;
using HaCreator.MapSimulator.IPC;
using HaCreator.MapSimulator.Pools;
using MapleLib.WzLib.WzStructure.Data;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private GymServer _gymServer;
        private int _gymPort;
        private bool _gymEnabled;
        private int _gymTick;
        private bool _gymPrevPickup;
        private bool _gymPrevReset;
        private int _gymPrevSkillSlot = -1;
        private string _gymPrevSkillToken = "";
        private int _gymLastMapId = -1;
        private bool _gymSpawnSettled;
        private int _gymSpawnStableFrames;
        private int _gymSpawnAdjustAttempts;

        public void EnableGymControl(int port)
        {
            _gymPort = port;
            _gymEnabled = port > 0;
            ResetGymSpawnStabilizer();
            EnsureGymServerStarted();
        }

        private void EnsureGymServerStarted()
        {
            if (!_gymEnabled || _gymPort <= 0 || _gymServer != null)
            {
                return;
            }

            _gymServer = new GymServer();
            _gymServer.Start(_gymPort);
            Console.WriteLine($"[SimGym] GymServer 已启动: 127.0.0.1:{_gymPort}");
        }

        private void ShutdownGymServer()
        {
            try
            {
                _gymServer?.Dispose();
            }
            catch
            {
            }

            _gymServer = null;
        }

        private void BeginGymControlFrame(int currentTime)
        {
            if (!_gymEnabled || _playerManager == null)
            {
                return;
            }

            EnsureGymServerStarted();
            _playerManager.IsGymControlled = true;
            SyncGymSpawnStabilizerMap();
            if (!EnsureGymPlayerRecoveredFromOutOfBounds())
            {
                _playerManager.Player?.ClearInput();
                return;
            }
            if (!EnsureGymSpawnStable())
            {
                _playerManager.Player?.ClearInput();
                return;
            }
            ApplyGymAction(currentTime);
        }

        private bool EnsureGymPlayerRecoveredFromOutOfBounds()
        {
            var player = _playerManager?.Player;
            if (player == null)
            {
                return false;
            }

            float playerX = player.X;
            float playerY = player.Y;
            if (float.IsNaN(playerX) || float.IsInfinity(playerX) || float.IsNaN(playerY) || float.IsInfinity(playerY))
            {
                AutoRecoverGymPlayer("INVALID_POSITION", playerX, playerY);
                return false;
            }

            if (!player.IsAlive)
            {
                AutoRecoverGymPlayer("PLAYER_DEAD", playerX, playerY);
                return false;
            }

            var rawVr = _mapBoard?.VRRectangle;
            float mapLeft = rawVr?.X ?? (_mapBoard?.CenterPoint.X - 5000f ?? -5000f);
            float mapRight = rawVr != null ? rawVr.X + rawVr.Width : (_mapBoard?.CenterPoint.X + 5000f ?? 5000f);
            float mapBottom = rawVr != null ? rawVr.Y + rawVr.Height : (_mapBoard?.CenterPoint.Y + 5000f ?? 5000f);
            const float horizontalOutMargin = 320f;
            const float bottomOutMargin = 240f;

            bool outOfHorizontalBounds = playerX < mapLeft - horizontalOutMargin || playerX > mapRight + horizontalOutMargin;
            bool outOfBottomBounds = playerY > mapBottom + bottomOutMargin;
            if (outOfHorizontalBounds || outOfBottomBounds)
            {
                AutoRecoverGymPlayer(outOfBottomBounds ? "OUT_OF_MAP_BOTTOM" : "OUT_OF_MAP_HORIZONTAL", playerX, playerY);
                return false;
            }

            return true;
        }

        private void AutoRecoverGymPlayer(string reason, float playerX, float playerY)
        {
            if (_playerManager == null)
            {
                return;
            }

            Console.WriteLine($"[SimGym] auto respawn map={_gymLastMapId} reason={reason} x={playerX:0.0} y={playerY:0.0}");
            _playerManager.Respawn();
            _playerManager.ForceStand();
            _playerManager.Player?.ClearInput();
            ResetGymSpawnStabilizer();
        }

        private void ResetGymSpawnStabilizer()
        {
            _gymSpawnSettled = false;
            _gymSpawnStableFrames = 0;
            _gymSpawnAdjustAttempts = 0;
        }

        private void SyncGymSpawnStabilizerMap()
        {
            int mapId = _mapBoard?.MapInfo?.id ?? 0;
            if (mapId == _gymLastMapId)
            {
                return;
            }

            _gymLastMapId = mapId;
            ResetGymSpawnStabilizer();
        }

        private bool EnsureGymSpawnStable()
        {
            if (_gymSpawnSettled || _playerManager == null || _playerManager.Player == null)
            {
                return _gymSpawnSettled;
            }

            var player = _playerManager.Player;
            var physics = player.Physics;
            float playerX = player.X;
            float playerY = player.Y;

            FootholdLine currentFh = physics?.CurrentFoothold;
            if (currentFh == null)
            {
                try
                {
                    currentFh = _playerManager.GetFootholdLookup()?.Invoke(playerX, playerY, 120f);
                }
                catch
                {
                }
            }

            float resolvedFhGap = float.MaxValue;
            if (currentFh == null)
            {
                currentFh = ResolveGymFoothold(playerX, playerY, 120f, out resolvedFhGap);
            }

            bool isGrounded = physics?.IsOnFoothold() ?? false;
            if (!isGrounded && currentFh != null && resolvedFhGap <= 8f)
            {
                isGrounded = true;
            }

            PortalItem overlapPortal = _portalPool?.CheckPortalCollision(playerX, playerY);
            bool portalOverlap = overlapPortal != null;

            if (!isGrounded && currentFh != null)
            {
                _playerManager.TeleportTo(playerX, playerY);
                _playerManager.ForceStand();
                _gymSpawnStableFrames = 0;
                _gymSpawnAdjustAttempts++;
                return false;
            }

            if (portalOverlap && currentFh != null)
            {
                float fhMinX = Math.Min(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float fhMaxX = Math.Max(currentFh.FirstDot.X, currentFh.SecondDot.X);
                float leftRoom = Math.Max(0f, playerX - fhMinX);
                float rightRoom = Math.Max(0f, fhMaxX - playerX);
                const float portalClearanceX = 64f;
                const float edgeMarginX = 8f;
                float targetX;
                if (rightRoom >= leftRoom)
                {
                    targetX = Math.Min(fhMaxX - edgeMarginX, playerX + portalClearanceX);
                }
                else
                {
                    targetX = Math.Max(fhMinX + edgeMarginX, playerX - portalClearanceX);
                }

                if (Math.Abs(targetX - playerX) >= 2f)
                {
                    _playerManager.TeleportTo(targetX, playerY);
                    _playerManager.ForceStand();
                    _gymSpawnStableFrames = 0;
                    _gymSpawnAdjustAttempts++;
                    return false;
                }
            }

            if (isGrounded && !portalOverlap)
            {
                _gymSpawnStableFrames++;
                if (_gymSpawnStableFrames >= 3)
                {
                    _gymSpawnSettled = true;
                    _playerManager.ForceStand();
                    if (_gymSpawnAdjustAttempts > 0)
                    {
                        Console.WriteLine($"[SimGym] spawn stabilized map={_gymLastMapId} attempts={_gymSpawnAdjustAttempts}");
                    }
                    return true;
                }
                return false;
            }

            _gymSpawnStableFrames = 0;
            return false;
        }

        private void EndGymControlFrame()
        {
            if (!_gymEnabled || _playerManager == null)
            {
                return;
            }

            PublishGymState();
        }

        private void ApplyGymAction(int currentTime)
        {
            GymAction action = _gymServer?.SnapshotAction();
            GymAction momentary = _gymServer?.ConsumeMomentaryAction();
            var player = _playerManager?.Player;
            if (action == null || player == null)
            {
                player?.ClearInput();
                return;
            }

            bool left = (action.Left || (momentary?.Left ?? false)) && !(action.Right || (momentary?.Right ?? false));
            bool right = (action.Right || (momentary?.Right ?? false)) && !(action.Left || (momentary?.Left ?? false));
            bool up = action.Up || (momentary?.Up ?? false);
            bool down = action.Down || (momentary?.Down ?? false);
            bool jump = (momentary?.Jump ?? false) || action.Jump;
            bool attack = (momentary?.Attack ?? false) || action.Attack;
            bool pickup = (momentary?.Pickup ?? false) || action.Pickup;

            if (jump || down)
            {
                Console.WriteLine(
                    $"[SimGym.Action] tick={_gymTick} left={left} right={right} up={up} down={down} " +
                    $"jump={jump} action_down={action.Down} action_jump={action.Jump} " +
                    $"momentary_down={(momentary?.Down ?? false)} momentary_jump={(momentary?.Jump ?? false)}");
            }

            player.SetInput(left, right, up, down, jump, attack, pickup);

            bool reset = (momentary?.Reset ?? false) || action.Reset;
            if (reset && !_gymPrevReset)
            {
                bool usedTargetRespawn = false;
                if (_playerManager != null)
                {
                    float tx = action.TargetX;
                    float ty = action.TargetY;
                    if (!float.IsNaN(tx) && !float.IsInfinity(tx) && !float.IsNaN(ty) && !float.IsInfinity(ty))
                    {
                        _playerManager.RespawnAt(tx, ty);
                        _playerManager.ForceStand();
                        ResetGymSpawnStabilizer();
                        usedTargetRespawn = true;
                    }
                }
                if (!usedTargetRespawn)
                {
                    _playerManager.Respawn();
                    _playerManager.ForceStand();
                    ResetGymSpawnStabilizer();
                }
            }
            _gymPrevReset = reset;

            if (pickup && !_gymPrevPickup)
            {
                _playerManager.Combat?.TryPickupDrop(_dropPool, currentTime);
            }
            _gymPrevPickup = pickup;

            int skillSlot = (momentary?.SkillSlot ?? -1) >= 0 ? momentary.SkillSlot : action.SkillSlot;
            string skillToken = !string.IsNullOrWhiteSpace(momentary?.SkillToken)
                ? momentary.SkillToken
                : (action.SkillToken ?? "");
            bool skillChanged = skillSlot != _gymPrevSkillSlot || !string.Equals(skillToken, _gymPrevSkillToken, StringComparison.Ordinal);
            if (skillChanged && (_playerManager.Skills != null))
            {
                if (skillSlot >= 0)
                {
                    _playerManager.Skills.TryCastHotkey(skillSlot, currentTime);
                }
                else if (!string.IsNullOrWhiteSpace(skillToken) && int.TryParse(skillToken, out int skillId))
                {
                    _playerManager.Skills.TryCastSkill(skillId, currentTime);
                }
            }
            _gymPrevSkillSlot = skillSlot;
            _gymPrevSkillToken = skillToken;

            if (up)
            {
                TryGymPortalInteract();
            }
        }

        private void TryGymPortalInteract()
        {
            if (_gameState.PendingMapChange || _playerManager == null || !_playerManager.IsPlayerActive)
            {
                return;
            }

            var playerPos = _playerManager.GetPlayerPosition();
            float playerX = playerPos.X;
            float playerY = playerPos.Y;
            const int portalRangeX = 40;
            const int portalRangeY = 60;

            PortalItem nearestPortal = null;
            float nearestDistance = float.MaxValue;
            if (_portalsArray != null)
            {
                for (int i = 0; i < _portalsArray.Length; i++)
                {
                    PortalItem portal = _portalsArray[i];
                    PortalInstance instance = portal?.PortalInstance;
                    if (instance == null || instance.tm <= 0 || instance.tm == MapConstants.MaxMap)
                    {
                        continue;
                    }

                    float dx = Math.Abs(playerX - instance.X);
                    float dy = Math.Abs(playerY - instance.Y);
                    if (dx <= portalRangeX && dy <= portalRangeY)
                    {
                        float distance = dx + dy;
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestPortal = portal;
                        }
                    }
                }
            }

            if (nearestPortal != null)
            {
                PlayPortalSE();
                _playerManager.ForceStand();
                _gameState.PendingMapChange = true;
                _gameState.PendingMapId = nearestPortal.PortalInstance.tm;
                _gameState.PendingPortalName = nearestPortal.PortalInstance.tn;
                return;
            }

            PortalInstance nearestHiddenPortal = null;
            nearestDistance = float.MaxValue;
            foreach (var portal in _mapBoard.BoardItems.Portals)
            {
                if (portal.tm <= 0 || portal.tm == MapConstants.MaxMap)
                {
                    continue;
                }

                int rangeX = portal.hRange ?? portalRangeX;
                int rangeY = portal.vRange ?? portalRangeY;
                float dx = Math.Abs(playerX - portal.X);
                float dy = Math.Abs(playerY - portal.Y);
                if (dx <= rangeX && dy <= rangeY)
                {
                    float distance = dx + dy;
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestHiddenPortal = portal;
                    }
                }
            }

            if (nearestHiddenPortal != null)
            {
                PlayPortalSE();
                _playerManager.ForceStand();
                _gameState.PendingMapChange = true;
                _gameState.PendingMapId = nearestHiddenPortal.tm;
                _gameState.PendingPortalName = nearestHiddenPortal.tn;
            }
        }

        private void PublishGymState()
        {
            if (!_gymEnabled || _gymServer == null)
            {
                return;
            }

            _gymTick++;
            _gymServer.SendState(BuildGymState());
        }

        private GymState BuildGymState()
        {
            var player = _playerManager?.Player;
            var physics = player?.Physics;
            float playerX = player?.X ?? 0f;
            float playerY = player?.Y ?? 0f;
            FootholdLine currentFh = physics?.CurrentFoothold;
            FootholdLine fallStartFh = physics?.FallStartFoothold;
            if (currentFh == null)
            {
                try
                {
                    currentFh = _playerManager?.GetFootholdLookup()?.Invoke(playerX, playerY, 120f);
                }
                catch
                {
                }
            }

            float resolvedFhGap = float.MaxValue;
            if (currentFh == null)
            {
                currentFh = ResolveGymFoothold(playerX, playerY, 120f, out resolvedFhGap);
            }

            bool isGrounded = physics?.IsOnFoothold() ?? false;
            if (!isGrounded && currentFh != null && resolvedFhGap <= 8f)
            {
                isGrounded = true;
            }
            float platformMinX = 0f;
            float platformMaxX = 0f;
            float distLeftEdge = 0f;
            float distRightEdge = 0f;
            if (currentFh != null)
            {
                platformMinX = Math.Min(currentFh.FirstDot.X, currentFh.SecondDot.X);
                platformMaxX = Math.Max(currentFh.FirstDot.X, currentFh.SecondDot.X);
                distLeftEdge = playerX - platformMinX;
                distRightEdge = platformMaxX - playerX;
            }

            var nearestLadder = _playerManager?.GetLadderLookup()?.Invoke(playerX, playerY, 50f);
            bool ladderOverlap = nearestLadder.HasValue && Math.Abs(playerX - nearestLadder.Value.x) <= 6f && playerY >= nearestLadder.Value.top - 8 && playerY <= nearestLadder.Value.bottom + 8;
            PortalItem overlapPortal = _portalPool?.CheckPortalCollision(playerX, playerY);
            PortalItem nearestPortal = overlapPortal ?? _portalPool?.FindPortalAtPosition(playerX, playerY, 60f);
            bool portalOverlap = overlapPortal != null;

            return new GymState
            {
                MapId = _mapBoard?.MapInfo?.id ?? 0,
                Tick = _gymTick,
                Frame = _frameNumber,
                X = playerX,
                Y = playerY,
                VX = (float)(physics?.VelocityX ?? 0.0),
                VY = (float)(physics?.VelocityY ?? 0.0),
                Alive = player?.IsAlive ?? false,
                Hp = player?.HP ?? 0,
                MaxHp = player?.MaxHP ?? 0,
                Mp = player?.MP ?? 0,
                MaxMp = player?.MaxMP ?? 0,
                IsGrounded = isGrounded,
                FacingRight = player?.FacingRight ?? true,
                CurrentFhId = currentFh?.num ?? -1,
                FallStartFhId = fallStartFh?.num ?? -1,
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
                IsInSwimArea = physics?.IsInSwimArea ?? false,
                IsJumpingDown = physics?.IsJumpingDown ?? false,
                CurrentFhCantThrough = currentFh?.CantThrough == MapleLib.WzLib.WzStructure.MapleBool.True,
                PhysicsMoveAction = (physics?.CurrentAction ?? Physics.MoveAction.Stand).ToString(),
                PhysicsJumpState = (physics?.CurrentJumpState ?? Physics.JumpState.None).ToString(),
                PlayerState = (player?.State ?? Character.PlayerState.Standing).ToString(),
                CharacterAction = (player?.CurrentAction ?? HaCreator.MapSimulator.Character.CharacterAction.Stand1).ToString(),
                Mobs = BuildGymMobStates(playerX, playerY),
            };
        }

        private FootholdLine ResolveGymFoothold(float x, float y, float searchRange, out float bestAbsDist)
        {
            bestAbsDist = float.MaxValue;
            var footholds = _mapBoard?.BoardItems?.FootholdLines;
            if (footholds == null || footholds.Count == 0)
            {
                return null;
            }

            FootholdLine bestFh = null;
            const float upwardTolerance = 10f;
            const float edgeTolerance = 2f;

            foreach (var fh in footholds)
            {
                if (fh == null || fh.IsWall)
                {
                    continue;
                }

                float fhMinX = Math.Min(fh.FirstDot.X, fh.SecondDot.X) - edgeTolerance;
                float fhMaxX = Math.Max(fh.FirstDot.X, fh.SecondDot.X) + edgeTolerance;
                if (x < fhMinX || x > fhMaxX)
                {
                    continue;
                }

                float dx = fh.SecondDot.X - fh.FirstDot.X;
                float dy = fh.SecondDot.Y - fh.FirstDot.Y;
                float t = (Math.Abs(dx) > 0.0001f) ? (x - fh.FirstDot.X) / dx : 0f;
                t = Math.Max(0f, Math.Min(1f, t));
                float fhY = fh.FirstDot.Y + t * dy;
                float dist = fhY - y;
                float absDist = Math.Abs(dist);

                if ((dist >= 0f && dist <= searchRange) || (dist < 0f && -dist <= upwardTolerance))
                {
                    if (absDist < bestAbsDist)
                    {
                        bestAbsDist = absDist;
                        bestFh = fh;
                    }
                }
            }

            return bestFh;
        }

        private GymMobState[] BuildGymMobStates(float playerX, float playerY)
        {
            IReadOnlyList<Entities.MobItem> mobs = _mobPool?.ActiveMobs;
            if (mobs == null || mobs.Count == 0)
            {
                return Array.Empty<GymMobState>();
            }

            var states = new List<GymMobState>(mobs.Count);
            foreach (var mob in mobs)
            {
                if (mob == null)
                {
                    continue;
                }

                int mobX = mob.CurrentX;
                int mobY = mob.CurrentY;
                bool alive = mob.AI?.IsDead != true;
                float distance = (float)Math.Sqrt(Math.Pow(mobX - playerX, 2) + Math.Pow(mobY - playerY, 2));
                states.Add(new GymMobState
                {
                    MobId = int.TryParse(mob.MobInstance?.MobInfo?.ID, out int mobId) ? mobId : 0,
                    PoolId = mob.PoolId,
                    Name = mob.MobInstance?.MobInfo?.Name ?? "",
                    X = mobX,
                    Y = mobY,
                    Alive = alive,
                    CurrentHp = mob.AI?.CurrentHp ?? 0,
                    MaxHp = mob.AI?.MaxHp ?? 0,
                    FhId = ResolveMobFootholdId(mob),
                    FacingRight = !(mob.MovementInfo?.FlipX ?? false),
                    CanAttack = alive && distance <= 90f,
                    DistanceToPlayer = distance,
                    AttackRangePx = 90f,
                });
            }

            return states.ToArray();
        }

        private int ResolveMobFootholdId(Entities.MobItem mob)
        {
            try
            {
                var fh = _playerManager?.GetFootholdLookup()?.Invoke(mob.CurrentX, mob.CurrentY, 120f);
                return fh?.num ?? -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
