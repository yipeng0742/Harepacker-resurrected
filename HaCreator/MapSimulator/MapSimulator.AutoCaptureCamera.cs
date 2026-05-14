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

            GetAutoCaptureCameraBounds(out int minX, out int maxX, out int minY, out int maxY);
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
                    Point normalized = ClampAutoCaptureCameraPoint(new Point(x, y));
                    if (_autoCaptureScanPath.Count == 0 || _autoCaptureScanPath[_autoCaptureScanPath.Count - 1] != normalized)
                    {
                        _autoCaptureScanPath.Add(normalized);
                    }
                }
            }

            if (_autoCaptureScanPath.Count == 0)
            {
                throw new InvalidOperationException("E_AUTOCAP_CAMERA_PATH_INVALID: grid path is empty.");
            }

            System.Console.WriteLine($"[AutoCap] camera_grid total_points={_autoCaptureScanPath.Count} step_x={stepX} step_y={stepY}");
        }

        private void GetAutoCaptureCameraBounds(out int minX, out int maxX, out int minY, out int maxY)
        {
            float scale = _renderParams.RenderObjectScaling;

            int leftRightVRDifference = (int)((_vrFieldBoundary.Right - _vrFieldBoundary.Left) * scale);
            if (leftRightVRDifference < _renderParams.RenderWidth)
            {
                int centeredX = ((leftRightVRDifference / 2) + (int)(_vrFieldBoundary.Left * scale)) - (_renderParams.RenderWidth / 2);
                minX = centeredX;
                maxX = centeredX;
            }
            else
            {
                minX = (int)(_vrFieldBoundary.Left * scale);
                maxX = (int)(_vrFieldBoundary.Right - (_renderParams.RenderWidth / scale));
            }

            int topDownVRDifference = (int)((_vrFieldBoundary.Bottom - _vrFieldBoundary.Top) * scale);
            if (topDownVRDifference < _renderParams.RenderHeight)
            {
                int centeredY = ((topDownVRDifference / 2) + (int)(_vrFieldBoundary.Top * scale)) - (_renderParams.RenderHeight / 2);
                minY = centeredY;
                maxY = centeredY;
            }
            else
            {
                minY = (int)(_vrFieldBoundary.Top * scale);
                maxY = (int)(_vrFieldBoundary.Bottom - (_renderParams.RenderHeight / scale));
            }
        }

        private Point ClampAutoCaptureCameraPoint(Point point)
        {
            GetAutoCaptureCameraBounds(out int minX, out int maxX, out int minY, out int maxY);
            int clampedX = Math.Max(minX, Math.Min(maxX, point.X));
            int clampedY = Math.Max(minY, Math.Min(maxY, point.Y));
            return new Point(clampedX, clampedY);
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
            Point applied = ClampAutoCaptureCameraPoint(p);
            mapShiftX = applied.X;
            mapShiftY = applied.Y;
            ClampCameraToBoundaries();
            Point finalPoint = new Point(mapShiftX, mapShiftY);
            if (finalPoint != p)
            {
                System.Console.WriteLine($"[AutoCap][camera_path_fixup] map={_autoCaptureOptions?.MapId:D9} res={_autoCaptureOptions?.ResolutionName} point_idx={nextPointIndex + 1}/{_autoCaptureScanPath.Count} requested=({p.X},{p.Y}) applied=({finalPoint.X},{finalPoint.Y})");
                _autoCaptureScanPath[nextPointIndex] = finalPoint;
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
            ExportAutoCaptureCaptureSummary();
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
