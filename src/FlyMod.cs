// FlyMod 0.2 Beta para Drive Up Together (BepInEx 6 IL2CPP).
// Menu movible (tecla 8): fly, voltereta, truco aereo, boost, potencia, velocidad maxima, traccion y teclas.
// Con el menu abierto la camara no se mueve con el mouse.
// En cuanto se usa cualquier truco se bloquean hasta cerrar el juego: subida al leaderboard,
// logros/estadisticas de Steam, recompensas de altura y monedas.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using NWH.Common.Cameras;
using NWH.WheelController3D;

namespace DutFly
{
    [BepInPlugin("local.dut.flymod", "FlyMod", "0.2.1")]
    public class Plugin : BasePlugin
    {
        public const string VersionName = "0.2.1 Beta";
        internal static ManualLogSource L;
        internal static ConfigFile Cfg;
        public static bool UsedThisSession;
        public static bool Unlimited; // desactiva los limitadores de velocidad del juego
        public static bool MenuOpen;  // congela la camara

        public override void Load()
        {
            L = Log;
            Cfg = Config;
            Cfg.SaveOnConfigSet = false;
            Settings.Bind(Cfg);
            Cfg.Save(); // crea el .cfg con los valores por defecto

            ClassInjector.RegisterTypeInIl2Cpp<FlyBehaviour>();
            AddComponent<FlyBehaviour>();

            var h = new Harmony("local.dut.flymod");
            var block = Prefix(nameof(BlockIfUsed));
            var unlimited = Prefix(nameof(BlockIfUnlimited));

            // Puntuacion / leaderboard / recompensas del juego
            PatchAll(h, typeof(Caporta.DriveUpTogether.Gameplay.ScoreController),
                block, "Upload", "UploadFinal", "ScheduleFinalRetry", "OnAltitude", "OnAltitudeRewardAchieved", "OnGameFinished");
            PatchAll(h, typeof(Caporta.DriveUpTogether.Achievements.AchievementService),
                block, "Unlock", "AddStat", "SetStat", "TrySet", "OnGameFinished");
            PatchAll(h, typeof(Caporta.DriveUpTogether.Gameplay.CurrencyRewardService), block, "OnRewardRequested");
            PatchAll(h, typeof(Caporta.DriveUpTogether.Player), block, "OnAltitudeQuestCompleted");
            PatchAll(h, typeof(Caporta.SaveSystem.AltitudeRewardSaveListener), block, null);
            // Red de seguridad a nivel de Steam
            PatchAll(h, typeof(Steamworks.SteamUserStats),
                block, "UploadLeaderboardScore", "SetAchievement", "SetStat", "StoreStats", "IndicateAchievementProgress", "UpdateAvgRateStat");

            // Limitadores de velocidad del juego (se apagan mientras haya un truco de velocidad activo)
            PatchAll(h, typeof(Caporta.DriveUpTogether.VehicleLaunchLimiter), unlimited, "FixedUpdate");
            PatchAll(h, typeof(Caporta.DriveUpTogether.Gameplay.Surfaces.VehicleLaunchGuard), unlimited, "FixedUpdate");
            PatchAll(h, typeof(Caporta.DriveUpTogether.VehicleSpeedGovernor), unlimited, "Limit");

            // Camara: con el menu abierto, el juego cree que el mouse esta sobre la interfaz
            try
            {
                var getter = AccessTools.PropertyGetter(typeof(CameraMouseDrag), "PointerOverUI");
                if (getter != null)
                {
                    h.Patch(getter, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(PointerOverUIPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("Camara: PointerOverUI parcheado");
                }
            }
            catch (Exception e) { L.LogWarning("No se pudo parchear la camara: " + e.Message); }

            L.LogInfo($"FlyMod {VersionName} cargado. {Settings.P(Settings.KeyMenu)} = menu.");
        }

        static HarmonyMethod Prefix(string name) =>
            new HarmonyMethod(typeof(Plugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        static bool BlockIfUsed() => !UsedThisSession;
        static bool BlockIfUsedBool(ref bool __result)
        {
            if (!UsedThisSession) return true;
            __result = false;
            return false;
        }
        static bool BlockIfUnlimited() => !Unlimited;
        static void PointerOverUIPostfix(ref bool __result) { if (MenuOpen) __result = true; }
        static HarmonyMethod _blockBool;

        public static void MarkUsed(string what)
        {
            if (UsedThisSession) return;
            UsedThisSession = true;
            L.LogWarning($"Truco usado ({what}): leaderboard, logros y recompensas bloqueados hasta cerrar el juego.");
        }

        // names == null -> metodos On*/Reward/Save (listeners de recompensas)
        static void PatchAll(Harmony h, Type t, HarmonyMethod prefix, params string[] names)
        {
            int n = 0;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName || m.IsGenericMethodDefinition) continue;
                bool match = names == null
                    ? (m.Name.StartsWith("On") || m.Name.Contains("Reward") || m.Name.Contains("Save"))
                    : Array.IndexOf(names, m.Name) >= 0;
                if (!match) continue;
                var p = prefix;
                if (prefix.method.Name == nameof(BlockIfUsed) && m.ReturnType == typeof(bool))
                    p = _blockBool ??= Prefix(nameof(BlockIfUsedBool));
                try { h.Patch(m, prefix: p); n++; }
                catch (Exception e) { L.LogWarning($"No se pudo parchear {t.FullName}.{m.Name}: {e.Message}"); }
            }
            L.LogInfo($"{t.FullName}: {n} metodos parcheados");
        }
    }

    // ---------------------------------------------------------------- configuracion
    static class Settings
    {
        public static ConfigEntry<float> FlySpeed, FlyFastSpeed;
        public static ConfigEntry<float> FlipSpin, FlipHop, FlipTurns;
        public static ConfigEntry<bool> FlipBackwards, FlipGroundOnly;
        public static ConfigEntry<float> TrickHeight, TrickDistance, TrickSpin, TrickTurns;
        public const float TrickRiseSpeed = 15f; // m/s de subida del truco
        public static ConfigEntry<float> BoostAccel;
        public static ConfigEntry<float> PowerMult, SpeedMult, GripMult;
        public static ConfigEntry<bool> NoLimiters;
        public static ConfigEntry<float> WinX, WinY;

        public static ConfigEntry<string> KeyMenu, KeyFly, KeyUp, KeyDown, KeyFast, KeyFlip, KeyTrick, KeyBoost;
        public static readonly List<(string label, ConfigEntry<string> entry)> Binds = new();

        public static void Bind(ConfigFile c)
        {
            FlySpeed = c.Bind("Fly", "Velocidad", 25f, "Velocidad de vuelo normal (m/s)");
            FlyFastSpeed = c.Bind("Fly", "VelocidadRapida", 70f, "Velocidad de vuelo con la tecla rapida (m/s)");
            FlipSpin = c.Bind("Flip", "VelocidadGiro", 8f, "Velocidad de giro de la voltereta (rad/s)");
            FlipHop = c.Bind("Flip", "Salto", 8f, "Impulso hacia arriba al empezar la voltereta (m/s)");
            FlipTurns = c.Bind("Flip", "Vueltas", 1f, "Numero de vueltas completas");
            FlipBackwards = c.Bind("Flip", "HaciaAtras", false, "Back flip en vez de front flip");
            FlipGroundOnly = c.Bind("Flip", "SoloDesdeSueloV2", false, "Solo permitir la voltereta con el coche en el suelo");
            TrickHeight = c.Bind("TrucoAereo", "Altura", 10f, "Metros que sube el coche");
            TrickDistance = c.Bind("TrucoAereo", "Distancia", 1f, "Metros que avanza mientras da las volteretas");
            TrickSpin = c.Bind("TrucoAereo", "VelocidadVolteretas", 1.5f, "Volteretas por segundo");
            TrickTurns = c.Bind("TrucoAereo", "Volteretas", 3f, "Cuantas volteretas da");
            BoostAccel = c.Bind("Coche", "Boost", 60f, "Aceleracion extra mientras mantienes la tecla de boost (m/s2)");
            PowerMult = c.Bind("Coche", "Potencia", 1f, "Multiplicador de potencia del motor");
            SpeedMult = c.Bind("Coche", "VelocidadMax", 1f, "Multiplicador de velocidad maxima (relacion final)");
            GripMult = c.Bind("Coche", "Traccion", 1f, "Multiplicador de agarre de las ruedas");
            NoLimiters = c.Bind("Coche", "SinLimitadores", true, "Apagar los limitadores de velocidad del juego mientras haya trucos activos");
            WinX = c.Bind("Menu", "X", 40f, ""); WinY = c.Bind("Menu", "Y", 80f, "");

            KeyMenu = Key(c, "Menu", "Digit8", "Abrir menu");
            KeyFly = Key(c, "Fly", "F8", "Fly on/off");
            KeyUp = Key(c, "FlySubir", "Space", "Fly: subir");
            KeyDown = Key(c, "FlyBajar", "LeftCtrl", "Fly: bajar");
            KeyFast = Key(c, "FlyRapido", "LeftShift", "Fly: rapido");
            KeyFlip = Key(c, "Flip", "G", "Voltereta");
            KeyTrick = Key(c, "TrucoAereo", "H", "Truco aereo");
            KeyBoost = Key(c, "Boost", "B", "Boost (mantener)");
        }

        // nombre legible de la tecla (Digit8 -> 8)
        public static string P(ConfigEntry<string> e) =>
            e.Value.StartsWith("Digit") ? e.Value.Substring(5) : e.Value.StartsWith("Numpad") ? "Num " + e.Value.Substring(6) : e.Value;

        static ConfigEntry<string> Key(ConfigFile c, string name, string def, string label)
        {
            var e = c.Bind("Teclas", name, def, label);
            Binds.Add((label, e));
            return e;
        }
    }

    // ---------------------------------------------------------------- comportamiento
    public class FlyBehaviour : MonoBehaviour
    {
        public FlyBehaviour(IntPtr ptr) : base(ptr) { }

        // estado
        bool _flying, _menuOpen;
        Rigidbody _rb;
        bool _prevGravity = true;
        float _flipLeft;
        Vector3 _flipAxisLocal;
        Quaternion _flipLevel;
        CursorLockMode _prevLock; bool _prevVisible;

        // truco aereo: 0 nada, 1 subiendo, 2 girando
        int _trick;
        Vector3 _trickTarget;
        Quaternion _trickLevel;
        float _trickAngle;
        bool _trickGravity;

        // camara congelada
        readonly List<(CameraMouseDrag cam, bool rot, float zoom)> _frozenCams = new();

        // menu
        Rect _win;
        GUI.WindowFunction _winFn;
        int _tab;
        ConfigEntry<string> _listening;
        readonly Dictionary<string, string> _txt = new();
        static Key[] _allKeys;

        // tuning
        IntPtr _tunedVc;
        float _basePower, _lastPower, _baseGear, _lastGear;
        float[] _baseLong, _baseLat, _lastLong, _lastLat;
        float _tuneTimer;
        bool _boostLogged;

        void Awake()
        {
            _win = new Rect(Settings.WinX.Value, Settings.WinY.Value, Ui.W, 480);
            _winFn = DelegateSupport.ConvertDelegate<GUI.WindowFunction>(new Action<int>(DrawWindow));
            var list = new List<Key>();
            foreach (Key k in Enum.GetValues(typeof(Key)))
                if (k != Key.None && k != Key.IMESelected) list.Add(k);
            _allKeys = list.ToArray();
        }

        // ---------- input helpers
        static Key Parse(ConfigEntry<string> e) => Enum.TryParse(e.Value, out Key k) ? k : Key.None;
        static bool Down(ConfigEntry<string> e)
        {
            var kb = Keyboard.current; var k = Parse(e);
            if (kb == null || k == Key.None) return false;
            try { return kb[k].wasPressedThisFrame; } catch { return false; }
        }
        static bool Held(ConfigEntry<string> e)
        {
            var kb = Keyboard.current; var k = Parse(e);
            if (kb == null || k == Key.None) return false;
            try { return kb[k].isPressed; } catch { return false; }
        }

        Rigidbody FindLocalBody()
        {
            var veh = LocalVehicle();
            if (veh == null) return null;
            var rb = veh.GetComponent<Rigidbody>();
            if (rb == null) rb = veh.GetComponentInChildren<Rigidbody>();
            if (rb == null) rb = veh.GetComponentInParent<Rigidbody>();
            return rb;
        }

        static Caporta.DriveUpTogether.Vehicle LocalVehicle()
        {
            var player = Caporta.DriveUpTogether.Player.Instance;
            return player == null ? null : player.CurrentVehicle;
        }

        // ---------- loop
        void Update()
        {
            if (_listening != null) { CaptureKey(); return; }

            if (Down(Settings.KeyMenu)) SetMenu(!_menuOpen);
            if (_menuOpen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

            if (Down(Settings.KeyFly)) ToggleFly();
            if (Down(Settings.KeyFlip)) StartFlip();
            if (Down(Settings.KeyTrick)) StartTrick();

            _tuneTimer -= Time.unscaledDeltaTime;
            if (_tuneTimer <= 0) { _tuneTimer = 0.25f; ApplyTuning(); }

            bool speedCheats = Settings.PowerMult.Value > 1.001f || Settings.SpeedMult.Value > 1.001f
                               || Held(Settings.KeyBoost) || _flipLeft > 0;
            Plugin.Unlimited = _flying || _trick != 0 || (Settings.NoLimiters.Value && speedCheats);
        }

        void SetMenu(bool open)
        {
            if (open == _menuOpen) return;
            _menuOpen = open;
            Plugin.MenuOpen = open;
            if (open)
            {
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                FreezeCameras();
            }
            else
            {
                UnfreezeCameras();
                _listening = null;
                Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
                Settings.WinX.Value = _win.x; Settings.WinY.Value = _win.y;
                Plugin.Cfg.Save();
            }
        }

        // La camara del coche (NWH CameraMouseDrag) deja de girar y de hacer zoom con el mouse
        void FreezeCameras()
        {
            _frozenCams.Clear();
            try
            {
                var cams = UnityEngine.Object.FindObjectsByType<CameraMouseDrag>(FindObjectsSortMode.None);
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    _frozenCams.Add((c, c.allowRotation, c.zoomSensitivity));
                    c.allowRotation = false;
                    c.zoomSensitivity = 0f;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("No se pudo congelar la camara: " + e.Message); }
        }

        void UnfreezeCameras()
        {
            foreach (var (cam, rot, zoom) in _frozenCams)
            {
                try { if (cam != null) { cam.allowRotation = rot; cam.zoomSensitivity = zoom; } } catch { }
            }
            _frozenCams.Clear();
        }

        void ToggleFly()
        {
            if (!_flying)
            {
                if (_trick != 0) EndTrick();
                _rb = FindLocalBody();
                if (_rb == null) { Plugin.L.LogWarning("No hay coche local todavia."); return; }
                _prevGravity = _rb.useGravity;
                _flying = true;
                Plugin.MarkUsed("fly");
            }
            else
            {
                _flying = false;
                if (_rb != null)
                {
                    _rb.useGravity = _prevGravity;
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                }
            }
        }

        void StartFlip()
        {
            if (_flying || _trick != 0 || _flipLeft > 0) return;   // no encadenar volteretas en el aire
            _rb = FindLocalBody();
            if (_rb == null) return;
            if (Settings.FlipGroundOnly.Value && !IsGrounded(_rb)) return;
            float spin = Mathf.Max(0.5f, Settings.FlipSpin.Value);
            _flipLeft = Mathf.Max(0.1f, Settings.FlipTurns.Value) * 2f * Mathf.PI / spin;
            _flipAxisLocal = Settings.FlipBackwards.Value ? Vector3.left : Vector3.right;
            _flipLevel = LevelRotation(_rb);
            // fija la velocidad vertical (no la suma) para que no se acumule altura
            var v = _rb.linearVelocity;
            v.y = Mathf.Max(v.y, Settings.FlipHop.Value);
            _rb.linearVelocity = v;
            Plugin.MarkUsed("flip");
        }

        // rotacion "horizontal perfecta": solo conserva hacia donde mira el coche
        static Quaternion LevelRotation(Rigidbody rb)
        {
            Vector3 fwd = rb.transform.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) { fwd = rb.transform.up; fwd.y = 0; }
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            return Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }

        // ---------- truco aereo: sube X metros, gira N vueltas, se pone horizontal y cae
        void StartTrick()
        {
            if (_flying || _trick != 0) return;
            _rb = FindLocalBody();
            if (_rb == null) return;
            _flipLeft = 0;
            _trickTarget = _rb.position + Vector3.up * Mathf.Max(0f, Settings.TrickHeight.Value);
            _trickLevel = LevelRotation(_rb);
            _trickAngle = 0;
            _trickGravity = _rb.useGravity;
            _rb.useGravity = false;
            _trick = 1;
            Plugin.MarkUsed("truco aereo");
        }

        void TrickStep()
        {
            if (_rb == null) { _trick = 0; return; }
            float dt = Time.fixedDeltaTime;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;

            if (_trick == 1)
            {
                // subir y enderezarse a la vez
                _rb.MovePosition(Vector3.MoveTowards(_rb.position, _trickTarget, Settings.TrickRiseSpeed * dt));
                _rb.MoveRotation(Quaternion.RotateTowards(_rb.rotation, _trickLevel, 540f * dt));
                if ((_rb.position - _trickTarget).sqrMagnitude < 0.0025f) { _rb.MoveRotation(_trickLevel); _trick = 2; }
                return;
            }

            // volteretas hacia adelante mientras avanza "Distancia" metros
            float total = 360f * Mathf.Max(0.25f, Settings.TrickTurns.Value);
            _trickAngle = Mathf.Min(total, _trickAngle + 360f * Mathf.Max(0.1f, Settings.TrickSpin.Value) * dt);
            float progress = _trickAngle / total;
            Vector3 fwd = _trickLevel * Vector3.forward;
            _rb.MovePosition(_trickTarget + fwd * (Settings.TrickDistance.Value * progress));
            _rb.MoveRotation(_trickLevel * Quaternion.AngleAxis(_trickAngle, Vector3.right));
            if (_trickAngle >= total) EndTrick();
        }

        void EndTrick()
        {
            if (_rb != null)
            {
                _rb.MoveRotation(_trickLevel);      // horizontal perfecto
                _rb.rotation = _trickLevel;
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.useGravity = _trickGravity;     // y lo deja caer
            }
            _trick = 0;
        }

        // suelo a menos de ~2.5 m bajo el coche (ignora los colliders del propio coche)
        static bool IsGrounded(Rigidbody rb)
        {
            var hits = Physics.RaycastAll(rb.worldCenterOfMass, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null) return false;
            foreach (var h in hits)
            {
                var col = h.collider;
                if (col == null) continue;
                if (col.attachedRigidbody != null && col.attachedRigidbody.Pointer == rb.Pointer) continue;
                return true;
            }
            return false;
        }

        void FixedUpdate()
        {
            if (_flying) FlyStep();
            else if (_trick != 0) TrickStep();
            else if (_flipLeft > 0) FlipStep();

            if (!_flying && _trick == 0 && Held(Settings.KeyBoost))
            {
                if (_rb == null) _rb = FindLocalBody();
                if (_rb != null)
                {
                    // empuje directo sobre la velocidad (no depende de fuerzas que el juego pueda anular)
                    _rb.linearVelocity += _rb.transform.forward * (Settings.BoostAccel.Value * Time.fixedDeltaTime);
                    if (!_boostLogged) { _boostLogged = true; Plugin.L.LogInfo("Boost activo."); }
                    Plugin.MarkUsed("boost");
                }
            }
        }

        void FlipStep()
        {
            if (_rb == null) { _flipLeft = 0; return; }
            _rb.angularVelocity = _rb.transform.TransformDirection(_flipAxisLocal) * Settings.FlipSpin.Value;
            _flipLeft -= Time.fixedDeltaTime;
            if (_flipLeft <= 0)
            {
                // termina en horizontal perfecto, mirando hacia donde miraba al empezar
                _rb.angularVelocity = Vector3.zero;
                _rb.rotation = _flipLevel;
                _rb.MoveRotation(_flipLevel);
            }
        }

        void FlyStep()
        {
            if (_rb == null) { _rb = FindLocalBody(); if (_rb == null) return; }
            var cam = Camera.main;
            Vector3 fwd = cam != null ? cam.transform.forward : _rb.transform.forward;
            Vector3 right = cam != null ? cam.transform.right : _rb.transform.right;
            fwd.y = 0; right.y = 0; fwd.Normalize(); right.Normalize();

            Vector3 dir = Vector3.zero;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed) dir += fwd;
                if (kb.sKey.isPressed) dir -= fwd;
                if (kb.dKey.isPressed) dir += right;
                if (kb.aKey.isPressed) dir -= right;
            }
            if (Held(Settings.KeyUp)) dir += Vector3.up;
            if (Held(Settings.KeyDown)) dir -= Vector3.up;
            float sp = Held(Settings.KeyFast) ? Settings.FlyFastSpeed.Value : Settings.FlySpeed.Value;

            _rb.useGravity = false;
            _rb.linearVelocity = dir.sqrMagnitude > 0.001f ? dir.normalized * sp : Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            if (fwd.sqrMagnitude > 0.001f)
                _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, Quaternion.LookRotation(fwd, Vector3.up), 0.2f));
        }

        // ---------- potencia / velocidad / traccion
        // Se guarda el valor original del juego y se aplica original * multiplicador.
        // Si el juego cambia el valor (mejoras del garaje), se toma como nuevo original.
        void ApplyTuning()
        {
            var veh = LocalVehicle();
            if (veh == null) return;
            var vc = veh.vehicleController;
            if (vc == null || vc.powertrain == null) return;
            var pt = vc.powertrain;

            if (vc.Pointer != _tunedVc)
            {
                _tunedVc = vc.Pointer;
                _basePower = _lastPower = float.NaN;
                _baseGear = _lastGear = float.NaN;
                _baseLong = _baseLat = _lastLong = _lastLat = null;
                _rb = null;
            }

            bool any = false;
            var eng = pt.engine;
            if (eng != null)
                any |= Apply(eng.maxPower, ref _basePower, ref _lastPower, Settings.PowerMult.Value, v => eng.maxPower = v);

            var tr = pt.transmission;
            if (tr != null)
            {
                float m = Mathf.Max(0.05f, Settings.SpeedMult.Value);
                any |= Apply(tr.finalGearRatio, ref _baseGear, ref _lastGear, 1f / m, v => tr.finalGearRatio = v);
            }

            var wheels = pt.wheels;
            if (wheels != null)
            {
                int n = wheels.Count;
                if (_baseLong == null || _baseLong.Length != n)
                {
                    _baseLong = new float[n]; _baseLat = new float[n]; _lastLong = new float[n]; _lastLat = new float[n];
                    for (int i = 0; i < n; i++) _baseLong[i] = _baseLat[i] = _lastLong[i] = _lastLat[i] = float.NaN;
                }
                for (int i = 0; i < n; i++)
                {
                    var w = wheels[i];
                    var wc = w?.wheelUAPI?.TryCast<WheelController>();
                    if (wc == null) continue;
                    any |= Apply(wc.LongitudinalFrictionGrip, ref _baseLong[i], ref _lastLong[i], Settings.GripMult.Value, v => wc.LongitudinalFrictionGrip = v);
                    any |= Apply(wc.LateralFrictionGrip, ref _baseLat[i], ref _lastLat[i], Settings.GripMult.Value, v => wc.LateralFrictionGrip = v);
                }
            }
            if (any) Plugin.MarkUsed("tuning");
        }

        // devuelve true si el multiplicador esta modificado (truco activo)
        static bool Apply(float current, ref float baseV, ref float last, float mult, Action<float> set)
        {
            if (float.IsNaN(baseV) || Mathf.Abs(current - last) > 0.0001f) baseV = current; // el juego lo cambio
            float target = baseV * mult;
            if (Mathf.Abs(current - target) > 0.0001f) set(target);
            last = target;
            return Mathf.Abs(mult - 1f) > 0.001f;
        }

        // ---------- rebind
        void CaptureKey()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.escapeKey.wasPressedThisFrame) { _listening = null; return; }
            foreach (var k in _allKeys)
            {
                bool pressed;
                try { pressed = kb[k].wasPressedThisFrame; } catch { continue; }
                if (!pressed) continue;
                _listening.Value = k.ToString();
                _listening = null;
                Plugin.Cfg.Save();
                return;
            }
        }

        // ================================================================ GUI
        void OnGUI()
        {
            Ui.Init();

            // aviso arriba a la izquierda
            bool boosting = Held(Settings.KeyBoost) && !_flying;
            string chip = _flying ? "FLY" : _trick != 0 ? "TRUCO AEREO" : boosting ? "BOOST" : null;
            float hx = 12;
            if (chip != null) { Ui.Chip(new Rect(hx, 12, 0, 24), chip, Ui.Accent, Ui.Dark); hx += Ui.ChipWidth(chip) + 6; }
            if (Plugin.UsedThisSession) Ui.Chip(new Rect(hx, 12, 0, 24), "Leaderboard bloqueado hasta reiniciar", Ui.Red, Color.white);

            if (_menuOpen)
            {
                _win = GUI.Window(73195, _win, _winFn, "", Ui.Window);
                // el mouse sobre el menu no debe llegar al juego
                if (_win.Contains(Event.current.mousePosition) && Event.current.type == EventType.ScrollWheel) Event.current.Use();
            }
        }

        float _y;
        const float X = 16;
        static float CW => Ui.W - 32; // ancho del contenido

        void DrawWindow(int id)
        {
            // cabecera
            GUI.DrawTexture(new Rect(0, 0, Ui.W, 46), Ui.TexHeader);
            GUI.DrawTexture(new Rect(0, 46, Ui.W, 2), Ui.TexAccent);
            GUI.Label(new Rect(X, 8, 200, 30), "FLYMOD", Ui.Title);
            GUI.Label(new Rect(X + 98, 16, 120, 20), Plugin.VersionName, Ui.Sub);
            if (GUI.Button(new Rect(Ui.W - 40, 10, 26, 26), "X", Ui.CloseBtn)) SetMenu(false);
            string state = Plugin.UsedThisSession ? "TRUCOS" : "LEGAL";
            Ui.Chip(new Rect(Ui.W - 48 - Ui.ChipWidth(state), 13, 0, 20), state, Plugin.UsedThisSession ? Ui.Red : Ui.Green, Color.white);

            // pestanas
            _y = 58;
            string[] tabs = { "Fly", "Flip", "Truco", "Coche", "Teclas" };
            float tw = CW / tabs.Length;
            for (int i = 0; i < tabs.Length; i++)
                if (GUI.Button(new Rect(X + i * tw, _y, tw - 4, 28), tabs[i], _tab == i ? Ui.TabOn : Ui.Tab)) _tab = i;
            _y += 40;

            switch (_tab)
            {
                case 0:
                    Section("VUELO");
                    if (Switch("Fly activo", _flying, Settings.P(Settings.KeyFly)) != _flying) ToggleFly();
                    Slider("Velocidad", Settings.FlySpeed, 1, 200, "m/s");
                    Slider("Velocidad rapida", Settings.FlyFastSpeed, 1, 500, "m/s");
                    Hint("WASD mover, " + Settings.P(Settings.KeyUp) + " subir, " + Settings.P(Settings.KeyDown) + " bajar, " + Settings.P(Settings.KeyFast) + " rapido");
                    break;
                case 1:
                    Section("VOLTERETA  [" + Settings.P(Settings.KeyFlip) + "]");
                    Settings.FlipBackwards.Value = Switch("Hacia atras (back flip)", Settings.FlipBackwards.Value, null);
                    Settings.FlipGroundOnly.Value = Switch("Solo desde el suelo", Settings.FlipGroundOnly.Value, null);
                    Slider("Velocidad de giro", Settings.FlipSpin, 1, 40, "rad/s");
                    Slider("Salto", Settings.FlipHop, 0, 50, "m/s");
                    Slider("Vueltas", Settings.FlipTurns, 0.5f, 10, "");
                    if (Button("Hacer voltereta")) StartFlip();
                    break;
                case 2:
                    Section("TRUCO AEREO  [" + Settings.P(Settings.KeyTrick) + "]");
                    Hint("Sube el coche, da vueltas, se pone horizontal y lo deja caer.");
                    Slider("Altura", Settings.TrickHeight, 1, 100, "m");
                    Slider("Distancia", Settings.TrickDistance, 0, 50, "m");
                    Slider("Volteretas", Settings.TrickTurns, 1, 20, "");
                    Slider("Velocidad de volteretas", Settings.TrickSpin, 0.2f, 10, "v/s");
                    Estimate();
                    if (Button(_trick != 0 ? "Truco en curso..." : "Hacer truco")) StartTrick();
                    break;
                case 3:
                    Section("MOTOR Y RUEDAS");
                    Slider("Potencia", Settings.PowerMult, 0.5f, 50, "x");
                    Slider("Velocidad maxima", Settings.SpeedMult, 0.5f, 10, "x");
                    Slider("Traccion / agarre", Settings.GripMult, 0.2f, 20, "x");
                    Section("BOOST  [" + Settings.P(Settings.KeyBoost) + "]");
                    Slider("Fuerza del boost", Settings.BoostAccel, 0, 300, "m/s2");
                    Settings.NoLimiters.Value = Switch("Quitar limitadores del juego", Settings.NoLimiters.Value, null);
                    Hint("Escribe en la caja para pasar del maximo del slider.");
                    if (Button("Restaurar coche original"))
                    {
                        Settings.PowerMult.Value = Settings.SpeedMult.Value = Settings.GripMult.Value = 1f;
                        _txt.Clear();
                    }
                    break;
                default:
                    Section("TECLAS");
                    Hint("Pulsa un boton y luego la tecla nueva (Esc cancela).");
                    foreach (var (label, entry) in Settings.Binds)
                    {
                        GUI.Label(new Rect(X, _y + 3, 170, 22), label, Ui.Label);
                        bool listening = _listening == entry;
                        string txt = listening ? "pulsa una tecla..." : Settings.P(entry);
                        if (GUI.Button(new Rect(X + CW - 150, _y, 150, 26), txt, listening ? Ui.TabOn : Ui.Key)) _listening = entry;
                        _y += 31;
                    }
                    break;
            }

            _win.height = _y + 14;
            GUI.DragWindow(new Rect(0, 0, Ui.W - 50, 46));
        }

        // ---------- controles
        void Section(string t)
        {
            _y += 4;
            GUI.Label(new Rect(X, _y, CW, 18), t, Ui.Section);
            _y += 22;
        }

        void Hint(string t)
        {
            GUI.Label(new Rect(X, _y, CW, 34), t, Ui.Hint);
            _y += Ui.Hint.CalcHeight(new GUIContent(t), CW) + 6;
        }

        bool Button(string t)
        {
            _y += 4;
            bool r = GUI.Button(new Rect(X, _y, CW, 32), t, Ui.Primary);
            _y += 40;
            return r;
        }

        bool Switch(string label, bool value, string key)
        {
            var row = new Rect(X, _y, CW, 26);
            GUI.Label(new Rect(X, _y + 3, CW - 60, 22), label + (key != null ? "  [" + key + "]" : ""), Ui.Label);
            var sw = new Rect(X + CW - 44, _y + 4, 44, 20);
            GUI.DrawTexture(sw, value ? Ui.TexAccent : Ui.TexTrack);
            GUI.DrawTexture(new Rect(value ? sw.x + 26 : sw.x + 2, sw.y + 2, 16, 16), Ui.TexKnob);
            if (GUI.Button(row, "", GUIStyle.none)) value = !value;
            _y += 30;
            return value;
        }

        void Choice(string label, ConfigEntry<int> e, string[] names)
        {
            GUI.Label(new Rect(X, _y, CW, 20), label, Ui.Label);
            _y += 22;
            float w = CW / names.Length;
            for (int i = 0; i < names.Length; i++)
                if (GUI.Button(new Rect(X + i * w, _y, w - 4, 26), names[i], e.Value == i ? Ui.TabOn : Ui.Tab)) e.Value = i;
            _y += 34;
        }

        void Slider(string label, ConfigEntry<float> e, float min, float max, string unit)
        {
            GUI.Label(new Rect(X, _y, CW - 90, 20), label, Ui.Label);
            string key = e.Definition.Section + "." + e.Definition.Key;
            if (!_txt.TryGetValue(key, out var buf)) buf = Fmt(e.Value);

            string nb = GUI.TextField(new Rect(X + CW - 84, _y, 60, 22), buf, Ui.Field);
            GUI.Label(new Rect(X + CW - 22, _y + 2, 24, 20), unit, Ui.Hint);
            if (nb != buf)
            {
                buf = nb;
                if (float.TryParse(nb.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float typed) && typed >= 0)
                    e.Value = typed; // sin limite superior
            }
            _y += 24;

            float shown = Mathf.Clamp(e.Value, min, max);
            float v = DrawSlider(new Rect(X, _y + 2, CW, 18), key, shown, min, max);
            if (Mathf.Abs(v - shown) > 0.0001f) { e.Value = (float)Math.Round(v, 2); buf = Fmt(e.Value); }
            _txt[key] = buf;
            _y += 24;
        }

        // Slider dibujado a mano: GUI.HorizontalSlider esta recortado en este juego (Method unstripping failed)
        string _dragKey;
        float DrawSlider(Rect r, string key, float value, float min, float max)
        {
            var ev = Event.current;
            float t = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0f;
            var track = new Rect(r.x, r.y + r.height / 2f - 3f, r.width, 6f);
            GUI.DrawTexture(track, Ui.TexTrack);
            GUI.DrawTexture(new Rect(track.x, track.y, track.width * t, track.height), Ui.TexAccent);
            bool dragging = _dragKey == key;
            GUI.DrawTexture(new Rect(r.x + r.width * t - 7f, r.y, 14f, r.height), dragging ? Ui.TexKnob : Ui.TexAccent);

            if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition)) { _dragKey = key; dragging = true; ev.Use(); }
            else if (ev.type == EventType.MouseUp && dragging) { _dragKey = null; ev.Use(); return value; }
            if (dragging && (ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag))
            {
                float nt = Mathf.Clamp01((ev.mousePosition.x - r.x) / r.width);
                value = min + nt * (max - min);
                ev.Use();
            }
            return value;
        }

        // tiempo aproximado del truco aereo: subida + volteretas + caida libre
        void Estimate()
        {
            float h = Mathf.Max(0f, Settings.TrickHeight.Value);
            float up = h / Settings.TrickRiseSpeed;
            float spin = Mathf.Max(0.25f, Settings.TrickTurns.Value) / Mathf.Max(0.1f, Settings.TrickSpin.Value);
            float fall = Mathf.Sqrt(2f * h / 9.81f);
            float total = up + spin + fall;
            var box = new Rect(X, _y + 2, CW, 40);
            GUI.DrawTexture(box, Ui.TexField);
            GUI.Label(new Rect(X + 10, _y + 4, CW - 20, 20), "Tiempo estimado:  " + total.ToString("0.0", CultureInfo.InvariantCulture) + " s", Ui.Label);
            GUI.Label(new Rect(X + 10, _y + 22, CW - 20, 18),
                "subida " + up.ToString("0.0", CultureInfo.InvariantCulture) + " s  +  volteretas " + spin.ToString("0.0", CultureInfo.InvariantCulture) +
                " s  +  caida " + fall.ToString("0.0", CultureInfo.InvariantCulture) + " s", Ui.Hint);
            _y += 50;
        }

        static string Fmt(float f) => f.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- estilos
    static class Ui
    {
        public const float W = 400;
        public static readonly Color Accent = new Color(1f, 0.72f, 0.1f);
        public static readonly Color Dark = new Color(0.08f, 0.09f, 0.11f);
        public static readonly Color Red = new Color(0.85f, 0.25f, 0.25f);
        public static readonly Color Green = new Color(0.2f, 0.7f, 0.4f);

        public static Texture2D TexBg, TexHeader, TexAccent, TexTrack, TexKnob, TexBtn, TexBtnHover, TexField, TexChip;
        public static GUIStyle Window, Title, Sub, Section, Label, Hint, Tab, TabOn, Primary, Key, Field, CloseBtn, ChipStyle;
        static bool _ready;

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static GUIStyle Box(GUIStyle baseStyle, Texture2D normal, Texture2D hover, Color text, int size = 13, FontStyle fs = FontStyle.Normal)
        {
            var s = new GUIStyle(baseStyle);
            s.normal.background = normal; s.normal.textColor = text;
            s.hover.background = hover ?? normal; s.hover.textColor = text;
            s.active.background = hover ?? normal; s.active.textColor = text;
            s.focused.background = normal; s.focused.textColor = text;
            s.onNormal.background = normal; s.onNormal.textColor = text;
            s.fontSize = size; s.fontStyle = fs;
            s.alignment = TextAnchor.MiddleCenter;
            s.border = new RectOffset(0, 0, 0, 0);
            return s;
        }

        public static void Init()
        {
            if (_ready && TexBg != null) return;
            _ready = true;

            TexBg = Tex(new Color(0.09f, 0.10f, 0.12f, 0.97f));
            TexHeader = Tex(new Color(0.13f, 0.14f, 0.17f, 1f));
            TexAccent = Tex(Accent);
            TexTrack = Tex(new Color(0.24f, 0.26f, 0.30f));
            TexKnob = Tex(Color.white);
            TexBtn = Tex(new Color(0.17f, 0.18f, 0.22f));
            TexBtnHover = Tex(new Color(0.23f, 0.25f, 0.30f));
            TexField = Tex(new Color(0.05f, 0.06f, 0.07f));
            TexChip = Tex(Color.white);

            var skin = GUI.skin;
            Window = new GUIStyle(skin.window);
            Window.normal.background = TexBg; Window.onNormal.background = TexBg;
            Window.border = new RectOffset(0, 0, 0, 0);
            Window.padding = new RectOffset(0, 0, 0, 0);

            Title = new GUIStyle(skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            Title.normal.textColor = Color.white;
            Sub = new GUIStyle(skin.label) { fontSize = 11 };
            Sub.normal.textColor = Accent;
            Section = new GUIStyle(skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };
            Section.normal.textColor = Accent;
            Label = new GUIStyle(skin.label) { fontSize = 13 };
            Label.normal.textColor = new Color(0.9f, 0.91f, 0.93f);
            Hint = new GUIStyle(skin.label) { fontSize = 11, wordWrap = true };
            Hint.normal.textColor = new Color(0.55f, 0.58f, 0.63f);

            Tab = Box(skin.button, TexBtn, TexBtnHover, new Color(0.75f, 0.77f, 0.8f), 12, FontStyle.Bold);
            TabOn = Box(skin.button, TexAccent, TexAccent, Dark, 12, FontStyle.Bold);
            Primary = Box(skin.button, TexAccent, Tex(new Color(1f, 0.8f, 0.3f)), Dark, 13, FontStyle.Bold);
            Key = Box(skin.button, TexField, TexBtnHover, Color.white, 12, FontStyle.Bold);
            CloseBtn = Box(skin.button, TexBtn, Tex(Red), Color.white, 12, FontStyle.Bold);

            Field = Box(skin.textField, TexField, TexField, Color.white, 12);
            Field.alignment = TextAnchor.MiddleRight;
            Field.padding = new RectOffset(4, 6, 2, 2);


            ChipStyle = new GUIStyle(skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }

        public static float ChipWidth(string t) => ChipStyle.CalcSize(new GUIContent(t)).x + 16;

        public static void Chip(Rect r, string t, Color bg, Color fg)
        {
            r.width = ChipWidth(t);
            var prev = GUI.color;
            GUI.color = bg;
            GUI.DrawTexture(r, TexChip);
            GUI.color = prev;
            ChipStyle.normal.textColor = fg;
            GUI.Label(r, t, ChipStyle);
        }
    }
}
