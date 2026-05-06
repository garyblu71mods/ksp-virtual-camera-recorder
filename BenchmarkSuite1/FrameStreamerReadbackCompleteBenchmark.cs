using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Microsoft.VSDiagnostics;
using Unity.Collections;

namespace VirtualCameraRecorder
{
    [CPUUsageDiagnoser]
    public class FrameStreamerReadbackCompleteBenchmark
    {
        private Type _streamerType;
        private object _streamer;
        private Action<object, int, long, long, NativeArray<byte>> _completeSuccessfulReadback;
        private FieldInfo _slotBuffersField;
        private FieldInfo _slotCaptureTicksField;
        private FieldInfo _readySlotsField;
        private FieldInfo _frameLockField;
        private FieldInfo _inFlightReadbacksField;
        private NativeArray<byte> _sourceData;
        private volatile bool _running;
        private long _requestTick;
        private long _nowTick;
        private int _iterationCount;

        [Params(6220800)]
        public int BufferSize;

        [GlobalSetup]
        public void Setup()
        {
            Assembly recorderAssembly = Assembly.Load("VirtualCameraRecorder");
            _streamerType = recorderAssembly.GetType("VirtualCameraRecorder.FrameStreamer", throwOnError: true);

            MethodInfo completeMethod = _streamerType.GetMethod("CompleteSuccessfulReadback", BindingFlags.Instance | BindingFlags.NonPublic);
            ParameterExpression target = Expression.Parameter(typeof(object), "target");
            ParameterExpression slot = Expression.Parameter(typeof(int), "slot");
            ParameterExpression requestTick = Expression.Parameter(typeof(long), "requestTick");
            ParameterExpression nowTick = Expression.Parameter(typeof(long), "nowTick");
            ParameterExpression sourceData = Expression.Parameter(typeof(NativeArray<byte>), "sourceData");

            _completeSuccessfulReadback = Expression.Lambda<Action<object, int, long, long, NativeArray<byte>>>(
                Expression.Call(Expression.Convert(target, _streamerType), completeMethod, slot, requestTick, nowTick, sourceData),
                target,
                slot,
                requestTick,
                nowTick,
                sourceData).Compile();

            _slotBuffersField = _streamerType.GetField("_slotBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
            _slotCaptureTicksField = _streamerType.GetField("_slotCaptureTicks", BindingFlags.Instance | BindingFlags.NonPublic);
            _readySlotsField = _streamerType.GetField("_readySlots", BindingFlags.Instance | BindingFlags.NonPublic);
            _frameLockField = _streamerType.GetField("_frameLock", BindingFlags.Instance | BindingFlags.NonPublic);
            _inFlightReadbacksField = _streamerType.GetField("_inFlightReadbacks", BindingFlags.Instance | BindingFlags.NonPublic);

            byte[] sourceBytes = new byte[BufferSize];
            for (int i = 0; i < sourceBytes.Length; i++)
                sourceBytes[i] = (byte)(i & 0xFF);

            _sourceData = new NativeArray<byte>(sourceBytes, Allocator.Persistent);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            _streamer = Activator.CreateInstance(_streamerType, nonPublic: true);
            _slotBuffersField.SetValue(_streamer, new[]
            {
                new byte[BufferSize],
                new byte[BufferSize],
                new byte[BufferSize],
                new byte[BufferSize],
            });
            _slotCaptureTicksField.SetValue(_streamer, new long[4]);
            _inFlightReadbacksField.SetValue(_streamer, 3);

            Queue<int> ready = (Queue<int>)_readySlotsField.GetValue(_streamer);
            ready.Clear();

            _iterationCount = 0;
            _running = true;
            _requestTick = 1000;
            _nowTick = 2500;
        }

        [Benchmark]
        public object CompleteSuccessfulReadbackLoop()
        {
            object frameLock = _frameLockField.GetValue(_streamer);
            Thread stopThread = new Thread(() =>
            {
                Thread.Sleep(250);
                _running = false;
                lock (frameLock)
                {
                    Monitor.PulseAll(frameLock);
                }
            });
            stopThread.IsBackground = true;
            stopThread.Start();

            int slot = 0;
            while (_running)
            {
                _completeSuccessfulReadback(_streamer, slot, _requestTick, _nowTick, _sourceData);
                _iterationCount++;
                slot = (slot + 1) & 3;
                _requestTick += 3;
                _nowTick += 5;
            }

            return new
            {
                IterationCount = _iterationCount,
                ReadyCount = ((Queue<int>)_readySlotsField.GetValue(_streamer)).Count,
                InFlightReadbacks = (int)_inFlightReadbacksField.GetValue(_streamer)
            };
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            if (_sourceData.IsCreated)
                _sourceData.Dispose();
        }
    }
}