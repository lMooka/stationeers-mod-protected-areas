using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;
using Util.Commands;

namespace ProtectedAreas
{
    /// <summary>
    /// Comandos "/area ..." (chat) e "area ..." (console F3).
    /// Sempre executados no host / singleplayer; clientes enviam o texto pelo chat.
    /// </summary>
    public static class Commands
    {
        private static readonly string[] Prefixes = { "/area", "!area" };

        public static bool IsCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            text = text.TrimStart();
            foreach (string prefix in Prefixes)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (text.Length == prefix.Length || char.IsWhiteSpace(text[prefix.Length])))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Executa um texto de chat ja identificado como comando.</summary>
        public static void Run(Human actor, string chatText)
        {
            string[] tokens = chatText.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string[] args = new string[tokens.Length - 1];
            Array.Copy(tokens, 1, args, 0, args.Length);
            Execute(actor, args);
        }

        public static void Execute(Human actor, string[] args)
        {
            void Reply(string message) => Guard.Tell(actor, message);

            try
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
                switch (sub)
                {
                    case "create":
                    case "new":
                        Create(actor, args, Reply);
                        break;
                    case "remove":
                    case "delete":
                        Remove(actor, args, Reply);
                        break;
                    case "add":
                    case "auth":
                        SetMember(actor, args, true, Reply);
                        break;
                    case "del":
                    case "deauth":
                        SetMember(actor, args, false, Reply);
                        break;
                    case "list":
                        List(Reply);
                        break;
                    case "info":
                    case "here":
                        Info(actor, args, Reply);
                        break;
                    case "show":
                        Show(actor, args, Reply);
                        break;
                    case "kick":
                    case "eject":
                        Kick(actor, args, Reply);
                        break;
                    case "sync":
                        // Enviado automaticamente pelo mod do cliente ao entrar no servidor.
                        Sync.Subscribe(actor);
                        break;
                    default:
                        Help(Reply);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error in area command: {ex}");
                Reply("Error while running the command (see the log).");
            }
        }

        private static void Help(Action<string> reply)
        {
            reply("Commands (in chat use /area, in the F3 console use area):");
            reply("  create <name> [radius]   - create a cubic area centered on you");
            reply("  remove [name]            - remove the area (owner or admin)");
            reply("  add [name] <player>      - authorize a player to modify the area");
            reply("  del [name] <player>      - revoke a player's authorization");
            reply("  list                     - list all areas");
            reply("  info [name]              - details of the area (or of the areas you are in)");
            reply("  kick [name] [force]      - move unauthorized players out of the area (force: admins, disconnects them)");
            reply("  show [on|off]            - show/hide the area boundaries (needs the mod in your game)");
            reply("Without [name], the command uses the area you are standing in.");
        }

