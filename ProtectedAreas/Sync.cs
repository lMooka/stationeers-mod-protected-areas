using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace ProtectedAreas
{
    /// <summary>
    /// Multiplayer: o host aplica as regras; clientes que tambem tem o mod recebem uma copia das areas
    /// (somente leitura) para mostrar o cursor vermelho, o tooltip de bloqueio e os limites.
    ///
    /// O transporte usa o proprio chat do jogo, para nao exigir o mod em quem nao tem:
    ///  - o cliente com o mod envia "/area sync" uma unica vez, e so depois de receber do host uma
    ///    mensagem "[Area]" (aviso de bloqueio ou resposta de comando), que prova que o host tem o mod;
    ///    o host intercepta esse texto e nao repassa. Em servidores sem o mod nada e enviado;
    ///  - o host responde so para esse cliente com mensagens de chat cujo DisplayName e Sync.Tag,
    ///    que o mod do cliente intercepta e nao mostra.
    /// Clientes sem o mod nunca se inscrevem e portanto nunca recebem essas mensagens.
    /// </summary>
    public static class Sync
    {
        public const string Tag = "#PA1#";
        public const string RequestText = "/area sync";

        private const int MaxPayload = 900;

        private static readonly HashSet<ulong> Subscribers = new HashSet<ulong>();
        private static int _pushedVersion = -1;

        public static int SubscriberCount => Subscribers.Count;

        /// <summary>
        /// Cliente: true depois que o host desta sessao enviou alguma mensagem do mod (aviso de bloqueio ou
        /// resposta de comando). So entao o cliente pede a copia das areas.
        /// </summary>
        public static bool HostHasMod;

        /// <summary>Mensagens que descrevem todas as areas do ponto de vista do jogador <paramref name="id"/>.</summary>
        public static List<string> BuildMessages(List<Area> areas, ulong id)
        {
            List<string> lines = new List<string> { "B" };
            foreach (Area area in areas)
            {
                lines.Add(string.Join("|", "A",
                    Uri.EscapeDataString(area.Name ?? string.Empty),
                    Uri.EscapeDataString(area.OwnerName ?? string.Empty),
                    Number(area.X), Number(area.Y), Number(area.Z), Number(area.Radius),
                    area.IsMember(id) || (Plugin.AdminsBypass.Value && Guard.IsAdminId(id)) ? "1" : "0"));
            }
            lines.Add("E");

            // Agrupa as linhas em poucas mensagens, sem passar do tamanho maximo.
            List<string> messages = new List<string>();
            StringBuilder current = new StringBuilder();
            foreach (string line in lines)
            {
                if (current.Length > 0 && current.Length + 1 + line.Length > MaxPayload)
                {
                    messages.Add(current.ToString());
                    current.Length = 0;
                }
                if (current.Length > 0)
                {
                    current.Append('\n');
                }
                current.Append(line);
            }
            if (current.Length > 0)
            {
                messages.Add(current.ToString());
            }
            return messages;
        }

        private static string Number(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Host: um cliente com o mod pediu a lista de areas.</summary>
        public static void Subscribe(Human actor)
        {
            if (!NetworkManager.IsServer || (object)actor == null || Guard.IsLocal(actor))
            {
                return;
            }
            ulong id = Guard.IdOf(actor);
            if (id == 0UL)
            {
                return;
            }
            Subscribers.Add(id);
            SendTo(id);
        }

        public static bool IsSubscriber(ulong id)
        {
            return Subscribers.Contains(id);
        }

        /// <summary>
        /// Host: pede ao mod do cliente que mova o proprio jogador (a posicao de um jogador e controlada
        /// pelo cliente dele, entao o host nao consegue move-lo sozinho). Retorna false se o cliente nao tem o mod.
        /// </summary>
        public static bool SendTeleport(ulong id, Vector3 position)
        {
            if (!NetworkManager.IsServer || !Subscribers.Contains(id))
            {
                return false;
            }
            Client client = Client.Find(id);
            if (client == null)
            {
                return false;
            }
            NetworkServer.SendToClient(new ChatMessage
            {
                HumanId = -1L,
                DisplayName = Tag,
                ChatText = string.Join("|", "T", Number(position.x), Number(position.y), Number(position.z))
            }, NetworkChannel.GeneralTraffic, client);
            return true;
        }

        /// <summary>Cliente: mensagem escondida vinda do host (copia das areas ou pedido de teleporte).</summary>
        public static void ApplyFromHost(string payload)
        {
            if (payload != null && payload.StartsWith("T|", StringComparison.Ordinal))
            {
                string[] parts = payload.Split('|');
                if (parts.Length >= 4
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    Guard.TeleportLocal(new Vector3(x, y, z));
                }
                return;
            }
            RemoteView.Apply(payload);
        }

        /// <summary>Host: reenvia as areas aos inscritos sempre que algo muda.</summary>
        public static void ServerTick()
        {
            if (!NetworkManager.IsServer)
            {
                if (Subscribers.Count > 0)
                {
                    Subscribers.Clear();
                }
                return;
            }
            int version = AreaStore.Version;
            if (version == _pushedVersion)
            {
                return;
            }
            _pushedVersion = version;
            if (Subscribers.Count == 0)
            {
                return;
            }
            foreach (ulong id in new List<ulong>(Subscribers))
            {
                if (!SendTo(id))
                {
                    Subscribers.Remove(id);
                }
            }
        }

        public static void Reset()
        {
            Subscribers.Clear();
            _pushedVersion = -1;
        }

        private static bool SendTo(ulong id)
        {
            try
            {
                Client client = Client.Find(id);
                if (client == null)
                {
                    return false;
                }
                foreach (string payload in BuildMessages(AreaStore.Snapshot(), id))
                {
                    NetworkServer.SendToClient(new ChatMessage
                    {
                        HumanId = -1L,
                        DisplayName = Tag,
                        ChatText = payload
                    }, NetworkChannel.GeneralTraffic, client);
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to sync areas to client {id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Cliente: pede a lista de areas ao host.</summary>
        public static void SendRequest()
        {
            Human local = Human.LocalHuman;
            if (!NetworkManager.IsClient || (object)local == null)
            {
                return;
            }
            NetworkClient.SendToServer(new ChatMessage
            {
                ChatText = RequestText,
                DisplayName = local.DisplayName,
                HumanId = local.ReferenceId
            });
        }
    }

    /// <summary>Cliente: copia somente leitura das areas enviada pelo host.</summary>
    public static class RemoteView
    {
        public struct Entry
        {
            public Area Area;
            public bool Member;
        }

        private static List<Entry> _current = new List<Entry>();
        private static List<Entry> _pending;

        public static int Version { get; private set; }

        public static int Count => _current.Count;

        public static List<Entry> Snapshot()
        {
            return new List<Entry>(_current);
        }

        public static void Clear()
        {
            if (_current.Count > 0 || _pending != null)
            {
                _current = new List<Entry>();
                _pending = null;
                Version++;
            }
        }

        /// <summary>Primeira area que contem a posicao e na qual o jogador local nao e autorizado.</summary>
        public static Area FirstBlocking(Vector3 position)
        {
            List<Entry> list = _current;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Member && list[i].Area.Contains(position))
                {
                    return list[i].Area;
                }
            }
            return null;
        }

        public static void Apply(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return;
            }
            foreach (string line in payload.Split('\n'))
            {
                if (line == "B")
                {
                    _pending = new List<Entry>();
                }
                else if (line == "E")
                {
                    _current = _pending ?? new List<Entry>();
                    _pending = null;
                    Version++;
                }
                else if (line.StartsWith("A|", StringComparison.Ordinal))
                {
                    if (_pending == null)
                    {
                        _pending = new List<Entry>();
                    }
                    if (TryParse(line, out Entry entry))
                    {
                        _pending.Add(entry);
                    }
                }
            }
        }

        private static bool TryParse(string line, out Entry entry)
        {
            entry = default;
            string[] parts = line.Split('|');
            if (parts.Length < 8)
            {
                return false;
            }
            NumberStyles style = NumberStyles.Float;
            CultureInfo culture = CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[3], style, culture, out float x)
                || !float.TryParse(parts[4], style, culture, out float y)
                || !float.TryParse(parts[5], style, culture, out float z)
                || !float.TryParse(parts[6], style, culture, out float radius))
            {
                return false;
            }
            entry.Area = new Area
            {
                Name = Uri.UnescapeDataString(parts[1]),
                OwnerName = Uri.UnescapeDataString(parts[2]),
                X = x,
                Y = y,
                Z = z,
                Radius = radius
            };
            entry.Member = parts[7] == "1";
            return true;
        }
    }
}
