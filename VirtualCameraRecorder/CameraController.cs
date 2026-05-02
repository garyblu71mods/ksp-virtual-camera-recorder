using System;
using UnityEngine;

namespace VirtualCameraRecorder
{
    public enum AnchorMode
    {
        VesselLocal,    // kamera podaza za statkiem (offset w local-space)
        FreeFloat,      // kamera wisi w swiecie, nie podaza za niczym
        SurfaceLocked,  // kamera obraca sie razem z planeta
        TargetTrack,    // kamera stoi w miejscu, ale zawsze patrzy na aktywny statek
    }

    internal sealed class CameraController : IDisposable
    {
        // ── config ─────────────────────────────────────────────────────
        public int   TargetWidth  = 1920;
        public int   TargetHeight = 1080;
        public int   TargetFps    = 30;
        public float FieldOfView  = 40f;

        // ── public state ───────────────────────────────────────────────
        public RenderTexture OutputTexture { get; private set; }
        public Camera        VirtualCamera { get; private set; }
        public AnchorMode    Mode          = AnchorMode.VesselLocal;

        public string AnchorDescription
        {
            get
            {
                switch (Mode)
                {
                    case AnchorMode.VesselLocal:
                        return _anchorVessel != null ? _anchorVessel.vesselName : "(none)";
                    case AnchorMode.FreeFloat:
                        return "free float";
                    case AnchorMode.SurfaceLocked:
                        return _anchorBody != null ? _anchorBody.name : "(none)";
                    case AnchorMode.TargetTrack:
                        return FlightGlobals.ActiveVessel != null
                            ? FlightGlobals.ActiveVessel.vesselName : "(none)";
                    default: return "?";
                }
            }
        }

        // keep for compat
        public string AnchorVesselName => AnchorDescription;

        public bool IsAnchorLoaded
        {
            get
            {
                switch (Mode)
                {
                    case AnchorMode.VesselLocal:
                        return _anchorVessel != null && !_anchorVessel.packed;
                    case AnchorMode.SurfaceLocked:
                        return _anchorBody != null;
                    default:
                        return true;
                }
            }
        }

        // ── private ────────────────────────────────────────────────────
        private GameObject    _cameraGO;
        private float         _lastRenderTime;

        // Rotation stored as pure quaternion — no gimbal lock, full 360 on all axes.
        private Quaternion _rotation = Quaternion.identity;
        // FPS-style rotation: yaw as a horizontal direction vector (surface-relative),
        // pitch as angle above/below horizon. Guarantees zero roll on LMB drag.
        private Vector3 _yawForward = Vector3.forward;
        private float   _pitch;   // stopnie powyzej (+) / ponizej (-) horyzontu
        private float   _roll;    // stopnie rollu (MMB+PPM)

        // Anchors
        private Vessel        _anchorVessel;
        private CelestialBody _anchorBody;
        private Vector3       _localOffset;   // vessel-local or body-local

        private Vector2 _lookInputTarget;
        private Vector2 _lookInputSmoothed;
        private Vector2 _panInputTarget;
        private Vector2 _panInputSmoothed;
        private float   _dollyInputTarget;
        private float   _dollyInputSmoothed;

        private static readonly int ExcludedLayers =
            (1 << 5); // only UI

        private Camera _spaceCamera;
        private GameObject _spaceCameraGO;
        private Camera _galaxyCamera;
        private GameObject _galaxyCameraGO;
        private int _spaceMask;
        private int _localMask;

        private Camera _spaceReferenceCamera;
        private Camera _galaxyReferenceCamera;
        private Camera _scaledReferenceCamera;

        private Camera _mainCaptureCamera;
        private MainCameraCaptureHook _mainCaptureHook;
        private bool _ownsMainCaptureHook;
        private bool _captureMainCameraFinalFrame = true;

        private static int IncludeLayerIfExists(int mask, string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0) mask |= (1 << layer);
            return mask;
        }

