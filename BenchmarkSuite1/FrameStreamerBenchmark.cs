using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using Microsoft.VSDiagnostics;

namespace VirtualCameraRecorder
{
    [CPUUsageDiagnoser]
    public class FrameStreamerBenchmark
    {
        private Type _streamerType;
        private object _streamer;
        private Func<object, object> _getTelemetrySnapshot;
        private MethodInfo _writeFrames;
        private FieldInfo _streamingField;
        private FieldInfo _disposedField;
        private FieldInfo _pipeField;
        private FieldInfo _nextFrameMsField;
        private FieldInfo _targetFpsField;
        private FieldInfo _slotBuffersField;
        private FieldInfo _slotCaptureTicksField;
        private FieldInfo _readySlotsField;
        private FieldInfo _freeSlotsField;
        private FieldInfo _frameLockField;
        private FieldInfo _throttleWatchField;

        [Params(30)]
        public int TargetFps;

        [Params(1, 2, 4)]
        public int ReadyFrameCount;

        [GlobalSetup]
        public void Setup()
        {
            var recorderAssembly = Assembly.Load("VirtualCameraRecorder");
            _streamerType = recorderAssembly.GetType("VirtualCameraRecorder.FrameStreamer", throwOnError: true);
            _streamer = Activator.CreateInstance(_streamerType, nonPublic: true);
            _getTelemetrySnapshot = _ => _streamerType.GetMethod("GetTelemetrySnapshot", BindingFlags.Instance | BindingFlags.Public).Invoke(_, null);
            _writeFrames = _streamerType.GetMethod("WriteFrames", BindingFlags.Instance | BindingFlags.NonPublic);
            _streamingField = _streamerType.GetField("_streaming", BindingFlags.Instance | BindingFlags.NonPublic);
            _disposedField = _streamerType.GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
            _pipeField = _streamerType.GetField("_pipe", BindingFlags.Instance | BindingFlags.NonPublic);
            _nextFrameMsField = _streamerType.GetField("_nextFrameMs", BindingFlags.Instance | BindingFlags.NonPublic);
            _targetFpsField = _streamerType.GetField("TargetFps", BindingFlags.Instance | BindingFlags.Public);
            _slotBuffersField = _streamerType.GetField("_slotBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
            _slotCaptureTicksField = _streamerType.GetField("_slotCaptureTicks", BindingFlags.Instance | BindingFlags.NonPublic);
            _readySlotsField = _streamerType.GetField("_readySlots", BindingFlags.Instance | BindingFlags.NonPublic);
            _freeSlotsField = _streamerType.GetField("_freeSlots", BindingFlags.Instance | BindingFlags.NonPublic);
            _frameLockField = _streamerType.GetField("_frameLock", BindingFlags.Instance | BindingFlags.NonPublic);
            _throttleWatchField = _streamerType.GetField("_throttleWatch", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            _streamer = Activator.CreateInstance(_streamerType, nonPublic: true);
            _targetFpsField.SetValue(_streamer, TargetFps);
            _disposedField.SetValue(_streamer, false);
            _streamingField.SetValue(_streamer, true);
            _nextFrameMsField.SetValue(_streamer, 0L);
            _slotBuffersField.SetValue(_streamer, new[] { new byte[1920 * 1080 * 3], new byte[1920 * 1080 * 3], new byte[1920 * 1080 * 3], new byte[1920 * 1080 * 3], });
            _slotCaptureTicksField.SetValue(_streamer, new long[4]);
            object frameLock = _frameLockField.GetValue(_streamer);
            var ready = (Queue<int>)_readySlotsField.GetValue(_streamer);
            var free = (Queue<int>)_freeSlotsField.GetValue(_streamer);
            ready.Clear();
            free.Clear();
            for (int i = 0; i < 4; i++)
                free.Enqueue(i);
            lock (frameLock)
            {
                for (int i = 0; i < ReadyFrameCount; i++)
                {
                    int slot = free.Dequeue();
                    ready.Enqueue(slot);
                }
            }

            var watch = (System.Diagnostics.Stopwatch)_throttleWatchField.GetValue(_streamer);
            watch.Reset();
            watch.Start();
            var pipeName = "VCRBench_" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 65536);
            _pipeField.SetValue(_streamer, server);
            var readerThread = new Thread(() =>
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.None))
                {
                    client.Connect(2000);
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        int read = client.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                            break;
                    }
                }
            });
            readerThread.IsBackground = true;
            readerThread.Start();
            server.WaitForConnection();
        }

        [Benchmark]
        public object WriteFramesLoop()
        {
            var stopThread = new Thread(() =>
            {
                Thread.Sleep(250);
                _streamingField.SetValue(_streamer, false);
                object frameLock = _frameLockField.GetValue(_streamer);
                lock (frameLock)
                {
                    Monitor.PulseAll(frameLock);
                }
            });
            stopThread.IsBackground = true;
            stopThread.Start();
            _writeFrames.Invoke(_streamer, null);
            return _getTelemetrySnapshot(_streamer);
        }
    }
}