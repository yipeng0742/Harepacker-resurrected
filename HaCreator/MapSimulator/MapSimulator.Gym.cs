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

        public void EnableGymControl(int port)
        {
            _gymPort = port;
            _gymEnabled = port > 0;
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
            ApplyGymAction(currentTime);
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
            GymAction action = _gymServer?.PendingAction;
            var player = _playerManager?.Player;
            if (action == null || player == null)
            {
                player?.ClearInput();
                return;
            }

            bool left = action.Left && !action.Right;
            bool right = action.Right && !action.Left;
            bool up = action.Up;
            bool down = action.Down;
            bool jump = action.Jump;
            bool attack = action.Attack;
            bool pickup = action.Pickup;

            player.SetInput(left, right, up, down, jump, attack, pickup);

            if (action.Reset && !_gymPrevReset)
            {
                _playerManager.Respawn();
            }
            _gymPrevReset = action.Reset;

            if (pickup && !_gymPrevPickup)
            {
                _playerManager.Combat?.TryPickupDrop(_dropPool, currentTime);
            }
            _gymPrevPickup = pickup;

            int skillSlot = action.SkillSlot;
            string skillToken = action.SkillToken ?? "";
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
                IsGrounded = physics?.IsOnFoothold() ?? false,
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
                Mobs = BuildGymMobStates(playerX, playerY),
            };
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
