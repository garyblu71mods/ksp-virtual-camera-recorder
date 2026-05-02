using System;
using System.IO.Pipes;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualCameraRecorder
{
    /// <summary>
    /// Captures frames from a RenderTexture and streams raw RGB24 bytes to
    /// a NamedPipe that FFmpeg reads.
    ///
    /// Key design decisions:
    ///   - PipeOptions.None (synchronous) — Mono's IsConnected is broken with Asynchronous
    ///   - Local _writerConnected flag instead of _pipe.IsConnected
    ///   - Pipe created fresh for each recording session — no reset/reconnect complexity
    ///   - RequestFrame called every frame (not just when recording) so frames are ready
    ///     the instant recording starts and FFmpeg connects
    /// </summary>
    internal sealed class FrameStreamer : IDisposable
    {
        // Unique per-session name — avoids "All pipe instances are busy" if KSP
        // was killed without graceful shutdown and the old pipe handle is still alive.
        public string PipeName { get; private set; }

        // ── state ──────────────────────────────────────────────────────
        private RenderTexture _source;
        private volatile bool _disposed;
        private volatile bool _streaming;   // true while a recording is active

        // FPS throttle — ustawiane przed StartStreaming()
        public int TargetFps = 30;
        private readonly System.Diagnostics.Stopwatch _throttleWatch = new System.Diagnostics.Stopwatch();
        private long _nextFrameMs;

        // Per-session pipe + thread (recreated for each recording)
        private NamedPipeServerStream _pipe;
        private Thread                _pipeThread;

        // Signals that the pipe object exists and WaitForConnection is about to block
        private ManualResetEventSlim _pipeReady = new ManualResetEventSlim(false);

        // ── telemetry ──────────────────────────────────────────────────
        public volatile bool IsClientConnected;
        private long _bytesWritten;
        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        // ── double frame buffer ────────────────────────────────────────
        // GPU callback writes to _pending; pipe thread reads from _ready.
        private byte[] _bufA, _bufB;
        private byte[] _pending, _ready;
        private readonly object _frameLock = new object();
        private bool _frameReady;
        private bool _requestInFlight;

        // ── init ───────────────────────────────────────────────────────

        public void Initialise(RenderTexture source)
        {
            _source  = source;
            int size = source.width * source.height * 3; // RGB24
            _bufA    = new byte[size];
            _bufB    = new byte[size];
            _pending = _bufA;
            _ready   = _bufB;

            CreatePipe();
        }

        // ── frame capture (call every frame from main thread) ──────────

        /// <summary>
        /// Enqueues an async GPU readback.  Call every frame regardless of
        /// recording state — this pre-fills the buffer so the first frame is
        /// available the instant recording starts.
        /// </summary>
        public void RequestFrame()
        {
            if (_disposed || _source == null || _requestInFlight) return;
            _requestInFlight = true;
            AsyncGPUReadback.Request(_source, 0, TextureFormat.RGB24, OnReadbackComplete);
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest req)
        {
            _requestInFlight = false;
            if (req.hasError || _disposed) return;

            req.GetData<byte>().CopyTo(_pending);

            lock (_frameLock)
            {
                byte[] tmp = _ready;
                _ready   = _pending;
                _pending = tmp;
                _frameReady = true;
                Monitor.Pulse(_frameLock);
            }
        }

        // ── recording control ──────────────────────────────────────────

        /// <summary>
        /// Mark streaming as active.  The pipe thread begins writing frames
        /// once FFmpeg connects.
        /// </summary>
        public void StartStreaming()
        {
            _throttleWatch.Restart();
            _nextFrameMs = 0;
            _streaming = true;
        }

        /// <summary>
        /// Stop streaming.  Wakes the pipe thread so it can exit cleanly.
        /// Waits up to 1 s for the thread to finish, then creates a fresh pipe
        /// ready for the next recording.
        /// </summary>
        public void StopStreaming()
        {
            _streaming = false;
            lock (_frameLock) { Monitor.PulseAll(_frameLock); }

            // Odblokuj WaitForConnection przez dummy klienta
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

        /// <summary>
        /// Block until the pipe server is in WaitForConnection state.
        /// Call this before launching FFmpeg.  Timeout: 3 s.
        /// </summary>
        public bool WaitUntilListening(int timeoutMs = 3000) => _pipeReady.Wait(timeoutMs);

        // ── pipe server ────────────────────────────────────────────────

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
                Name         = "VCR-Pipe",
            };
            _pipeThread.Start();
        }

        private void UnblockWaitForConnection()
        {
            string name = PipeName;
            if (name == null) return;
            try
            {
                using (var dummy = new System.IO.Pipes.NamedPipeClientStream(
                    ".", name, PipeDirection.In, PipeOptions.None))
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
                // Signal that the pipe handle exists and we are about to block.
                // FFmpeg can now be launched — the pipe object is visible to it.
                _pipeReady.Set();
                Debug.Log("[VCR] Pipe listening, waiting for FFmpeg...");

                _pipe.WaitForConnection();

                // Jesli polaczyl sie dummy klient (unblock) lub jestesmy disposed — wyjdz
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

            while (connected && _streaming && !_disposed)
            {
                // Throttle do TargetFps
                long now = _throttleWatch.ElapsedMilliseconds;
                if (now < _nextFrameMs)
                {
                    int wait = (int)(_nextFrameMs - now);
                    if (wait > 0) Thread.Sleep(wait);
                }
                _nextFrameMs = _throttleWatch.ElapsedMilliseconds + intervalMs;

                byte[] frame;
                lock (_frameLock)
                {
                    while (!_frameReady && _streaming && !_disposed)
                        Monitor.Wait(_frameLock, 50);

                    if (!_streaming || _disposed) return;

                    frame       = _ready;
                    _frameReady = false;
                }

                try
                {
                    _pipe.Write(frame, 0, frame.Length);
                    Interlocked.Add(ref _bytesWritten, frame.Length);
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                        Debug.LogWarning("[VCR] Pipe write failed: " + ex.Message);
                    connected = false;
                }
            }
        }

        // ── IDisposable ────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed  = true;
            _streaming = false;

            lock (_frameLock) { Monitor.PulseAll(_frameLock); }
            _pipeReady.Set();

            // Odblokuj WaitForConnection przez dummy klienta
            UnblockWaitForConnection();

            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
    }
}
