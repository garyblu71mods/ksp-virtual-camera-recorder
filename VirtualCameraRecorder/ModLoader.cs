using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
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

        private bool  _initialised;
        private float _nextSetUpAttempt; // realtime seconds – cooldown between retries

        // ── recording state ────────────────────────────────────────────
        private bool  _recording;
        private float _recordStartTime;
        private long  _lastBytesSample;
        private float _lastBytesSampleTime;
        private float _bitrateKBps;

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

            _camCtrl.Tick();
            _streamer?.RequestFrame();   // zawsze — bufor gotowy gdy REC zostanie klikniety

            UpdateWindowState();
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

            // Natychmiast zatrzymaj nagrywanie i zabij FFmpeg — nie czekaj
            _recording = false;
            KillFFmpeg();

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
            // If previous anchor is gone/unloaded, silently freeze in place.
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
                Debug.Log("[VCR] Creating CameraController 1280x720 30fps...");
                _camCtrl = new CameraController
                {
                    TargetWidth  = 1280,
                    TargetHeight = 720,
                    TargetFps    = 30,
                };
                _camCtrl.Initialise();
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
                _camWin.OnSnapHere      = () =>
                {
                    if (_camCtrl != null)
                        _camCtrl.SnapHere(FlightGlobals.ActiveVessel);
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

            if (_recording) StopRecording();

            _streamer?.Dispose();
            _streamer = null;

            _camCtrl?.Dispose();
            _camCtrl = null;

            _camWin = null;
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

            // Zamknij pipe — to wysyla EOF do ffmpeg, ktory sam zapisze moov atom
            _streamer?.StopStreaming();

            // Czekaj na ffmpeg w watku tla (nie blokuj glownego watku Unity)
            var ffmpegToWait = _ffmpeg;
            _ffmpeg = null;
            if (ffmpegToWait != null)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        // daj ffmpeg 10s na finalizacje pliku
                        if (!ffmpegToWait.WaitForExit(10000))
                        {
                            Debug.LogWarning("[VCR] FFmpeg did not finish in 10s — killing.");
                            try { ffmpegToWait.Kill(); } catch { }
                            ffmpegToWait.WaitForExit(2000);
                        }
                        Debug.Log("[VCR] FFmpeg exited with code " + ffmpegToWait.ExitCode);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[VCR] FFmpeg wait error: " + ex.Message);
                    }
                    finally
                    {
                        ffmpegToWait.Dispose();
                    }
                })
                {
                    IsBackground = true,
                    Name = "VCR-FFmpegWait"
                };
                t.Start();
            }

            Debug.Log("[VCR] Recording stop requested — waiting for FFmpeg to finalise file.");
        }

        // ── window → ModLoader callbacks ├─────────────────────────────

        private void OnWindowApplySettings(int w, int h, int fps)
        {
            bool wasRecording = _recording;
            if (wasRecording) StopRecording();

            // Rebuild camera + streamer with new resolution / fps.
            _streamer?.Dispose();
            _camCtrl?.Dispose();

            _camCtrl = new CameraController
            {
                TargetWidth  = w,
                TargetHeight = h,
                TargetFps    = fps,
            };
            _camCtrl.Initialise();

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

            // Rolling bitrate sample every second.
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
        }

        // ── FFmpeg process management ──────────────────────────────────

        private void LaunchFFmpeg()
        {
            if (_camCtrl == null) return;
            try
            {
                string outputPath = NextOutputPath();

                string args = string.Format(
                    "-f rawvideo -pixel_format rgb24 -video_size {0}x{1} -framerate {2} " +
                    "-i \\\\.\\pipe\\{3} " +
                    "-c:v libx264 -pix_fmt yuv420p -preset fast -vf vflip -y \"{4}\"",
                    _camCtrl.TargetWidth, _camCtrl.TargetHeight,
                    _camCtrl.TargetFps,  _streamer.PipeName,
                    outputPath);

                string logPath = Path.Combine(RecordingsDir, "ffmpeg_lastrun.log");

                var psi = new ProcessStartInfo(FfmpegPath, args)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                };
                _ffmpeg = Process.Start(psi);
                _ffmpeg.ErrorDataReceived  += (s, ev) => { if (ev.Data != null) File.AppendAllText(logPath, ev.Data + "\n"); };
                _ffmpeg.OutputDataReceived += (s, ev) => { if (ev.Data != null) File.AppendAllText(logPath, ev.Data + "\n"); };
                _ffmpeg.BeginErrorReadLine();
                _ffmpeg.BeginOutputReadLine();
                Debug.Log("[VCR] FFmpeg launched (PID " + _ffmpeg.Id + "). Log: " + logPath);
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
            try { if (!_ffmpeg.HasExited) _ffmpeg.Kill(); }
            catch (Exception ex) { Debug.LogWarning("[VCR] KillFFmpeg: " + ex.Message); }
            finally { _ffmpeg.Dispose(); _ffmpeg = null; }
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
