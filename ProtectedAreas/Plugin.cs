using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Entities;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Util.Commands;

namespace ProtectedAreas
{
    public enum DamageProtectionMode
    {
        /// <summary>Estruturas da area ficam imunes a dano enquanto o dono esta desconectado.</summary>
        OwnerOffline,
        Always,
        Never
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mooka.stationeers.protectedareas";
        public const string PluginName = "ProtectedAreas";
        public const string PluginVersion = "1.4.0";

        public static ManualLogSource Log;

        public static ConfigEntry<float> DefaultRadius;
        public static ConfigEntry<float> MaxRadius;
        public static ConfigEntry<int> MaxAreasPerPlayer;
        public static ConfigEntry<bool> AdminOnlyCreate;
        public static ConfigEntry<float> SpawnProtectionDistance;
        public static ConfigEntry<DamageProtectionMode> DamageProtection;
        public static ConfigEntry<bool> ProtectInteractions;
        public static ConfigEntry<float> TrespassKickSeconds;
        public static ConfigEntry<float> ExplosionBufferBlocks;
        public static ConfigEntry<bool> AdminsBypass;
        public static ConfigEntry<string> AdminSteamIds;

        /// <summary>Liga/desliga o desenho dos limites das areas (somente no host / singleplayer).</summary>
        public static bool ShowAreas;

        /// <summary>Quantidade de linhas desenhadas na ultima reconstrucao (diagnostico).</summary>
        public static int LineCount { get; private set; }

        private static bool _patched;

        private float _nextFlush;
        private float _nextTrespass;
        private bool _syncRequested;
        private GameObject _linesRoot;
        private Material _lineMaterial;
        private int _drawnVersion = -1;
        private ulong _drawnFor;

        private void Awake()
        {
            Log = Logger;

            // Secao "Rules" (a antiga "Areas" da versao 1.x e ignorada, para os novos padroes valerem).
            DefaultRadius = Config.Bind("Rules", "DefaultRadius", 15f,
                "Radius (half the cube side, in meters) used when 'create' is given no radius. 15 = a 30x30 area.");
            MaxRadius = Config.Bind("Rules", "MaxRadius", 15f,
                "Maximum radius regular players may use. 15 = a 30x30 area. Admins have no limit.");
            MaxAreasPerPlayer = Config.Bind("Rules", "MaxAreasPerPlayer", 1,
                "How many areas each player may own (0 = unlimited). Admins have no limit.");
            AdminOnlyCreate = Config.Bind("Rules", "AdminOnlyCreate", false,
                "If true, only admins can create areas.");
            SpawnProtectionDistance = Config.Bind("Rules", "SpawnProtectionDistance", 100f,
                "Regular players cannot create an area closer than this (meters) to a world start/respawn location. 0 = off.");
            DamageProtection = Config.Bind("Rules", "DamageProtection", DamageProtectionMode.OwnerOffline,
                "When structures inside an area are immune to damage (explosions, fire, pressure): OwnerOffline, Always or Never.");
            ProtectInteractions = Config.Bind("Rules", "ProtectInteractions", true,
                "If true, unauthorized players cannot use doors, levers, valves, switches, slots or containers inside an area.");
            ExplosionBufferBlocks = Config.Bind("Rules", "ExplosionBufferBlocks", 10f,
                "Explosions (grenades, explosives, canisters, furnaces...) do nothing inside an area or within this many blocks of it (1 block = 2 m), unless the exploding object belongs to a player authorized in that area. -1 = off.");
            TrespassKickSeconds = Config.Bind("Rules", "TrespassKickSeconds", 30f,
                "Seconds an unauthorized player may stay inside someone else's area after being warned. When time is up they are moved out (if they have the mod) or disconnected from the server. 0 = off.");
            AdminsBypass = Config.Bind("Admins", "AdminsBypass", true,
                "If true, admins ignore every restriction except the one against overlapping areas.");
            AdminSteamIds = Config.Bind("Admins", "AdminSteamIds", "",
                "Comma-separated SteamID64 list of extra admins. The host is always an admin.");

            if (_patched)
            {
                return;
            }
            _patched = true;

            try
            {
                Patches.Apply(new Harmony(PluginGuid));
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to apply patches: {ex}");
            }
            try
            {
                CommandLine.AddCommand("area", new AreaCommand());
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to register the 'area' command: {ex}");
            }
            GameManager.OnGameStateChange += AreaStore.OnGameStateChange;

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            try
            {
                GameState state = GameManager.GameState;
                bool inWorld = state == GameState.Running || state == GameState.Paused;

                if (inWorld && Guard.Enforcing && Time.unscaledTime >= _nextFlush)
                {
                    _nextFlush = Time.unscaledTime + 2f;
                    AreaStore.Flush();
                    Guard.RefreshOwnersOnline();
                }

                if (!inWorld || !Guard.Enforcing)
                {
                    Trespass.Reset();
                }
                else if (state == GameState.Running && Time.unscaledTime >= _nextTrespass)
                {
                    _nextTrespass = Time.unscaledTime + 1f;
#if SELFTEST
                    if (!SelfTest.Active)
#endif
                    Trespass.Tick(Time.unscaledTime);
                }

                UpdateNetwork(inWorld);
                UpdateLines(inWorld && ShowAreas && !GameManager.IsBatchMode);
#if SELFTEST
                SelfTest.Tick();
#endif
            }
            catch (Exception ex)
            {
                // Nunca deixa um erro visual derrubar o Update a cada frame.
                ShowAreas = false;
                Log.LogError($"Error in Update (boundary display turned off): {ex}");
            }
        }

