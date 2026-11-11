using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using UnityEngine;

namespace ProtectedAreas
{
    /// <summary>
    /// Pontos do jogo interceptados:
    ///  - Structure.AttackWith (e overrides): usar ferramenta/kit em uma estrutura (construir etapa, desmontar, reparar, pintar...).
    ///  - Constructor.Construct / MultiConstructor.Construct (e overrides): colocar uma estrutura nova.
    ///  - Structure.CanConstruct (e overrides): deixa o cursor de construcao vermelho com o motivo.
    ///  - OnServer.MineAsteroid / PlaceVoxelAtWorldPosition: minerar ou depositar terreno.
    ///  - ChatMessage.Process / InventoryManager.OnChatMessage: comandos "/area" pelo chat.
    /// </summary>
    public static class Patches
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Titulo do tooltip quando a acao e bloqueada.</summary>
        public const string BlockedAction = "Protected area";

        private static bool _loggedError;

        public static void Apply(Harmony harmony)
        {
            Type self = typeof(Patches);

            int attack = PatchOverrides(harmony, typeof(Structure), "AttackWith",
                new[] { typeof(Attack), typeof(bool) },
                self.GetMethod(nameof(AttackWithPrefix), Static), null);

            int canConstruct = PatchOverrides(harmony, typeof(Structure), "CanConstruct",
                Type.EmptyTypes,
                null, self.GetMethod(nameof(CanConstructPostfix), Static));

            int constructor = PatchOverrides(harmony, typeof(Constructor), "Construct",
                new[] { typeof(Grid3), typeof(Quaternion), typeof(bool), typeof(ulong) },
                self.GetMethod(nameof(ConstructorPrefix), Static), null);

            int multi = PatchOverrides(harmony, typeof(MultiConstructor), "Construct",
                new[] { typeof(Grid3), typeof(Quaternion), typeof(int), typeof(Item), typeof(bool), typeof(ulong), typeof(int) },
                self.GetMethod(nameof(MultiConstructorPrefix), Static), null);

            PatchSingle(harmony, AccessTools.Method(typeof(OnServer), "MineAsteroid"), nameof(MineAsteroidPrefix));
            PatchSingle(harmony, AccessTools.Method(typeof(OnServer), "PlaceVoxelAtWorldPosition"), nameof(PlaceVoxelPrefix));
            PatchSingle(harmony, AccessTools.Method(typeof(ChatMessage), "Process"), nameof(ChatProcessPrefix));
            PatchSingle(harmony, AccessTools.Method(typeof(InventoryManager), "OnChatMessage"), nameof(LocalChatPrefix));

            Plugin.Log.LogInfo($"Patches applied: AttackWith x{attack}, CanConstruct x{canConstruct}, Constructor x{constructor}, MultiConstructor x{multi}");

            ProtectionPatches.Apply(harmony);
        }

