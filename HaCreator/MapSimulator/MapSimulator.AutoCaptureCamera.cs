using HaCreator.MapSimulator.Automation;
using HaCreator.MapSimulator.Entities;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private void BuildAutoCaptureScanPath()
        {
            _autoCaptureScanPath = new List<Point>();
            if (_renderParams.RenderWidth <= 0 || _renderParams.RenderHeight <= 0 || _renderParams.RenderObjectScaling <= 0f)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PATH_INVALID: viewport is invalid.");
            }

            int rawMinX = (int)Math.Round(_vrFieldBoundary.Left * _renderParams.RenderObjectScaling);
            int rawMinY = (int)Math.Round(_vrFieldBoundary.Top * _renderParams.RenderObjectScaling);
            int rawMaxX = (int)Math.Round(_vrFieldBoundary.Right * _renderParams.RenderObjectScaling) - _renderParams.RenderWidth;
            int rawMaxY = (int)Math.Round(_vrFieldBoundary.Bottom * _renderParams.RenderObjectScaling) - _renderParams.RenderHeight;

            int minX = Math.Min(rawMinX, rawMaxX);
            int minY = Math.Min(rawMinY, rawMaxY);
            int maxX = Math.Max(rawMinX, rawMaxX);
            int maxY = Math.Max(rawMinY, rawMaxY);
            if (maxX < minX || maxY < minY)
            {
                throw new InvalidOperationException($"E_AUTOCAP_CAMERA_PATH_INVALID: invalid map bounds min=({minX},{minY}) max=({maxX},{maxY}).");
            }

            int stepX = Math.Max(1, (int)Math.Floor(_renderParams.RenderWidth * (1d - _autoCaptureCameraPlan.GridOverlapRatioX)));
            int stepY = Math.Max(1, (int)Math.Floor(_renderParams.RenderHeight * (1d - _autoCaptureCameraPlan.GridOverlapRatioY)));
            List<int> xPoints = BuildAxisPoints(minX, maxX, stepX);
            List<int> yPoints = BuildAxisPoints(minY, maxY, stepY);
            if (xPoints.Count == 0 || yPoints.Count == 0)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PATH_INVALID: failed to build grid points.");
            }

            for (int rowIndex = 0; rowIndex < yPoints.Count; rowIndex++)
            {
                int y = yPoints[rowIndex];
                IReadOnlyList<int> rowPoints = (rowIndex % 2 == 0) ? xPoints : Enumerable.Reverse(xPoints).ToArray();
                foreach (int x in rowPoints)
                {
                    _autoCaptureScanPath.Add(new Point(x, y));
                }
            }

            if (_autoCaptureScanPath.Count == 0)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PATH_INVALID: grid path is empty.");
            }

            System.Console.WriteLine($"[AutoCap] camera_grid total_points={_autoCaptureScanPath.Count} step_x={stepX} step_y={stepY}");
        }

        private static List<int> BuildAxisPoints(int minValue, int maxValue, int step)
        {
            var points = new List<int>();
            int safeStep = Math.Max(1, step);
            if (maxValue <= minValue)
            {
                points.Add(minValue);
                return points;
            }

            for (int value = minValue; value <= maxValue; value += safeStep)
            {
                points.Add(value);
                if (value > maxValue - safeStep)
                {
                    break;
                }
            }

            if (points.Count == 0 || points[points.Count - 1] != maxValue)
            {
                points.Add(maxValue);
            }

            return points;
        }

        private void TickAutoCaptureCamera()
        {
            if (!IsAutoCaptureEnabled || !_autoCaptureStarted || _autoCaptureScanPath == null || _autoCaptureScanPath.Count == 0)
                return;

            switch (_autoCaptureCameraPhase)
            {
                case AutoCaptureCameraPhase.Init:
                    if (_autoCaptureWarmupFramesRemaining > 0)
                    {
                        _autoCaptureWarmupFramesRemaining--;
                        return;
                    }
                    _autoCaptureCameraPhase = AutoCaptureCameraPhase.MoveToPoint;
                    return;
                case AutoCaptureCameraPhase.MoveToPoint:
                    AdvanceAutoCaptureCameraOnce();
                    return;
                case AutoCaptureCameraPhase.Settling:
                    if (_autoCaptureSettleFramesRemaining > 0)
                    {
                        _autoCaptureSettleFramesRemaining--;
                        return;
                    }
                    PrepareAutoCaptureBucketForSampling();
                    _autoCaptureSampledFramesAtPoint = 0;
                    _autoCaptureCameraPhase = AutoCaptureCameraPhase.Sampling;
                    return;
                case AutoCaptureCameraPhase.Sampling:
                case AutoCaptureCameraPhase.Complete:
                    HandleAutoCaptureCompletionIfNeeded();
                    return;
                default:
                    return;
            }
        }

        private void AdvanceAutoCaptureCameraOnce()
        {
            if (_autoCaptureScanPath == null || _autoCaptureScanPath.Count == 0)
            {
                return;
            }

            int nextPointIndex = _autoCaptureCurrentPointIndex + 1;
            if (nextPointIndex >= _autoCaptureScanPath.Count)
            {
                _autoCaptureCameraPhase = AutoCaptureCameraPhase.Complete;
                HandleAutoCaptureCompletionIfNeeded();
                return;
            }

            Point p = _autoCaptureScanPath[nextPointIndex];
            mapShiftX = p.X;
            mapShiftY = p.Y;
            ClampCameraToBoundaries();
            if (mapShiftX != p.X || mapShiftY != p.Y)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PATH_INVALID: camera point clamped out of range.");
            }

            _autoCaptureCurrentPointIndex = nextPointIndex;
            _autoCaptureSettleFramesRemaining = Math.Max(0, _autoCaptureCameraPlan?.SettleFrames ?? 0);
            _autoCaptureSampledFramesAtPoint = 0;
            _autoCaptureCameraPhase = _autoCaptureSettleFramesRemaining > 0
                ? AutoCaptureCameraPhase.Settling
                : AutoCaptureCameraPhase.Sampling;
        }

        private void MarkAutoCaptureSamplingDecision()
        {
            if (_autoCaptureCameraPhase == AutoCaptureCameraPhase.Sampling)
            {
                _autoCaptureSampledFramesAtPoint++;
                if (_autoCaptureSampledFramesAtPoint >= Math.Max(1, _autoCaptureSampleFramesPerPoint))
                {
                    _autoCaptureCameraPhase = (_autoCaptureCurrentPointIndex + 1) >= _autoCaptureTotalPointCount
                        ? AutoCaptureCameraPhase.Complete
                        : AutoCaptureCameraPhase.MoveToPoint;
                    if (_autoCaptureCameraPhase == AutoCaptureCameraPhase.Complete)
                    {
                        HandleAutoCaptureCompletionIfNeeded();
                    }
                }
            }
        }

        private void HandleAutoCaptureCompletionIfNeeded()
        {
            if (_autoCaptureCameraPhase != AutoCaptureCameraPhase.Complete || _autoCaptureCompletionHandled)
            {
                return;
            }

            _autoCaptureCompletionHandled = true;
            int capturedFrames = _datasetGenerator?.CapturedFrameCount ?? 0;
            _autoCaptureLastCompleteLogFrame = capturedFrames;
            System.Console.WriteLine($"[AutoCap][complete] map={_autoCaptureOptions?.MapId:D9} res={_autoCaptureOptions?.ResolutionName} captured_frames={capturedFrames} expected_frames={_autoCaptureExpectedFrameCount} point_idx={_autoCaptureCurrentPointIndex + 1}/{_autoCaptureTotalPointCount} real_skill_fx_triggers={_autoCaptureRealSkillEffectTriggerCount}");
            try
            {
                _datasetGenerator?.StopGeneration();
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AutoCap][complete] stop_generation_failed: {ex.Message}");
            }
            this.Exit();
        }
    }
}
