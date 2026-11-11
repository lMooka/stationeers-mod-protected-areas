using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Serialization;

namespace ProtectedAreas
{
    /// <summary>
    /// Guarda as areas do mundo atual. O arquivo fica na pasta do save:
    /// saves/&lt;NomeDaEstacao&gt;/ProtectedAreas.xml
    /// </summary>
    public static class AreaStore
    {
        public const string FileName = "ProtectedAreas.xml";

        private static readonly object Sync = new object();
        private static List<Area> _areas = new List<Area>();
        private static string _loadedName;
        private static bool _needsLoad = true;
        private static bool _dirty;

        /// <summary>Incrementado a cada alteracao; usado pelo visualizador para saber quando redesenhar.</summary>
        public static int Version { get; private set; }

        /// <summary>Copia da lista atual de areas (sempre do mundo carregado).</summary>
        public static List<Area> Snapshot()
        {
            lock (Sync)
            {
                EnsureLoaded();
                return new List<Area>(_areas);
            }
        }

        public static int Count
        {
            get
            {
                lock (Sync)
                {
                    EnsureLoaded();
                    return _areas.Count;
                }
            }
        }

        public static Area Find(string name)
        {
            lock (Sync)
            {
                EnsureLoaded();
                foreach (Area area in _areas)
                {
                    if (string.Equals(area.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return area;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Primeira area que contem a posicao. Com skipMembersOf != null, ignora as areas em que esse id e autorizado.
        /// </summary>
        public static Area FirstContaining(UnityEngine.Vector3 position, ulong? skipMembersOf)
        {
            lock (Sync)
            {
                EnsureLoaded();
                for (int i = 0; i < _areas.Count; i++)
                {
                    Area area = _areas[i];
                    if (!area.Contains(position))
                    {
                        continue;
                    }
                    if (skipMembersOf.HasValue && area.IsMember(skipMembersOf.Value))
                    {
                        continue;
                    }
                    return area;
                }
                return null;
            }
        }

        /// <summary>
        /// Copia imutavel das areas, para consultas muito frequentes e vindas de outras threads (dano).
        /// Mantida pelo carregamento (feito na thread principal) e por cada alteracao.
        /// </summary>
        private static volatile Area[] _published = new Area[0];

        private static void Publish()
        {
            _published = _areas.ToArray();
        }

        /// <summary>True se estruturas nessa posicao estao imunes a dano agora.</summary>
        public static bool IsDamageProtected(UnityEngine.Vector3 position)
        {
            Area[] areas = _published;
            for (int i = 0; i < areas.Length; i++)
            {
                if (areas[i].Contains(position))
                {
                    return IsDamageProtected(areas[i]);
                }
            }
            return false;
        }

        /// <summary>True se alguma area imune a dano encosta na esfera (explosoes).</summary>
        public static bool AnyDamageProtectedIntersecting(UnityEngine.Vector3 center, float radius)
        {
            Area[] areas = _published;
            for (int i = 0; i < areas.Length; i++)
            {
                if (areas[i].Intersects(center, radius) && IsDamageProtected(areas[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Primeira area a ate <paramref name="buffer"/> metros da posicao em que esse id nao e autorizado.
        /// </summary>
        public static Area FirstNear(UnityEngine.Vector3 position, float buffer, ulong id)
        {
            Area[] areas = _published;
            for (int i = 0; i < areas.Length; i++)
            {
                Area area = areas[i];
                float reach = area.Radius + buffer;
                if (Math.Abs(position.x - area.X) <= reach
                    && Math.Abs(position.y - area.Y) <= reach
                    && Math.Abs(position.z - area.Z) <= reach
                    && !(id != 0UL && area.IsMember(id)))
                {
                    return area;
                }
            }
            return null;
        }

        public static bool IsDamageProtected(Area area)
        {
            switch (Plugin.DamageProtection.Value)
            {
                case DamageProtectionMode.Always:
                    return true;
                case DamageProtectionMode.Never:
                    return false;
                default:
                    return !area.OwnerOnline;
            }
        }

        public static void Add(Area area)
        {
            lock (Sync)
            {
                EnsureLoaded();
                _areas.Add(area);
                Changed();
            }
        }

        public static void Remove(Area area)
        {
            lock (Sync)
            {
                EnsureLoaded();
                _areas.Remove(area);
                Changed();
            }
        }

        /// <summary>Chamar depois de alterar os membros de uma area.</summary>
        public static void MarkChanged()
        {
            lock (Sync)
            {
                Changed();
            }
        }

        /// <summary>Chamado periodicamente: grava alteracoes pendentes (ex.: mundo novo que ainda nao tinha pasta de save).</summary>
        public static void Flush()
        {
            lock (Sync)
            {
                EnsureLoaded();
            }
        }

        public static void OnGameStateChange()
        {
            GameState state = GameManager.GameState;
            if (state == GameState.Running || state == GameState.Paused)
            {
                return;
            }
            // Saiu do mundo, esta carregando outro ou entrando em um servidor: descarta o que estava em memoria.
            lock (Sync)
            {
                _areas = new List<Area>();
                _loadedName = null;
                _needsLoad = true;
                _dirty = false;
                Version++;
                Publish();
            }
        }

        private static void Changed()
        {
            Publish();
            Version++;
            _dirty = true;
            TrySave();
        }

        private static string CurrentName()
        {
            try
            {
                XmlSaveLoad instance = XmlSaveLoad.Instance;
                string name = (object)instance == null ? null : instance.CurrentStationName;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }

        private static string DirectoryFor(string name)
        {
            return Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, name);
        }

        private static void EnsureLoaded()
        {
            string name = CurrentName();
            if (_needsLoad)
            {
                _needsLoad = false;
                _areas = Load(name);
                _loadedName = name;
                _dirty = false;
                Version++;
                Publish();
                return;
            }
            if (name != _loadedName)
            {
                // Primeiro save de um mundo novo, "salvar como" ou renomear: leva as areas junto.
                _loadedName = name;
                if (_areas.Count > 0)
                {
                    _dirty = true;
                }
            }
            if (_dirty)
            {
                TrySave();
            }
        }

        private static List<Area> Load(string name)
        {
            if (name == null)
            {
                return new List<Area>();
            }
            try
            {
                string path = Path.Combine(DirectoryFor(name), FileName);
                if (!File.Exists(path))
                {
                    return new List<Area>();
                }
                XmlSerializer serializer = new XmlSerializer(typeof(AreaFile));
                using (FileStream stream = File.OpenRead(path))
                {
                    AreaFile file = (AreaFile)serializer.Deserialize(stream);
                    List<Area> areas = file?.Areas ?? new List<Area>();
                    foreach (Area area in areas)
                    {
                        if (area.Members == null)
                        {
                            area.Members = new List<AreaMember>();
                        }
                    }
                    Plugin.Log.LogInfo($"{areas.Count} protected area(s) loaded from {path}");
                    return areas;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to load areas of '{name}': {ex}");
                return new List<Area>();
            }
        }

        private static void TrySave()
        {
            string name = _loadedName;
            if (name == null)
            {
                return;
            }
            try
            {
                string dir = DirectoryFor(name);
                if (!Directory.Exists(dir))
                {
                    // Mundo novo ainda nao salvo: fica pendente ate o primeiro save criar a pasta.
                    return;
                }
                string path = Path.Combine(dir, FileName);
                if (_areas.Count == 0 && !File.Exists(path))
                {
                    _dirty = false;
                    return;
                }
                string temp = path + ".tmp";
                XmlSerializer serializer = new XmlSerializer(typeof(AreaFile));
                using (FileStream stream = File.Create(temp))
                {
                    serializer.Serialize(stream, new AreaFile { Areas = _areas });
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to save areas of '{name}': {ex}");
            }
        }
    }
}
