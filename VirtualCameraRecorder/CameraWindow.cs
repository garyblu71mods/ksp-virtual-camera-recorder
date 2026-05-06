using System;
using UnityEngine;

namespace VirtualCameraRecorder
{
    internal sealed class CameraWindow
    {
        // ── layout ─────────────────────────────────────────────────────
        private const int WindowId = 0x56_43_52_00;
        private const int PreviewW = 427;
        private const int PreviewH = 240;
        private const int WinW     = PreviewW + 12;

        // ── delegates ─────────────────────────────────────────────────
        public Action              OnRecordToggle;
        public Action<int,int,int> OnApplySettings;
        public Action              OnAimToVessel;   // keep mode, reframe vessel
        public Action              OnKeepDistance;
        public Action<bool>        OnHidePartHighlightsChanged;
        public Action              OnRepairExistingFiles;

        // ── state written by ModLoader ─────────────────────────────────
        public bool  IsRecording;
        public bool  IsPipeConnected;
        public float RecordSeconds;
        public float BitrateKBps;
        public bool  HidePartHighlights;
        public bool  IsRepairing;
        public string RepairStatus;

        // ── internal UI state ──────────────────────────────────────────
        private Rect    _winRect;
        public  bool    IsVisible;   // ustawiane bezposrednio przez ModLoader
        private bool    _lmbHeld;      // lewy przycisk myszy
        private bool    _rmbHeld;      // prawy przycisk myszy
        private bool    _mmbHeld;      // kolko myszy
        private Vector2 _lastMouse;
        private float   _fovValue = 40f;

        private bool    _joyHeld;
        private Vector2 _joyInput;
        private Vector2 _joyCenter;
        private const float JoyRadius = 42f;

        private bool    _panJoyHeld;
        private Vector2 _panJoyInput;
        private Vector2 _panJoyCenter;

        private float   _dollySlider;

        // blink
        private bool  _blinkState;
        private float _blinkTimer;

        // FPS counter
        private int   _fpsFrames;
        private float _fpsTimer;
        private float _measuredFps;

        private int _selRes = 2; // 1080p
        private int _selFps = 1; // 30fps

        // style cache
        private GUIStyle _smallLabel;
        private GUIStyle _recButton;
        private GUIStyle _activePreset;
        private bool     _stylesBuilt;

        private CameraController _camera;

        // ── init ───────────────────────────────────────────────────────

        public void Initialise(CameraController camera)
        {
            _camera   = camera;
            _fovValue = camera.FieldOfView;
            _winRect  = new Rect(Screen.width - WinW - 20, 20, WinW, 488);
        }

        public void SetCamera(CameraController camera)
        {
            _camera   = camera;
            _fovValue = camera.FieldOfView;
        }

        // ── OnGUI ──────────────────────────────────────────────────────

        public void OnGUI()
        {
            if (!IsVisible || _camera == null || _camera.OutputTexture == null)
            {
                Debug.Log("[VCR] OnGUI skip: visible=" + IsVisible + " camera=" + (_camera != null) + " texture=" + (_camera?.OutputTexture != null));
                return;
            }
            BuildStyles();
            UpdateBlink();
            GUI.skin = HighLogic.Skin;
            _winRect = GUILayout.Window(WindowId, _winRect, DrawWindow,
                          TitleText(), GUILayout.Width(WinW));
        }

        private string TitleText()
        {
            string dot = (IsRecording && _blinkState) ? "● " : "○ ";
            return string.Format("{0}VCR  |  {1}  {2} fps",
                dot, "1080p", 30);
        }

        // ── window body ────────────────────────────────────────────────

        private void DrawWindow(int id)
        {
            // close
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("×", GUILayout.Width(24), GUILayout.Height(20)))
                IsVisible = false;
            GUILayout.EndHorizontal();

            // preview
            Rect previewRect = GUILayoutUtility.GetRect(PreviewW, PreviewH);
            GUI.DrawTexture(previewRect, _camera.OutputTexture, ScaleMode.ScaleToFit, false);
            HandleMouseInput(previewRect);

            // info row
            CountFps();
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("Preview: {0:F1} fps", _measuredFps), _smallLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(string.Format("FOV  {0:F0}°", _fovValue), _smallLabel);
            GUILayout.EndHorizontal();

            // FOV slider 1-90
            float newFov = GUILayout.HorizontalSlider(_fovValue, 1f, 90f);
            if (Mathf.Abs(newFov - _fovValue) > 0.1f)
            {
                _fovValue = newFov;
                if (_camera.VirtualCamera != null)
                    _camera.VirtualCamera.fieldOfView = _fovValue;
            }

            GUILayout.Space(4);

