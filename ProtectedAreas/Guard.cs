using System;
using System.Collections.Generic;
using System.Diagnostics;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using UnityEngine;

namespace ProtectedAreas
{
    /// <summary>Regras de permissao: quem pode modificar o que, e os avisos ao jogador.</summary>
    public static class Guard
    {
        public const string Tag = "[Area]";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly Dictionary<ulong, long> LastNotice = new Dictionary<ulong, long>();

        /// <summary>Quantas acoes foram bloqueadas nesta sessao (diagnostico).</summary>
        public static int DeniedCount { get; private set; }

        /// <summary>So o host / singleplayer aplica as regras. Clientes apenas enviam os pedidos ao servidor.</summary>
        public static bool Enforcing => GameManager.RunSimulation;

        public static bool IsLocal(Human human)
        {
            return (object)human != null && !GameManager.IsBatchMode && human == Human.LocalHuman;
        }

        public static ulong IdOf(Human human)
        {
            if ((object)human == null)
            {
                return 0UL;
            }
            Brain brain = human.OrganBrain;
            if ((object)brain != null && brain.ClientId != 0UL)
            {
                return brain.ClientId;
            }
            if (human.OwnerClientId != 0UL)
            {
                return human.OwnerClientId;
            }
            return IsLocal(human) ? NetworkManager.LocalClientId : 0UL;
        }

        /// <summary>Somente para o autoteste: faz o host ser tratado como jogador comum.</summary>
        public static bool TestNoAdmin;

        /// <summary>Menor SteamID64 valido de conta individual; abaixo disso o "dono" de uma estrutura nao e um SteamID.</summary>
        public const ulong MinSteamId = 76561197960265728UL;

        private static readonly HashSet<ulong> AdminIds = new HashSet<ulong>();
        private static string _adminIdsSource;

        /// <summary>
        /// Admins: o host (ou o jogador em singleplayer), o console do servidor dedicado
        /// e os SteamIDs listados em AdminSteamIds na configuracao.
        /// </summary>
        public static bool IsAdmin(Human human)
        {
            if (TestNoAdmin)
            {
                return false;
            }
            if ((object)human == null)
            {
                return true; // console do servidor dedicado
            }
            if (IsLocal(human))
            {
                return true;
            }
            return IsAdminId(IdOf(human));
        }

        public static bool IsAdminId(ulong id)
        {
            if (TestNoAdmin || id == 0UL)
            {
                return false;
            }
            string source = Plugin.AdminSteamIds.Value ?? string.Empty;
            lock (AdminIds)
            {
                if (source != _adminIdsSource)
                {
                    _adminIdsSource = source;
                    AdminIds.Clear();
                    foreach (string part in source.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (ulong.TryParse(part.Trim(), out ulong parsed))
                        {
                            AdminIds.Add(parsed);
                        }
                    }
                }
                return AdminIds.Contains(id);
            }
        }

        /// <summary>True se o jogador pode modificar/interagir dentro da area.</summary>
        public static bool IsAllowed(Area area, Human human)
        {
            return area.IsMember(IdOf(human)) || (Plugin.AdminsBypass.Value && IsAdmin(human));
        }

        /// <summary>Retorna a area que bloqueia o jogador nessa posicao, ou null se ele pode modificar.</summary>
        public static Area FindBlocking(Human human, Vector3 position)
        {
            if (!Enforcing || AreaStore.Count == 0)
            {
                return null;
            }
            if (Plugin.AdminsBypass.Value && IsAdmin(human))
            {
                return null;
            }
            return AreaStore.FirstContaining(position, IdOf(human));
        }

        /// <summary>
        /// Como FindBlocking, mas tambem serve em clientes com o mod: para o jogador local usa a copia
        /// das areas enviada pelo host (so para antecipar na interface o que o host vai recusar).
        /// </summary>
        public static Area FindBlockingFor(Human human, Vector3 position)
        {
            if (Enforcing)
            {
                return FindBlocking(human, position);
            }
            return IsLocal(human) ? RemoteView.FirstBlocking(position) : null;
        }

        /// <summary>
        /// Coisas cujo uso e protegido dentro de uma area: estruturas e objetos soltos (caixas, tanques...).
        /// Jogadores e o que eles carregam ficam de fora.
        /// </summary>
        public static bool IsProtectable(Thing thing)
        {
            if ((object)thing == null || thing is Entity)
            {
                return false;
            }
            return (object)thing.RootParentHuman == null;
        }

