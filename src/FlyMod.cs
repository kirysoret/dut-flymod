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
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes;
using Caporta.DriveUpTogether.Maps;
using UnityEngine;
using UnityEngine.InputSystem;
using NWH.Common.Cameras;
using NWH.WheelController3D;

namespace DutFly
{
    [BepInPlugin("local.dut.flymod", "FlyMod", "0.2.5")]
    public class Plugin : BasePlugin
    {
        public const string VersionName = "0.2.5.1 Beta";
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

            // La camara se congela desde FlyBehaviour (allowRotation/zoom). No se parchea ningun getter:
            // en IL2CPP los getters pequenos comparten codigo entre clases y parchear uno los parchea todos.

            MapMod.Patch(h);

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
        public static ConfigEntry<bool> NoLimiters, AnyCarAnyMap, RgbCar, CubeCar;
        public static ConfigEntry<float> RgbSpeed;
        public static ConfigEntry<bool> Spider;
        public static ConfigEntry<float> SpiderStick, SpiderTurn, SpiderReach;
        public static ConfigEntry<float> WinX, WinY;

        public static ConfigEntry<string> KeyMenu, KeyFly, KeyUp, KeyDown, KeyFast, KeyFlip, KeyTrick, KeyBoost, KeySpec, KeySpider;
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
            AnyCarAnyMap = c.Bind("Mapas", "CualquierAutoEnCualquierMapa", true, "Permitir todas las clases de auto en todos los mapas");
            RgbCar = c.Bind("Estilo", "ColorRGB", false, "El auto cambia de color como RGB (lo ven todos)");
            RgbSpeed = c.Bind("Estilo", "VelocidadRGB", 0.25f, "Vueltas al arcoiris por segundo");
            CubeCar = c.Bind("Estilo", "AutoCubo", false, "Tu auto se ve como un cubo (solo en tu pantalla)");
            Spider = c.Bind("Arana", "Activo", false, "Modo arana: el auto sube por paredes y techos");
            SpiderStick = c.Bind("Arana", "Agarre", 1.5f, "Fuerza que lo pega a la superficie (en gravedades)");
            SpiderTurn = c.Bind("Arana", "RapidezGiro", 4f, "Que tan rapido se inclina hacia la pared");
            SpiderReach = c.Bind("Arana", "Alcance", 4f, "Distancia a la que empieza a inclinarse hacia una pared (m)");
            WinX = c.Bind("Menu", "X", 40f, ""); WinY = c.Bind("Menu", "Y", 80f, "");

            KeyMenu = Key(c, "Menu", "Digit8", "Abrir menu");
            KeyFly = Key(c, "Fly", "F8", "Fly on/off");
            KeyUp = Key(c, "FlySubir", "Space", "Fly: subir");
            KeyDown = Key(c, "FlyBajar", "LeftCtrl", "Fly: bajar");
            KeyFast = Key(c, "FlyRapido", "LeftShift", "Fly: rapido");
            KeyFlip = Key(c, "Flip", "G", "Voltereta");
            KeyTrick = Key(c, "TrucoAereo", "H", "Truco aereo");
            KeyBoost = Key(c, "Boost", "B", "Boost (mantener)");
            KeySpec = Key(c, "Espectar", "N", "Ver siguiente jugador");
            KeySpider = Key(c, "Arana", "J", "Modo arana on/off");
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
        float _flipLeft;
        Vector3 _flipAxisLocal;
        Quaternion _flipLevel;
        CursorLockMode _prevLock; bool _prevVisible;

        // truco aereo: 0 nada, 1 en el aire girando
        int _trick;
        Quaternion _trickLevel;
        float _trickAngle, _trickTime;
        Vector3 _trickStart;

        // TP / espectar / mapa
        readonly List<(string name, Caporta.DriveUpTogether.Vehicle veh)> _others = new();
        float _othersTimer;
        Vector3? _savedPos; Quaternion _savedRot;
        Caporta.DriveUpTogether.Vehicle _specTarget;
        string _specName;
        Camera _specCam;
        readonly List<Camera> _disabledCams = new();
        float _specYaw, _specPitch = 15f, _specDist = 9f, _specLost;
        GameObject _floor;
        bool _inVoid;
        Vector3 _voidReturnPos; Quaternion _voidReturnRot;
        static readonly Vector3 VoidOrigin = new Vector3(20000f, 0f, 20000f);

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
            if (Down(Settings.KeySpec)) SpectateNext();
            if (Down(Settings.KeySpider)) { Settings.Spider.Value = !Settings.Spider.Value; Plugin.Cfg.Save(); }

            _othersTimer -= Time.unscaledDeltaTime;
            if (_othersTimer <= 0) { _othersTimer = 1f; RefreshOthers(); }

            MapMod.Tick();

            StyleTick();

            _tuneTimer -= Time.unscaledDeltaTime;
            if (_tuneTimer <= 0) { _tuneTimer = 0.25f; ApplyTuning(); }

