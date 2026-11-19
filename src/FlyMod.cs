// FlyMod v2 para Drive Up Together (BepInEx 6 IL2CPP).
// Menu movible (Insert por defecto): fly, front flip, boost, potencia, velocidad maxima, traccion y teclas.
// En cuanto se usa cualquier truco, se bloquean hasta cerrar el juego: subida al leaderboard,
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
using NWH.WheelController3D;

namespace DutFly
{
    [BepInPlugin("local.dut.flymod", "FlyMod", "2.0.1")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource L;
        internal static ConfigFile Cfg;
        public static bool UsedThisSession;
        public static bool Unlimited; // desactiva los limitadores de velocidad del juego

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

            L.LogInfo($"FlyMod 2 cargado. {Settings.P(Settings.KeyMenu)} = menu.");
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
        public static ConfigEntry<bool> FlipBackwards;
        public static ConfigEntry<float> BoostAccel;
        public static ConfigEntry<float> PowerMult, SpeedMult, GripMult;
        public static ConfigEntry<bool> NoLimiters;
        public static ConfigEntry<float> WinX, WinY;

        public static ConfigEntry<string> KeyMenu, KeyFly, KeyUp, KeyDown, KeyFast, KeyFlip, KeyBoost;
        public static readonly List<(string label, ConfigEntry<string> entry)> Binds = new();

        public static void Bind(ConfigFile c)
        {
            FlySpeed = c.Bind("Fly", "Velocidad", 25f, "Velocidad de vuelo normal (m/s)");
            FlyFastSpeed = c.Bind("Fly", "VelocidadRapida", 70f, "Velocidad de vuelo con la tecla rapida (m/s)");
            FlipSpin = c.Bind("Flip", "VelocidadGiro", 8f, "Velocidad de giro de la voltereta (rad/s)");
            FlipHop = c.Bind("Flip", "Salto", 8f, "Impulso hacia arriba al empezar la voltereta (m/s)");
            FlipTurns = c.Bind("Flip", "Vueltas", 1f, "Numero de vueltas completas");
            FlipBackwards = c.Bind("Flip", "HaciaAtras", false, "Back flip en vez de front flip");
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
        CursorLockMode _prevLock; bool _prevVisible;

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
            _win = new Rect(Settings.WinX.Value, Settings.WinY.Value, 360, 470);
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

            _tuneTimer -= Time.unscaledDeltaTime;
            if (_tuneTimer <= 0) { _tuneTimer = 0.25f; ApplyTuning(); }

            bool speedCheats = Settings.PowerMult.Value > 1.001f || Settings.SpeedMult.Value > 1.001f
                               || Held(Settings.KeyBoost) || _flipLeft > 0;
            Plugin.Unlimited = _flying || (Settings.NoLimiters.Value && speedCheats);
        }

        void SetMenu(bool open)
        {
            if (open == _menuOpen) return;
            _menuOpen = open;
            if (open) { _prevLock = Cursor.lockState; _prevVisible = Cursor.visible; }
            else
            {
                Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
                Settings.WinX.Value = _win.x; Settings.WinY.Value = _win.y;
                Plugin.Cfg.Save();
            }
        }

        void ToggleFly()
        {
            if (!_flying)
            {
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
            if (_flying) return;
            _rb = FindLocalBody();
            if (_rb == null) return;
            float spin = Mathf.Max(0.5f, Settings.FlipSpin.Value);
            _flipLeft = Mathf.Max(0.1f, Settings.FlipTurns.Value) * 2f * Mathf.PI / spin;
            _flipAxisLocal = Settings.FlipBackwards.Value ? Vector3.left : Vector3.right;
            _rb.linearVelocity += Vector3.up * Settings.FlipHop.Value;
            Plugin.MarkUsed("flip");
        }

        void FixedUpdate()
        {
            if (_flying) FlyStep();
            else if (_flipLeft > 0) FlipStep();

            if (!_flying && Held(Settings.KeyBoost))
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
            if (_flipLeft <= 0) _rb.angularVelocity = Vector3.zero;
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

        // ---------- GUI
        void OnGUI()
        {
            bool boosting = Held(Settings.KeyBoost) && !_flying;
            string status = _flying ? "FLY ON" : boosting ? "BOOST" : null;
            if (boosting && !Plugin.UsedThisSession) GUI.Label(new Rect(10, 10, 200, 25), "BOOST");
            if (Plugin.UsedThisSession)
                GUI.Label(new Rect(10, 10, 600, 25), (status != null ? status + "  |  " : "") + "Leaderboard/logros bloqueados (reinicia el juego para jugar legal)");
            if (_menuOpen) _win = GUI.Window(73195, _win, _winFn, "FlyMod  (" + Settings.P(Settings.KeyMenu) + " cerrar)");
        }

        float _y;
        const float X = 10, W = 340, LH = 22;

        void DrawWindow(int id)
        {
            _y = 24;
            float tw = (W - 10) / 3f;
            if (GUI.Button(new Rect(X, _y, tw - 4, LH), _tab == 0 ? "[ Fly / Flip ]" : "Fly / Flip")) _tab = 0;
            if (GUI.Button(new Rect(X + tw, _y, tw - 4, LH), _tab == 1 ? "[ Coche ]" : "Coche")) _tab = 1;
            if (GUI.Button(new Rect(X + 2 * tw, _y, tw - 4, LH), _tab == 2 ? "[ Teclas ]" : "Teclas")) _tab = 2;
            _y += LH + 8;

            if (_tab == 0)
            {
                bool nf = GUI.Toggle(new Rect(X, _y, W, LH), _flying, " Fly activo (" + Settings.P(Settings.KeyFly) + ")");
                if (nf != _flying) ToggleFly();
                _y += LH + 2;
                Slider("Velocidad fly", Settings.FlySpeed, 1, 200);
                Slider("Velocidad fly rapida", Settings.FlyFastSpeed, 1, 500);
                _y += 6;
                GUI.Label(new Rect(X, _y, W, LH), "Voltereta (" + Settings.P(Settings.KeyFlip) + ")"); _y += LH;
                Settings.FlipBackwards.Value = GUI.Toggle(new Rect(X, _y, W, LH), Settings.FlipBackwards.Value, " Hacia atras (back flip)"); _y += LH + 2;
                Slider("Velocidad de giro", Settings.FlipSpin, 1, 40);
                Slider("Salto", Settings.FlipHop, 0, 50);
                Slider("Vueltas", Settings.FlipTurns, 0.5f, 10);
                if (GUI.Button(new Rect(X, _y, W, LH), "Hacer voltereta ahora")) StartFlip();
                _y += LH;
            }
            else if (_tab == 1)
            {
                Slider("Potencia del motor (x)", Settings.PowerMult, 0.5f, 50);
                Slider("Velocidad maxima (x)", Settings.SpeedMult, 0.5f, 10);
                Slider("Traccion / agarre (x)", Settings.GripMult, 0.2f, 20);
                Slider("Boost (" + Settings.P(Settings.KeyBoost) + ", m/s2)", Settings.BoostAccel, 0, 300);
                Settings.NoLimiters.Value = GUI.Toggle(new Rect(X, _y, W, LH), Settings.NoLimiters.Value, " Quitar limitadores de velocidad del juego"); _y += LH + 4;
                GUI.Label(new Rect(X, _y, W, LH * 2), "Escribe en la caja para pasar del maximo del slider."); _y += LH * 2;
                if (GUI.Button(new Rect(X, _y, W, LH), "Restaurar coche original (x1)"))
                {
                    Settings.PowerMult.Value = Settings.SpeedMult.Value = Settings.GripMult.Value = 1f;
                    _txt.Clear();
                }
                _y += LH;
            }
            else
            {
                foreach (var (label, entry) in Settings.Binds)
                {
                    GUI.Label(new Rect(X, _y, 170, LH), label);
                    string txt = _listening == entry ? "pulsa una tecla... (Esc)" : Settings.P(entry);
                    if (GUI.Button(new Rect(X + 175, _y, W - 175, LH), txt)) _listening = entry;
                    _y += LH + 4;
                }
                GUI.Label(new Rect(X, _y, W, LH), "WASD siempre mueve en fly."); _y += LH;
            }

            _win.height = _y + 12;
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        void Slider(string label, ConfigEntry<float> e, float min, float max)
        {
            GUI.Label(new Rect(X, _y, W, LH), label); _y += LH - 2;
            string key = e.Definition.Key + e.Definition.Section;
            if (!_txt.TryGetValue(key, out var buf)) buf = Fmt(e.Value);

            float v = GUI.HorizontalSlider(new Rect(X, _y + 5, W - 80, LH), Mathf.Clamp(e.Value, min, max), min, max);
            if (Mathf.Abs(v - Mathf.Clamp(e.Value, min, max)) > 0.0001f) { e.Value = (float)Math.Round(v, 2); buf = Fmt(e.Value); }

            string nb = GUI.TextField(new Rect(X + W - 72, _y, 72, LH), buf);
            if (nb != buf)
            {
                buf = nb;
                if (float.TryParse(nb.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float typed) && typed >= 0)
                    e.Value = typed; // sin limite superior
            }
            _txt[key] = buf;
            _y += LH + 4;
        }

        static string Fmt(float f) => f.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