        /// <summary>
        /// Para pedidos de clientes que nao dizem quem os enviou: bloqueia quando ha um jogador nao
        /// autorizado perto do alvo e nenhum autorizado por perto.
        /// </summary>
        public static Area FindBlockingByProximity(Vector3 position, out Human culprit)
        {
            culprit = null;
            if (!Enforcing || AreaStore.Count == 0)
            {
                return null;
            }
            Area area = AreaStore.FirstContaining(position, null);
            if (area == null)
            {
                return null;
            }
            const float range = 10f;
            bool allowedNearby = false;
            foreach (Human human in Human.AllHumans)
            {
                if ((object)human == null || (human.Position - position).sqrMagnitude > range * range)
                {
                    continue;
                }
                if (IsAllowed(area, human))
                {
                    allowedNearby = true;
                }
                else
                {
                    culprit = human;
                }
            }
            return (object)culprit != null && !allowedNearby ? area : null;
        }

        /// <summary>True se o jogador com esse SteamID esta conectado (ou e o host local).</summary>
        public static bool IsOnline(ulong id)
        {
            if (id == 0UL)
            {
                return false;
            }
            if (!GameManager.IsBatchMode && IdOf(Human.LocalHuman) == id)
            {
                return true;
            }
            return Client.Find(id) != null;
        }

        /// <summary>Chamado na thread principal: atualiza quais donos de area estao online.</summary>
        public static void RefreshOwnersOnline()
        {
            foreach (Area area in AreaStore.Snapshot())
            {
                area.OwnerOnline = IsOnline(area.OwnerId);
            }
        }

        /// <summary>Move o jogador local para a posicao (usado ao ser expulso de uma area).</summary>
        public static void TeleportLocal(Vector3 position)
        {
            Human local = Human.LocalHuman;
            if ((object)local == null)
            {
                return;
            }
            local.Transform.position = position;
            Rigidbody body = local.RigidBody;
            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>Retorna a primeira area que contem a posicao (para acoes cujo autor nao pode ser identificado).</summary>
        public static Area FindAny(Vector3 position)
        {
            if (!Enforcing || AreaStore.Count == 0)
            {
                return null;
            }
            return AreaStore.FirstContaining(position, null);
        }

        public static string DeniedText(Area area)
        {
            return $"Protected area '{area.Name}' (owner: {area.OwnerName}). You are not authorized to modify here.";
        }

        /// <summary>
        /// Para a interface do jogador local (cursor, tooltip): no host usa as areas reais,
        /// em clientes usa a copia enviada pelo host.
        /// </summary>
        public static Area FindBlockingLocal(Vector3 position)
        {
            if (Enforcing)
            {
                Human human = Human.LocalHuman;
                return (object)human == null ? null : FindBlocking(human, position);
            }
            return RemoteView.FirstBlocking(position);
        }

        /// <summary>Avisa o jogador que a acao foi bloqueada (no maximo um aviso a cada 3 segundos por jogador).</summary>
        public static void NotifyDenied(Human human, Area area)
        {
            DeniedCount++;
            if ((object)human == null)
            {
                return;
            }
            ulong id = IdOf(human);
            long now = Clock.ElapsedMilliseconds;
            lock (LastNotice)
            {
                if (LastNotice.TryGetValue(id, out long last) && now - last < 3000)
                {
                    return;
                }
                LastNotice[id] = now;
            }
            Tell(human, DeniedText(area));
        }

        /// <summary>Envia uma mensagem para um jogador: console local para o host, chat para clientes.</summary>
        public static void Tell(Human human, string message)
        {
            try
            {
                if ((object)human == null || IsLocal(human) || !NetworkManager.IsServer)
                {
                    ConsoleWindow.Print(Tag + " " + message, ConsoleColor.Yellow);
                    return;
                }
                ulong id = IdOf(human);
                Client client = Client.Find(id);
                if (client == null)
                {
                    return;
                }
                NetworkServer.SendToClient(new ChatMessage
                {
                    HumanId = -1L,
                    DisplayName = Tag,
                    ChatText = message
                }, NetworkChannel.GeneralTraffic, client);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to send message: {ex.Message}");
            }
        }
    }
}
