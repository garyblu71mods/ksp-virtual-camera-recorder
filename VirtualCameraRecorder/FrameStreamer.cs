using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualCameraRecorder
{
    internal struct StreamerTelemetrySnapshot
    {
        public long FramesWritten;
        public long DuplicateFrames;
        public long LateCadenceFrames;
        public int ReadbackErrors;
        public long ReadbackRequestDrops;
        public float ReadbackAvgMs;
        public float ReadbackMaxMs;
        public float ReadbackCopyAvgMs;
        public float ReadbackCopyMaxMs;
        public float ReadbackPublishAvgMs;
        public float ReadbackPublishMaxMs;
        public float ReadbackCallbackAvgMs;
        public float ReadbackCallbackMaxMs;
        public float WriteAvgMs;
        public float WriteMaxMs;
        public float FrameAgeAvgMs;
        public float FrameAgeMaxMs;
    }

    /// <summary>
    /// Captures frames from a RenderTexture and streams raw RGB24 bytes to
    /// a NamedPipe that FFmpeg reads.
    /// </summary>
    internal sealed class FrameStreamer : IDisposable
    {
        public string PipeName { get; private set; }

        private const int BufferSlotCount = 4;
        private const int MaxInFlightReadbacks = 3;
        private const int TelemetryFlushFrameInterval = 8;

        // ── state ──────────────────────────────────────────────────────
        private RenderTexture _source;
        private volatile bool _disposed;
        private volatile bool _streaming;

        public int TargetFps = 30;
        private readonly System.Diagnostics.Stopwatch _throttleWatch = new System.Diagnostics.Stopwatch();
        private long _nextFrameMs;

        private NamedPipeServerStream _pipe;
        private Thread _pipeThread;
        private ManualResetEventSlim _pipeReady = new ManualResetEventSlim(false);

        // ── telemetry ──────────────────────────────────────────────────
        public volatile bool IsClientConnected;
        private long _bytesWritten;
        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        private readonly object _telemetryLock = new object();
        private readonly System.Diagnostics.Stopwatch _telemetryWatch = System.Diagnostics.Stopwatch.StartNew();
        private int _readbackErrors;
        private long _readbackRequestDrops;
        private int _readbackSamples;
        private float _readbackAvgMs;
        private float _readbackMaxMs;
        private int _readbackCopySamples;
        private float _readbackCopyAvgMs;
        private float _readbackCopyMaxMs;
        private int _readbackPublishSamples;
        private float _readbackPublishAvgMs;
        private float _readbackPublishMaxMs;
        private int _readbackCallbackSamples;
        private float _readbackCallbackAvgMs;
        private float _readbackCallbackMaxMs;
        private int _writeSamples;
        private float _writeAvgMs;
        private float _writeMaxMs;
        private int _frameAgeSamples;
        private float _frameAgeAvgMs;
        private float _frameAgeMaxMs;
        private long _framesWritten;
        private long _duplicateFrames;
        private long _lateCadenceFrames;

        // ── frame slots ────────────────────────────────────────────────
        private byte[][] _slotBuffers;
        private long[] _slotCaptureTicks;

        // GPU callback writes to ready queue; pipe thread consumes latest-ready slot.
        private readonly object _frameLock = new object();
        private readonly Queue<int> _freeSlots = new Queue<int>();
        private readonly Queue<int> _readySlots = new Queue<int>();
        private int _inFlightReadbacks;

        public void Initialise(RenderTexture source)
        {
            _source = source;
            int size = source.width * source.height * 3; // RGB24

            _slotBuffers = new byte[BufferSlotCount][];
            _slotCaptureTicks = new long[BufferSlotCount];
            for (int i = 0; i < BufferSlotCount; i++)
                _slotBuffers[i] = new byte[size];

            ResetFrameSlotsState();
            ResetTelemetry();
            CreatePipe();
        }

        public void RequestFrame()
        {
            if (_disposed || _source == null) return;

            int slot;
            long requestTick = _telemetryWatch.ElapsedTicks;
            lock (_frameLock)
            {
                if (_inFlightReadbacks >= MaxInFlightReadbacks || _freeSlots.Count == 0)
                {
                    lock (_telemetryLock) _readbackRequestDrops++;
                    return;
                }

                slot = _freeSlots.Dequeue();
                _inFlightReadbacks++;
            }

            AsyncGPUReadback.Request(
                _source,
                0,
                TextureFormat.RGB24,
                req => OnReadbackComplete(req, slot, requestTick));
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest req, int slot, long requestTick)
        {
            if (_disposed)
            {
                ReleaseSlotAfterCallback(slot);
                return;
            }

            if (req.hasError)
            {
                lock (_telemetryLock) _readbackErrors++;
                ReleaseSlotAfterCallback(slot);
                return;
            }

            long nowTick = _telemetryWatch.ElapsedTicks;
            CompleteSuccessfulReadback(slot, requestTick, nowTick, req.GetData<byte>());
        }

        private void CompleteSuccessfulReadback(int slot, long requestTick, long nowTick, NativeArray<byte> sourceData)
        {
            long callbackStartTick = _telemetryWatch.ElapsedTicks;
            float readbackMs = TicksToMs(nowTick - requestTick);
            lock (_telemetryLock)
            {
                UpdateAverage(ref _readbackAvgMs, ++_readbackSamples, readbackMs);
                if (readbackMs > _readbackMaxMs) _readbackMaxMs = readbackMs;
            }

            long copyStartTick = _telemetryWatch.ElapsedTicks;
            sourceData.CopyTo(_slotBuffers[slot]);
            float copyMs = TicksToMs(_telemetryWatch.ElapsedTicks - copyStartTick);

            long publishStartTick = _telemetryWatch.ElapsedTicks;
            lock (_frameLock)
            {
                if (_inFlightReadbacks > 0)
                    _inFlightReadbacks--;

                _slotCaptureTicks[slot] = nowTick;
                _readySlots.Enqueue(slot);
                Monitor.Pulse(_frameLock);
            }
            float publishMs = TicksToMs(_telemetryWatch.ElapsedTicks - publishStartTick);
            float callbackMs = TicksToMs(_telemetryWatch.ElapsedTicks - callbackStartTick);

            lock (_telemetryLock)
            {
                UpdateAverage(ref _readbackCopyAvgMs, ++_readbackCopySamples, copyMs);
                if (copyMs > _readbackCopyMaxMs) _readbackCopyMaxMs = copyMs;

                UpdateAverage(ref _readbackPublishAvgMs, ++_readbackPublishSamples, publishMs);
                if (publishMs > _readbackPublishMaxMs) _readbackPublishMaxMs = publishMs;

                UpdateAverage(ref _readbackCallbackAvgMs, ++_readbackCallbackSamples, callbackMs);
                if (callbackMs > _readbackCallbackMaxMs) _readbackCallbackMaxMs = callbackMs;
            }
        }

        public void StartStreaming()
        {
            _throttleWatch.Restart();
            _nextFrameMs = 0;
            ResetFrameSlotsState();
            ResetTelemetry();
            _streaming = true;
        }

        public void StopStreaming()
        {
            _streaming = false;
            lock (_frameLock) { Monitor.PulseAll(_frameLock); }

            UnblockWaitForConnection();

            var threadToJoin = _pipeThread;
            new Thread(() =>
            {
                threadToJoin?.Join(2000);
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
                if (!_disposed) CreatePipe();
            })
            {
                IsBackground = true,
                Name = "VCR-PipeStop"
            }.Start();
        }

        public bool WaitUntilListening(int timeoutMs = 3000) => _pipeReady.Wait(timeoutMs);

        public StreamerTelemetrySnapshot GetTelemetrySnapshot()
        {
            lock (_telemetryLock)
            {
                return new StreamerTelemetrySnapshot
                {
                    FramesWritten = _framesWritten,
                    DuplicateFrames = _duplicateFrames,
                    LateCadenceFrames = _lateCadenceFrames,
                    ReadbackErrors = _readbackErrors,
                    ReadbackRequestDrops = _readbackRequestDrops,
                    ReadbackAvgMs = _readbackAvgMs,
                    ReadbackMaxMs = _readbackMaxMs,
                    ReadbackCopyAvgMs = _readbackCopyAvgMs,
                    ReadbackCopyMaxMs = _readbackCopyMaxMs,
                    ReadbackPublishAvgMs = _readbackPublishAvgMs,
                    ReadbackPublishMaxMs = _readbackPublishMaxMs,
                    ReadbackCallbackAvgMs = _readbackCallbackAvgMs,
                    ReadbackCallbackMaxMs = _readbackCallbackMaxMs,
                    WriteAvgMs = _writeAvgMs,
                    WriteMaxMs = _writeMaxMs,
                    FrameAgeAvgMs = _frameAgeAvgMs,
                    FrameAgeMaxMs = _frameAgeMaxMs,
                };
            }
        }

        private void CreatePipe()
        {
            _pipeReady.Reset();
            IsClientConnected = false;

            // Fresh unique name every session — no collision with stale OS pipe handles.
            PipeName = "VCR_" + Guid.NewGuid().ToString("N").Substring(0, 12);

            // PipeOptions.None (synchronous) — Mono's IsConnected is unreliable
            // with PipeOptions.Asynchronous on Unity 2019 Mono runtime.
            _pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                0,
                65536);

            _pipeThread = new Thread(PipeThreadMain)
            {
                IsBackground = true,
                Name = "VCR-Pipe",
            };
            _pipeThread.Start();
        }

        private void UnblockWaitForConnection()
        {
            string name = PipeName;
            if (name == null) return;
            try
            {
                using (var dummy = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.None))
                {
                    dummy.Connect(300);
                }
            }
            catch { }
        }

        private void PipeThreadMain()
        {
            try
            {
                _pipeReady.Set();
                Debug.Log("[VCR] Pipe listening, waiting for FFmpeg...");

                _pipe.WaitForConnection();

                if (_disposed || !_streaming)
                {
                    Debug.Log("[VCR] Pipe thread: unblocked (not streaming), exiting.");
                    return;
                }

                IsClientConnected = true;
                Debug.Log("[VCR] FFmpeg connected to pipe.");
                WriteFrames();
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Debug.LogWarning("[VCR] Pipe thread exception: " + ex.Message);
            }
            finally
            {
                IsClientConnected = false;
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
                Debug.Log("[VCR] Pipe thread exited.");
            }
        }

        private void WriteFrames()
        {
            bool connected = true;
            long intervalMs = 1000L / (TargetFps > 0 ? TargetFps : 30);
            long nextFrameMs = _nextFrameMs;
            int currentSlot = -1;

            var throttleWatch = _throttleWatch;
            var telemetryWatch = _telemetryWatch;
            var frameLock = _frameLock;
            var readySlots = _readySlots;
            var freeSlots = _freeSlots;
            var slotCaptureTicks = _slotCaptureTicks;
            var slotBuffers = _slotBuffers;
            var pipe = _pipe;

            int writeSamples = 0;
            float writeAvgMs = 0f;
            float writeMaxMs = 0f;
            int frameAgeSamples = 0;
            float frameAgeAvgMs = 0f;
            float frameAgeMaxMs = 0f;
            long framesWritten = 0;
            long duplicateFrames = 0;
            long lateCadenceFrames = 0;

            while (connected && _streaming && !_disposed)
            {
                long now = throttleWatch.ElapsedMilliseconds;
                if (now > nextFrameMs + 2)
                    lateCadenceFrames++;

                if (now < nextFrameMs)
                    Thread.Sleep((int)(nextFrameMs - now));

                long nextScheduled = nextFrameMs + intervalMs;
                nextFrameMs = nextScheduled < now ? now + intervalMs : nextScheduled;

                bool duplicated = false;
                long currentFrameTick = 0;

                lock (frameLock)
                {
                    if (readySlots.Count > 0)
                    {
                        if (currentSlot >= 0)
                            freeSlots.Enqueue(currentSlot);

                        while (readySlots.Count > 1)
                            freeSlots.Enqueue(readySlots.Dequeue());

                        currentSlot = readySlots.Dequeue();
                        currentFrameTick = slotCaptureTicks[currentSlot];
                    }
                    else if (currentSlot < 0)
                    {
                        while (readySlots.Count == 0 && _streaming && !_disposed)
                            Monitor.Wait(frameLock, 50);

                        if (!_streaming || _disposed)
                            break;

                        currentSlot = readySlots.Dequeue();
                        currentFrameTick = slotCaptureTicks[currentSlot];
                    }
                    else
                    {
                        duplicated = true;
                        currentFrameTick = slotCaptureTicks[currentSlot];
                    }
                }

                if (currentSlot < 0)
                    continue;

                long beforeWriteTick = telemetryWatch.ElapsedTicks;
                if (currentFrameTick > 0)
                {
                    float ageMs = TicksToMs(beforeWriteTick - currentFrameTick);
                    UpdateAverage(ref frameAgeAvgMs, ++frameAgeSamples, ageMs);
                    if (ageMs > frameAgeMaxMs) frameAgeMaxMs = ageMs;
                }

                try
                {
                    byte[] currentFrame = slotBuffers[currentSlot];
                    pipe.Write(currentFrame, 0, currentFrame.Length);
                    Interlocked.Add(ref _bytesWritten, currentFrame.Length);

                    float writeMs = TicksToMs(telemetryWatch.ElapsedTicks - beforeWriteTick);
                    framesWritten++;
                    if (duplicated) duplicateFrames++;
                    UpdateAverage(ref writeAvgMs, ++writeSamples, writeMs);
                    if (writeMs > writeMaxMs) writeMaxMs = writeMs;

                    if ((framesWritten & (TelemetryFlushFrameInterval - 1)) == 0)
                        CommitWriteTelemetry(ref framesWritten, ref duplicateFrames, ref lateCadenceFrames,
                            ref writeSamples, ref writeAvgMs, ref writeMaxMs,
                            ref frameAgeSamples, ref frameAgeAvgMs, ref frameAgeMaxMs);
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                        Debug.LogWarning("[VCR] Pipe write failed: " + ex.Message);
                    connected = false;
                }
            }

            _nextFrameMs = nextFrameMs;
            CommitWriteTelemetry(ref framesWritten, ref duplicateFrames, ref lateCadenceFrames,
                ref writeSamples, ref writeAvgMs, ref writeMaxMs,
                ref frameAgeSamples, ref frameAgeAvgMs, ref frameAgeMaxMs);

            lock (frameLock)
            {
                if (currentSlot >= 0)
                    freeSlots.Enqueue(currentSlot);
            }
        }

        private void CommitWriteTelemetry(
            ref long framesWritten,
            ref long duplicateFrames,
            ref long lateCadenceFrames,
            ref int writeSamples,
            ref float writeAvgMs,
            ref float writeMaxMs,
            ref int frameAgeSamples,
            ref float frameAgeAvgMs,
            ref float frameAgeMaxMs)
        {
            if (framesWritten == 0 && duplicateFrames == 0 && lateCadenceFrames == 0
                && writeSamples == 0 && frameAgeSamples == 0)
                return;

            lock (_telemetryLock)
            {
                _framesWritten += framesWritten;
                _duplicateFrames += duplicateFrames;
                _lateCadenceFrames += lateCadenceFrames;

                MergeAverage(ref _writeAvgMs, ref _writeSamples, writeAvgMs, writeSamples);
                if (writeMaxMs > _writeMaxMs) _writeMaxMs = writeMaxMs;

                MergeAverage(ref _frameAgeAvgMs, ref _frameAgeSamples, frameAgeAvgMs, frameAgeSamples);
                if (frameAgeMaxMs > _frameAgeMaxMs) _frameAgeMaxMs = frameAgeMaxMs;
            }

            framesWritten = 0;
            duplicateFrames = 0;
            lateCadenceFrames = 0;
            writeSamples = 0;
            writeAvgMs = 0f;
            writeMaxMs = 0f;
            frameAgeSamples = 0;
            frameAgeAvgMs = 0f;
            frameAgeMaxMs = 0f;
        }

        private static void MergeAverage(ref float totalAverage, ref int totalSamples, float batchAverage, int batchSamples)
        {
            if (batchSamples <= 0)
                return;

            if (totalSamples <= 0)
            {
                totalAverage = batchAverage;
                totalSamples = batchSamples;
                return;
            }

            totalAverage = ((totalAverage * totalSamples) + (batchAverage * batchSamples)) / (totalSamples + batchSamples);
            totalSamples += batchSamples;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _streaming = false;

            lock (_frameLock) { Monitor.PulseAll(_frameLock); }
            _pipeReady.Set();
            UnblockWaitForConnection();

            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        private void ResetFrameSlotsState()
        {
            lock (_frameLock)
            {
                _freeSlots.Clear();
                _readySlots.Clear();
                for (int i = 0; i < BufferSlotCount; i++)
                {
                    _slotCaptureTicks[i] = 0;
                    _freeSlots.Enqueue(i);
                }
                _inFlightReadbacks = 0;
            }
        }

        private void ReleaseSlotAfterCallback(int slot)
        {
            lock (_frameLock)
            {
                if (_inFlightReadbacks > 0)
                    _inFlightReadbacks--;

                if ((uint)slot < BufferSlotCount)
                    _freeSlots.Enqueue(slot);
            }
        }

        private void ResetTelemetry()
        {
            lock (_telemetryLock)
            {
                _readbackErrors = 0;
                _readbackRequestDrops = 0;
                _readbackSamples = 0;
                _readbackAvgMs = 0f;
                _readbackMaxMs = 0f;
                _readbackCopySamples = 0;
                _readbackCopyAvgMs = 0f;
                _readbackCopyMaxMs = 0f;
                _readbackPublishSamples = 0;
                _readbackPublishAvgMs = 0f;
                _readbackPublishMaxMs = 0f;
                _readbackCallbackSamples = 0;
                _readbackCallbackAvgMs = 0f;
                _readbackCallbackMaxMs = 0f;
                _writeSamples = 0;
                _writeAvgMs = 0f;
                _writeMaxMs = 0f;
                _frameAgeSamples = 0;
                _frameAgeAvgMs = 0f;
                _frameAgeMaxMs = 0f;
                _framesWritten = 0;
                _duplicateFrames = 0;
                _lateCadenceFrames = 0;
            }
        }

        private static float TicksToMs(long ticks)
        {
            return (float)(ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        private static void UpdateAverage(ref float avg, int samples, float value)
        {
            if (samples <= 1) avg = value;
            else avg += (value - avg) / samples;
        }
    }
}