            bool speedCheats = Settings.PowerMult.Value > 1.001f || Settings.SpeedMult.Value > 1.001f
                               || Held(Settings.KeyBoost) || _flipLeft > 0;
            Plugin.Unlimited = _flying || _trick != 0 || _spiderActive || (Settings.NoLimiters.Value && speedCheats);
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
                _flying = true;
                Plugin.MarkUsed("fly");
            }
            else
            {
                _flying = false;
                if (_rb != null)
                {
                    _rb.useGravity = true;
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

        // ---------- truco aereo guiado por fisica:
        // 1) sube hasta exactamente "Altura" controlando la velocidad cada paso de fisica (y se endereza),
        // 2) ya arriba, da las volteretas con velocidad angular mientras se mantiene a esa altura
        //    y avanza "Distancia" metros,
        // 3) se pone horizontal y lo suelta: cae con la gravedad.
        // Nunca gira cerca del suelo (antes el morro golpeaba el suelo y lo lanzaba lejos).
        const float TrickRise = 15f;        // m/s maximos de subida
        const float TrickMaxSpeed = 60f;    // m/s maximos para seguir la trayectoria
        static float Gravity => Mathf.Max(0.1f, Mathf.Abs(Physics.gravity.y));

        // devuelve (tiempo subiendo, tiempo de volteretas, tiempo de caida)
        public static (float up, float spin, float fall) TrickTimes()
        {
            float h = Mathf.Max(0.1f, Settings.TrickHeight.Value);
            float up = h / TrickRise + 0.3f;
            float spin = Mathf.Max(0.25f, Settings.TrickTurns.Value) / Mathf.Max(0.1f, Settings.TrickSpin.Value);
            float fall = Mathf.Sqrt(2f * h / Gravity);
            return (up, spin, fall);
        }

        void StartTrick()
        {
            if (_flying || _trick != 0) return;
            _rb = FindLocalBody();
            if (_rb == null) return;
            _flipLeft = 0;
            _trickLevel = LevelRotation(_rb);
            _trickStart = _rb.position + Vector3.up * Mathf.Max(0.5f, Settings.TrickHeight.Value);
            _trickAngle = 0;
            _trickTime = 0;
            _rb.angularVelocity = Vector3.zero;
            _trick = 1;
            Plugin.MarkUsed("truco aereo");
        }

        // velocidad para llegar a "target" en este paso de fisica, limitada a maxSpeed
        Vector3 VelocityTo(Vector3 target, float maxSpeed)
        {
            Vector3 v = (target - _rb.position) / Time.fixedDeltaTime;
            return Vector3.ClampMagnitude(v, maxSpeed);
        }

        // velocidad angular para girar hacia "target" (control proporcional)
        Vector3 AngularTo(Quaternion target, float gain)
        {
            Quaternion d = target * Quaternion.Inverse(_rb.rotation);
            d.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            if (float.IsNaN(axis.x) || Mathf.Abs(ang) < 0.01f) return Vector3.zero;
            return axis.normalized * (ang * Mathf.Deg2Rad * gain);
        }

        void TrickStep()
        {
            if (_rb == null) { _trick = 0; return; }
            float dt = Time.fixedDeltaTime;
            _trickTime += dt;
            Vector3 fwd = _trickLevel * Vector3.forward;

            if (_trick == 1)
            {
                // subiendo: seguir hasta la altura exacta y enderezarse
                _rb.linearVelocity = VelocityTo(_trickStart, TrickRise);
                _rb.angularVelocity = AngularTo(_trickLevel, 10f);
                bool arrived = (_rb.position - _trickStart).sqrMagnitude < 0.04f;
                if (arrived || _trickTime > Settings.TrickHeight.Value / TrickRise + 3f)
                {
                    _rb.rotation = _trickLevel;
                    _rb.angularVelocity = Vector3.zero;
                    _trickTime = 0;
                    _trick = 2;
                }
                return;
            }

            // volteretas: se mantiene a la altura y avanza "Distancia"
            float total = 360f * Mathf.Max(0.25f, Settings.TrickTurns.Value);
            float w = 360f * Mathf.Max(0.1f, Settings.TrickSpin.Value);      // grados por segundo
            _trickAngle = Mathf.Min(total, _trickAngle + w * dt);
            float progress = _trickAngle / total;
            _rb.linearVelocity = VelocityTo(_trickStart + fwd * (Settings.TrickDistance.Value * progress), TrickMaxSpeed);
            _rb.angularVelocity = (_trickLevel * Vector3.right) * (w * Mathf.Deg2Rad);
            if (_trickAngle >= total) EndTrick();
        }

        void EndTrick()
        {
            if (_rb != null)
            {
                _rb.angularVelocity = Vector3.zero;
                _rb.rotation = _trickLevel;          // horizontal perfecto
                _rb.MoveRotation(_trickLevel);
                _rb.linearVelocity = Vector3.zero;   // y lo suelta: cae con la gravedad
            }
            _trick = 0;
        }

        // ---------- modo arana
        // Rayo hacia abajo del auto (superficie que pisa) y hacia adelante (pared que se acerca).
        // La "normal objetivo" se mezcla hacia la pared segun lo cerca que este: el auto se va
        // inclinando poco a poco. La gravedad tira hacia la superficie que pisa en vez de hacia abajo.
        // El giro sobre su propio eje (volante) se conserva.
        bool _spiderActive, _spiderSurface;
        Vector3 _spiderNormal = Vector3.up;
        float _spiderAir;

        // primer impacto que no sea del propio auto
        bool CastSurface(Vector3 origin, Vector3 dir, float len, out RaycastHit best)
        {
            best = default;
            bool found = false;
            float bestD = float.MaxValue;
            foreach (var h in Physics.RaycastAll(origin, dir, len, ~0, QueryTriggerInteraction.Ignore))
            {
                var col = h.collider;
                if (col == null) continue;
                if (col.attachedRigidbody != null && col.attachedRigidbody.Pointer == _rb.Pointer) continue;
                if (h.distance < bestD) { bestD = h.distance; best = h; found = true; }
            }
            return found;
        }

        void SpiderStep()
        {
            if (!Settings.Spider.Value) return;
            if (_rb == null) { _rb = FindLocalBody(); if (_rb == null) return; }
            float dt = Time.fixedDeltaTime;
            var t = _rb.transform;
            Vector3 com = _rb.worldCenterOfMass;

            if (!_spiderActive)
            {
                _spiderActive = true;
                _spiderNormal = t.up;
                Plugin.MarkUsed("modo arana");
            }

            // superficie que pisa
            bool down = CastSurface(com, -t.up, 2.5f, out var dh);
            Vector3 target = down ? dh.normal : _spiderNormal;

            // pared que se acerca (en la direccion en que avanza)
            Vector3 vel = _rb.linearVelocity;
            Vector3 along = Vector3.ProjectOnPlane(vel, t.up);
            Vector3 fwd = along.sqrMagnitude > 1f ? along.normalized : t.forward;
            float reach = Mathf.Max(0.5f, Settings.SpiderReach.Value) + along.magnitude * 0.15f;
            if (CastSurface(com, fwd, reach, out var fh))
            {
                float ang = Vector3.Angle(fh.normal, target);
                if (ang > 25f)
                {
                    float k = 1f - Mathf.Clamp01(fh.distance / reach);   // 0 lejos -> 1 pegado
                    target = Vector3.Slerp(target, fh.normal, k).normalized;
                }
            }

            if (down) { _spiderAir = 0; _spiderSurface = true; }
            else
            {
                _spiderAir += dt;
                if (_spiderAir > 0.6f)
                {
                    // en el aire: volver poco a poco a la gravedad normal
                    _spiderSurface = false;
                    target = Vector3.up;
                }
            }
            _spiderNormal = Vector3.Slerp(_spiderNormal, target, Mathf.Clamp01(dt * Mathf.Max(0.1f, Settings.SpiderTurn.Value) * 2f)).normalized;

            // gravedad hacia la superficie
            _rb.useGravity = false;
            _rb.AddForce(-_spiderNormal * (Gravity * Mathf.Max(0.1f, _spiderSurface ? Settings.SpiderStick.Value : 1f)), ForceMode.Acceleration);

            // inclinar el auto hacia la normal conservando su giro con el volante
            Quaternion want = Quaternion.FromToRotation(t.up, _spiderNormal) * _rb.rotation;
            Vector3 align = AngularTo(want, Mathf.Max(0.1f, Settings.SpiderTurn.Value));
            Vector3 w = _rb.angularVelocity;
            Vector3 yaw = Vector3.Project(w, t.up);
            _rb.angularVelocity = yaw + align;
        }

        void SpiderRelease()
        {
            _spiderActive = false;
            _spiderSurface = false;
            if (_rb != null) _rb.useGravity = true;
        }

        // deja el auto de fabrica: multiplicadores x1, gravedad encendida y todos los modos apagados
        void RestoreCar()
        {
            Settings.PowerMult.Value = Settings.SpeedMult.Value = Settings.GripMult.Value = 1f;
            _txt.Clear();
            Settings.Spider.Value = false;
            if (_spiderActive) SpiderRelease();
            if (_trick != 0) EndTrick();
            _flipLeft = 0;
            if (_flying) ToggleFly();
            var rb = FindLocalBody();
            if (rb != null)
            {
                rb.useGravity = true;
                rb.angularVelocity = Vector3.zero;
            }
            _tuneTimer = 0;   // aplicar los x1 ya
            Plugin.Cfg.Save();
            Plugin.L.LogInfo("Auto restaurado a original.");
        }

        // ---------- estilo del auto
        // RGB: se pinta la carroceria con el sistema del juego (VehicleCustomizationApplicator).
        //   En tu pantalla cada fotograma (ApplyLocal) y por red ~10 veces por segundo (_networkBodyColor),
        //   asi lo ven todos. Al apagarlo se devuelve el color original.
        // Cubo: oculta los modelos de tu auto y pone un cubo del mismo tamano (solo visual y solo local).
        Caporta.DriveUpTogether.VehicleCustomizationApplicator _paint;
        IntPtr _paintVeh;
        bool _rgbOn, _hasOrigColor, _netWriteFailed;
        Color32 _origColor;
        float _hue, _netTimer;
        GameObject _cube;
        IntPtr _cubeVeh;
        readonly List<Renderer> _cubeHidden = new();

        void StyleTick()
        {
            var veh = LocalVehicle();
            IntPtr vp = veh == null ? IntPtr.Zero : veh.Pointer;

            // ---- RGB
            if (vp != _paintVeh) { _paint = null; _paintVeh = vp; _hasOrigColor = false; _rgbOn = false; }
            if (veh != null && _paint == null)
                try { _paint = veh.GetComponentInChildren<Caporta.DriveUpTogether.VehicleCustomizationApplicator>(); } catch { }

            if (Settings.RgbCar.Value && _paint != null)
            {
                if (!_rgbOn)
                {
                    try { _origColor = _paint._networkBodyColor.Value; _hasOrigColor = true; } catch { }
                    _rgbOn = true;
                    Plugin.MarkUsed("color rgb");
                }
                _hue = (_hue + Time.unscaledDeltaTime * Mathf.Max(0.01f, Settings.RgbSpeed.Value)) % 1f;
                Color c = Color.HSVToRGB(_hue, 0.85f, 1f);
                Vector4 finish = Vector4.zero;
                try { finish = _paint._networkFinish.Value; } catch { }
                try { _paint.ApplyLocal(c, finish); } catch { }
                _netTimer -= Time.unscaledDeltaTime;
                if (_netTimer <= 0 && !_netWriteFailed)
                {
                    _netTimer = 0.1f;
                    try { _paint._networkBodyColor.Value = (Color32)c; }
                    catch (Exception e) { _netWriteFailed = true; Plugin.L.LogWarning("Color RGB solo local (la red no lo permite): " + e.Message); }
                }
                if (_cube != null) SetCubeColor(c);
            }
            else if (_rgbOn)
            {
                _rgbOn = false;
                if (_paint != null && _hasOrigColor)
                {
                    Vector4 finish = Vector4.zero;
                    try { finish = _paint._networkFinish.Value; } catch { }
                    try { _paint.ApplyLocal(_origColor, finish); } catch { }
                    try { if (!_netWriteFailed) _paint._networkBodyColor.Value = _origColor; } catch { }
                }
            }

            // ---- cubo
            bool wantCube = Settings.CubeCar.Value && veh != null;
            if (_cube != null && (!wantCube || vp != _cubeVeh)) RemoveCube();
            if (wantCube && _cube == null) MakeCube(veh);
        }

        void MakeCube(Caporta.DriveUpTogether.Vehicle veh)
        {
            try
            {
                var rends = veh.GetComponentsInChildren<Renderer>();
                bool any = false;
                Bounds b = new Bounds(veh.transform.position, Vector3.one);
                _cubeHidden.Clear();
                foreach (var r in rends)
                {
                    if (r == null || !r.enabled) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                    r.enabled = false;
                    _cubeHidden.Add(r);
                }
                var t = veh.transform;
                _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube.name = "FlyModAutoCubo";
                var col = _cube.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);     // solo visual: la fisica sigue siendo la del auto
                _cube.transform.SetParent(t, false);
                // tamano: el mayor lado de la caja del auto, para que sea un cubo de verdad
                Vector3 local = t.InverseTransformVector(b.size);
                float side = Mathf.Max(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                side = Mathf.Clamp(side * 0.75f, 1f, 8f);
                _cube.transform.localScale = Vector3.one * side;
                _cube.transform.position = b.center;
                _cube.transform.localRotation = Quaternion.identity;
                var r2 = _cube.GetComponent<Renderer>();
                Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (r2 != null && sh != null) r2.material = new Material(sh);
                SetCubeColor(Ui.Accent);
                _cubeVeh = veh.Pointer;
                Plugin.L.LogInfo($"Auto cubo: {_cubeHidden.Count} piezas ocultas, lado {side:0.0} m");
            }
            catch (Exception e) { Plugin.L.LogWarning("No se pudo hacer el auto cubo: " + e.Message); }
        }

        void SetCubeColor(Color c)
        {
            try
            {
                var m = _cube.GetComponent<Renderer>().material;
                m.color = c;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            }
            catch { }
        }

        void RemoveCube()
        {
            foreach (var r in _cubeHidden) { try { if (r != null) r.enabled = true; } catch { } }
            _cubeHidden.Clear();
            try { if (_cube != null) UnityEngine.Object.Destroy(_cube); } catch { }
            _cube = null;
            _cubeVeh = IntPtr.Zero;
        }

        // ---------- otros jugadores
        void RefreshOthers()
        {
            _others.Clear();
            try
            {
                var mine = LocalVehicle();
                var all = UnityEngine.Object.FindObjectsByType<Caporta.DriveUpTogether.Vehicle>(FindObjectsSortMode.None);
                int i = 1;
                foreach (var v in all)
                {
                    if (v == null || (mine != null && v.Pointer == mine.Pointer)) continue;
                    bool ghost = false;
                    try { ghost = v.IsGhost != null && v.IsGhost.Value; } catch { }
                    if (ghost) continue;
                    string name = null;
                    try { name = v.DebugDisplayName; } catch { }
                    if (string.IsNullOrWhiteSpace(name)) name = "Jugador " + i;
                    _others.Add((name, v));
                    i++;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("No se pudo listar jugadores: " + e.Message); }
        }

        // ---------- teletransporte
        void TeleportTo(Vector3 pos, Quaternion rot)
        {
            var veh = LocalVehicle();
            var rb = FindLocalBody();
            if (veh == null || rb == null) return;
            if (_trick != 0) EndTrick();
            _flipLeft = 0;
            try { veh.SetPositionAndRotation(pos, rot); } catch { }
            rb.position = pos;
            rb.rotation = rot;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            Plugin.MarkUsed("tp");
        }

        void TeleportToVehicle(Caporta.DriveUpTogether.Vehicle target)
        {
            if (target == null) return;
            var t = target.transform;
            Vector3 fwd = t.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            // al lado del otro coche, un poco por encima para no chocar
            TeleportTo(t.position + rot * Vector3.right * 4f + Vector3.up * 1.5f, rot);
        }

        void SavePosition()
        {
            var rb = FindLocalBody();
            if (rb == null) return;
            _savedPos = rb.position;
            _savedRot = LevelRotation(rb);
        }

        // ---------- espectar: camara propia detras del coche de otro jugador
        void EnsureSpecCam()
        {
            if (_specCam != null) return;
            var go = new GameObject("FlyModSpectatorCam");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _specCam = go.AddComponent<Camera>();
            _specCam.nearClipPlane = 0.1f;
            _specCam.farClipPlane = 20000f;
            _specCam.clearFlags = CameraClearFlags.Skybox;
        }

        // apaga las camaras del juego (guarda cuales para devolverlas despues)
        void DisableGameCameras()
        {
            foreach (var c in Camera.allCameras)
            {
                if (c == null || c == _specCam || !c.enabled) continue;
                c.enabled = false;
                if (!_disabledCams.Contains(c)) _disabledCams.Add(c);
            }
        }

        void StartSpectate(Caporta.DriveUpTogether.Vehicle target, string name)
        {
            if (target == null) return;
            EnsureSpecCam();
            var main = Camera.main;
            if (main != null && main != _specCam)
            {
                _specCam.fieldOfView = main.fieldOfView;
                _specCam.farClipPlane = Mathf.Max(main.farClipPlane, 5000f);
                _specCam.depth = main.depth + 1;
                _specCam.cullingMask = main.cullingMask;
            }
            if (_specTarget == null) _specYaw = target.transform.eulerAngles.y;
            _specTarget = target;
            _specName = name;
            _specLost = 0;
            _specCam.enabled = true;
            DisableGameCameras();
        }

        void StopSpectate()
        {
            _specTarget = null;
            _specName = null;
            if (_specCam != null) _specCam.enabled = false;
            foreach (var c in _disabledCams) { try { if (c != null) c.enabled = true; } catch { } }
            _disabledCams.Clear();
        }

        void SpectateNext()
        {
            RefreshOthers();
            if (_others.Count == 0) { StopSpectate(); return; }
            int idx = -1;
            for (int i = 0; i < _others.Count; i++)
                if (_specTarget != null && _others[i].veh.Pointer == _specTarget.Pointer) idx = i;
            if (idx == _others.Count - 1) { StopSpectate(); return; }   // despues del ultimo vuelve a tu coche
            var next = _others[idx + 1];
            StartSpectate(next.veh, next.name);
        }

        static bool IsUsable(Caporta.DriveUpTogether.Vehicle v)
        {
            try
            {
                if (v == null || v.gameObject == null || !v.gameObject.activeInHierarchy) return false;
                if (v.IsGhost != null && v.IsGhost.Value) return false;
                var p = v.transform.position;
                return !(float.IsNaN(p.x) || float.IsInfinity(p.y) || p.y < -5000f);
            }
            catch { return false; }
        }

        void LateUpdate()
        {
            // camara de espectador
            if (_specTarget != null && !IsUsable(_specTarget))
            {
                // se reinicio o murio: buscar su coche nuevo por nombre durante unos segundos
                _specLost += Time.unscaledDeltaTime;
                RefreshOthers();
                foreach (var (n, v) in _others)
                    if (n == _specName && IsUsable(v)) { _specTarget = v; _specLost = 0; break; }
                if (_specLost > 3f) StopSpectate();
            }
            if (_specTarget != null && IsUsable(_specTarget))
            {
                EnsureSpecCam();
                if (!_specCam.enabled) _specCam.enabled = true;
                DisableGameCameras();   // por si el juego reactivo alguna al reiniciar
                var mouse = Mouse.current;
                if (mouse != null && !_menuOpen)
                {
                    var d = mouse.delta.ReadValue();
                    _specYaw += d.x * 0.15f;
                    _specPitch = Mathf.Clamp(_specPitch - d.y * 0.15f, -20f, 80f);
                    float s = mouse.scroll.ReadValue().y;
                    if (Mathf.Abs(s) > 0.01f) _specDist = Mathf.Clamp(_specDist - Mathf.Sign(s) * 1f, 3f, 40f);
                }
                Vector3 focus = _specTarget.transform.position + Vector3.up * 1.2f;
                var rot = Quaternion.Euler(_specPitch, _specYaw, 0f);
                _specCam.transform.position = focus - rot * Vector3.forward * _specDist;
                _specCam.transform.rotation = rot;
            }
            else if (_specTarget == null && _disabledCams.Count > 0)
            {
                StopSpectate(); // seguridad: nunca dejar el juego sin camara
            }

            // mapa "Infinito (Mod)": se aplica cuando el juego carga nuestro mapa de la lista
            if (MapMod.IsActive)
            {
                int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if (_inVoid && scene != _mapScene) LeaveMap();      // se recargo la escena
                if (!_inVoid) TryApplyMap();
                else FollowFloor();
            }
            else if (_inVoid) LeaveMap();
        }

        void FollowFloor()
        {
            if (_floor == null) return;
            var rb = FindLocalBody();
            if (rb == null) return;
            // sigue al coche a saltos de una baldosa, asi la textura no se mueve
            var p = rb.position;
            float sx = Mathf.Round(p.x / FloorTile) * FloorTile, sz = Mathf.Round(p.z / FloorTile) * FloorTile;
            _floor.transform.position = new Vector3(sx, _floorY - 0.5f, sz);
        }

        // ---------- mapa propio: piso gris con cuadricula, sin el mundo del juego
        const float FloorTile = 4f;      // metros por baldosa
        const float FloorSize = 6000f;
        float _floorY;
        int _mapScene;
        readonly List<Renderer> _hiddenRenderers = new();
        readonly List<Terrain> _hiddenTerrains = new();
        readonly List<Collider> _disabledColliders = new();
        readonly List<(Camera cam, CameraClearFlags flags, Color bg)> _camBg = new();
        Material _prevSkybox; bool _prevFog;

        void TryApplyMap()
        {
            var rb = FindLocalBody();
            if (rb == null) return;     // todavia no aparecio el coche

            // altura del suelo en el punto de salida (antes de quitar las colisiones del mundo)
            _floorY = rb.position.y - 1f;
            try
            {
                foreach (var h in Physics.RaycastAll(rb.worldCenterOfMass + Vector3.up * 2f, Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore))
                {
                    var col = h.collider;
                    if (col == null) continue;
                    if (col.attachedRigidbody != null && col.attachedRigidbody.Pointer == rb.Pointer) continue;
                    _floorY = h.point.y;
                    break;
                }
            }
            catch { }

            if (_floor == null) _floor = CreateFloor(rb);
            _floor.SetActive(true);
            FollowFloor();
            HideWorld();
            _mapScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            _inVoid = true;
            // por si el coche quedo dentro de algo
            var p = rb.position;
            if (p.y < _floorY + 0.5f) TeleportTo(new Vector3(p.x, _floorY + 1.5f, p.z), LevelRotation(rb));
            Plugin.MarkUsed("mapa " + MapMod.DisplayName);
            Plugin.L.LogInfo($"Mapa {MapMod.DisplayName} aplicado (piso a {_floorY:0.0} m)");
        }

        void LeaveMap()
        {
            _inVoid = false;
            ShowWorld();
            if (_floor != null) _floor.SetActive(false);
        }

        static bool UnderVehicle(Transform t, Il2CppArrayBase<Caporta.DriveUpTogether.Vehicle> cars)
        {
            foreach (var c in cars)
                if (c != null && t.IsChildOf(c.transform)) return true;
            return false;
        }

        // oculta todo lo que se dibuja del mapa (no los coches ni el piso propio) y pone el cielo oscuro
        void HideWorld()
        {
            _hiddenRenderers.Clear(); _hiddenTerrains.Clear(); _camBg.Clear();
            try
            {
                var cars = UnityEngine.Object.FindObjectsByType<Caporta.DriveUpTogether.Vehicle>(FindObjectsSortMode.None);
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r == null || !r.enabled || r.gameObject == _floor) continue;
                    bool isCar = false;
                    foreach (var c in cars)
                        if (c != null && r.transform.IsChildOf(c.transform)) { isCar = true; break; }
                    if (isCar) continue;
                    r.enabled = false;
                    _hiddenRenderers.Add(r);
                }
                foreach (var t in UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None))
                    if (t != null && t.enabled) { t.enabled = false; _hiddenTerrains.Add(t); }
                _disabledColliders.Clear();
                foreach (var col in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
                {
                    if (col == null || !col.enabled || col.isTrigger || col.gameObject == _floor) continue;
                    if (UnderVehicle(col.transform, cars)) continue;
                    col.enabled = false;
                    _disabledColliders.Add(col);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("No se pudo ocultar el mapa: " + e.Message); }

            _prevSkybox = RenderSettings.skybox; _prevFog = RenderSettings.fog;
            RenderSettings.skybox = null; RenderSettings.fog = false;
            var bg = new Color(0.04f, 0.045f, 0.05f);
            foreach (var c in Camera.allCameras)
            {
                if (c == null) continue;
                _camBg.Add((c, c.clearFlags, c.backgroundColor));
                c.clearFlags = CameraClearFlags.SolidColor;
                c.backgroundColor = bg;
            }
            if (_specCam != null) { _specCam.clearFlags = CameraClearFlags.SolidColor; _specCam.backgroundColor = bg; }
            Plugin.L.LogInfo($"Mapa propio: {_hiddenRenderers.Count} objetos ocultos, {_disabledColliders.Count} colisiones quitadas");
        }

        void ShowWorld()
        {
            foreach (var r in _hiddenRenderers) { try { if (r != null) r.enabled = true; } catch { } }
            foreach (var t in _hiddenTerrains) { try { if (t != null) t.enabled = true; } catch { } }
            foreach (var c in _disabledColliders) { try { if (c != null) c.enabled = true; } catch { } }
            _disabledColliders.Clear();
            foreach (var (c, f, bg) in _camBg) { try { if (c != null) { c.clearFlags = f; c.backgroundColor = bg; } } catch { } }
            if (_specCam != null) _specCam.clearFlags = CameraClearFlags.Skybox;
            _hiddenRenderers.Clear(); _hiddenTerrains.Clear(); _camBg.Clear();
            RenderSettings.skybox = _prevSkybox; RenderSettings.fog = _prevFog;
        }

        // textura de baldosa: gris con bordes mas oscuros y una linea fina en el medio
        static Texture2D MakeGridTexture()
        {
            const int N = 128;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, true);
            var baseC = new Color(0.42f, 0.43f, 0.45f);
            var line = new Color(0.27f, 0.28f, 0.30f);
            var thin = new Color(0.36f, 0.37f, 0.39f);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Color c = baseC;
                    if (x < 3 || y < 3 || x >= N - 3 || y >= N - 3) c = line;
                    else if (x == N / 2 || y == N / 2) c = thin;
                    // un poco de ruido para que se note el movimiento
                    float n = (((x * 73856093) ^ (y * 19349663)) & 7) / 300f;
                    t.SetPixel(x, y, new Color(c.r + n, c.g + n, c.b + n));
                }
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 8;
            t.Apply(true);
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        GameObject CreateFloor(Rigidbody car)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "FlyModPiso";
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.transform.localScale = new Vector3(FloorSize, 1f, FloorSize);

            // misma capa que el suelo que pisa el coche ahora, para que las ruedas lo detecten
            try
            {
                foreach (var h in Physics.RaycastAll(car.worldCenterOfMass, Vector3.down, 10f, ~0, QueryTriggerInteraction.Ignore))
                {
                    var col = h.collider;
                    if (col == null) continue;
                    if (col.attachedRigidbody != null && col.attachedRigidbody.Pointer == car.Pointer) continue;
                    go.layer = col.gameObject.layer;
                    break;
                }
            }
            catch { }

            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Texture");
                var m = sh != null ? new Material(sh) : r.material;
                var tex = MakeGridTexture();
                m.mainTexture = tex;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                m.color = Color.white;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                var scale = new Vector2(FloorSize / FloorTile, FloorSize / FloorTile);
                m.mainTextureScale = scale;
                if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", scale);
                r.material = m;
                Plugin.L.LogInfo("Piso: shader " + (sh != null ? sh.name : "(por defecto)"));
            }
            return go;
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
            else SpiderStep();
            if ((_flying || _trick != 0 || _flipLeft > 0 || !Settings.Spider.Value) && _spiderActive) SpiderRelease();