        private static int RemoveLayerIfExists(int mask, string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0) mask &= ~(1 << layer);
            return mask;
        }

        private int BuildGalaxyMask()
        {
            int namedMask = 0;
            namedMask = IncludeLayerIfExists(namedMask, "Galaxy");
            namedMask = IncludeLayerIfExists(namedMask, "Stars");

            int refMask = _galaxyReferenceCamera != null ? _galaxyReferenceCamera.cullingMask : 0;
            int mask = refMask != 0 ? refMask : namedMask;
            return mask & ~ExcludedLayers;
        }

        private int BuildSpaceMask()
        {
            int namedMask = 0;
            namedMask = IncludeLayerIfExists(namedMask, "Scaled Scenery");
            namedMask = IncludeLayerIfExists(namedMask, "ScaledScenery");

            int refMask = _scaledReferenceCamera != null ? _scaledReferenceCamera.cullingMask : 0;
            int mask = refMask != 0 ? refMask : namedMask;
            return mask & ~ExcludedLayers;
        }

        private int BuildLocalMask(int spaceMaskToExclude)
        {
            int mask = Camera.main != null
                ? Camera.main.cullingMask
                : ~0;
            mask &= ~ExcludedLayers;
            // Prevent duplicated rendering between passes (caused terrain ghosting/smear).
            mask &= ~spaceMaskToExclude;
            return mask;
        }

        // ── init ───────────────────────────────────────────────────────

        public void Initialise()
        {
            OutputTexture = new RenderTexture(TargetWidth, TargetHeight, 24,
                                              RenderTextureFormat.ARGB32)
            {
                name         = "VCR_RenderTex",
                antiAliasing = 1,
                filterMode   = FilterMode.Bilinear,
                wrapMode     = TextureWrapMode.Clamp,
            };
            OutputTexture.Create();

            _cameraGO = new GameObject("VCR_Camera");
            UnityEngine.Object.DontDestroyOnLoad(_cameraGO);

            _spaceCameraGO = new GameObject("VCR_SpaceCamera");
            UnityEngine.Object.DontDestroyOnLoad(_spaceCameraGO);

            _galaxyCameraGO = new GameObject("VCR_GalaxyCamera");
            UnityEngine.Object.DontDestroyOnLoad(_galaxyCameraGO);

            _spaceReferenceCamera = FindSpaceCamera();
            _galaxyReferenceCamera = FindCameraByKeyword("Galaxy");
            _scaledReferenceCamera = FindCameraByKeyword("Scaled");
            int galaxyMask = BuildGalaxyMask();
            _spaceMask = BuildSpaceMask();
            _localMask = BuildLocalMask(_spaceMask | galaxyMask);

            LogSpaceDiagnostics("Init after camera discovery");

            // Galaxy/star camera (rendered first)
            _galaxyCamera = _galaxyCameraGO.AddComponent<Camera>();
            _galaxyCamera.targetTexture = OutputTexture;
            _galaxyCamera.cullingMask = galaxyMask;
            _galaxyCamera.clearFlags = CameraClearFlags.SolidColor;
            _galaxyCamera.backgroundColor = Color.black;
            _galaxyCamera.nearClipPlane = 0.01f;
            _galaxyCamera.farClipPlane = 50000000f;
            _galaxyCamera.depth = -12f;
            SyncGalaxyCameraFromReference();
            _galaxyCamera.enabled = false;

            // Foreground/local camera
            VirtualCamera = _cameraGO.AddComponent<Camera>();
            VirtualCamera.targetTexture = OutputTexture;
            VirtualCamera.cullingMask = _localMask;
            VirtualCamera.fieldOfView   = FieldOfView;
            VirtualCamera.nearClipPlane = 0.1f;
            VirtualCamera.farClipPlane  = 2000000f;
            VirtualCamera.depth         = -10;
            VirtualCamera.clearFlags    = CameraClearFlags.Depth;
            VirtualCamera.backgroundColor = Color.black;
            VirtualCamera.enabled = false;

            // Background/scaled-space camera
            _spaceCamera = _spaceCameraGO.AddComponent<Camera>();
            _spaceCamera.targetTexture = OutputTexture;
            _spaceCamera.cullingMask   = _spaceMask;
            _spaceCamera.fieldOfView   = FieldOfView;
            _spaceCamera.nearClipPlane = 0.01f;
            _spaceCamera.farClipPlane  = 50000000f;
            _spaceCamera.depth         = -11;
            _spaceCamera.clearFlags    = CameraClearFlags.Skybox;
            _spaceCamera.backgroundColor = Color.black;

            SyncSpaceCameraFromReference();
            _spaceCamera.enabled = false;

            Debug.Log("[VCR] CameraController initialised (dual-camera: space+local).");
        }

        // ── anchor methods ─────────────────────────────────────────────

        /// <summary>
        /// Zmienia tryb mocowania BEZ ruszania kamera.
        /// Zapisuje biezaca pozycje w swiecie jako offset dla nowego trybu.
        /// Poziomuje kamere wzgledem horyzontu.
        /// </summary>
        public void SetMode(AnchorMode newMode)
        {
            if (newMode == Mode || _cameraGO == null) return;

            Vector3 worldPos = _cameraGO.transform.position;
            Quaternion worldRot = _cameraGO.transform.rotation;

            Mode = newMode;
            switch (newMode)
            {
                case AnchorMode.VesselLocal:
                    if (_anchorVessel != null)
                        _localOffset = _anchorVessel.transform.InverseTransformPoint(worldPos);
                    break;

                case AnchorMode.SurfaceLocked:
                    _anchorBody = FlightGlobals.currentMainBody;
                    if (_anchorBody != null)
                        _localOffset = _anchorBody.transform.InverseTransformPoint(worldPos);
                    break;

                case AnchorMode.FreeFloat:
                case AnchorMode.TargetTrack:
                    // brak zmiany pozycji, brak zmiany offsetu
                    break;
            }

            // Zostaw orientacje bez zmian
            _rotation = worldRot;
            SyncFromRotation(_rotation, GetSurfaceUp(worldPos));
            _cameraGO.transform.rotation = _rotation;

            Debug.Log("[VCR] SetMode: " + newMode + " worldPos=" + worldPos + " (no visible move)");
        }

        /// <summary>Teleportuje kamere za statek i zapisuje offset w local-space.</summary>
        public void SnapToVessel(Vessel v)
        {
            if (v == null || _cameraGO == null) return;
            _localOffset  = new Vector3(0f, 8f, -20f);
            _cameraGO.transform.position = v.transform.TransformPoint(_localOffset);
            // Patrz na statek ale pozostan poziomo wzgledem horyzontu
            LevelToHorizon(_cameraGO.transform.position, v.transform.position);
            _rotation     = _cameraGO.transform.rotation;
            _anchorVessel = v;
            Mode          = AnchorMode.VesselLocal;
            Debug.Log("[VCR] SnapToVessel: " + v.vesselName);
        }

        /// <summary>Zmienia anchor na inny statek bez ruszania kamera.</summary>
        public void ReanchorToVessel(Vessel v)
        {
            if (v == null || _cameraGO == null) return;
            _anchorVessel = v;
            _localOffset  = v.transform.InverseTransformPoint(_cameraGO.transform.position);
            Debug.Log("[VCR] ReanchorToVessel: " + v.vesselName + " offset=" + _localOffset);
        }

        /// <summary>Zakotwicza kamere do powierzchni planety w biezacej pozycji.</summary>
        public void ReanchorToSurface()
        {
            if (_cameraGO == null) return;
            _anchorBody  = FlightGlobals.currentMainBody;
            if (_anchorBody == null) return;
            _localOffset = _anchorBody.transform.InverseTransformPoint(_cameraGO.transform.position);
            Debug.Log("[VCR] ReanchorToSurface: " + _anchorBody.name);
        }

        /// <summary>Przycisk "Snap here" — dziala zalezenia od trybu.</summary>
        public void SnapHere(Vessel activeVessel)
        {
            switch (Mode)
            {
                case AnchorMode.VesselLocal:
                    ReanchorToVessel(activeVessel);
                    break;
                case AnchorMode.SurfaceLocked:
                    ReanchorToSurface();
                    break;
            }
        }

        // ── per-frame tick ─────────────────────────────────────────────

        public void Tick()
        {
            if (_cameraGO == null) return;
            UpdatePosition();

            float dt = Mathf.Max(0.001f, Time.unscaledDeltaTime);
            float smooth = 1f - Mathf.Exp(-8f * dt);

            _lookInputSmoothed = Vector2.Lerp(_lookInputSmoothed, _lookInputTarget, smooth);
            if (_lookInputSmoothed.sqrMagnitude > 0.000001f)
            {
                float mag = Mathf.Clamp01(_lookInputSmoothed.magnitude);
                float speed = Mathf.Lerp(20f, 160f, mag * mag);
                float yawStep = _lookInputSmoothed.x * speed * dt;
                float pitchStep = _lookInputSmoothed.y * speed * dt;
                ApplyMouseDelta(yawStep, pitchStep, 1f);
            }

            _panInputSmoothed = Vector2.Lerp(_panInputSmoothed, _panInputTarget, smooth);
            if (_panInputSmoothed.sqrMagnitude > 0.000001f)
            {
                float mag = Mathf.Clamp01(_panInputSmoothed.magnitude);
                float speed = Mathf.Lerp(0.5f, 4.0f, mag * mag);
                float dx = _panInputSmoothed.x * speed * dt * 60f;
                float dy = _panInputSmoothed.y * speed * dt * 60f;
                ApplyMoveDelta(dx, dy, 0.05f);
            }

            _dollyInputSmoothed = Mathf.Lerp(_dollyInputSmoothed, _dollyInputTarget, smooth);
            if (Mathf.Abs(_dollyInputSmoothed) > 0.0001f)
            {
                float speed = Mathf.Lerp(0.6f, 4.5f, Mathf.Abs(_dollyInputSmoothed));
                float dy = -_dollyInputSmoothed * speed * dt * 60f;
                ApplyDollyDelta(0f, dy, 0.05f);
            }

            float interval = 1f / Mathf.Max(1, TargetFps);
            if (Time.realtimeSinceStartup - _lastRenderTime < interval) return;
            _lastRenderTime = Time.realtimeSinceStartup;

            if (_captureMainCameraFinalFrame)
            {
                if (!EnsureMainCaptureHook())
                {
                    // fallback when Camera.main unavailable
                    SyncVisualSettingsFromMainCamera();
                    RenderReferenceSpacePasses(_cameraGO.transform.position, _cameraGO.transform.rotation, VirtualCamera.fieldOfView);
                    VirtualCamera.enabled = true;
                    VirtualCamera.Render();
                    VirtualCamera.enabled = false;
                }
                return;
            }

            SyncVisualSettingsFromMainCamera();
            RenderReferenceSpacePasses(_cameraGO.transform.position, _cameraGO.transform.rotation, VirtualCamera.fieldOfView);
            VirtualCamera.enabled = true;
            VirtualCamera.Render();
            VirtualCamera.enabled = false;
        }

        private void UpdatePosition()
        {
            switch (Mode)
            {
                case AnchorMode.VesselLocal:
                    if (_anchorVessel != null && !_anchorVessel.packed)
                    {
                        _cameraGO.transform.position =
                            _anchorVessel.transform.TransformPoint(_localOffset);

                        // Keep vessel in frame while attached to vessel.
                        Vector3 toVessel = _anchorVessel.transform.position - _cameraGO.transform.position;
                        if (toVessel.sqrMagnitude > 0.01f)
                        {
                            Vector3 up = GetSurfaceUp(_cameraGO.transform.position);
                            Quaternion targetRot = Quaternion.LookRotation(toVessel, up);
                            _rotation = Quaternion.Slerp(
                                _cameraGO.transform.rotation,
                                targetRot,
                                1f - Mathf.Exp(-12f * Mathf.Max(0.001f, Time.unscaledDeltaTime)));
                            _cameraGO.transform.rotation = _rotation;
                            SyncFromRotation(_rotation, up);
                        }
                    }
                    break;

                case AnchorMode.SurfaceLocked:
                    if (_anchorBody != null)
                        _cameraGO.transform.position =
                            _anchorBody.transform.TransformPoint(_localOffset);
                    break;

                case AnchorMode.TargetTrack:
                    Vessel t = FlightGlobals.ActiveVessel;
                    if (t != null)
                    {
                        Vector3 dir = t.transform.position - _cameraGO.transform.position;
                        if (dir.sqrMagnitude > 0.01f)
                        {
                            Vector3 up = GetSurfaceUp(_cameraGO.transform.position);
                            Quaternion targetRot = Quaternion.LookRotation(dir, up);
                            _rotation = Quaternion.Slerp(_cameraGO.transform.rotation, targetRot,
                                1f - Mathf.Exp(-4f * Mathf.Max(0.001f, Time.unscaledDeltaTime)));
                            _cameraGO.transform.rotation = _rotation;
                            SyncFromRotation(_rotation, up);
                        }
                    }
                    break;

                // FreeFloat: pozycja zamrozona, kamera sie nie rusza
            }
        }

        // ── poziomowanie kamery wzgledem horyzontu ─────────────────────

        /// <summary>
        /// Buduje kwaternion rotacji z _yawForward, _pitch, _roll wzgledem surface-up.
        /// Obrot yaw jest wzgledem surface-up — dziala poprawnie w przestrzeni kosmicznej.
        /// Obrot pitch jest wzgledem lokalnej prawej osi po yaw — brak driftu rollu.
        /// </summary>
        private Quaternion ComputeRotation(Vector3 surfaceUp)
        {
            // Upewnij sie ze _yawForward lezy w plaszczyznie poziomej
            Vector3 fwd = Vector3.ProjectOnPlane(_yawForward, surfaceUp);
            if (fwd.sqrMagnitude < 0.001f)
                fwd = Vector3.ProjectOnPlane(Vector3.forward, surfaceUp);
            fwd.Normalize();
            _yawForward = fwd;

            Quaternion qYaw   = Quaternion.LookRotation(fwd, surfaceUp);
            Quaternion qPitch = Quaternion.AngleAxis(-_pitch, qYaw * Vector3.right);
            Quaternion qRoll  = Quaternion.AngleAxis(_roll,   qYaw * Vector3.forward);
            return qRoll * qPitch * qYaw;
        }

        /// <summary>Synchronizuje _yawForward/_pitch/_roll z podanego kwaternionu.</summary>
        private void SyncFromRotation(Quaternion q, Vector3 surfaceUp)
        {
            Vector3 forward  = q * Vector3.forward;
            Vector3 fwdLevel = Vector3.ProjectOnPlane(forward, surfaceUp);
            _yawForward = fwdLevel.sqrMagnitude > 0.001f
                ? fwdLevel.normalized
                : Vector3.ProjectOnPlane(Vector3.forward, surfaceUp).normalized;
            // kat od horyzontu: sin(pitch) = dot(forward, surfaceUp)
            _pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(forward, surfaceUp), -1f, 1f))
                     * Mathf.Rad2Deg;
            _roll  = 0f;
        }

        /// <summary>
        /// Ustawia rotacje kamery tak zeby:
        ///   - "up" kamery pokrywal sie z normalem powierzchni planety
        ///   - kamera patrzy w kierunku <paramref name="lookAt"/> ale pozostaje poziomo
        /// Dzieki temu na powierzchni kamera zawsze startuje z poziomym horyzontem.
        /// </summary>
        private void LevelToHorizon(Vector3 cameraPos, Vector3 lookAt)
        {
            // Normal powierzchni = kierunek od srodka planety do kamery
            Vector3 surfaceUp = GetSurfaceUp(cameraPos);

            // Rzut kierunku patrzenia na plaszczyzne horyzontu
            Vector3 toTarget = lookAt - cameraPos;
            Vector3 forward  = Vector3.ProjectOnPlane(toTarget, surfaceUp).normalized;

            // Jesli kierunek jest pionowy (kamera patrzy prosto w gore/dol),
            // uzyj jako forward kierunek "polnocy" (dowolna pozioma os)
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.ProjectOnPlane(Vector3.forward, surfaceUp).normalized;
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.ProjectOnPlane(Vector3.right, surfaceUp).normalized;

            _rotation   = Quaternion.LookRotation(forward, surfaceUp);
            _yawForward = forward;   // forward lezy juz w plaszczyznie poziomej
            _pitch      = 0f;
            _roll       = 0f;
            if (_cameraGO != null)
                _cameraGO.transform.rotation = _rotation;
        }

        /// <summary>
        /// Zwraca wektor "w gore" wzgledem powierzchni planety dla danej pozycji w swiecie.
        /// W przestrzeni kosmicznej (brak ciala niebieskiego) zwraca Vector3.up.
        /// </summary>
        private static Vector3 GetSurfaceUp(Vector3 worldPos)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body != null)
            {
                Vector3 up = (worldPos - (Vector3)body.transform.position).normalized;
                if (up.sqrMagnitude > 0.5f) return up;
            }
            return Vector3.up;
        }

        // ── sterowanie ─────────────────────────────────────────────────

        /// <summary>
        /// LMB drag — rozgladanie FPS: yaw wokal surface-up, pitch nad/pod horyzontem.
        /// Zero roll, dziala poprawnie w kazdej orientacji (orbit, powierzchnia, kosmosa).
        /// </summary>
        public void ApplyMouseDelta(float dx, float dy, float sensitivity = 0.3f)
        {
            if (_cameraGO == null) return;
            Vector3 surfaceUp = GetSurfaceUp(_cameraGO.transform.position);
            _yawForward = Quaternion.AngleAxis(dx * sensitivity, surfaceUp) * _yawForward;
            // dy > 0 = mysz w dol = kamera patrzy w dol (naturalny FPS)
            _pitch = Mathf.Clamp(_pitch - dy * sensitivity, -89f, 89f);
            _rotation = ComputeRotation(surfaceUp);
            _cameraGO.transform.rotation = _rotation;
        }

        /// <summary>
        /// MMB drag — dolly: dy>0 = do przodu, dx>0 = w prawo.
        /// </summary>
        public void ApplyDollyDelta(float dx, float dy, float sensitivity = 0.05f)
        {
            if (_cameraGO == null) return;
            float fovScale = (VirtualCamera != null ? VirtualCamera.fieldOfView : FieldOfView) / 40f;
            float s        = sensitivity * fovScale;
            Vector3 delta  = _cameraGO.transform.right   * ( dx * s)
                           + _cameraGO.transform.forward * (-dy * s);
            _cameraGO.transform.position += delta;

            switch (Mode)
            {
                case AnchorMode.VesselLocal:
                    if (_anchorVessel != null)
                        _localOffset = _anchorVessel.transform
                            .InverseTransformPoint(_cameraGO.transform.position);
                    break;
                case AnchorMode.SurfaceLocked:
                    if (_anchorBody != null)
                        _localOffset = _anchorBody.transform
                            .InverseTransformPoint(_cameraGO.transform.position);
                    break;
            }
        }

        /// <summary>
        /// MMB+RMB drag — roll wokal osi patrzenia kamery.
        /// dx > 0 = obrot zgodnie z ruchem wskazowek zegara.
        /// </summary>
        public void ApplyRollDelta(float dx, float sensitivity = 0.3f)
        {
            if (_cameraGO == null) return;
            _roll += dx * sensitivity;
            Vector3 surfaceUp = GetSurfaceUp(_cameraGO.transform.position);
            _rotation = ComputeRotation(surfaceUp);
            _cameraGO.transform.rotation = _rotation;
        }

        /// <summary>
        /// RMB drag (bez MMB) — pan w plaszczyznie widoku.
        /// Predkosc skaluje sie z FOV — przy duzym zoomie ruch jest wolniejszy.
        /// </summary>
        public void ApplyMoveDelta(float dx, float dy, float sensitivity = 0.05f)
        {
            if (_cameraGO == null) return;
            // Przy FOV=40 skalowanie=1; przy FOV=1 ~0.025 (bardzo wolno).
            float fovScale = (VirtualCamera != null ? VirtualCamera.fieldOfView : FieldOfView) / 40f;
            float s        = sensitivity * fovScale;
            Vector3 delta  = _cameraGO.transform.right * ( dx * s)
                           + _cameraGO.transform.up    * (-dy * s);
            _cameraGO.transform.position += delta;

            switch (Mode)
            {
                case AnchorMode.VesselLocal:
                    if (_anchorVessel != null)
                        _localOffset = _anchorVessel.transform
                            .InverseTransformPoint(_cameraGO.transform.position);
                    break;
                case AnchorMode.SurfaceLocked:
                    if (_anchorBody != null)
                        _localOffset = _anchorBody.transform
                            .InverseTransformPoint(_cameraGO.transform.position);
                    break;
            }
        }

        public void SetLookJoystick(Vector2 input)
        {
            _lookInputTarget = Vector2.ClampMagnitude(input, 1f);
        }

        public void SetPanJoystick(Vector2 input)
        {
            _panInputTarget = Vector2.ClampMagnitude(input, 1f);
        }

        public void SetDollySlider(float value)
        {
            _dollyInputTarget = Mathf.Clamp(value, -1f, 1f);
        }

        // ── IDisposable ────────────────────────────────────────────────

        public void Dispose()
        {
            if (_mainCaptureHook != null)
            {
                _mainCaptureHook.TargetTexture = null;
                if (_ownsMainCaptureHook)
                    UnityEngine.Object.Destroy(_mainCaptureHook);
                _mainCaptureHook = null;
                _mainCaptureCamera = null;
                _ownsMainCaptureHook = false;
            }

            if (_cameraGO != null)
            {
                UnityEngine.Object.Destroy(_cameraGO);
                _cameraGO = null;
            }
            if (_spaceCameraGO != null)
            {
                UnityEngine.Object.Destroy(_spaceCameraGO);
                _spaceCameraGO = null;
                _spaceCamera = null;
            }
            if (_galaxyCameraGO != null)
            {
                UnityEngine.Object.Destroy(_galaxyCameraGO);
                _galaxyCameraGO = null;
                _galaxyCamera = null;
            }
            if (OutputTexture != null)
            {
                OutputTexture.Release();
                UnityEngine.Object.Destroy(OutputTexture);
                OutputTexture = null;
            }
        }

        private float _nextVisualSyncTime;
        private float _nextDiagLogTime;

        private static string LayerMaskToNames(int mask)
        {
            if (mask == 0) return "<none>";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                int bit = 1 << i;
                if ((mask & bit) == 0) continue;
                string name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) name = "#" + i;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name);
            }
            return sb.ToString();
        }

        private static string CamInfo(Camera c)
        {
            if (c == null) return "<null>";
            return string.Format(
                "{0} | clear={1} near={2:F3} far={3:F0} depth={4:F1} mask=0x{5:X8} [{6}]",
                c.name,
                c.clearFlags,
                c.nearClipPlane,
                c.farClipPlane,
                c.depth,
                c.cullingMask,
                LayerMaskToNames(c.cullingMask));
        }

        private void LogSpaceDiagnostics(string tag)
        {
            try
            {
                Camera main = Camera.main;
                Debug.Log("[VCR][DIAG] " + tag + " main=" + CamInfo(main));
                Debug.Log("[VCR][DIAG] refs: space=" + CamInfo(_spaceReferenceCamera)
                    + " | galaxy=" + CamInfo(_galaxyReferenceCamera)
                    + " | scaled=" + CamInfo(_scaledReferenceCamera));
                Debug.Log("[VCR][DIAG] vcr: local=" + CamInfo(VirtualCamera)
                    + " | galaxyPass=" + CamInfo(_galaxyCamera)
                    + " | spacePass=" + CamInfo(_spaceCamera));
                Debug.Log("[VCR][DIAG] masks: local=0x" + _localMask.ToString("X8")
                    + " [" + LayerMaskToNames(_localMask) + "]"
                    + " space=0x" + _spaceMask.ToString("X8")
                    + " [" + LayerMaskToNames(_spaceMask) + "]");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VCR][DIAG] log failed: " + ex.Message);
            }
        }

        private void Update()
        {
            if (_spaceCamera != null && _galaxyCamera != null)
            {
                // Throttled runtime diagnostics
                float dt = Time.realtimeSinceStartup - _nextDiagLogTime;
                if (dt >= 5f)
                {
                    _nextDiagLogTime = Time.realtimeSinceStartup + 5f;
                    LogSpaceDiagnostics("diagUpdate");
                }
            }
        }

        private void FixedUpdate()
        {
            if (_spaceCamera != null && _galaxyCamera != null)
            {
                // Throttled runtime diagnostics
                float dt = Time.realtimeSinceStartup - _nextDiagLogTime;
                if (dt >= 5f)
                {
                    _nextDiagLogTime = Time.realtimeSinceStartup + 5f;
                    LogSpaceDiagnostics("diagFixedUpdate");
                }
            }
        }

        private void SyncVisualSettingsFromMainCamera()
        {
            if (Time.realtimeSinceStartup < _nextVisualSyncTime) return;
            _nextVisualSyncTime = Time.realtimeSinceStartup + 0.25f;

            Camera main = Camera.main;
            if (main == null || VirtualCamera == null) return;

            if (_spaceReferenceCamera == null)
                _spaceReferenceCamera = FindSpaceCamera();
            if (_galaxyReferenceCamera == null)
                _galaxyReferenceCamera = FindCameraByKeyword("Galaxy");
            if (_scaledReferenceCamera == null)
                _scaledReferenceCamera = FindCameraByKeyword("Scaled");

            // Throttled diagnostics (every ~5s)
            if (Time.realtimeSinceStartup >= _nextDiagLogTime)
            {
                _nextDiagLogTime = Time.realtimeSinceStartup + 5f;
                LogSpaceDiagnostics("SyncVisualSettingsFromMainCamera");
            }

            VirtualCamera.allowHDR = main.allowHDR;
            VirtualCamera.allowMSAA = main.allowMSAA;
            VirtualCamera.renderingPath = main.renderingPath;
            VirtualCamera.useOcclusionCulling = main.useOcclusionCulling;
            VirtualCamera.depthTextureMode = main.depthTextureMode;
            VirtualCamera.backgroundColor = main.backgroundColor;

            int galaxyMask = BuildGalaxyMask();
            _spaceMask = BuildSpaceMask();
            _localMask = BuildLocalMask(_spaceMask | galaxyMask);
            VirtualCamera.cullingMask = _localMask;

            if (_galaxyCamera != null)
            {
                _galaxyCamera.allowHDR = main.allowHDR;
                _galaxyCamera.allowMSAA = main.allowMSAA;
                _galaxyCamera.renderingPath = main.renderingPath;
                _galaxyCamera.useOcclusionCulling = main.useOcclusionCulling;
                _galaxyCamera.depthTextureMode = main.depthTextureMode;
                _galaxyCamera.cullingMask = galaxyMask;
            }

            if (_spaceCamera != null)
            {
                SyncSpaceCameraFromReference();

                _spaceCamera.allowHDR = main.allowHDR;
                _spaceCamera.allowMSAA = main.allowMSAA;
                _spaceCamera.renderingPath = main.renderingPath;
                _spaceCamera.useOcclusionCulling = main.useOcclusionCulling;
                _spaceCamera.depthTextureMode = main.depthTextureMode;
                _spaceCamera.cullingMask = _spaceMask;
            }

            EnsureMainCaptureHook();
        }

        private Camera FindSpaceCamera()
        {
            Camera[] cams = UnityEngine.Object.FindObjectsOfType<Camera>();
            Camera best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null) continue;

                int score = 0;
                string n = c.name ?? string.Empty;
                if (n.IndexOf("Galaxy", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
                if (n.IndexOf("Scaled", StringComparison.OrdinalIgnoreCase) >= 0) score += 80;
                if (n.IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0) score += 40;

                if (c.farClipPlane > 1_000_000f) score += 30;
                if (c.clearFlags == CameraClearFlags.Skybox || c.clearFlags == CameraClearFlags.SolidColor) score += 20;

                int mask = c.cullingMask;
                int ls = LayerMask.NameToLayer("Scaled Scenery");
                int lss = LayerMask.NameToLayer("ScaledScenery");
                int lg = LayerMask.NameToLayer("Galaxy");
                int lst = LayerMask.NameToLayer("Stars");

                if (ls >= 0 && (mask & (1 << ls)) != 0) score += 40;
                if (lss >= 0 && (mask & (1 << lss)) != 0) score += 40;
                if (lg >= 0 && (mask & (1 << lg)) != 0) score += 30;
                if (lst >= 0 && (mask & (1 << lst)) != 0) score += 30;

                // Penalize obvious local/UI cameras.
                if ((mask & (1 << 5)) != 0) score -= 50; // UI

                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            if (best != null)
                Debug.Log("[VCR] Space camera selected: " + best.name + " score=" + bestScore + " mask=" + best.cullingMask);
            else
                Debug.LogWarning("[VCR] Space camera not found.");

            return best;
        }

        private static Material GetCameraSkyboxMaterial(Camera cam)
        {
            if (cam == null) return null;
            var sb = cam.GetComponent<Skybox>();
            if (sb != null && sb.material != null)
                return sb.material;
            return null;
        }

        private void SyncSpaceCameraFromReference()
        {
            if (_spaceCamera == null) return;
            if (_spaceReferenceCamera == null)
                _spaceReferenceCamera = FindSpaceCamera();
            if (_spaceReferenceCamera == null) return;

            _spaceCamera.CopyFrom(_spaceReferenceCamera);
            _spaceCamera.targetTexture = OutputTexture;
            _spaceCamera.enabled = false;

            // Keep only space pass layers (no local terrain/vessels).
            _spaceCamera.cullingMask = _spaceMask;

            // Preserve KSP sky behavior; if reference has skybox material, force Skybox clear.
            Material skyMat = GetCameraSkyboxMaterial(_spaceReferenceCamera);
            if (skyMat != null)
            {
                _spaceCamera.clearFlags = CameraClearFlags.Skybox;
                var ownSky = _spaceCameraGO.GetComponent<Skybox>();
                if (ownSky == null) ownSky = _spaceCameraGO.AddComponent<Skybox>();
                ownSky.material = skyMat;
            }
            else
            {
                _spaceCamera.clearFlags = _spaceReferenceCamera.clearFlags;
            }

            _spaceCamera.depth = -11f;
            Debug.Log("[VCR][DIAG] Space pass from ref: " + CamInfo(_spaceReferenceCamera)
                + " -> pass mask=0x" + _spaceMask.ToString("X8")
                + " [" + LayerMaskToNames(_spaceMask) + "]");
        }

        private Camera FindCameraByKeyword(string keyword)
        {
            Camera[] cams = UnityEngine.Object.FindObjectsOfType<Camera>();
            Camera fallback = null;
            int fallbackScore = int.MinValue;

            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null || c == Camera.main) continue;

                string n = c.name ?? string.Empty;
                if (n.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return c;

                // Fallback heuristics for modded/non-standard camera names.
                int score = 0;
                if (c.farClipPlane > 1_000_000f) score += 30;
                if (c.clearFlags == CameraClearFlags.Skybox || c.clearFlags == CameraClearFlags.SolidColor) score += 20;
                if (keyword.Equals("Galaxy", StringComparison.OrdinalIgnoreCase))
                {
                    if (c.clearFlags == CameraClearFlags.Skybox) score += 40;
                }
                else if (keyword.Equals("Scaled", StringComparison.OrdinalIgnoreCase))
                {
                    int ls = LayerMask.NameToLayer("Scaled Scenery");
                    int lss = LayerMask.NameToLayer("ScaledScenery");
                    if (ls >= 0 && (c.cullingMask & (1 << ls)) != 0) score += 40;
                    if (lss >= 0 && (c.cullingMask & (1 << lss)) != 0) score += 40;
                }

                if (score > fallbackScore)
                {
                    fallbackScore = score;
                    fallback = c;
                }
            }

            return fallback;
        }

        private void SyncGalaxyCameraFromReference()
        {
            if (_galaxyCamera == null) return;
            if (_galaxyReferenceCamera == null)
                _galaxyReferenceCamera = FindCameraByKeyword("Galaxy");
            if (_galaxyReferenceCamera == null) return;

            _galaxyCamera.CopyFrom(_galaxyReferenceCamera);
            _galaxyCamera.targetTexture = OutputTexture;
            _galaxyCamera.enabled = false;

            // Keep only galaxy layers (no scaled planets/terrain).
            int galaxyMask = BuildGalaxyMask();
            _galaxyCamera.cullingMask = galaxyMask;

            // Preserve KSP sky behavior; if reference has skybox material, force Skybox clear.
            Material skyMat = GetCameraSkyboxMaterial(_galaxyReferenceCamera);
            if (skyMat != null)
            {
                _galaxyCamera.clearFlags = CameraClearFlags.Skybox;
                var ownSky = _galaxyCameraGO.GetComponent<Skybox>();
                if (ownSky == null) ownSky = _galaxyCameraGO.AddComponent<Skybox>();
                ownSky.material = skyMat;
            }
            else
            {
                _galaxyCamera.clearFlags = _galaxyReferenceCamera.clearFlags;
            }

            _galaxyCamera.depth = -12f;
            Debug.Log("[VCR][DIAG] Galaxy pass from ref: " + CamInfo(_galaxyReferenceCamera)
                + " -> pass mask=0x" + galaxyMask.ToString("X8")
                + " [" + LayerMaskToNames(galaxyMask) + "]");
        }

        private void RenderReferenceSpacePasses(Vector3 worldPos, Quaternion worldRot, float fov)
        {
            Vector3 scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);

            RenderReferenceCamera(_galaxyReferenceCamera, scaledPos, worldRot, fov, true);
            RenderReferenceCamera(_scaledReferenceCamera, scaledPos, worldRot, fov, false);
        }

        private void RenderReferenceCamera(Camera cam, Vector3 scaledPos, Quaternion scaledRot, float fov, bool firstPass)
        {
            if (cam == null) return;

            Vector3 oldPos = cam.transform.position;
            Quaternion oldRot = cam.transform.rotation;
            float oldFov = cam.fieldOfView;
            RenderTexture oldTarget = cam.targetTexture;
            CameraClearFlags oldClear = cam.clearFlags;
            Color oldBg = cam.backgroundColor;

            try
            {
                cam.transform.position = scaledPos;
                cam.transform.rotation = scaledRot;
                cam.fieldOfView = fov;
                cam.targetTexture = OutputTexture;
                cam.clearFlags = firstPass ? CameraClearFlags.SolidColor : CameraClearFlags.Depth;
                cam.backgroundColor = Color.black;
                cam.Render();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VCR] RenderReferenceCamera failed for " + cam.name + ": " + ex.Message);
            }
            finally
            {
                cam.targetTexture = oldTarget;
                cam.fieldOfView = oldFov;
                cam.transform.position = oldPos;
                cam.transform.rotation = oldRot;
                cam.clearFlags = oldClear;
                cam.backgroundColor = oldBg;
            }
        }

        private bool EnsureMainCaptureHook()
        {
            if (OutputTexture == null) return false;

            Camera main = Camera.main;
            if (main == null) return false;

            if (_mainCaptureCamera != main || _mainCaptureHook == null)
            {
                _mainCaptureCamera = main;
                _mainCaptureHook = main.GetComponent<MainCameraCaptureHook>();
                if (_mainCaptureHook == null)
                {
                    _mainCaptureHook = main.gameObject.AddComponent<MainCameraCaptureHook>();
                    _ownsMainCaptureHook = true;
                }
                else
                {
                    _ownsMainCaptureHook = false;
                }
            }

            _mainCaptureHook.TargetTexture = OutputTexture;
            return true;
        }
    }
}