        private static void Create(Human actor, string[] args, Action<string> reply)
        {
            if ((object)actor == null)
            {
                reply("You must be in the game with a character to create an area.");
                return;
            }
            if (args.Length < 2)
            {
                reply("Usage: create <name> [radius]");
                return;
            }
            bool admin = Guard.IsAdmin(actor);
            if (Plugin.AdminOnlyCreate.Value && !admin)
            {
                reply("Only admins can create areas on this server.");
                return;
            }
            string name = args[1];
            float radius = Plugin.DefaultRadius.Value;
            if (args.Length >= 3 && !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
            {
                reply($"Invalid radius: '{args[2]}'.");
                return;
            }
            float max = Plugin.MaxRadius.Value;
            if (radius < 1f || (!admin && radius > max))
            {
                reply($"The radius must be between 1 and {max:0} meters.");
                return;
            }
            if (AreaStore.Find(name) != null)
            {
                reply($"An area named '{name}' already exists.");
                return;
            }
            ulong id = Guard.IdOf(actor);
            List<Area> all = AreaStore.Snapshot();
            if (!admin)
            {
                int limit = Plugin.MaxAreasPerPlayer.Value;
                if (limit > 0)
                {
                    int owned = 0;
                    foreach (Area other in all)
                    {
                        if (other.OwnerId == id)
                        {
                            owned++;
                        }
                    }
                    if (owned >= limit)
                    {
                        reply($"You already own the maximum of {limit} area(s).");
                        return;
                    }
                }
            }
            Vector3 center = Snap(actor.Position);
            // Areas nunca se sobrepoem: e a unica regra que vale tambem para admins.
            foreach (Area other in all)
            {
                if (other.Intersects(center, radius))
                {
                    reply($"Too close to area '{other.Name}' owned by {other.OwnerName}: areas cannot overlap.");
                    return;
                }
            }
            if (!admin)
            {
                float spawnDistance = Plugin.SpawnProtectionDistance.Value;
                if (spawnDistance > 0f && TooCloseToSpawn(center, radius, spawnDistance, out float distance))
                {
                    reply($"Too close to a spawn point ({distance:0}m). Areas must be at least {spawnDistance:0}m away from it.");
                    return;
                }
                int foreign = CountForeignStructures(actor, id, center, radius, out string foreignOwner);
                if (foreign > 0)
                {
                    reply($"This spot contains {foreign} structure(s) built by another player ({foreignOwner}). You cannot claim someone else's base.");
                    return;
                }
            }
            Area area = new Area
            {
                Name = name,
                OwnerId = id,
                OwnerName = actor.DisplayName,
                X = center.x,
                Y = center.y,
                Z = center.z,
                Radius = radius
            };
            area.Members.Add(new AreaMember { Id = id, Name = actor.DisplayName });
            AreaStore.Add(area);
            if (Guard.IsLocal(actor))
            {
                Plugin.ShowAreas = true;
            }
            reply($"Area '{name}' created: {radius * 2f:0}m cube centered at ({center.x:0}, {center.y:0}, {center.z:0}).");
            reply("Only you can build, use devices or take items here. Use 'add <player>' to authorize friends.");
            if (Plugin.DamageProtection.Value == DamageProtectionMode.OwnerOffline)
            {
                reply("While you are offline, structures inside it cannot be damaged.");
            }
        }

        /// <summary>Locais de inicio/respawn do mundo (nao inclui pontos de respawn construidos por jogadores).</summary>
        public static List<Vector3> WorldSpawnPositions()
        {
            List<Vector3> result = new List<Vector3>();
            StartLocationData start = WorldSetting.Current != null ? WorldSetting.Current.StartLocationData : null;
            if (start == null)
            {
                return result;
            }
            if (start is RoundRobinStartLocationData roundRobin && roundRobin.Locations != null)
            {
                foreach (StartLocationData location in roundRobin.Locations)
                {
                    if (location != null && location.Position != null)
                    {
                        result.Add(location.WorldPosition());
                    }
                }
            }
            else if (start.Position != null)
            {
                result.Add(start.WorldPosition());
            }
            return result;
        }

        /// <summary>Distancia horizontal da borda da area ate o ponto de inicio mais proximo.</summary>
        private static bool TooCloseToSpawn(Vector3 center, float radius, float minimum, out float distance)
        {
            distance = float.MaxValue;
            foreach (Vector3 spawn in WorldSpawnPositions())
            {
                float dx = Mathf.Max(0f, Mathf.Abs(center.x - spawn.x) - radius);
                float dz = Mathf.Max(0f, Mathf.Abs(center.z - spawn.z) - radius);
                distance = Mathf.Min(distance, Mathf.Sqrt(dx * dx + dz * dz));
            }
            return distance < minimum;
        }

        /// <summary>
        /// Estruturas dentro do cubo cujo dono e outro jogador. Estruturas sem dono conhecido
        /// (do mundo, ou antigas sem SteamID gravado) nao contam.
        /// </summary>
        internal static int CountForeignStructures(Human actor, ulong id, Vector3 center, float radius, out string ownerName)
        {
            ownerName = null;
            ulong brainId = (object)actor != null && (object)actor.OrganBrain != null ? (ulong)actor.OrganBrain.ReferenceId : 0UL;
            Area probe = new Area { X = center.x, Y = center.y, Z = center.z, Radius = radius };
            int count = 0;
            ulong firstOwner = 0UL;
            foreach (Structure structure in UnityEngine.Object.FindObjectsOfType<Structure>())
            {
                ulong owner = structure.OwnerClientId;
                if (owner < Guard.MinSteamId || owner == id || owner == brainId || !probe.Contains(structure.Position))
                {
                    continue;
                }
                if (count == 0)
                {
                    firstOwner = owner;
                }
                count++;
            }
            if (count > 0)
            {
                Human other = Human.Find(firstOwner);
                Client client = Client.Find(firstOwner);
                ownerName = (object)other != null ? other.DisplayName : client != null ? client.name : firstOwner.ToString(CultureInfo.InvariantCulture);
            }
            return count;
        }

        /// <summary>Ponto seguro do lado de fora da area, pela face horizontal mais proxima do jogador.</summary>
        public static Vector3 ExitPoint(Area area, Vector3 from)
        {
            float dx = from.x - area.X;
            float dz = from.z - area.Z;
            float outside = area.Radius + 4f;
            Vector3 target = Mathf.Abs(dx) >= Mathf.Abs(dz)
                ? new Vector3(area.X + (dx >= 0f ? outside : -outside), from.y, from.z)
                : new Vector3(from.x, from.y, area.Z + (dz >= 0f ? outside : -outside));
            return SpawnPoint.GetSafePoint(target, Vector3.zero);
        }

        /// <summary>
        /// "kick [name] [force]": tira da area os jogadores nao autorizados que estao dentro dela.
        /// A posicao de um jogador e controlada pelo cliente dele, entao so da para mover o host e quem tem o mod;
        /// com "force" (somente admins), quem nao pode ser movido e desconectado do servidor.
        /// </summary>
        private static void Kick(Human actor, string[] args, Action<string> reply)
        {
            bool force = false;
            string name = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "force", StringComparison.OrdinalIgnoreCase))
                {
                    force = true;
                }
                else if (name == null)
                {
                    name = args[i];
                }
            }
            Area area = ResolveArea(actor, name, reply);
            if (area == null || !CanManage(actor, area, reply))
            {
                return;
            }
            if (force && !Guard.IsAdmin(actor))
            {
                reply("Only admins can use 'force' (it disconnects players from the server).");
                return;
            }
            List<string> moved = new List<string>();
            List<string> stuck = new List<string>();
            List<string> dropped = new List<string>();
            foreach (Human human in new List<Human>(Human.AllHumans))
            {
                if ((object)human == null || !area.Contains(human.Position) || Guard.IsAllowed(area, human))
                {
                    continue;
                }
                ulong id = Guard.IdOf(human);
                if (id == 0UL || !Guard.IsOnline(id))
                {
                    continue; // corpo de jogador desconectado
                }
                Vector3 target = ExitPoint(area, human.Position);
                if (Guard.IsLocal(human))
                {
                    Guard.TeleportLocal(target);
                    moved.Add(human.DisplayName);
                }
                else if (Sync.SendTeleport(id, target))
                {
                    moved.Add(human.DisplayName);
                }
                else if (force)
                {
                    Client client = Client.Find(id);
                    if (client != null)
                    {
                        Guard.Tell(human, $"You were removed from the server for staying inside protected area '{area.Name}'.");
                        client.Disconnect();
                        dropped.Add(human.DisplayName);
                    }
                }
                else
                {
                    stuck.Add(human.DisplayName);
                }
                if (moved.Count > 0 && ReferenceEquals(moved[moved.Count - 1], human.DisplayName))
                {
                    Guard.Tell(human, $"You were moved out of protected area '{area.Name}' by its owner.");
                }
            }
            if (moved.Count == 0 && stuck.Count == 0 && dropped.Count == 0)
            {
                reply($"No unauthorized players inside area '{area.Name}'.");
                return;
            }
            if (moved.Count > 0)
            {
                reply("Moved out: " + string.Join(", ", moved));
            }
            if (dropped.Count > 0)
            {
                reply("Disconnected: " + string.Join(", ", dropped));
            }
            if (stuck.Count > 0)
            {
                reply("Could not move (they do not have the mod, so the game does not let the server move them): " + string.Join(", ", stuck));
                if (Plugin.TrespassKickSeconds.Value > 0f)
                {
                    reply($"They are disconnected automatically if they stay more than {Plugin.TrespassKickSeconds.Value:0} seconds.");
                }
                reply("An admin can use 'kick " + area.Name + " force' to disconnect them right away.");
            }
        }

        private static void Remove(Human actor, string[] args, Action<string> reply)
        {
            Area area = ResolveArea(actor, args.Length >= 2 ? args[1] : null, reply);
            if (area == null || !CanManage(actor, area, reply))
            {
                return;
            }
            AreaStore.Remove(area);
            reply($"Area '{area.Name}' removed.");
        }

        private static void SetMember(Human actor, string[] args, bool add, Action<string> reply)
        {
            if (args.Length < 2)
            {
                reply(add ? "Usage: add [name] <player>" : "Usage: del [name] <player>");
                return;
            }
            // "add <jogador>" usa a area atual; "add <nome> <jogador...>" usa a area indicada.
            Area area = null;
            string playerText;
            if (args.Length >= 3 && (area = AreaStore.Find(args[1])) != null)
            {
                playerText = string.Join(" ", args, 2, args.Length - 2);
            }
            else
            {
                area = ResolveArea(actor, null, reply);
                playerText = string.Join(" ", args, 1, args.Length - 1);
            }
            if (area == null || !CanManage(actor, area, reply))
            {
                return;
            }
            if (!add)
            {
                // Remover aceita tambem o nome gravado na lista (jogador offline).
                AreaMember existing = FindMember(area, playerText);
                if (existing != null)
                {
                    area.Members.Remove(existing);
                    AreaStore.MarkChanged();
                    reply($"{existing.Name} can no longer modify area '{area.Name}'.");
                    return;
                }
            }
            if (!ResolvePlayer(playerText, out ulong id, out string name, reply))
            {
                return;
            }
            if (add)
            {
                if (area.IsMember(id))
                {
                    reply($"{name} is already authorized in area '{area.Name}'.");
                    return;
                }
                area.Members.Add(new AreaMember { Id = id, Name = name });
                AreaStore.MarkChanged();
                reply($"{name} can now modify area '{area.Name}'.");
            }
            else
            {
                int removed = area.Members.RemoveAll(m => m.Id == id);
                if (removed == 0)
                {
                    reply($"{name} is not authorized in area '{area.Name}'.");
                    return;
                }
                AreaStore.MarkChanged();
                reply($"{name} can no longer modify area '{area.Name}'.");
            }
        }

        private static void List(Action<string> reply)
        {
            List<Area> all = AreaStore.Snapshot();
            if (all.Count == 0)
            {
                reply("No protected areas in this world.");
                return;
            }
            reply($"{all.Count} protected area(s):");
            foreach (Area area in all)
            {
                reply("  " + area.Describe());
            }
        }

        private static void Info(Human actor, string[] args, Action<string> reply)
        {
            List<Area> found = new List<Area>();
            if (args.Length >= 2)
            {
                Area named = AreaStore.Find(args[1]);
                if (named == null)
                {
                    reply($"Area '{args[1]}' not found.");
                    return;
                }
                found.Add(named);
            }
            else if ((object)actor != null)
            {
                Vector3 position = actor.Position;
                foreach (Area area in AreaStore.Snapshot())
                {
                    if (area.Contains(position))
                    {
                        found.Add(area);
                    }
                }
                if (found.Count == 0)
                {
                    reply("You are not inside any protected area.");
                    return;
                }
            }
            else
            {
                reply("Usage: info <name>");
                return;
            }
            foreach (Area area in found)
            {
                reply(area.Describe());
                List<string> names = new List<string>();
                foreach (AreaMember member in area.Members)
                {
                    names.Add(member.Name);
                }
                reply("  Authorized: " + (names.Count > 0 ? string.Join(", ", names) : "(nobody)"));
                bool online = Guard.IsOnline(area.OwnerId);
                area.OwnerOnline = online;
                reply("  Owner " + (online ? "online" : "offline") + "; structures " + (AreaStore.IsDamageProtected(area) ? "cannot be damaged right now." : "can be damaged right now."));
            }
        }

        /// <summary>
        /// Cliente (nao host): resolve localmente os comandos que so afetam a propria tela.
        /// Retorna true se o texto foi tratado aqui e nao deve seguir para o host.
        /// </summary>
        public static bool TryRunClientSide(string chatText)
        {
            string[] tokens = chatText.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string[] args = new string[tokens.Length - 1];
            Array.Copy(tokens, 1, args, 0, args.Length);
            return TryExecuteClientSide(args);
        }

        public static bool TryExecuteClientSide(string[] args)
        {
            if (args.Length == 0 || !string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            Show(Human.LocalHuman, args, message => Guard.Tell(Human.LocalHuman, message));
            return true;
        }

        private static void Show(Human actor, string[] args, Action<string> reply)
        {
            if ((object)actor != null && !Guard.IsLocal(actor))
            {
                // So chega aqui se o cliente nao tem o mod (com o mod, "show" e resolvido no proprio cliente).
                reply("Boundary display needs the Protected Areas mod installed in your game. Use 'info' to see center and radius.");
                return;
            }
            if (GameManager.IsBatchMode)
            {
                reply("No boundary display on a dedicated server.");
                return;
            }
            bool value = !Plugin.ShowAreas;
            if (args.Length >= 2)
            {
                string arg = args[1].ToLowerInvariant();
                value = arg == "on" || arg == "1" || arg == "yes" || arg == "true";
            }
            Plugin.ShowAreas = value;
            reply(value
                ? "Boundaries visible (green = you are authorized, red = blocked)."
                : "Boundaries hidden.");
        }

        // ---------------------------------------------------------------- auxiliares

        /// <summary>Centro alinhado ao grid de construcao grande (2m), para os limites coincidirem com as paredes.</summary>
        private static Vector3 Snap(Vector3 p)
        {
            return new Vector3(Mathf.Round(p.x / 2f) * 2f, Mathf.Round(p.y / 2f) * 2f, Mathf.Round(p.z / 2f) * 2f);
        }

        private static bool CanManage(Human actor, Area area, Action<string> reply)
        {
            if (Guard.IsAdmin(actor) || area.OwnerId == Guard.IdOf(actor))
            {
                return true;
            }
            reply($"Only the owner ({area.OwnerName}) or an admin can manage area '{area.Name}'.");
            return false;
        }

        private static Area ResolveArea(Human actor, string name, Action<string> reply)
        {
            if (!string.IsNullOrEmpty(name))
            {
                Area named = AreaStore.Find(name);
                if (named == null)
                {
                    reply($"Area '{name}' not found.");
                }
                return named;
            }
            if ((object)actor == null)
            {
                reply("Specify the area name.");
                return null;
            }
            Vector3 position = actor.Position;
            Area found = null;
            foreach (Area area in AreaStore.Snapshot())
            {
                if (!area.Contains(position))
                {
                    continue;
                }
                if (found != null)
                {
                    reply("You are inside more than one area; specify the name.");
                    return null;
                }
                found = area;
            }
            if (found == null)
            {
                reply("You are not inside any area; specify the name.");
            }
            return found;
        }

        private static AreaMember FindMember(Area area, string text)
        {
            foreach (AreaMember member in area.Members)
            {
                if (string.Equals(member.Name, text, StringComparison.OrdinalIgnoreCase)
                    || member.Id.ToString(CultureInfo.InvariantCulture) == text)
                {
                    return member;
                }
            }
            return null;
        }

        /// <summary>Aceita o nome (ou parte unica do nome) de um jogador presente no mundo, ou o SteamID.</summary>
        private static bool ResolvePlayer(string text, out ulong id, out string name, Action<string> reply)
        {
            id = 0UL;
            name = text;
            Human exact = null;
            List<Human> partial = new List<Human>();
            foreach (Human human in Human.AllHumans)
            {
                if ((object)human == null || Guard.IdOf(human) == 0UL)
                {
                    continue;
                }
                string display = human.DisplayName ?? string.Empty;
                if (display.Equals(text, StringComparison.OrdinalIgnoreCase))
                {
                    exact = human;
                    break;
                }
                if (display.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial.Add(human);
                }
            }
            if ((object)exact == null && partial.Count == 1)
            {
                exact = partial[0];
            }
            if ((object)exact != null)
            {
                id = Guard.IdOf(exact);
                name = exact.DisplayName;
                return true;
            }
            if (partial.Count > 1)
            {
                reply($"More than one player matches '{text}'; be more specific.");
                return false;
            }
            if (text.Length >= 5 && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0UL)
            {
                Client client = Client.Find(id);
                name = client != null ? client.name : text;
                return true;
            }
            reply($"Player '{text}' not found. Use the name of someone who has joined this world, or their SteamID.");
            return false;
        }
    }

    /// <summary>Comando "area" do console (F3).</summary>
    public class AreaCommand : CommandBase
    {
        public override string HelpText => "Protected areas: only authorized players can build/deconstruct/mine inside them. Use 'area help'.";

        public override string[] Arguments => new[] { "create <name> [radius] | remove [name] | add [name] <player> | del [name] <player> | kick [name] [force] | list | info [name] | show [on|off]" };

        public override bool IsLaunchCmd => false;

        public override string Execute(string[] args)
        {
            if (GameManager.GameState != Assets.Scripts.GridSystem.GameState.Running
                && GameManager.GameState != Assets.Scripts.GridSystem.GameState.Paused)
            {
                return "only available inside a world.";
            }
            if (NetworkManager.IsClient)
            {
                if (Commands.TryExecuteClientSide(args))
                {
                    return null;
                }
                // Clientes nao tem autoridade: o pedido vai para o host como comando de chat.
                Human local = Human.LocalHuman;
                if ((object)local == null)
                {
                    return "local character not found.";
                }
                NetworkClient.SendToServer(new ChatMessage
                {
                    ChatText = "/area " + string.Join(" ", args),
                    DisplayName = local.DisplayName,
                    HumanId = local.ReferenceId
                });
                return null;
            }
            Commands.Execute(GameManager.IsBatchMode ? null : Human.LocalHuman, args);
            return null;
        }
    }
}