            // red de seguridad: sin ningun modo activo la gravedad siempre esta encendida
            if (!_flying && _trick == 0 && !_spiderActive && _rb != null)
            {
                try { if (!_rb.useGravity) _rb.useGravity = true; } catch { }
            }

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
            string chip = _flying ? "FLY" : _trick != 0 ? "TRUCO AEREO" : boosting ? "BOOST" : (_spiderActive && _spiderSurface) ? "ARANA" : null;
            if (_specTarget != null) chip = "VIENDO: " + _specName + "  [" + Settings.P(Settings.KeySpec) + " siguiente]";
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
            string[] tabs = { "Fly", "Flip", "Truco", "Coche", "TP", "Ver", "Mapa", "Teclas" };
            const int perRow = 4;
            float tw = CW / perRow;
            for (int i = 0; i < tabs.Length; i++)
            {
                float tx = X + (i % perRow) * tw, ty = _y + (i / perRow) * 32;
                if (GUI.Button(new Rect(tx, ty, tw - 4, 28), tabs[i], _tab == i ? Ui.TabOn : Ui.Tab)) _tab = i;
            }
            _y += 32 * ((tabs.Length + perRow - 1) / perRow) + 8;

            switch (_tab)
            {
                case 4:
                    Section("IR A UN JUGADOR");
                    PlayerList("TP", (v, n) => TeleportToVehicle(v));
                    Section("IR A COORDENADAS");
                    CoordFields();
                    {
                        float bw = (CW - 8) / 2f;
                        if (GUI.Button(new Rect(X, _y, bw, 28), "Usar mi posicion", Ui.Tab))
                        {
                            var rb = FindLocalBody();
                            if (rb != null)
                            {
                                _txt["tpX"] = Fmt(rb.position.x); _txt["tpY"] = Fmt(rb.position.y); _txt["tpZ"] = Fmt(rb.position.z);
                            }
                        }
                        if (GUI.Button(new Rect(X + bw + 8, _y, bw, 28), "TP a coordenadas", Ui.Primary))
                        {
                            var rb = FindLocalBody();
                            if (rb != null)
                                TeleportTo(new Vector3(ParseF(_txt["tpX"]), ParseF(_txt["tpY"]), ParseF(_txt["tpZ"])), LevelRotation(rb));
                        }
                        _y += 38;
                    }
                    Section("POSICION GUARDADA");
                    {
                        float bw = (CW - 8) / 2f;
                        if (GUI.Button(new Rect(X, _y, bw, 28), "Guardar aqui", Ui.Tab)) SavePosition();
                        bool has = _savedPos.HasValue;
                        if (GUI.Button(new Rect(X + bw + 8, _y, bw, 28), has ? "Volver" : "(sin guardar)", has ? Ui.Primary : Ui.Tab) && has)
                            TeleportTo(_savedPos.Value + Vector3.up * 0.5f, _savedRot);
                        _y += 36;
                        if (has) Hint("Guardada: " + Fmt(_savedPos.Value.x) + ", " + Fmt(_savedPos.Value.y) + ", " + Fmt(_savedPos.Value.z));
                    }
                    break;
                case 5:
                    Section("VER A OTROS JUGADORES  [" + Settings.P(Settings.KeySpec) + "]");
                    Hint("Tu camara se pone detras de su coche. Mouse para girar, rueda para acercar.");
                    PlayerList("Ver", (v, n) => StartSpectate(v, n));
                    if (_specTarget != null && Button("Volver a mi coche")) StopSpectate();
                    break;
                case 6:
                    Section("MOD DE MAPAS");
                    Hint("El mapa '" + MapMod.DisplayName + "' aparece en la lista de mapas del juego (gratis). Eligelo al crear la partida: piso gris con cuadricula infinito y nada mas.");
                    Hint("Tus amigos tambien necesitan el mod para jugarlo contigo. En ese mapa nunca se sube puntuacion.");
                    GUI.Label(new Rect(X, _y, CW, 22), MapMod.IsActive ? "Estado: estas en " + MapMod.DisplayName : "Estado: no estas en el mapa", Ui.Label);
                    _y += 28;
                    Section("AUTOS");
                    {
                        bool v = Switch("Cualquier auto en cualquier mapa", Settings.AnyCarAnyMap.Value, null);
                        if (v != Settings.AnyCarAnyMap.Value) { Settings.AnyCarAnyMap.Value = v; MapMod.ForceRefresh(); }
                    }
                    Hint(MapMod.CarClassesInfo);
                    break;
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
                    FlipEstimate();
                    if (Button("Hacer voltereta")) StartFlip();
                    break;
                case 2:
                    Section("TRUCO AEREO  [" + Settings.P(Settings.KeyTrick) + "]");
                    Hint("Sube el coche con fisica hasta la altura, da las volteretas ahi avanzando la distancia, se pone horizontal y cae.");
                    Slider("Altura", Settings.TrickHeight, 1, 100, "m");
                    Slider("Distancia", Settings.TrickDistance, 0, 50, "m");
                    Slider("Volteretas", Settings.TrickTurns, 1, 20, "");
                    Slider("Velocidad de volteretas", Settings.TrickSpin, 0.2f, 10, "v/s");
                    Estimate();
                    if (Button(_trick != 0 ? "Truco en curso..." : "Hacer truco")) StartTrick();
                    break;
                case 3:
                    Section("MODO ARANA  [" + Settings.P(Settings.KeySpider) + "]");
                    Settings.Spider.Value = Switch("Subir por paredes y techos", Settings.Spider.Value, null);
                    if (Settings.Spider.Value)
                    {
                        Slider("Agarre", Settings.SpiderStick, 0.5f, 5, "g");
                        Slider("Rapidez al inclinarse", Settings.SpiderTurn, 0.5f, 15, "");
                        Slider("Alcance hacia la pared", Settings.SpiderReach, 1, 15, "m");
                    }
                    Section("ESTILO");
                    Settings.RgbCar.Value = Switch("Color RGB (lo ven todos)", Settings.RgbCar.Value, null);
                    if (Settings.RgbCar.Value) Slider("Velocidad RGB", Settings.RgbSpeed, 0.02f, 3, "v/s");
                    Settings.CubeCar.Value = Switch("Auto cubo (solo en tu pantalla)", Settings.CubeCar.Value, null);
                    Section("MOTOR Y RUEDAS");
                    Slider("Potencia", Settings.PowerMult, 0.5f, 50, "x");
                    Slider("Velocidad maxima", Settings.SpeedMult, 0.5f, 10, "x");
                    Slider("Traccion / agarre", Settings.GripMult, 0.2f, 20, "x");
                    Section("BOOST  [" + Settings.P(Settings.KeyBoost) + "]");
                    Slider("Fuerza del boost", Settings.BoostAccel, 0, 300, "m/s2");
                    Settings.NoLimiters.Value = Switch("Quitar limitadores del juego", Settings.NoLimiters.Value, null);
                    Hint("Escribe en la caja para pasar del maximo del slider.");
                    if (Button("Restaurar auto original")) RestoreCar();
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

        // tiempo aproximado del truco aereo: subida + volteretas + caida
        void Estimate()
        {
            var (up, spin, fall) = TrickTimes();
            TimeBox(up + spin + fall, "subida " + Dur(up) + "  +  volteretas " + Dur(spin) + "  +  caida " + Dur(fall));
        }

        // voltereta: tiempo girando y tiempo en el aire por el salto
        void FlipEstimate()
        {
            float spinT = Mathf.Max(0.1f, Settings.FlipTurns.Value) * 2f * Mathf.PI / Mathf.Max(0.5f, Settings.FlipSpin.Value);
            float airT = 2f * Mathf.Max(0f, Settings.FlipHop.Value) / Gravity;
            TimeBox(Mathf.Max(spinT, airT), "giro " + Dur(spinT) + "   |   en el aire " + Dur(airT));
        }

        void TimeBox(float total, string detail)
        {
            var box = new Rect(X, _y + 2, CW, 40);
            GUI.DrawTexture(box, Ui.TexField);
            GUI.Label(new Rect(X + 10, _y + 4, CW - 20, 20), "Tiempo estimado:  " + Dur(total), Ui.Label);
            GUI.Label(new Rect(X + 10, _y + 22, CW - 20, 18), detail, Ui.Hint);
            _y += 50;
        }

        // 4.1 s | 1 minuto | 1h y 1 minuto | 1d, 2h y 5 minutos ...
        public static string Dur(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds)) return "-";
            if (seconds < 60f) return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            long t = (long)Math.Round(seconds);
            long d = t / 86400; t %= 86400;
            long h = t / 3600; t %= 3600;
            long m = t / 60; long sec = t % 60;
            var parts = new List<string>();
            if (d > 0) parts.Add(d + "d");
            if (h > 0) parts.Add(h + "h");
            if (m > 0) parts.Add(m + (m == 1 ? " minuto" : " minutos"));
            if (sec > 0) parts.Add(sec + " s");
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " y " + parts[parts.Count - 1];
        }

        // lista de jugadores (con un boton por fila)
        void PlayerList(string action, Action<Caporta.DriveUpTogether.Vehicle, string> onClick)
        {
            if (_others.Count == 0) { Hint("No hay otros jugadores en la partida."); return; }
            foreach (var (name, veh) in _others)
            {
                GUI.Label(new Rect(X, _y + 3, CW - 110, 22), name, Ui.Label);
                if (GUI.Button(new Rect(X + CW - 100, _y, 100, 26), action, Ui.Key)) onClick(veh, name);
                _y += 31;
            }
        }

        void CoordFields()
        {
            float w = (CW - 16) / 3f;
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                string k = "tp" + axes[i];
                if (!_txt.TryGetValue(k, out var buf)) buf = "0";
                GUI.Label(new Rect(X + i * (w + 8), _y + 3, 14, 20), axes[i], Ui.Section);
                _txt[k] = GUI.TextField(new Rect(X + i * (w + 8) + 14, _y, w - 14, 24), buf, Ui.Field);
            }
            _y += 32;
        }

        static float ParseF(string s) =>
            float.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

        static string Fmt(float f) => f.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- mod de mapas
    // Agrega "Infinito (Mod)" a la lista de mapas del juego. Usa la escena de Playground como base;
    // al cargarse, FlyBehaviour oculta el mundo y pone el piso gris infinito.
    //
    // OJO (IL2CPP): metodos pequenos con el mismo codigo maquina se comparten entre clases
    // (p. ej. todos los getters que devuelven un campo). Parchear uno parchea todos. Por eso:
    //  - no se parchea ningun getter; el catalogo se busca con Resources.FindObjectsOfTypeAll,
    //  - en cada parche se comprueba la clase real del objeto antes de usarlo.
    static class MapMod
    {
        public const string Id = "flymod_infinito";
        public const string DisplayName = "Infinito (Mod)";
        public static MapDefinition Def;
        public static string BaseScene;
        public static string CurrentId;
        public static bool IsActive => CurrentId == Id;
        static bool _logged;
        static float _searchTimer;

        // la clase IL2CPP real del objeto es T (o hereda de T)
        static bool Is<T>(Il2CppObjectBase o) where T : Il2CppObjectBase
        {
            try
            {
                if (o == null || o.Pointer == IntPtr.Zero) return false;
                var klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
                var want = Il2CppClassPointerStore<T>.NativeClassPtr;
                return klass != IntPtr.Zero && want != IntPtr.Zero && IL2CPP.il2cpp_class_is_assignable_from(want, klass);
            }
            catch { return false; }
        }

        static bool IsOurs(MapDefinition m) => m != null && Def != null && m.Pointer == Def.Pointer;

        public static void Patch(Harmony h)
        {
            void Hook(Type t, string method, string pre, string post)
            {
                try
                {
                    var m = AccessTools.Method(t, method);
                    if (m == null) { Plugin.L.LogWarning($"Mapas: no existe {t.Name}.{method}"); return; }
                    h.Patch(m,
                        prefix: pre == null ? null : new HarmonyMethod(typeof(MapMod).GetMethod(pre, BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: post == null ? null : new HarmonyMethod(typeof(MapMod).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic)));
                }
                catch (Exception e) { Plugin.L.LogWarning($"Mapas: no se pudo parchear {t.Name}.{method}: {e.Message}"); }
            }

            // solo metodos con logica propia (no getters)
            Hook(typeof(MapCatalog), "Resolve", null, nameof(ResolvePost));
            Hook(typeof(MapCatalog), "FindByScene", null, nameof(FindByScenePost));
            Hook(typeof(GameSceneContext), "SetActiveMap", nameof(SetActivePre), null);
            foreach (var n in new[] { "IsFree", "IsOwned", "CanAfford" }) Hook(typeof(MapOwnership), n, null, nameof(OwnedPost));
            Hook(typeof(LobbyGameSettings), "IsOwnedByMe", null, nameof(OwnedPost));

            // ventana "Game Settings": asegurar el mapa en SU catalogo y reconstruir las filas si hace falta
            Hook(typeof(Caporta.DriveUpTogether.UI.GameSettingsPresenter), "Open", nameof(PresenterPre), null);
            Hook(typeof(Caporta.DriveUpTogether.UI.GameSettingsPresenter), "BuildMapRows", nameof(PresenterPre), null);
        }

        static void PresenterPre(Caporta.DriveUpTogether.UI.GameSettingsPresenter __instance)
        {
            try
            {
                if (!Is<Caporta.DriveUpTogether.UI.GameSettingsPresenter>(__instance)) return;
                var settings = __instance._settings;
                if (!Is<LobbyGameSettings>(settings)) return;
                var cat = settings.Catalog;
                if (!Is<MapCatalog>(cat)) return;
                bool added = Ensure(cat);
                ApplyCarClasses(cat);
                if (added && __instance._built)
                {
                    // las filas se hicieron sin nuestro mapa: borrarlas para que el juego las rehaga
                    __instance._built = false;
                    try { __instance._rows?.Clear(); } catch { }
                    try { __instance._mapList?.Clear(); } catch { }
                    Plugin.L.LogInfo("Mapas: lista de la ventana reconstruida");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Mapas (ventana): " + e.Message); }
        }

        static void ResolvePost(MapCatalog __instance, string __0, ref MapDefinition __result)
        {
            if (Def == null || !Is<MapCatalog>(__instance)) return;
            if (__0 == Id) __result = Def;
        }

        // nuestro mapa comparte escena con Playground: si estamos en el nuestro, devolver el nuestro
        static void FindByScenePost(MapCatalog __instance, string __0, ref MapDefinition __result)
        {
            if (Def == null || !IsActive || !Is<MapCatalog>(__instance)) return;
            if (__0 == BaseScene) __result = Def;
        }

        static void SetActivePre(MapDefinition __0)
        {
            if (__0 == null) { CurrentId = null; return; }
            if (IsOurs(__0)) { CurrentId = Id; return; }
            if (Is<MapDefinition>(__0)) CurrentId = __0.mapId;
        }

        static void OwnedPost(MapDefinition __0, ref bool __result)
        {
            if (IsOurs(__0)) __result = true;   // solo compara punteros: seguro aunque el metodo sea compartido
        }

        // llamado desde FlyBehaviour.Update: busca el catalogo y agrega el mapa si falta
        public static void Tick()
        {
            _searchTimer -= Time.unscaledDeltaTime;
            if (_searchTimer > 0) return;
            _searchTimer = 2f;
            try
            {
                foreach (var cat in Resources.FindObjectsOfTypeAll<MapCatalog>())
                    if (Is<MapCatalog>(cat)) { Ensure(cat); ApplyCarClasses(cat); }
            }
            catch (Exception e) { Plugin.L.LogWarning("Mapas: " + e.Message); }
        }

        // ---------- cualquier auto en cualquier mapa
        // El juego trata una lista allowedCarClasses VACIA como "All cars". Se vacia la lista de cada
        // mapa (y se guarda la original para poder volver). Solo cambia datos; no parchea funciones.
        static readonly Dictionary<IntPtr, List<string>> _origClasses = new();
        public static string CarClassesInfo = "Todos los mapas muestran \"All cars\".";
        public static void ForceRefresh() => _searchTimer = 0;

        static void ApplyCarClasses(MapCatalog cat)
        {
            var maps = cat.maps;
            if (maps == null) return;
            for (int i = 0; i < maps.Count; i++)
            {
                var m = maps[i];
                if (!Is<MapDefinition>(m)) continue;
                var l = m.allowedCarClasses;
                if (l == null) continue;
                if (!_origClasses.ContainsKey(m.Pointer))
                {
                    var copy = new List<string>();
                    for (int k = 0; k < l.Count; k++) copy.Add(l[k]);
                    _origClasses[m.Pointer] = copy;
                }
                if (Settings.AnyCarAnyMap.Value)
                {
                    if (l.Count > 0) l.Clear();          // vacia = "All cars"
                }
                else if (l.Count == 0 && _origClasses.TryGetValue(m.Pointer, out var orig) && orig.Count > 0)
                {
                    foreach (var c in orig) l.Add(c);    // volver a la lista original
                }
            }
            CarClassesInfo = Settings.AnyCarAnyMap.Value
                ? "Todos los mapas muestran \"All cars\"."
                : "Cada mapa usa sus autos originales.";
        }

        // devuelve true si lo acaba de agregar a este catalogo
        static bool Ensure(MapCatalog cat)
        {
            var list = cat.maps;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (IsOurs(list[i])) return false;   // ya esta

            // elegir la base: Playground si existe, si no el mapa por defecto
            MapDefinition baseMap = null;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (!Is<MapDefinition>(m)) continue;
                if (!_logged) Plugin.L.LogInfo($"Mapa del juego: id={m.mapId} nombre={m.displayName} escena={m.sceneName} precio={m.price}");
                string all = ((m.displayName ?? "") + " " + (m.sceneName ?? "") + " " + (m.mapId ?? "")).ToLowerInvariant();
                if (baseMap == null && all.Contains("playground")) baseMap = m;
            }
            _logged = true;
            if (baseMap == null && Is<MapDefinition>(cat.defaultMap)) baseMap = cat.defaultMap;
            if (baseMap == null)
                for (int i = 0; i < list.Count && baseMap == null; i++) if (Is<MapDefinition>(list[i])) baseMap = list[i];
            if (baseMap == null || string.IsNullOrEmpty(baseMap.sceneName)) return false;

            if (Def == null)
            {
                Def = ScriptableObject.CreateInstance<MapDefinition>();
                Def.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Def.name = Id;
                Def.mapId = Id;
                Def.displayName = DisplayName;
                Def.sceneName = baseMap.sceneName;
                Def.descriptionKey = baseMap.descriptionKey;
                Def.finishAltitude = baseMap.finishAltitude;
                Def.hudContrastFlip = baseMap.hudContrastFlip;
                Def.drawDistance = baseMap.drawDistance;
                Def.allowedCarClasses = baseMap.allowedCarClasses;
                Def.chapters = baseMap.chapters;
                Def.price = 0;
                Def.thumbnail = MakeThumbnail() ?? baseMap.thumbnail;
                BaseScene = baseMap.sceneName;
            }
            list.Add(Def);
            Plugin.L.LogInfo($"Mapa '{DisplayName}' agregado a un catalogo (base: {baseMap.displayName} / {BaseScene})");
            return true;
        }

        static Sprite MakeThumbnail()
        {
            try
            {
                const int W = 256, H = 144;
                var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        // cuadricula en perspectiva: lineas que se juntan hacia el horizonte
                        float v = y / (float)H;
                        Color c = v > 0.55f ? new Color(0.06f, 0.065f, 0.07f) : new Color(0.40f, 0.41f, 0.43f);
                        if (v <= 0.55f)
                        {
                            float depth = 1f / (0.56f - v);
                            float gx = (x - W / 2f) / W * depth * 2f;
                            float gz = depth * 0.6f;
                            if (Mathf.Abs(gx - Mathf.Round(gx)) < 0.03f * depth || Mathf.Abs(gz - Mathf.Round(gz)) < 0.03f * depth)
                                c = new Color(0.26f, 0.27f, 0.29f);
                        }
                        t.SetPixel(x, y, c);
                    }
                t.Apply();
                t.hideFlags = HideFlags.HideAndDontSave;
                var s = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f));
                s.hideFlags = HideFlags.HideAndDontSave;
                return s;
            }
            catch { return null; }
        }
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
        public static GUIStyle Window, Title, Sub, Section, Label, Hint, Warn, Tab, TabOn, Primary, Key, Field, CloseBtn, ChipStyle;
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
            Warn = new GUIStyle(skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, wordWrap = true };
            Warn.normal.textColor = new Color(1f, 0.45f, 0.4f);

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
