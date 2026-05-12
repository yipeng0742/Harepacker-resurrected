using HaCreator.MapSimulator.Entities;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private void IncrementAutoCaptureSaveFailReason(string reason)
        {
            string key = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
            if (_autoCaptureSaveFailByReason.TryGetValue(key, out int count))
            {
                _autoCaptureSaveFailByReason[key] = count + 1;
            }
            else
            {
                _autoCaptureSaveFailByReason[key] = 1;
            }
        }

        private static Rectangle ClampRectToFrame(Rectangle rect, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return Rectangle.Empty;
            }

            int left = Math.Max(0, rect.Left);
            int top = Math.Max(0, rect.Top);
            int right = Math.Min(width, rect.Right);
            int bottom = Math.Min(height, rect.Bottom);
            if (right <= left || bottom <= top)
            {
                return Rectangle.Empty;
            }

            return new Rectangle(left, top, right - left, bottom - top);
        }

        private static bool IsUsableCaptureRect(Rectangle rect, int width, int height)
        {
            Rectangle clipped = ClampRectToFrame(rect, width, height);
            return clipped.Width >= 2 && clipped.Height >= 2;
        }

        private static int CountUsableCaptureRects(List<(int classId, Rectangle bounds)> boundsList, int width, int height)
        {
            if (boundsList == null || boundsList.Count == 0)
            {
                return 0;
            }

            int count = 0;
            foreach (var item in boundsList)
            {
                if (IsUsableCaptureRect(item.bounds, width, height))
                {
                    count++;
                }
            }

            return count;
        }

        private bool TryInjectFallbackMobBox(ref List<(int classId, Rectangle bounds)> boundsList, int width, int height)
        {
            if (_mobPool?.ActiveMobs == null || _mobPool.ActiveMobs.Count == 0 || width <= 0 || height <= 0)
            {
                return false;
            }

            int mapCenterX = _mapBoard?.CenterPoint.X ?? _mapCenterX;
            int mapCenterY = _mapBoard?.CenterPoint.Y ?? _mapCenterY;
            float scale = Math.Max(1f, _renderParams.RenderObjectScaling);
            int synthW = Math.Max(18, (int)Math.Round(34f * scale));
            int synthH = Math.Max(18, (int)Math.Round(30f * scale));
            int frameCenterX = width / 2;
            int frameCenterY = height / 2;

            Rectangle best = Rectangle.Empty;
            double bestDist2 = double.MaxValue;
            foreach (var mob in _mobPool.ActiveMobs)
            {
                if (mob == null)
                {
                    continue;
                }

                var realRect = mob.GetScreenBounds(mapShiftX, mapShiftY, mapCenterX, mapCenterY, _renderParams.RenderObjectScaling);
                if (realRect != null)
                {
                    Rectangle clippedReal = ClampRectToFrame(realRect.Value, width, height);
                    if (clippedReal.Width > 2 && clippedReal.Height > 2)
                    {
                        best = clippedReal;
                        break;
                    }
                }

                int sx = (int)Math.Round((double)mob.CurrentX - mapShiftX + mapCenterX);
                int sy = (int)Math.Round((double)mob.CurrentY - mapShiftY + mapCenterY);
                Rectangle synth = new Rectangle(
                    sx - (synthW / 2),
                    sy - (int)Math.Round(synthH * 0.85f),
                    synthW,
                    synthH);
                Rectangle clippedSynth = ClampRectToFrame(synth, width, height);
                if (clippedSynth.Width <= 2 || clippedSynth.Height <= 2)
                {
                    continue;
                }

                int cx = clippedSynth.Left + clippedSynth.Width / 2;
                int cy = clippedSynth.Top + clippedSynth.Height / 2;
                double dx = cx - frameCenterX;
                double dy = cy - frameCenterY;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDist2)
                {
                    bestDist2 = d2;
                    best = clippedSynth;
                }
            }

            if (best.Width <= 2 || best.Height <= 2)
            {
                return false;
            }

            if (boundsList == null)
            {
                boundsList = new List<(int classId, Rectangle bounds)>();
            }

            boundsList.Add((AutoCapClassMobActive, best));
            return true;
        }
    }
}
