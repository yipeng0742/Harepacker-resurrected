using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HaCreator.MapSimulator
{
    /// <summary>
    /// AutoCap dataset writer:
    /// 1) timed capture gate
    /// 2) render-thread capture + encode
    /// 3) async disk write via bounded queue
    ///
    /// Stable path:
    /// - no soft recover-wait state
    /// - hard recover inside same capture call (recreate + one retry)
    /// </summary>
    public sealed class DatasetGenerator
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

        private int _writerThreads = 4;
        private int _queueCapacity = 128;
        private BlockingCollection<FrameWriteItem> _writeQueue;
        private CancellationTokenSource _writerCts;
        private Task[] _writerTasks;
        private long _directWriteFallbackCount;
        private long _captureFailureCount;

        private int[] _backBufferData;
        private Texture2D _captureTexture;
        private int _captureTextureWidth;
        private int _captureTextureHeight;

        private int _consecutiveCaptureFailures = 0;
        private int _lastFailureLogTick = int.MinValue / 2;
        private string _lastSaveFailureReason = "none";

        public bool IsGenerating => _isGenerating;
        public int CapturedFrameCount => Volatile.Read(ref _frameIndex);
        public int WriterThreadsEffective => _writerThreads;
        public int WriterQueueCapacityEffective => _queueCapacity;
        public int ConsecutiveCaptureFailures => Volatile.Read(ref _consecutiveCaptureFailures);
        public string LastSaveFailureReason => string.IsNullOrWhiteSpace(_lastSaveFailureReason) ? "none" : _lastSaveFailureReason;
        public string CaptureStateName => _isGenerating ? "Capturing" : "Uninitialized";

        public void ConfigureOutputDirectory(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                throw new ArgumentException("outputDir is empty.", nameof(outputDir));
            }

            _sessionDir = outputDir;
            _imageDir = Path.Combine(_sessionDir, "images");
            _labelDir = Path.Combine(_sessionDir, "labels");
            Directory.CreateDirectory(_imageDir);
            Directory.CreateDirectory(_labelDir);
        }

        public void ConfigureWriter(int threads, int queueCapacity)
        {
            _writerThreads = Math.Max(1, threads);
            _queueCapacity = Math.Max(16, queueCapacity);
        }

        // Kept for compatibility with runtime options; no soft backoff state is used anymore.
        public void ConfigureRecoverBackoff(int[] backoffMs)
        {
            _ = backoffMs;
        }

        public void StartGeneration()
        {
            if (_isGenerating)
            {
                return;
            }

            EnsureOutputDirectories();
            StartWriterWorkers();

            _isGenerating = true;
            _lastCaptureTick = Environment.TickCount;
            _lastSaveFailureReason = "none";
            _consecutiveCaptureFailures = 0;

            Console.WriteLine($"[DatasetGenerator] 开始采集，输出目录: {_sessionDir}");
            Console.WriteLine($"[DatasetGenerator] writer threads={_writerThreads}, queue_capacity={_queueCapacity}");
        }

        public void StopGeneration()
        {
            if (!_isGenerating)
            {
                return;
            }

            _isGenerating = false;
            StopWriterWorkers();
            DisposeCaptureTexture();
            _consecutiveCaptureFailures = 0;
            _lastSaveFailureReason = "none";
            Console.WriteLine("[DatasetGenerator] 已停止采集。");
        }

        public void ToggleGeneration()
        {
            if (_isGenerating)
            {
                StopGeneration();
            }
            else
            {
                StartGeneration();
            }
        }

        public bool IsCaptureDue()
        {
            int now = Environment.TickCount;
            int elapsed = unchecked(now - _lastCaptureTick);
            return elapsed >= DefaultCaptureIntervalMs;
        }

        public void MarkCaptureConsumed()
        {
            _lastCaptureTick = Environment.TickCount;
        }

        public bool ShouldCaptureFrame()
        {
            if (!IsCaptureDue())
            {
                return false;
            }

            MarkCaptureConsumed();
            return true;
        }

        public bool TrySaveFrameAndLabels(
            GraphicsDevice graphicsDevice,
            List<(int classId, Rectangle bounds)> boundsList,
            out string failReason)
        {
            failReason = "none";

            if (!_isGenerating)
            {
                failReason = "not_generating";
                return false;
            }

            if (graphicsDevice == null)
            {
                RegisterCaptureFailure("gd_null", null);
                failReason = "gd_null";
                return false;
            }

            EnsureOutputDirectories();
            EnsureWriterWorkersAlive();

            int width = graphicsDevice.PresentationParameters.BackBufferWidth;
            int height = graphicsDevice.PresentationParameters.BackBufferHeight;
            if (width <= 0 || height <= 0)
            {
                RegisterCaptureFailure("invalid_size", null);
                failReason = "invalid_size";
                return false;
            }

            // Attempt 1
            if (!TryCapturePngBytes(graphicsDevice, width, height, out byte[] pngBytes, out string reason1, out Exception ex1))
            {
                // Hard recover: force rebuild + retry once in same capture call.
                DisposeCaptureTexture();
                if (!TryCapturePngBytes(graphicsDevice, width, height, out pngBytes, out string reason2, out Exception ex2))
                {
                    string finalReason = string.IsNullOrWhiteSpace(reason2) ? reason1 : reason2;
                    Exception finalEx = ex2 ?? ex1;
                    RegisterCaptureFailure(finalReason, finalEx);
                    failReason = finalReason;
                    return false;
                }
            }

            int frameNo = Volatile.Read(ref _frameIndex);
            string frameName = $"frame_{frameNo:D8}";
            string imagePath = Path.Combine(_imageDir, $"{frameName}.png");
            string labelPath = Path.Combine(_labelDir, $"{frameName}.txt");
            string labelText = BuildLabelText(boundsList, width, height);
            var item = new FrameWriteItem(imagePath, labelPath, pngBytes, labelText);

            try
            {
                if (_writeQueue == null || !_writeQueue.TryAdd(item))
                {
                    WriteFrameToDisk(item);
                    long fallbackCount = Interlocked.Increment(ref _directWriteFallbackCount);
                    if (fallbackCount % 50 == 1)
                    {
                        Console.WriteLine($"[DatasetGenerator][警告] writer queue saturated, fallback sync writes={fallbackCount}");
                    }
                }
            }
            catch (Exception ex)
            {
                RegisterCaptureFailure("disk_write_fail", ex);
                failReason = "disk_write_fail";
                return false;
            }

            RegisterCaptureSuccess();
            Interlocked.Increment(ref _frameIndex);
            failReason = "none";
            return true;
        }

        private bool TryCapturePngBytes(
            GraphicsDevice graphicsDevice,
            int width,
            int height,
            out byte[] pngBytes,
            out string reason,
            out Exception ex)
        {
            pngBytes = null;
            reason = "none";
            ex = null;

            if (!TryEnsureCaptureTexture(graphicsDevice, width, height, out reason))
            {
                return false;
            }

            try
            {
                if (_captureTexture == null)
                {
                    reason = "capture_texture_null";
                    return false;
                }
                if (_captureTexture.IsDisposed)
                {
                    reason = "capture_texture_disposed";
                    return false;
                }
                if (_backBufferData == null)
                {
                    reason = "backbuffer_null";
                    return false;
                }

                if (!TryReadBackBufferData(graphicsDevice, _backBufferData, out string backBufferReason, out Exception backBufferEx))
                {
                    reason = backBufferReason;
                    ex = backBufferEx;
                    return false;
                }
                _captureTexture.SetData(_backBufferData);

                using (var ms = new MemoryStream(256 * 1024))
                {
                    _captureTexture.SaveAsPng(ms, width, height);
                    pngBytes = ms.ToArray();
                }

                if (pngBytes == null || pngBytes.Length == 0)
                {
                    reason = "png_empty";
                    return false;
                }

                return true;
            }
            catch (Exception captureEx)
            {
                ex = captureEx;
                reason = ClassifyCaptureException(captureEx);
                return false;
            }
        }

        private static bool TryReadBackBufferData(
            GraphicsDevice graphicsDevice,
            int[] backBufferData,
            out string reason,
            out Exception ex)
        {
            reason = "none";
            ex = null;

            if (graphicsDevice == null)
            {
                reason = "gd_null";
                return false;
            }

            if (backBufferData == null)
            {
                reason = "backbuffer_null";
                return false;
            }

            try
            {
                var activeTargets = graphicsDevice.GetRenderTargets();
                if (activeTargets != null && activeTargets.Length > 0)
                {
                    graphicsDevice.SetRenderTarget(null);
                }

                graphicsDevice.GetBackBufferData(backBufferData);
                return true;
            }
            catch (Exception readEx)
            {
                ex = readEx;
                reason = ClassifyCaptureException(readEx);
                return false;
            }
        }

        private void RegisterCaptureSuccess()
        {
            if (_consecutiveCaptureFailures > 0)
            {
                Console.WriteLine($"[DatasetGenerator] 离屏恢复成功，连续失败已清零: {_consecutiveCaptureFailures}");
            }

            _consecutiveCaptureFailures = 0;
            _lastSaveFailureReason = "none";
        }

        private void RegisterCaptureFailure(string reason, Exception ex)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
            _lastSaveFailureReason = reason;

            long failNo = Interlocked.Increment(ref _captureFailureCount);
            int consecutive = Interlocked.Increment(ref _consecutiveCaptureFailures);

            DisposeCaptureTexture();

            int tick = Environment.TickCount;
            bool shouldLog = failNo <= 5 || failNo % 20 == 0 || unchecked(tick - _lastFailureLogTick) >= 1000;
            if (!shouldLog)
            {
                return;
            }

            _lastFailureLogTick = tick;
            if (ex == null)
            {
                Console.WriteLine($"[DatasetGenerator] 保存失败: reason={reason} fail_count={failNo} consecutive={consecutive}");
            }
            else
            {
                Console.WriteLine($"[DatasetGenerator] 保存失败: reason={reason} ex={ex.GetType().Name} msg={ex.Message} fail_count={failNo} consecutive={consecutive}");
            }
        }

        private static string ClassifyCaptureException(Exception ex)
        {
            if (ex == null)
            {
                return "unknown";
            }

            if (ex is ArgumentNullException)
            {
                return "arg_null";
            }

            if (ex is ArgumentException)
            {
                return "backbuffer_format_mismatch";
            }

            if (ex is InvalidOperationException)
            {
                return "device_lost_like";
            }

            if (ex is ObjectDisposedException)
            {
                return "device_lost_like";
            }

            return ex.GetType().Name;
        }

        private void EnsureOutputDirectories()
        {
            if (!string.IsNullOrEmpty(_sessionDir))
            {
                return;
            }

            string root = Path.Combine(Environment.CurrentDirectory, "dataset_output");
            string sessionName = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _sessionDir = Path.Combine(root, sessionName);
            _imageDir = Path.Combine(_sessionDir, "images");
            _labelDir = Path.Combine(_sessionDir, "labels");

            Directory.CreateDirectory(_imageDir);
            Directory.CreateDirectory(_labelDir);
        }

        private bool TryEnsureCaptureTexture(GraphicsDevice graphicsDevice, int width, int height, out string reason)
        {
            reason = "none";
            if (graphicsDevice == null)
            {
                reason = "gd_null";
                return false;
            }
            if (width <= 0 || height <= 0)
            {
                reason = "invalid_size";
                return false;
            }

            int pixelCount = width * height;
            if (_backBufferData == null || _backBufferData.Length != pixelCount)
            {
                _backBufferData = new int[pixelCount];
            }

            bool needRecreate = _captureTexture == null || _captureTexture.IsDisposed ||
                                _captureTextureWidth != width || _captureTextureHeight != height;
            if (needRecreate)
            {
                try
                {
                    DisposeCaptureTexture();
                    _captureTexture = new Texture2D(graphicsDevice, width, height, false, SurfaceFormat.Color);
                    _captureTextureWidth = width;
                    _captureTextureHeight = height;
                }
                catch
                {
                    DisposeCaptureTexture();
                    reason = "texture_create_fail";
                    return false;
                }
            }

            if (_captureTexture == null || _captureTexture.IsDisposed || _backBufferData == null)
            {
                if (_captureTexture == null)
                {
                    reason = "capture_texture_null";
                }
                else if (_captureTexture.IsDisposed)
                {
                    reason = "capture_texture_disposed";
                }
                else
                {
                    reason = "backbuffer_null";
                }
                return false;
            }

            return true;
        }

        private void DisposeCaptureTexture()
        {
            try
            {
                _captureTexture?.Dispose();
            }
            catch
            {
            }

            _captureTexture = null;
            _captureTextureWidth = 0;
            _captureTextureHeight = 0;
        }

        private void EnsureWriterWorkersAlive()
        {
            if (_writeQueue == null || _writerTasks == null)
            {
                StartWriterWorkers();
            }
        }

        private void StartWriterWorkers()
        {
            StopWriterWorkers();

            _writerCts = new CancellationTokenSource();
            _writeQueue = new BlockingCollection<FrameWriteItem>(_queueCapacity);
            _writerTasks = new Task[_writerThreads];

            for (int i = 0; i < _writerThreads; i++)
            {
                _writerTasks[i] = Task.Run(() => WriterLoop(_writerCts.Token));
            }
        }

        private void StopWriterWorkers()
        {
            try
            {
                _writeQueue?.CompleteAdding();
            }
            catch
            {
            }

            try
            {
                if (_writerTasks != null && _writerTasks.Length > 0)
                {
                    Task.WaitAll(_writerTasks, 10_000);
                }
            }
            catch
            {
            }

            try
            {
                _writerCts?.Cancel();
            }
            catch
            {
            }

            _writerTasks = null;
            _writerCts?.Dispose();
            _writerCts = null;
            _writeQueue?.Dispose();
            _writeQueue = null;
        }

        private void WriterLoop(CancellationToken token)
        {
            try
            {
                foreach (var item in _writeQueue.GetConsumingEnumerable(token))
                {
                    WriteFrameToDisk(item);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
                // queue completed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatasetGenerator] writer thread failed: {ex.Message}");
            }
        }

        private static void WriteFrameToDisk(FrameWriteItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.ImagePath) || string.IsNullOrEmpty(item.LabelPath))
            {
                return;
            }
            if (item.PngBytes == null || item.PngBytes.Length == 0)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(item.ImagePath));
            Directory.CreateDirectory(Path.GetDirectoryName(item.LabelPath));
            File.WriteAllBytes(item.ImagePath, item.PngBytes);
            File.WriteAllText(item.LabelPath, item.LabelText ?? string.Empty, Utf8NoBom);
        }

        private static string BuildLabelText(List<(int classId, Rectangle bounds)> boundsList, int width, int height)
        {
            var sb = new StringBuilder(512);
            if (boundsList == null)
            {
                return string.Empty;
            }

            foreach (var (classId, rawRect) in boundsList)
            {
                var rect = ClampToFrame(rawRect, width, height);
                if (rect.Width <= 1 || rect.Height <= 1)
                {
                    continue;
                }

                if (!TryBuildYoloBox(rect, width, height, out float cx, out float cy, out float bw, out float bh))
                {
                    continue;
                }

                sb.Append(classId.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(cx.ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(cy.ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(bw.ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(bh.ToString("0.########", CultureInfo.InvariantCulture)).AppendLine();
            }
            return sb.ToString();
        }

        private static Rectangle ClampToFrame(Rectangle rect, int width, int height)
        {
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

        private static bool TryBuildYoloBox(Rectangle rect, int width, int height, out float cx, out float cy, out float bw, out float bh)
        {
            cx = cy = bw = bh = 0f;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

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
            {
                return false;
            }

            cx = (x1 + x2) * 0.5f;
            cy = (y1 + y2) * 0.5f;
            cx = Math.Clamp(cx, YoloEdgeEpsilon, 1f - YoloEdgeEpsilon);
            cy = Math.Clamp(cy, YoloEdgeEpsilon, 1f - YoloEdgeEpsilon);
            return true;
        }

        private sealed class FrameWriteItem
        {
            public string ImagePath { get; }
            public string LabelPath { get; }
            public byte[] PngBytes { get; }
            public string LabelText { get; }

            public FrameWriteItem(string imagePath, string labelPath, byte[] pngBytes, string labelText)
            {
                ImagePath = imagePath;
                LabelPath = labelPath;
                PngBytes = pngBytes;
                LabelText = labelText ?? string.Empty;
            }
        }
    }
}
