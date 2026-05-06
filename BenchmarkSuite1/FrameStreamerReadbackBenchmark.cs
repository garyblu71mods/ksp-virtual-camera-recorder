using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Microsoft.VSDiagnostics;

namespace VirtualCameraRecorder
{
    [CPUUsageDiagnoser]
    public class FrameStreamerReadbackBenchmark
    {
        private Type _streamerType;
        private object _streamer;
        private Action<object, int> _releaseSlotAfterCallback;
        private FieldInfo _frameLockField;
        private FieldInfo _freeSlotsField;
        private FieldInfo _inFlightReadbacksField;
        private volatile bool _running;
        private int _callbackCount;

        [Params(1, 3)]
        public int InitialInFlightReadbacks;

        [GlobalSetup]
        public void Setup()
        {
            var recorderAssembly = Assembly.Load("VirtualCameraRecorder");
            _streamerType = recorderAssembly.GetType("VirtualCameraRecorder.FrameStreamer", throwOnError: true);

            MethodInfo releaseMethod = _streamerType.GetMethod("ReleaseSlotAfterCallback", BindingFlags.Instance | BindingFlags.NonPublic);
            var target = Expression.Parameter(typeof(object), "target");
            var slot = Expression.Parameter(typeof(int), "slot");
            _releaseSlotAfterCallback = Expression.Lambda<Action<object, int>>(
                Expression.Call(Expression.Convert(target, _streamerType), releaseMethod, slot),
                target,
                slot).Compile();

            _frameLockField = _streamerType.GetField("_frameLock", BindingFlags.Instance | BindingFlags.NonPublic);
            _freeSlotsField = _streamerType.GetField("_freeSlots", BindingFlags.Instance | BindingFlags.NonPublic);
            _inFlightReadbacksField = _streamerType.GetField("_inFlightReadbacks", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            _streamer = Activator.CreateInstance(_streamerType, nonPublic: true);
            _inFlightReadbacksField.SetValue(_streamer, InitialInFlightReadbacks);
            _callbackCount = 0;
            _running = true;

            var freeSlots = (Queue<int>)_freeSlotsField.GetValue(_streamer);
            freeSlots.Clear();
        }

        [Benchmark]
        public object ReleaseSlotAfterCallbackLoop()
        {
            object frameLock = _frameLockField.GetValue(_streamer);

            var stopThread = new Thread(() =>
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
                _releaseSlotAfterCallback(_streamer, slot);
                _callbackCount++;
                slot = (slot + 1) & 3;
            }

            return new
            {
                CallbackCount = _callbackCount,
                FreeSlotCount = ((Queue<int>)_freeSlotsField.GetValue(_streamer)).Count,
                InFlightReadbacks = (int)_inFlightReadbacksField.GetValue(_streamer)
            };
        }
    }
}