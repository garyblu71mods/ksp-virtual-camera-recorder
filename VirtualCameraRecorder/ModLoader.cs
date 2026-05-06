using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;
using Debug = UnityEngine.Debug;
namespace VirtualCameraRecorder
{
    /// <summary>
    /// KSP addon entry-point.  Registers itself for Flight scene, wires together
    /// CameraController / CameraWindow / FrameStreamer, and adds an AppLauncher
    /// toolbar button so the window can be toggled from the stock toolbar.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class ModLoader : MonoBehaviour
    {
        private const string FixedFfmpegPreset = "ultrafast";

        // ── default config ─────────────────────────────────────────────
        // ffmpeg.exe lives next to the mod DLL:
        //   GameData\VirtualCameraRecorder\Plugins\ffmpeg.exe
        // Falls back to system PATH if not found locally.
        private static string FfmpegPath
        {
            get
            {
                string pluginsDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                string local = Path.Combine(pluginsDir, "ffmpeg.exe");
                return File.Exists(local) ? local : "ffmpeg";
            }
        }

        // Videos are saved to  <KSP_root>\Recordings\vcr_TIMESTAMP.mp4
        // KSPUtil.ApplicationRootPath is the authoritative KSP root — always writable.
        private static string RecordingsDir =>
            Path.Combine(KSPUtil.ApplicationRootPath.TrimEnd('/', '\\'), "Recordings");

        private static string NextOutputPath()
        {
            Directory.CreateDirectory(RecordingsDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string path  = Path.Combine(RecordingsDir, "vcr_" + stamp + ".mp4");
            Debug.Log("[VCR] Output video: " + path);
            return path;
        }

        // ── owned objects ──────────────────────────────────────────────
        private CameraController _camCtrl;
        private CameraWindow     _camWin;
        private FrameStreamer    _streamer;
        private Process          _ffmpeg;
        private string           _activeOutputPath;
        private MainCameraBlitCapture _mainBlitCapture;

        private bool  _initialised;
        private float _nextSetUpAttempt; // realtime seconds – cooldown between retries

        // ── recording state ────────────────────────────────────────────
        private bool  _recording;
        private float _recordStartTime;
        private long  _lastBytesSample;
        private float _lastBytesSampleTime;
        private float _bitrateKBps;
        private float _nextPerfLogTime;
        private volatile bool _repairRunning;
        private string _repairStatus;
        private int _vesselSwitchCheckToken;

        // ── AppLauncher button ─────────────────────────────────────────
        private ApplicationLauncherButton _appButton;
        private Texture2D                 _appIcon;

        // ── MonoBehaviour lifecycle ────────────────────────────────────

        private void Awake()
        {
            // Awake() runs before Start() and before most KSP events fire.
            // Subscribe here to catch onFlightReady even if it fires between
            // Awake() and Start().
            Debug.Log("[VCR] ModLoader.Awake — subscribing events."
                + " LoadedScene=" + HighLogic.LoadedScene
                + " FlightGlobals.ready=" + FlightGlobals.ready);
            SubscribeEvents();
        }

        private void Start()
        {
            Debug.Log("[VCR] ModLoader.Start."
                + " LoadedScene=" + HighLogic.LoadedScene
                + " FlightGlobals.ready=" + FlightGlobals.ready
                + " _initialised=" + _initialised);

            // AppLauncher race-fix: add button immediately if already ready.
            if (ApplicationLauncher.Ready)
            {
                Debug.Log("[VCR] ApplicationLauncher already ready at Start.");
                AddAppLauncherButton();
            }
        }

        private void Update()
        {
            // ── Lazy init: bulletproof catch-all ─────────────────────────
            // Handles every timing edge-case: fires SetUp() in the first
            // Update() frame where KSP reports the flight scene is live,
            // regardless of whether onFlightReady was missed.
            if (!_initialised)
            {
                if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready
                    && Time.realtimeSinceStartup >= _nextSetUpAttempt)
                {
                    Debug.Log("[VCR] Update: flight ready — calling SetUp.");
                    SetUp();
                    if (!_initialised)
                    {
                        _nextSetUpAttempt = Time.realtimeSinceStartup + 3f;
                        Debug.LogWarning("[VCR] SetUp failed — will retry in 3 s.");
                    }
                }
                return;
            }

            // Reattach if camera stack changed (mods/scene transitions)
            EnsureMainCameraCapture();

            SuppressPartHighlights();

            _camCtrl.Tick();
            _streamer?.RequestFrame();

            UpdateWindowState();
        }

        private static void SuppressPartHighlights()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                try
                {
                    p.SetHighlight(false, false);
                    p.SetHighlightDefault();
                }
                catch { }
            }
        }

        private void OnGUI()
        {
            if (!_initialised) return;
            _camWin.OnGUI();
        }

        private void OnDestroy()
        {
            Debug.Log("[VCR] ModLoader.OnDestroy");
            UnsubscribeEvents();
            RemoveAppLauncherButton();

            // Priorytet: domknij plik nagrania nawet podczas niszczenia obiektu/sceny.
            if (_recording)
                StopRecordingAndWait(15000);
            else if (_ffmpeg != null)
            {
                Process ffmpegToWait = _ffmpeg;
                _ffmpeg = null;
                FinalizeAndDisposeFFmpeg(ffmpegToWait, 10000);
            }

            // Dispose streamera — odblokuje wszystkie wiszące wątki tła
            if (_streamer != null)
            {
                _streamer.Dispose();
                _streamer = null;
            }

            _camCtrl?.Dispose();
            _camCtrl = null;
            _camWin  = null;
            _initialised = false;
        }

        // ── KSP events ─────────────────────────────────────────────────

        private void SubscribeEvents()
        {
            Debug.Log("[VCR] SubscribeEvents");
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
            GameEvents.onGUIApplicationLauncherReady.Add(AddAppLauncherButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveAppLauncherButton);
            Debug.Log("[VCR] SubscribeEvents done.");
        }

        private void UnsubscribeEvents()
        {
            GameEvents.onFlightReady.Remove(OnFlightReady);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
            GameEvents.onGUIApplicationLauncherReady.Remove(AddAppLauncherButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveAppLauncherButton);
        }

        private void OnFlightReady()
        {
            Debug.Log("[VCR] onFlightReady — calling SetUp. initialised=" + _initialised + " appButton=" + (_appButton != null));
            SetUp();
        }

        private void OnVesselChange(Vessel v)
        {
            Debug.Log("[VCR] onVesselChange vessel=" + (v != null ? v.name : "null"));
            // Camera stays where it is. If we have an anchor vessel still loaded
            // we keep tracking it. If the user wants to anchor to the new active
            // vessel they press "Snap here" in the window.
            if (!_initialised) return;

            // Do not stop immediately on event: vessel switches can report transient packed/unloaded states.
            if (_recording)
            {
                int token = ++_vesselSwitchCheckToken;
                StartCoroutine(ValidateVesselSwitchForFinalize(v, token));
            }
        }

        private IEnumerator ValidateVesselSwitchForFinalize(Vessel switchedTo, int token)
        {
            // Let KSP settle vessel state after switch.
            yield return new WaitForSecondsRealtime(1.0f);

            if (!_recording || token != _vesselSwitchCheckToken)
                yield break;

            Vessel active = FlightGlobals.ActiveVessel;
            Vessel v = active ?? switchedTo;

            bool newActiveOutOfRange =
                (v == null) ||
                v.packed ||
                !v.loaded ||
                v.gameObject == null;

            bool anchorOutOfRange = (_camCtrl != null && !_camCtrl.IsAnchorLoaded);

            // Finalize only when the new active vessel is still out-of-range after delay
            // and anchor context is also unavailable.
            if (newActiveOutOfRange && anchorOutOfRange)
            {
                Debug.Log("[VCR] Vessel change sustained out-of-range state — finalizing recording.");
                StopRecordingAndWait(15000);
            }
            else
            {
                Debug.Log("[VCR] Vessel change remained in render range — keeping recording active.");
            }
        }

        private void OnLevelWasLoaded(GameScenes scene)
        {
            if (scene != GameScenes.FLIGHT) { TearDown(); return; }
            TearDown();
            SetUp();
        }

        // ── setup / teardown ───────────────────────────────────────────

        private void SetUp()
        {
            Debug.Log("[VCR] SetUp called. _initialised=" + _initialised + " appButton=" + (_appButton != null));
            if (_initialised) { Debug.Log("[VCR] SetUp skipped — already initialised."); return; }
            try
            {
                Debug.Log("[VCR] Creating CameraController 1920x1080 30fps...");
                _camCtrl = new CameraController
                {
                    TargetWidth  = 1920,
                    TargetHeight = 1080,
                    TargetFps    = 30,
                    UseMainCameraFinalFrame = false,
                };
                _camCtrl.Initialise();
                EnsureMainCameraCapture();
                Debug.Log("[VCR] CameraController OK.");

                if (FlightGlobals.ActiveVessel != null)
                {
                    Debug.Log("[VCR] Snapping to vessel: " + FlightGlobals.ActiveVessel.name);
                    _camCtrl.SnapToVessel(FlightGlobals.ActiveVessel);
                }
                else
                {
                    Debug.LogWarning("[VCR] No active vessel — camera placed at origin.");
                }

                Debug.Log("[VCR] Creating CameraWindow...");
                _camWin = new CameraWindow();
                _camWin.Initialise(_camCtrl);
                _camWin.OnRecordToggle  = ToggleRecording;
                _camWin.OnApplySettings = OnWindowApplySettings;
                _camWin.OnAimToVessel = () =>
                {
                    if (_camCtrl != null && FlightGlobals.ActiveVessel != null)
                        _camCtrl.AimNearVessel(FlightGlobals.ActiveVessel);
                };
                _camWin.OnKeepDistance = () =>
                {
                    if (_camCtrl != null && FlightGlobals.ActiveVessel != null)
                        _camCtrl.KeepTrackDistance(FlightGlobals.ActiveVessel);
                };
                Debug.Log("[VCR] CameraWindow OK. visible=" + _camWin.IsVisible);

                Debug.Log("[VCR] Creating FrameStreamer...");
                _streamer = new FrameStreamer();
                _streamer.Initialise(_camCtrl.OutputTexture);
                Debug.Log("[VCR] FrameStreamer OK. Pipe name=" + _streamer.PipeName);

                _initialised = true;
                Debug.Log("[VCR] SetUp complete. appButton=" + (_appButton != null));
                // Window starts hidden. User clicks the toolbar button to open it.
            }
            catch (Exception ex)
            {
                Debug.LogError("[VCR] SetUp FAILED: " + ex);
                TearDown();
            }
        }

        private void TearDown()
        {
            _initialised = false;

            if (_recording) StopRecordingAndWait(15000);
            else if (_ffmpeg != null)
            {
                Process ffmpegToWait = _ffmpeg;
                _ffmpeg = null;
                FinalizeAndDisposeFFmpeg(ffmpegToWait, 10000);
            }

            if (_mainBlitCapture != null)
            {
                UnityEngine.Object.Destroy(_mainBlitCapture);
                _mainBlitCapture = null;
            }

            _streamer?.Dispose();
            _streamer = null;

            _camCtrl?.Dispose();
            _camCtrl = null;

            _camWin = null;
        }

        private void EnsureMainCameraCapture()
        {
            if (_camCtrl == null) return;

            // If final-frame mode is disabled, remove hook to avoid hijacking preview.
            if (!_camCtrl.UseMainCameraFinalFrame)
            {
                if (_mainBlitCapture != null)
                {
                    UnityEngine.Object.Destroy(_mainBlitCapture);
                    _mainBlitCapture = null;
                }
                return;
            }

            Camera main = Camera.main;
            if (main == null) return;

            if (_mainBlitCapture == null || _mainBlitCapture.gameObject != main.gameObject)
            {
                if (_mainBlitCapture != null)
                    UnityEngine.Object.Destroy(_mainBlitCapture);

                _mainBlitCapture = main.GetComponent<MainCameraBlitCapture>();
                if (_mainBlitCapture == null)
                    _mainBlitCapture = main.gameObject.AddComponent<MainCameraBlitCapture>();
            }

            _mainBlitCapture.Target = _camCtrl.OutputTexture;
        }

        // ── recording ──────────────────────────────────────────────────

        private void ToggleRecording()
        {
            Debug.Log("[VCR] ToggleRecording — _recording=" + _recording
                + " _initialised=" + _initialised
                + " _streamer=" + (_streamer != null));
            if (_recording) StopRecording();
            else            StartRecording();
        }

        private void StartRecording()
        {
            Debug.Log("[VCR] StartRecording called. _recording=" + _recording + " _initialised=" + _initialised);
            if (_recording || !_initialised) return;

            _streamer.TargetFps = _camCtrl.TargetFps;

            var streamer = _streamer;
            new Thread(() =>
            {
                // Czekaj az pipe bedzie gotowy — ale max 3s i przerwij gdy disposed
                streamer.WaitUntilListening(3000);

                // Jeśli w międzyczasie zamknięto grę — nie startuj
                if (!_initialised || streamer != _streamer) return;

                LaunchFFmpeg();
                streamer.StartStreaming();
                _recording           = true;
                _recordStartTime     = Time.realtimeSinceStartup;
                _lastBytesSample     = 0;
                _lastBytesSampleTime = Time.realtimeSinceStartup;
                _nextPerfLogTime     = Time.realtimeSinceStartup + 2f;
                Debug.Log("[VCR] Recording started. PipeName=" + streamer.PipeName);
            })
            {
                IsBackground = true,
                Name = "VCR-StartRec"
            }.Start();
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            LogRecordingPerf("final");

            // Zamknij pipe — to wysyla EOF do ffmpeg, ktory sam zapisze moov atom
            _streamer?.StopStreaming();

            // Czekaj na ffmpeg w watku tla (nie blokuj glownego watku Unity)
            var ffmpegToWait = _ffmpeg;
            _ffmpeg = null;
            if (ffmpegToWait != null)
            {
                var t = new Thread(() =>
                {
                    FinalizeAndDisposeFFmpeg(ffmpegToWait, 10000);
                })
                {
                    IsBackground = true,
                    Name = "VCR-FFmpegWait"
                };
                t.Start();
            }

            Debug.Log("[VCR] Recording stop requested — waiting for FFmpeg to finalise file.");
        }

        private void StopRecordingAndWait(int timeoutMs)
        {
            if (!_recording) return;

            _recording = false;
            LogRecordingPerf("final");
            _streamer?.StopStreaming();

            Process ffmpegToWait = _ffmpeg;
            _ffmpeg = null;
            if (ffmpegToWait != null)
                FinalizeAndDisposeFFmpeg(ffmpegToWait, timeoutMs);

            Debug.Log("[VCR] Recording stopped and finalised synchronously.");
        }

        private void FinalizeAndDisposeFFmpeg(Process ffmpegToWait, int timeoutMs)
        {
            int exitCode = int.MinValue;
            bool exited = false;
            try
            {
                // Daj ffmpeg czas na finalizacje pliku.
                exited = ffmpegToWait.WaitForExit(timeoutMs);
                if (!exited)
                {
                    Debug.LogWarning("[VCR] FFmpeg did not finish in " + timeoutMs + "ms — requesting soft quit.");
                    try { ffmpegToWait.StandardInput.WriteLine("q"); } catch { }
                    exited = ffmpegToWait.WaitForExit(5000);
                }

                if (exited)
                {
                    exitCode = ffmpegToWait.ExitCode;
                    Debug.Log("[VCR] FFmpeg exited with code " + exitCode);
                }
                else
                {
                    Debug.LogWarning("[VCR] FFmpeg still running after soft-close attempts; not killing process.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VCR] FFmpeg wait error: " + ex.Message);
            }
            finally
            {
                ffmpegToWait.Dispose();
            }

            if (exited && exitCode != 0)
                TryRepairActiveOutput();
        }

        // ── window → ModLoader callbacks ├─────────────────────────────

        private void OnWindowApplySettings(int w, int h, int fps)
        {
            bool wasRecording = _recording;
            if (wasRecording) StopRecording();

            _streamer?.Dispose();
            _camCtrl?.Dispose();

            _camCtrl = new CameraController
            {
                TargetWidth  = w,
                TargetHeight = h,
                TargetFps    = fps,
                UseMainCameraFinalFrame = false,
            };
            _camCtrl.Initialise();
            EnsureMainCameraCapture();

            if (FlightGlobals.ActiveVessel != null)
                _camCtrl.SnapToVessel(FlightGlobals.ActiveVessel);

            _camWin?.SetCamera(_camCtrl);

            _streamer = new FrameStreamer();
            _streamer.Initialise(_camCtrl.OutputTexture);

            if (wasRecording) StartRecording();
        }

        // ── push live state to the window every frame ──────────────────

        private void UpdateWindowState()
        {
            if (_camWin == null) return;

            _camWin.IsRecording    = _recording;
            _camWin.IsPipeConnected = _streamer != null && _streamer.IsClientConnected;
            _camWin.RecordSeconds  = _recording
                ? Time.realtimeSinceStartup - _recordStartTime
                : 0f;

            if (_recording && _streamer != null)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastBytesSampleTime >= 1f)
                {
                    long  cur      = _streamer.BytesWritten;
                    float elapsed  = now - _lastBytesSampleTime;
                    _bitrateKBps   = (cur - _lastBytesSample) / elapsed / 1024f;
                    _lastBytesSample     = cur;
                    _lastBytesSampleTime = now;
                }
            }
            else
            {
                _bitrateKBps = 0f;
            }

            _camWin.BitrateKBps = _bitrateKBps;

            if (_recording && Time.realtimeSinceStartup >= _nextPerfLogTime)
            {
                _nextPerfLogTime = Time.realtimeSinceStartup + 2f;
                LogRecordingPerf("live");
            }
        }

        private void LogRecordingPerf(string tag)
        {
            if (_camCtrl == null || _streamer == null) return;

            CameraTelemetrySnapshot cam = _camCtrl.GetTelemetrySnapshot();
            StreamerTelemetrySnapshot str = _streamer.GetTelemetrySnapshot();

            float now = Time.realtimeSinceStartup;
            float recSec = Mathf.Max(0.001f, now - _recordStartTime);
            float expectedFrames = recSec * Mathf.Max(1, _camCtrl.TargetFps);

            float camLatePct = cam.RenderedFrames > 0
                ? (100f * cam.LateFrames / cam.RenderedFrames)
                : 0f;
            float dupPct = str.FramesWritten > 0
                ? (100f * str.DuplicateFrames / str.FramesWritten)
                : 0f;
            float missPct = expectedFrames > 0f
                ? (100f * Mathf.Max(0f, expectedFrames - cam.RenderedFrames) / expectedFrames)
                : 0f;

            string bottleneck;
            float frameBudgetMs = 1000f / Mathf.Max(1, _camCtrl.TargetFps);
            if (str.WriteAvgMs > frameBudgetMs * 0.7f || str.LateCadenceFrames > cam.LateFrames)
                bottleneck = "pipe/encoder pressure";
            else if (str.ReadbackAvgMs > frameBudgetMs * 0.5f || str.ReadbackErrors > 0)
                bottleneck = "GPU readback";
            else if (cam.AvgRenderMs > frameBudgetMs * 0.7f || camLatePct > 10f)
                bottleneck = "render cadence";
            else
                bottleneck = "mixed/unknown";

            Debug.Log(string.Format(
                "[VCR][PERF:{0}] t={1:F1}s expected={2:F0} cam={3} miss={4:F1}% late={5:F1}% interval(avg/max)={6:F2}/{7:F2}ms render(avg/max)={8:F2}/{9:F2}ms | readback(avg/max)={10:F2}/{11:F2}ms err={12} copy(avg/max)={13:F2}/{14:F2}ms publish(avg/max)={15:F2}/{16:F2}ms callback(avg/max)={17:F2}/{18:F2}ms | pipeWrite(avg/max)={19:F2}/{20:F2}ms dup={21:F1}% frameAge(avg/max)={22:F2}/{23:F2}ms cadenceLate={24} | out={25:F2}MB/s suspect={26}",
                tag,
                recSec,
                expectedFrames,
                cam.RenderedFrames,
                missPct,
                camLatePct,
                cam.AvgFrameIntervalMs,
                cam.MaxFrameIntervalMs,
                cam.AvgRenderMs,
                cam.MaxRenderMs,
                str.ReadbackAvgMs,
                str.ReadbackMaxMs,
                str.ReadbackErrors,
                str.ReadbackCopyAvgMs,
                str.ReadbackCopyMaxMs,
                str.ReadbackPublishAvgMs,
                str.ReadbackPublishMaxMs,
                str.ReadbackCallbackAvgMs,
                str.ReadbackCallbackMaxMs,
                str.WriteAvgMs,
                str.WriteMaxMs,
                dupPct,
                str.FrameAgeAvgMs,
                str.FrameAgeMaxMs,
                str.LateCadenceFrames,
                _bitrateKBps / 1024f,
                bottleneck));
        }

        // ── FFmpeg process management ──────────────────────────────────

        private void LaunchFFmpeg()
        {
            if (_camCtrl == null) return;
            try
            {
                string outputPath = NextOutputPath();
                _activeOutputPath = outputPath;
                string preset = FixedFfmpegPreset;

                string args = string.Format(
                    "-f rawvideo -pixel_format rgb24 -video_size {0}x{1} -framerate {2} " +
                    "-i \\\\.\\pipe\\{3} " +
                    "-c:v libx264 -preset {4} -crf 17 -profile:v high -pix_fmt yuv420p -vf vflip -movflags +frag_keyframe+empty_moov+default_base_moof+faststart -y \"{5}\"",
                    _camCtrl.TargetWidth, _camCtrl.TargetHeight,
                    _camCtrl.TargetFps,  _streamer.PipeName,
                    preset,
                    outputPath);

                string logPath = Path.Combine(RecordingsDir, "ffmpeg_lastrun.log");

                var psi = new ProcessStartInfo(FfmpegPath, args)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput  = true,
                };
                _ffmpeg = Process.Start(psi);
                _ffmpeg.ErrorDataReceived  += (s, ev) => { if (ev.Data != null) File.AppendAllText(logPath, ev.Data + "\n"); };
                _ffmpeg.OutputDataReceived += (s, ev) => { if (ev.Data != null) File.AppendAllText(logPath, ev.Data + "\n"); };
                _ffmpeg.BeginErrorReadLine();
                _ffmpeg.BeginOutputReadLine();
                Debug.Log("[VCR] FFmpeg launched (PID " + _ffmpeg.Id + ") preset=" + preset + ". Log: " + logPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VCR] Could not launch FFmpeg: " + ex.Message);
                _ffmpeg = null;
            }
        }

        private void KillFFmpeg()
        {
            if (_ffmpeg == null) return;
            try
            {
                if (!_ffmpeg.HasExited)
                {
                    try { _ffmpeg.StandardInput.WriteLine("q"); } catch { }
                    _ffmpeg.WaitForExit(5000);
                }
            }
            catch (Exception ex) { Debug.LogWarning("[VCR] KillFFmpeg(soft): " + ex.Message); }
            finally { _ffmpeg.Dispose(); _ffmpeg = null; }
        }

        private void TryRepairActiveOutput()
        {
            string inputPath = _activeOutputPath;
            _activeOutputPath = null;

            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
                return;

            try
            {
                RepairFileInPlace(inputPath, 10000);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VCR] Recovery exception: " + ex.Message);
            }
        }

        private void RepairExistingRecordings()
        {
            if (_repairRunning)
            {
                _repairStatus = "Repair already running...";
                return;
            }

            _repairRunning = true;
            _repairStatus = "Scanning recordings...";

            new Thread(() =>
            {
                int checkedCount = 0;
                int repairedCount = 0;
                int failedCount = 0;
                try
                {
                    if (!Directory.Exists(RecordingsDir))
                    {
                        _repairStatus = "Recordings folder not found.";
                        return;
                    }

                    string[] files = Directory.GetFiles(RecordingsDir, "*.mp4", SearchOption.TopDirectoryOnly);
                    var candidates = new List<string>();
                    for (int i = 0; i < files.Length; i++)
                    {
                        string f = files[i];
                        string n = Path.GetFileName(f);
                        if (n.EndsWith("_repaired.mp4", StringComparison.OrdinalIgnoreCase)) continue;
                        if (n.EndsWith(".broken", StringComparison.OrdinalIgnoreCase)) continue;
                        candidates.Add(f);
                    }

                    for (int i = 0; i < candidates.Count; i++)
                    {
                        string file = candidates[i];
                        checkedCount++;
                        _repairStatus = string.Format("Repairing {0}/{1}: {2}", checkedCount, candidates.Count, Path.GetFileName(file));

                        bool ok = RepairFileInPlace(file, 15000);
                        if (ok) repairedCount++;
                        else failedCount++;
                    }

                    _repairStatus = string.Format("Repair finished. Checked: {0}, repaired: {1}, failed: {2}", checkedCount, repairedCount, failedCount);
                }
                catch (Exception ex)
                {
                    _repairStatus = "Repair failed: " + ex.Message;
                    Debug.LogWarning("[VCR] RepairExistingRecordings exception: " + ex.Message);
                }
                finally
                {
                    _repairRunning = false;
                }
            })
            {
                IsBackground = true,
                Name = "VCR-RepairExisting"
            }.Start();
        }

        private bool RepairFileInPlace(string inputPath, int timeoutMs)
        {
            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
                return false;

            string dir = Path.GetDirectoryName(inputPath) ?? RecordingsDir;
            string name = Path.GetFileNameWithoutExtension(inputPath);
            string ext = Path.GetExtension(inputPath);
            string repairedPath = Path.Combine(dir, name + "_repaired" + ext);

            var psi = new ProcessStartInfo(
                FfmpegPath,
                string.Format("-err_detect ignore_err -i \"{0}\" -c copy -movflags +faststart -y \"{1}\"", inputPath, repairedPath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
            };

            using (var fix = Process.Start(psi))
            {
                if (fix == null)
                {
                    Debug.LogWarning("[VCR] Recovery FFmpeg process failed to start for: " + inputPath);
                    return false;
                }

                string stdErr = fix.StandardError.ReadToEnd();
                string stdOut = fix.StandardOutput.ReadToEnd();
                bool done = fix.WaitForExit(timeoutMs);

                if (!done)
                {
                    try { fix.StandardInput.WriteLine("q"); } catch { }
                    done = fix.WaitForExit(5000);
                    if (!done)
                    {
                        Debug.LogWarning("[VCR] Recovery timed out for: " + inputPath + " (soft-close only, no kill)");
                        return false;
                    }
                }

                if (fix.ExitCode != 0 || !File.Exists(repairedPath))
                {
                    Debug.LogWarning("[VCR] Recovery failed for " + inputPath + ". Exit=" + fix.ExitCode + " err=" + stdErr);
                    return false;
                }

                long newSize = new FileInfo(repairedPath).Length;
                if (newSize <= 0)
                {
                    try { File.Delete(repairedPath); } catch { }
                    Debug.LogWarning("[VCR] Recovery output empty for: " + inputPath);
                    return false;
                }

                string backupPath = inputPath + ".broken";
                try
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(inputPath, backupPath);
                    File.Move(repairedPath, inputPath);
                    Debug.Log("[VCR] Repaired existing file: " + inputPath + " backup=" + backupPath);
                }
                catch (Exception moveEx)
                {
                    Debug.LogWarning("[VCR] Recovery move failed for " + inputPath + ": " + moveEx.Message);
                    return false;
                }

                if (!string.IsNullOrEmpty(stdOut))
                    Debug.Log("[VCR] Recovery output: " + stdOut);

                return true;
            }
        }

        // ── AppLauncher toolbar button ─────────────────────────────────

        private void AddAppLauncherButton()
        {
            Debug.Log("[VCR] AddAppLauncherButton called. _appButton=" + (_appButton != null)
                + " Instance=" + (ApplicationLauncher.Instance != null)
                + " Ready=" + ApplicationLauncher.Ready);

            if (_appButton != null)
            {
                Debug.Log("[VCR] Button already exists — skipping.");
                return;
            }
            if (ApplicationLauncher.Instance == null)
            {
                Debug.LogWarning("[VCR] ApplicationLauncher.Instance is null — cannot add button.");
                return;
            }

            _appIcon   = BuildCameraIcon();
            _appButton = ApplicationLauncher.Instance.AddModApplication(
                onTrue:          OnAppButtonOn,
                onFalse:         OnAppButtonOff,
                onHover:         null,
                onHoverOut:      null,
                onEnable:        null,
                onDisable:       null,
                visibleInScenes: ApplicationLauncher.AppScenes.FLIGHT,
                texture:         _appIcon);

            if (_appButton != null)
                Debug.Log("[VCR] AppLauncher button added successfully.");
            else
                Debug.LogError("[VCR] AddModApplication returned null!");
        }

        private void RemoveAppLauncherButton()
        {
            Debug.Log("[VCR] RemoveAppLauncherButton. _appButton=" + (_appButton != null));
            if (_appButton == null) return;
            if (ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(_appButton);
            _appButton = null;
            Debug.Log("[VCR] AppLauncher button removed.");
        }

        private void OnAppButtonOn()
        {
            Debug.Log("[VCR] OnAppButtonOn — _initialised=" + _initialised
                + " _camWin=" + (_camWin != null));
            if (_camWin == null)
            {
                Debug.LogWarning("[VCR] OnAppButtonOn: _camWin is null (SetUp may not have run yet).");
                return;
            }
            _camWin.Show();
            Debug.Log("[VCR] Window shown.");
        }

        private void OnAppButtonOff()
        {
            Debug.Log("[VCR] OnAppButtonOff — _camWin=" + (_camWin != null));
            _camWin?.Hide();
        }

        // ── toolbar icon (38×38, procedurally drawn) ───────────────────

        private static Texture2D BuildCameraIcon()
        {
            const int S = 38;
            var tex = new Texture2D(S, S, TextureFormat.ARGB32, false);

            Color bg     = new Color(0.11f, 0.11f, 0.13f, 0.85f);
            Color body   = new Color(0.60f, 0.65f, 0.70f, 1.00f);
            Color lens0  = new Color(0.90f, 0.90f, 0.92f, 1.00f);
            Color lens1  = new Color(0.15f, 0.20f, 0.28f, 1.00f);
            Color lens2  = new Color(0.70f, 0.80f, 0.95f, 1.00f);
            Color shine  = new Color(0.95f, 0.98f, 1.00f, 0.70f);

            // Background fill
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    tex.SetPixel(x, y, bg);

            // Camera body (rectangle)
            FillRect(tex,  2, 10, 35, 28, body);

            // Viewfinder bump (top centre)
            FillRect(tex, 13,  4, 24, 10, body);

            // Shutter button (small bump top-right of body)
            FillRect(tex, 27,  6, 33, 10, body);

            // Lens rings
            FillCircle(tex, 18, 19,  9, lens0);
            FillCircle(tex, 18, 19,  7, lens1);
            FillCircle(tex, 18, 19,  4, lens2);
            FillCircle(tex, 18, 19,  2, lens1);

            // Lens shine (small highlight)
            FillCircle(tex, 15, 22, 1, shine);

            tex.Apply();
            return tex;
        }

        private static void FillRect(Texture2D t, int x0, int y0, int x1, int y1, Color c)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    t.SetPixel(x, y, c);
        }

        private static void FillCircle(Texture2D t, int cx, int cy, int r, Color c)
        {
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                        t.SetPixel(x, y, c);
        }
    }
}
