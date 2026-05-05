using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HaCreator.MapSimulator
{
    /// <summary>
    /// 简化版数据采集器：
    /// 1) F4 开关采集
    /// 2) 定时截帧
    /// 3) 输出 PNG + YOLO 标签
    /// </summary>
    public class DatasetGenerator
    {
        private const int DefaultCaptureIntervalMs = 120;
        private const float YoloEdgeEpsilon = 1e-6f;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private int _lastCaptureTick = Environment.TickCount;
        private int _frameIndex = 0;
        private bool _isGenerating = false;

        private string _sessionDir;
        private string _imageDir;
        private string _labelDir;

        public bool IsGenerating => _isGenerating;
        public int CapturedFrameCount => _frameIndex;

        public void ConfigureOutputDirectory(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
                throw new ArgumentException("outputDir is empty.", nameof(outputDir));

            _sessionDir = outputDir;
            _imageDir = Path.Combine(_sessionDir, "images");
            _labelDir = Path.Combine(_sessionDir, "labels");
            Directory.CreateDirectory(_imageDir);
            Directory.CreateDirectory(_labelDir);
        }

        public void StartGeneration()
        {
            if (_isGenerating)
                return;

            EnsureOutputDirectories();
            _isGenerating = true;
            _lastCaptureTick = Environment.TickCount;
            Console.WriteLine($"[DatasetGenerator] 开始采集，输出目录: {_sessionDir}");
        }

        public void StopGeneration()
        {
            if (!_isGenerating)
                return;
            _isGenerating = false;
            Console.WriteLine("[DatasetGenerator] 已停止采集。");
        }

        public void ToggleGeneration()
        {
            if (_isGenerating) StopGeneration();
            else StartGeneration();
        }

        public bool ShouldCaptureFrame()
        {
            int now = Environment.TickCount;
            int elapsed = unchecked(now - _lastCaptureTick);
            if (elapsed < DefaultCaptureIntervalMs)
            {
                return false;
            }

            _lastCaptureTick = now;
            return true;
        }

        public void SaveFrameAndLabels(GraphicsDevice graphicsDevice, List<(int classId, Rectangle bounds)> boundsList)
        {
            if (graphicsDevice == null)
                return;

            EnsureOutputDirectories();

            int width = graphicsDevice.PresentationParameters.BackBufferWidth;
            int height = graphicsDevice.PresentationParameters.BackBufferHeight;
            if (width <= 0 || height <= 0)
                return;

            string frameName = $"frame_{_frameIndex:D8}";
            string imagePath = Path.Combine(_imageDir, $"{frameName}.png");
            string labelPath = Path.Combine(_labelDir, $"{frameName}.txt");

            try
            {
                var data = new Microsoft.Xna.Framework.Color[width * height];
                graphicsDevice.GetBackBufferData(data);

                using (var texture = new Texture2D(graphicsDevice, width, height, false, SurfaceFormat.Color))
                {
                    texture.SetData(data);
                    using (var fs = File.Create(imagePath))
                    {
                        texture.SaveAsPng(fs, width, height);
                    }
                }

                var sb = new StringBuilder();
                if (boundsList != null)
                {
                    foreach (var (classId, rawRect) in boundsList)
                    {
                        var rect = ClampToFrame(rawRect, width, height);
                        if (rect.Width <= 1 || rect.Height <= 1)
                            continue;

                        if (!TryBuildYoloBox(rect, width, height, out float cx, out float cy, out float bw, out float bh))
                            continue;

                        sb.Append(classId.ToString(CultureInfo.InvariantCulture)).Append(' ')
                            .Append(cx.ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                            .Append(cy.ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                            .Append(bw.ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                            .Append(bh.ToString("0.########", CultureInfo.InvariantCulture)).AppendLine();
                    }
                }

                File.WriteAllText(labelPath, sb.ToString(), Utf8NoBom);
                _frameIndex++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatasetGenerator] 保存失败: {ex.Message}");
            }
        }

        private void EnsureOutputDirectories()
        {
            if (!string.IsNullOrEmpty(_sessionDir))
                return;

            string root = Path.Combine(Environment.CurrentDirectory, "dataset_output");
            string sessionName = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _sessionDir = Path.Combine(root, sessionName);
            _imageDir = Path.Combine(_sessionDir, "images");
            _labelDir = Path.Combine(_sessionDir, "labels");

            Directory.CreateDirectory(_imageDir);
            Directory.CreateDirectory(_labelDir);
        }

        private static Rectangle ClampToFrame(Rectangle rect, int width, int height)
        {
            int left = Math.Max(0, rect.Left);
            int top = Math.Max(0, rect.Top);
            int right = Math.Min(width, rect.Right);
            int bottom = Math.Min(height, rect.Bottom);

            if (right <= left || bottom <= top)
                return Rectangle.Empty;

            return new Rectangle(left, top, right - left, bottom - top);
        }

        private static bool TryBuildYoloBox(Rectangle rect, int width, int height, out float cx, out float cy, out float bw, out float bh)
        {
            cx = cy = bw = bh = 0f;
            if (width <= 0 || height <= 0)
                return false;

            float x1 = rect.Left / (float)width;
            float y1 = rect.Top / (float)height;
            float x2 = rect.Right / (float)width;
            float y2 = rect.Bottom / (float)height;

            x1 = Math.Clamp(x1, 0f, 1f);
            y1 = Math.Clamp(y1, 0f, 1f);
            x2 = Math.Clamp(x2, 0f, 1f);
            y2 = Math.Clamp(y2, 0f, 1f);

            if (x1 <= 0f) x1 = YoloEdgeEpsilon;
            if (y1 <= 0f) y1 = YoloEdgeEpsilon;
            if (x2 >= 1f) x2 = 1f - YoloEdgeEpsilon;
            if (y2 >= 1f) y2 = 1f - YoloEdgeEpsilon;

            bw = x2 - x1;
            bh = y2 - y1;
            if (bw <= YoloEdgeEpsilon || bh <= YoloEdgeEpsilon)
                return false;

            cx = (x1 + x2) * 0.5f;
            cy = (y1 + y2) * 0.5f;
            cx = Math.Clamp(cx, YoloEdgeEpsilon, 1f - YoloEdgeEpsilon);
            cy = Math.Clamp(cy, YoloEdgeEpsilon, 1f - YoloEdgeEpsilon);
            return true;
        }
    }
}