            // ── tryb mocowania ────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode:", _smallLabel, GUILayout.Width(40));
            DrawModeButton(AnchorMode.VesselLocal,  "Vessel");
            DrawModeButton(AnchorMode.FreeFloat,    "Free");
            DrawModeButton(AnchorMode.SurfaceLocked,"Surface");
            DrawModeButton(AnchorMode.TargetTrack,  "Track");
            GUILayout.EndHorizontal();

            GUILayout.Space(2);

            // ── anchor row ────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            bool unloaded = !_camera.IsAnchorLoaded;
            string anchorLabel = "Anchor: " + _camera.AnchorDescription
                + (unloaded ? " (unloaded)" : "");
            GUILayout.Label(anchorLabel, _smallLabel);
            GUILayout.FlexibleSpace();
            AnchorMode m = _camera.Mode;
            if (GUILayout.Button("Aim", GUILayout.Width(52), GUILayout.Height(22)))
                OnAimToVessel?.Invoke();
            if (m == AnchorMode.TargetTrack)
            {
                GUILayout.Space(4);
                if (GUILayout.Button("Keep distance", GUILayout.Width(104), GUILayout.Height(22)))
                    OnKeepDistance?.Invoke();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            if (_camera.Mode == AnchorMode.VesselLocal)
            {
                GUILayout.BeginHorizontal();
                bool lockRot = GUILayout.Toggle(_camera.VesselLockRotation, "Vessel: lock rotation", GUILayout.Height(20));
                if (lockRot != _camera.VesselLockRotation)
                    _camera.SetVesselLockRotation(lockRot);
                GUILayout.FlexibleSpace();
                GUILayout.Label(lockRot ? "ON" : "OFF", _smallLabel, GUILayout.Width(28));
                GUILayout.EndHorizontal();
            }

            // analog controls row (single set, no duplicates)
            DrawAnalogControlsRow();

            GUILayout.Space(6);

            DrawRecordBar();

            // ── controls legend ───────────────────────────────────────
            GUILayout.Space(4);
            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("LMB drag — rotate  |  RMB drag — pan", _smallLabel);
            GUILayout.Label("MMB drag — dolly  |  MMB+RMB — roll  |  Scroll — zoom", _smallLabel);
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0, 0, WinW, 22));
        }

        private void DrawModeButton(AnchorMode mode, string label)
        {
            bool isActive  = _camera.Mode == mode;
            GUIStyle style = isActive ? _activePreset : HighLogic.Skin.button;
            if (GUILayout.Button(label, style, GUILayout.Height(22)) && !isActive)
                _camera.SetMode(mode);
        }

        // ── record bar ─────────────────────────────────────────────────

        private void DrawRecordBar()
        {
            GUILayout.BeginHorizontal();

            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = IsRecording ? new Color(0.75f, 0.1f, 0.1f) : prevBg;
            string btnTxt = IsRecording ? "\u25a0 STOP" : "\u25cf REC";
            if (GUILayout.Button(btnTxt, _recButton, GUILayout.Width(74), GUILayout.Height(28)))
            {
                Debug.Log("[VCR] REC button clicked. OnRecordToggle=" + (OnRecordToggle != null));
                OnRecordToggle?.Invoke();
            }
            GUI.backgroundColor = prevBg;

            if (IsRecording)
            {
                int sec = (int)RecordSeconds % 60;
                int min = ((int)RecordSeconds / 60) % 60;
                int hr  = (int)RecordSeconds / 3600;
                GUILayout.Label(string.Format("  {0:D2}:{1:D2}:{2:D2}", hr, min, sec), _smallLabel);
            }

            GUILayout.FlexibleSpace();

            Color prevColor = GUI.color;
            GUI.color = IsPipeConnected ? Color.green : Color.red;
            GUILayout.Label(IsPipeConnected ? "Pipe \u2713" : "Pipe \u2717", _smallLabel);
            GUI.color = prevColor;

            if (IsRecording && BitrateKBps > 1f)
            {
                GUILayout.Space(6);
                GUILayout.Label(string.Format("{0:F1} MB/s", BitrateKBps / 1024f), _smallLabel);
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            bool prevEnabled = GUI.enabled;
            GUI.enabled = !IsRecording && !IsRepairing;
            if (GUILayout.Button(IsRepairing ? "Repairing..." : "Repair existing MP4", GUILayout.Height(22)))
                OnRepairExistingFiles?.Invoke();
            GUI.enabled = prevEnabled;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(RepairStatus))
                GUILayout.Label(RepairStatus, _smallLabel);
        }

        // ── mouse input ────────────────────────────────────────────────
        // LMB (0)         — obrot (pitch inverted + yaw)
        // RMB (1)         — pan
        // MMB (2)         — dolly (przod/tyl + lewo/prawo)
        // MMB+RMB         — roll
        // Scroll          — zoom FOV

        private void HandleMouseInput(Rect previewRect)
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (!previewRect.Contains(e.mousePosition)) break;
                    if (e.button == 0) { _lmbHeld = true; _lastMouse = e.mousePosition; }
                    else if (e.button == 1) { _rmbHeld = true; _lastMouse = e.mousePosition; }
                    else if (e.button == 2) { _mmbHeld = true; _lastMouse = e.mousePosition; }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0) _lmbHeld = false;
                    else if (e.button == 1) _rmbHeld = false;
                    else if (e.button == 2) _mmbHeld = false;
                    break;

                case EventType.MouseDrag:
                    if (_lmbHeld || _rmbHeld || _mmbHeld)
                    {
                        Vector2 d  = e.mousePosition - _lastMouse;
                        _lastMouse = e.mousePosition;

                        if (_mmbHeld && _rmbHeld)
                            _camera.ApplyRollDelta(d.x);                  // MMB+RMB: roll
                        else if (_lmbHeld)
                            _camera.ApplyMouseDelta(d.x, d.y);            // LMB: obrot
                        else if (_rmbHeld)
                            _camera.ApplyMoveDelta(d.x, d.y);             // RMB: pan
                        else if (_mmbHeld)
                            _camera.ApplyDollyDelta(d.x, d.y);            // MMB: dolly
                        e.Use();
                    }
                    break;

                case EventType.ScrollWheel:
                    if (previewRect.Contains(e.mousePosition))
                    {
                        float step = e.delta.y * _fovValue * 0.04f;
                        _fovValue = Mathf.Clamp(_fovValue + step, 1f, 90f);
                        if (_camera.VirtualCamera != null)
                            _camera.VirtualCamera.fieldOfView = _fovValue;
                        e.Use();
                    }
                    break;
            }
        }

        // ── helpers ────────────────────────────────────────────────────

        private void UpdateBlink()
        {
            if (!IsRecording) { _blinkState = false; return; }
            _blinkTimer += Time.unscaledDeltaTime;
            if (_blinkTimer >= 0.5f) { _blinkTimer = 0f; _blinkState = !_blinkState; }
        }

        private void CountFps()
        {
            _fpsFrames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 1f)
            {
                _measuredFps = _fpsFrames / _fpsTimer;
                _fpsFrames   = 0;
                _fpsTimer    = 0f;
            }
        }

        private void BuildStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _smallLabel = new GUIStyle(HighLogic.Skin.label) { fontSize = 11 };
            _recButton  = new GUIStyle(HighLogic.Skin.button) { fontSize = 12 };
            _activePreset = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.4f, 0.9f, 1f) },
                hover    = { textColor = new Color(0.6f, 1f,  1f) },
            };
        }

        public void Show()  
        { 
            try 
            { 
                Debug.Log("[VCR] CameraWindow.Show() called.");
                IsVisible = true; 
                Debug.Log("[VCR] CameraWindow.IsVisible set to true.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[VCR] Show() exception: " + ex);
            }
        }
        public void Hide()  { Debug.Log("[VCR] CameraWindow.Hide() called."); IsVisible = false; }

        // Stale wartosci zamiast presetow
        public int SelectedWidth  => 1920;
        public int SelectedHeight => 1080;
        public int SelectedFps    => 30;

        private void DrawLookJoystick()
        {
            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Look Joystick (LMB drag)", _smallLabel);

            Rect joyRect = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
            _joyCenter = new Vector2(joyRect.x + joyRect.width * 0.5f, joyRect.y + joyRect.height * 0.5f);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            GUI.Box(joyRect, GUIContent.none);
            GUI.color = prev;

            Vector2 knob = _joyCenter + _joyInput * JoyRadius;
            Rect knobRect = new Rect(knob.x - 10f, knob.y - 10f, 20f, 20f);
            GUI.Box(knobRect, "●");

            HandleJoystickInput(joyRect);
            GUILayout.EndVertical();
        }

        private void HandleJoystickInput(Rect joyRect)
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && joyRect.Contains(e.mousePosition))
                    {
                        _joyHeld = true;
                        UpdateJoystick(e.mousePosition);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_joyHeld)
                    {
                        UpdateJoystick(e.mousePosition);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && _joyHeld)
                    {
                        _joyHeld = false;
                        _joyInput = Vector2.zero;
                        _camera.SetLookJoystick(Vector2.zero);
                        e.Use();
                    }
                    break;
            }
        }

        private void UpdateJoystick(Vector2 mouse)
        {
            Vector2 delta = mouse - _joyCenter;
            _joyInput = Vector2.ClampMagnitude(delta / JoyRadius, 1f);
            _camera.SetLookJoystick(_joyInput);
        }

        private void DrawPanJoystick()
        {
            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Pan Joystick (LMB drag)", _smallLabel);

            Rect joyRect = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
            _panJoyCenter = new Vector2(joyRect.x + joyRect.width * 0.5f, joyRect.y + joyRect.height * 0.5f);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            GUI.Box(joyRect, GUIContent.none);
            GUI.color = prev;

            Vector2 knob = _panJoyCenter + _panJoyInput * JoyRadius;
            Rect knobRect = new Rect(knob.x - 10f, knob.y - 10f, 20f, 20f);
            GUI.Box(knobRect, "●");

            HandlePanJoystickInput(joyRect);
            GUILayout.EndVertical();
        }

        private void HandlePanJoystickInput(Rect joyRect)
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && joyRect.Contains(e.mousePosition))
                    {
                        _panJoyHeld = true;
                        UpdatePanJoystick(e.mousePosition);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_panJoyHeld)
                    {
                        UpdatePanJoystick(e.mousePosition);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && _panJoyHeld)
                    {
                        _panJoyHeld = false;
                        _panJoyInput = Vector2.zero;
                        _camera.SetPanJoystick(Vector2.zero);
                        e.Use();
                    }
                    break;
            }
        }

        private void UpdatePanJoystick(Vector2 mouse)
        {
            Vector2 delta = mouse - _panJoyCenter;
            _panJoyInput = Vector2.ClampMagnitude(delta / JoyRadius, 1f);
            _camera.SetPanJoystick(_panJoyInput);
        }

        private void DrawDollySlider()
        {
            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Dolly (Forward / Back)", _smallLabel);

            float newDolly = GUILayout.HorizontalSlider(_dollySlider, -1f, 1f);
            if (Mathf.Abs(newDolly - _dollySlider) > 0.0001f)
            {
                _dollySlider = newDolly;
                _camera.SetDollySlider(_dollySlider);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Back", _smallLabel, GUILayout.Width(32));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Stop", _smallLabel, GUILayout.Width(30));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Forward", _smallLabel, GUILayout.Width(48));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawAnalogControlsRow()
        {
            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Analog Controls", _smallLabel);

            GUILayout.BeginHorizontal();
            DrawLookJoystickCompact();
            GUILayout.Space(8);
            DrawPanJoystickCompact();
            GUILayout.Space(8);
            DrawDollySliderVertical();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawLookJoystickCompact()
        {
            GUILayout.BeginVertical(GUILayout.Width(110));
            GUILayout.Label("Look", _smallLabel);

            Rect joyRect = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
            _joyCenter = new Vector2(joyRect.x + joyRect.width * 0.5f, joyRect.y + joyRect.height * 0.5f);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            GUI.Box(joyRect, GUIContent.none);
            GUI.color = prev;

            Vector2 knob = _joyCenter + _joyInput * JoyRadius;
            Rect knobRect = new Rect(knob.x - 10f, knob.y - 10f, 20f, 20f);
            GUI.Box(knobRect, "●");

            HandleJoystickInput(joyRect);
            GUILayout.EndVertical();
        }

        private void DrawPanJoystickCompact()
        {
            GUILayout.BeginVertical(GUILayout.Width(110));
            GUILayout.Label("Pan", _smallLabel);

            Rect joyRect = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
            _panJoyCenter = new Vector2(joyRect.x + joyRect.width * 0.5f, joyRect.y + joyRect.height * 0.5f);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            GUI.Box(joyRect, GUIContent.none);
            GUI.color = prev;

            Vector2 knob = _panJoyCenter + _panJoyInput * JoyRadius;
            Rect knobRect = new Rect(knob.x - 10f, knob.y - 10f, 20f, 20f);
            GUI.Box(knobRect, "●");

            HandlePanJoystickInput(joyRect);
            GUILayout.EndVertical();
        }

        private void DrawDollySliderVertical()
        {
            GUILayout.BeginVertical(GUILayout.Width(36));
            GUILayout.Label("Dolly", _smallLabel);

            Rect track = GUILayoutUtility.GetRect(20, 96, GUILayout.Width(20), GUILayout.Height(96));
            GUI.Box(track, GUIContent.none);

            float t = (_dollySlider + 1f) * 0.5f;
            float y = Mathf.Lerp(track.yMax - 8f, track.yMin + 8f, t);
            Rect knob = new Rect(track.x + 2f, y - 8f, track.width - 4f, 16f);
            GUI.Box(knob, GUIContent.none);

            Event e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) &&
                e.button == 0 && track.Contains(e.mousePosition))
            {
                float ny = Mathf.InverseLerp(track.yMax, track.yMin, e.mousePosition.y);
                _dollySlider = Mathf.Clamp(ny * 2f - 1f, -1f, 1f);
                _camera.SetDollySlider(_dollySlider);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                _dollySlider = 0f;
                _camera.SetDollySlider(0f);
            }

            GUILayout.EndVertical();
        }
    }
}