        internal static void PatchSingle(Harmony harmony, MethodBase target, string prefixName, Type owner = null)
        {
            if (target == null)
            {
                Plugin.Log.LogError($"Target method for {prefixName} not found; that protection is disabled.");
                return;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod((owner ?? typeof(Patches)).GetMethod(prefixName, Static)));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to apply {prefixName}: {ex}");
            }
        }

        /// <summary>Aplica o mesmo patch no metodo virtual de baseType e em todas as classes derivadas que o sobrescrevem.</summary>
        internal static int PatchOverrides(Harmony harmony, Type baseType, string name, Type[] args, MethodInfo prefix, MethodInfo postfix)
        {
            int count = 0;
            foreach (Type type in AccessTools.GetTypesFromAssembly(baseType.Assembly))
            {
                if (type == null || !baseType.IsAssignableFrom(type) || type.IsGenericTypeDefinition)
                {
                    continue;
                }
                try
                {
                    MethodInfo method = type.GetMethod(name, Declared, null, args, null);
                    if (method == null || method.IsAbstract || method.GetMethodBody() == null)
                    {
                        continue;
                    }
                    harmony.Patch(method,
                        prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                        postfix: postfix != null ? new HarmonyMethod(postfix) : null);
                    count++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Could not patch {type.FullName}.{name}: {ex.Message}");
                }
            }
            if (count == 0)
            {
                Plugin.Log.LogError($"No {baseType.Name}.{name} method found; that protection is disabled.");
            }
            return count;
        }

        internal static void LogOnce(Exception ex)
        {
            if (_loggedError)
            {
                return;
            }
            _loggedError = true;
            Plugin.Log.LogError($"Error in a patch (the action was allowed): {ex}");
        }

        // ---------------------------------------------------------------- ferramentas em estruturas

        private static bool AttackWithPrefix(Thing __instance, Attack __0, bool __1, ref Thing.DelayedActionInstance __result)
        {
            try
            {
                bool enforcing = Guard.Enforcing;
                if (!enforcing && RemoteView.Count == 0)
                {
                    return true;
                }
                DynamicThing source = __0.SourceItem;
                if ((object)source == null)
                {
                    return true;
                }
                Human human = __0.ActiveHand?.Parent as Human;
                if ((object)human == null)
                {
                    human = source.RootParentHuman;
                }
                if ((object)human == null)
                {
                    return true; // nao e um jogador (ex.: robos)
                }
                Area area;
                if (enforcing)
                {
                    area = Guard.FindBlocking(human, __instance.Position);
                }
                else
                {
                    // Cliente com o mod: so antecipa na interface o que o host vai recusar.
                    area = Guard.IsLocal(human) ? RemoteView.FirstBlocking(__instance.Position) : null;
                }
                if (area == null)
                {
                    return true;
                }
                if (__1)
                {
                    Guard.NotifyDenied(human, area);
                    __result = null;
                }
                else
                {
                    __result = new Thing.DelayedActionInstance
                    {
                        Duration = 0f,
                        ActionMessage = BlockedAction
                    }.Fail(Guard.DeniedText(area));
                }
                return false;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return true;
            }
        }

        // ---------------------------------------------------------------- colocar estruturas

        private static void CanConstructPostfix(Structure __instance, ref CanConstructInfo __result)
        {
            try
            {
                if (!__result.CanConstruct || !ReferenceEquals(__instance, InventoryManager.ConstructionCursor))
                {
                    return;
                }
                Area area = Guard.FindBlockingLocal(__instance.ThingTransformPosition);
                if (area != null)
                {
                    __result = CanConstructInfo.InvalidPlacement(Guard.DeniedText(area));
                }
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        private static bool ConstructorPrefix(Thing __instance, Grid3 __0, ref ulong __3)
        {
            return AllowConstruct(__instance, __0, ref __3);
        }

        private static bool MultiConstructorPrefix(Thing __instance, Grid3 __0, ref ulong __5)
        {
            return AllowConstruct(__instance, __0, ref __5);
        }

        private static bool AllowConstruct(Thing kit, Grid3 localGrid, ref ulong steamId)
        {
            try
            {
                if (!Guard.Enforcing)
                {
                    return true;
                }
                Human human = ResolveBuilder(kit, steamId);
                // Clientes enviam o ReferenceId do cerebro como "dono". Grava o SteamID real na estrutura,
                // para a regra "nao criar area sobre estruturas de outro jogador" reconhecer o dono.
                ulong realId = Guard.IdOf(human);
                if (realId >= Guard.MinSteamId)
                {
                    steamId = realId;
                }
                if (AreaStore.Count == 0)
                {
                    return true;
                }
                Vector3 position = GridController.World.LocalToWorld(localGrid);
                Area area = (object)human != null ? Guard.FindBlocking(human, position) : Guard.FindAny(position);
                if (area == null)
                {
                    return true;
                }
                Guard.NotifyDenied(human, area);
                return false;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return true;
            }
        }

        private static Human ResolveBuilder(Thing kit, ulong steamId)
        {
            Human human = (object)kit != null ? kit.RootParentHuman : null;
            if ((object)human != null)
            {
                return human;
            }
            if (steamId == 0UL)
            {
                return null;
            }
            // O host envia o id do cliente; clientes enviam o ReferenceId do cerebro do jogador.
            human = Human.Find(steamId);
            if ((object)human != null)
            {
                return human;
            }
            Brain brain = Thing.Find<Brain>((long)steamId);
            return (object)brain != null ? brain.ParentEntity as Human : null;
        }

        // ---------------------------------------------------------------- terreno

        private static bool MineAsteroidPrefix(long __0, Vector3 __1)
        {
            try
            {
                if (!Guard.Enforcing || AreaStore.Count == 0)
                {
                    return true;
                }
                Human human = Thing.Find<Human>(__0);
                if ((object)human == null)
                {
                    return true;
                }
                Area area = Guard.FindBlocking(human, __1);
                if (area == null)
                {
                    return true;
                }
                Guard.NotifyDenied(human, area);
                return false;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return true;
            }
        }

        private static bool PlaceVoxelPrefix(long __0, Vector3 __1)
        {
            try
            {
                if (!Guard.Enforcing || AreaStore.Count == 0)
                {
                    return true;
                }
                Thing tool = Thing.Find(__0);
                Human human = (object)tool != null ? tool.RootParentHuman : null;
                if ((object)human == null)
                {
                    return true;
                }
                Area area = Guard.FindBlocking(human, __1);
                if (area == null)
                {
                    return true;
                }
                Guard.NotifyDenied(human, area);
                return false;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return true;
            }
        }

        // ---------------------------------------------------------------- comandos pelo chat

        /// <summary>
        /// Host: chat vindo de um cliente (comandos). Cliente: mensagens de sincronizacao vindas do host.
        /// </summary>
        private static bool ChatProcessPrefix(ChatMessage __instance)
        {
            try
            {
                if (NetworkManager.IsClient)
                {
                    if (__instance.DisplayName == Sync.Tag)
                    {
                        Sync.HostHasMod = true;
                        Sync.ApplyFromHost(__instance.ChatText);
                        return false; // nao mostra no console
                    }
                    if (__instance.HumanId == -1L && __instance.DisplayName == Guard.Tag)
                    {
                        // Aviso ou resposta do mod no host: prova que o host tem o mod.
                        Sync.HostHasMod = true;
                    }
                    return true;
                }
                if (!NetworkManager.IsServer || !Commands.IsCommand(__instance.ChatText))
                {
                    return true;
                }
                Human human = Thing.Find<Human>(__instance.HumanId);
                if ((object)human == null)
                {
                    return true;
                }
                Commands.Run(human, __instance.ChatText);
                return false; // nao repassa o comando como chat para os outros jogadores
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return true;
            }
        }

        /// <summary>Jogador local digitando no chat. Em clientes o texto segue normalmente para o servidor.</summary>
        private static bool LocalChatPrefix(string __0)
        {
            try
            {
                if (!Commands.IsCommand(__0))
                {
                    return true;
                }
                if (!Guard.Enforcing)
                {
                    // Cliente: so "show" e resolvido aqui; o resto segue como chat para o host.
                    return !Commands.TryRunClientSide(__0);
                }
                Commands.Run(Human.LocalHuman, __0);
                return false;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return true;
            }
        }
    }
}
