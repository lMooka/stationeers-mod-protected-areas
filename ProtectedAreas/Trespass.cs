using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace ProtectedAreas
{
    /// <summary>
    /// Invasao de area: quem entra na area de outro jogador recebe um aviso com contagem regressiva.
    /// Se nao sair a tempo e removido: teleportado para fora quando possivel (host e clientes com o mod),
    /// ou desconectado do servidor (clientes sem o mod, cuja posicao o servidor nao controla).
    /// Roda somente no host, uma vez por segundo.
    /// </summary>
    public static class Trespass
    {
        private class Entry
        {
            public Area Area;
            public float Deadline;
            public int LastWarned;
        }

        private static readonly Dictionary<ulong, Entry> Tracked = new Dictionary<ulong, Entry>();
        private static readonly int[] WarnAt = { 20, 10, 5 };

        public static int TrackedCount => Tracked.Count;

        public static void Reset()
        {
            Tracked.Clear();
        }

        /// <param name="now">Relogio em segundos (Time.unscaledTime no jogo).</param>
        public static void Tick(float now)
        {
            float limit = Plugin.TrespassKickSeconds.Value;
            if (limit <= 0f || !Guard.Enforcing || AreaStore.Count == 0)
            {
                if (Tracked.Count > 0)
                {
                    Tracked.Clear();
                }
                return;
            }

            HashSet<ulong> inside = new HashSet<ulong>();
            foreach (Human human in new List<Human>(Human.AllHumans))
            {
                if ((object)human == null || human.State != EntityState.Alive)
                {
                    continue; // mortos e inconscientes nao conseguem sair sozinhos
                }
                ulong id = Guard.IdOf(human);
                if (id == 0UL || !Guard.IsOnline(id))
                {
                    continue;
                }
                Area area = Guard.FindBlocking(human, human.Position);
                if (area == null)
                {
                    continue;
                }
                inside.Add(id);

                if (!Tracked.TryGetValue(id, out Entry entry) || !ReferenceEquals(entry.Area, area))
                {
                    entry = new Entry { Area = area, Deadline = now + limit, LastWarned = int.MaxValue };
                    Tracked[id] = entry;
                    Guard.Tell(human, $"You entered protected area '{area.Name}' (owner: {area.OwnerName}). Leave within {limit:0} seconds or you will be removed.");
                    continue;
                }

                float remaining = entry.Deadline - now;
                if (remaining <= 0f)
                {
                    Tracked.Remove(id);
                    inside.Remove(id);
                    Remove(human, id, area);
                    continue;
                }
                foreach (int mark in WarnAt)
                {
                    if (remaining <= mark && entry.LastWarned > mark && mark < limit)
                    {
                        entry.LastWarned = mark;
                        Guard.Tell(human, $"Leave protected area '{area.Name}' now: {mark} seconds left.");
                        break;
                    }
                }
            }

            // Quem saiu (ou desconectou, ou foi autorizado) deixa de ser acompanhado.
            if (Tracked.Count > inside.Count)
            {
                foreach (ulong id in new List<ulong>(Tracked.Keys))
                {
                    if (!inside.Contains(id))
                    {
                        Tracked.Remove(id);
                    }
                }
            }
        }

        private static void Remove(Human human, ulong id, Area area)
        {
            try
            {
                Vector3 target = Commands.ExitPoint(area, human.Position);
                if (Guard.IsLocal(human))
                {
                    Guard.TeleportLocal(target);
                    Guard.Tell(human, $"You were moved out of protected area '{area.Name}'.");
                    return;
                }
                if (Sync.SendTeleport(id, target))
                {
                    Guard.Tell(human, $"You were moved out of protected area '{area.Name}'.");
                    return;
                }
                Client client = Client.Find(id);
                if (client != null)
                {
                    Guard.Tell(human, $"You were disconnected for staying inside protected area '{area.Name}'.");
                    Plugin.Log.LogInfo($"Disconnecting {client.name} ({id}): stayed inside protected area '{area.Name}'.");
                    client.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to remove trespasser {id}: {ex.Message}");
            }
        }
    }
}