        // ---------------------------------------------------------------- multiplayer

        private void UpdateNetwork(bool inWorld)
        {
            if (Guard.Enforcing)
            {
                // Host / singleplayer.
                _syncRequested = false;
                if (inWorld)
                {
                    Sync.ServerTick();
                }
                else
                {
                    Sync.Reset();
                }
                return;
            }
            // Cliente: nunca envia nada por conta propria. So pede as areas (uma unica vez por sessao)
            // depois que o host provou ter o mod, ao mandar uma mensagem "[Area]" para este jogador.
            // Em servidores sem o mod, nada e enviado ao chat.
            if (!inWorld)
            {
                _syncRequested = false;
                Sync.HostHasMod = false;
                RemoteView.Clear();
                return;
            }
            if (_syncRequested || !Sync.HostHasMod || (object)Human.LocalHuman == null)
            {
                return;
            }
            _syncRequested = true;
            Sync.SendRequest();
        }

        // ---------------------------------------------------------------- visualizacao dos limites

        private void UpdateLines(bool visible)
        {
            if (!visible)
            {
                if (_linesRoot != null && _linesRoot.activeSelf)
                {
                    _linesRoot.SetActive(false);
                }
                return;
            }
            bool enforcing = Guard.Enforcing;
            ulong localId = Guard.IdOf(Human.LocalHuman);
            int version = enforcing ? AreaStore.Version : -1 - RemoteView.Version;
            if (_linesRoot == null || _drawnVersion != version || _drawnFor != localId)
            {
                List<RemoteView.Entry> areas;
                if (enforcing)
                {
                    areas = new List<RemoteView.Entry>();
                    foreach (Area area in AreaStore.Snapshot())
                    {
                        areas.Add(new RemoteView.Entry { Area = area, Member = area.IsMember(localId) });
                    }
                }
                else
                {
                    areas = RemoteView.Snapshot();
                }
                _drawnVersion = enforcing ? AreaStore.Version : -1 - RemoteView.Version;
                _drawnFor = localId;
                Rebuild(areas);
            }
            if (!_linesRoot.activeSelf)
            {
                _linesRoot.SetActive(true);
            }
        }

        private void Rebuild(List<RemoteView.Entry> areas)
        {
            if (_linesRoot != null)
            {
                Destroy(_linesRoot);
            }
            _linesRoot = new GameObject("ProtectedAreasLines");
            DontDestroyOnLoad(_linesRoot);

            if (_lineMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Log.LogWarning("Shader for drawing boundaries not found.");
                    return;
                }
                _lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            foreach (RemoteView.Entry entry in areas)
            {
                Area area = entry.Area;
                Color color = entry.Member ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(1f, 0.25f, 0.2f, 1f);
                Vector3 c = area.Center;
                float r = area.Radius;
                Vector3[] v = new Vector3[8];
                for (int i = 0; i < 8; i++)
                {
                    v[i] = c + new Vector3((i & 1) == 0 ? -r : r, (i & 2) == 0 ? -r : r, (i & 4) == 0 ? -r : r);
                }
                // 12 arestas do cubo: pares de vertices que diferem em exatamente um eixo.
                for (int i = 0; i < 8; i++)
                {
                    for (int bit = 1; bit <= 4; bit <<= 1)
                    {
                        if ((i & bit) == 0)
                        {
                            AddLine(v[i], v[i | bit], color);
                        }
                    }
                }
                // Anel horizontal na altura do centro, para o limite ser visivel do chao.
                Vector3 a = c + new Vector3(-r, 0f, -r);
                Vector3 b = c + new Vector3(r, 0f, -r);
                Vector3 d = c + new Vector3(r, 0f, r);
                Vector3 e = c + new Vector3(-r, 0f, r);
                AddLine(a, b, color);
                AddLine(b, d, color);
                AddLine(d, e, color);
                AddLine(e, a, color);
            }
            LineCount = _linesRoot.transform.childCount;
        }

        private void AddLine(Vector3 from, Vector3 to, Color color)
        {
            GameObject go = new GameObject("edge");
            go.transform.SetParent(_linesRoot.transform, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = _lineMaterial;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.startWidth = 0.12f;
            line.endWidth = 0.12f;
            line.startColor = color;
            line.endColor = color;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
        }
    }
}
