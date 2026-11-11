using System;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using HarmonyLib;
using UnityEngine;

namespace ProtectedAreas
{
    /// <summary>
    /// Protecoes contra griefing alem de construir/desmontar:
    ///  - Thing.PreventInteraction (e overrides): portas, alavancas, valvulas, botoes, slots de maquinas e armarios.
    ///    E o ponto que o jogo ja usa para recusar uma interacao, tanto no tooltip quanto no servidor.
    ///  - InteractionMessage / RequestInteractionToServer: pedidos de interacao vindos de clientes.
    ///  - MoveToSlotMessage / SwapSlotsMessage: tirar ou colocar itens em recipientes pela janela de inventario.
    ///  - IndestructableDamageState.Damage: estruturas imunes a dano (dono offline).
    ///  - Explosion.Explode: explosoes nao escavam o terreno junto a uma area imune.
    /// </summary>
    public static class ProtectionPatches
    {
        private const System.Reflection.BindingFlags Static =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        public static void Apply(Harmony harmony)
        {
            Type self = typeof(ProtectionPatches);

            int prevent = Patches.PatchOverrides(harmony, typeof(Thing), "PreventInteraction",
                new[] { typeof(Thing.DelayedActionInstance).MakeByRefType(), typeof(Interactable), typeof(Interaction) },
                null, self.GetMethod(nameof(PreventInteractionPostfix), Static));

            int damage = Patches.PatchOverrides(harmony, typeof(IndestructableDamageState), "Damage",
                new[] { typeof(ChangeDamageType), typeof(float), typeof(DamageUpdateType) },
                self.GetMethod(nameof(DamagePrefix), Static), null);

            Patches.PatchSingle(harmony, AccessTools.Method(typeof(InteractionMessage), "Process"), nameof(InteractionMessagePrefix), self);
            Patches.PatchSingle(harmony, AccessTools.Method(typeof(RequestInteractionToServer), "Process"), nameof(RequestInteractionPrefix), self);
            Patches.PatchSingle(harmony, AccessTools.Method(typeof(MoveToSlotMessage), "Process"), nameof(MoveToSlotPrefix), self);
            Patches.PatchSingle(harmony, AccessTools.Method(typeof(SwapSlotsMessage), "Process"), nameof(SwapSlotsPrefix), self);
            Patches.PatchSingle(harmony, AccessTools.Method(typeof(Explosion), "Explode"), nameof(ExplodePrefix), self);

            Plugin.Log.LogInfo($"Protection patches applied: PreventInteraction x{prevent}, Damage x{damage}");
            ApplyExplosionSources(harmony);
        }

        // ---------------------------------------------------------------- interacoes

        /// <summary>Area que impede o jogador de usar essa coisa, ou null.</summary>
        private static Area BlockedUse(Human actor, Thing target)
        {
            if (!Plugin.ProtectInteractions.Value || (object)actor == null || !Guard.IsProtectable(target))
            {
                return null;
            }
            return Guard.FindBlockingFor(actor, target.Position);
        }

        private static Human HumanOf(Thing thing)
        {
            if ((object)thing == null)
            {
                return null;
            }
            return thing as Human ?? thing.RootParentHuman;
        }

        private static void PreventInteractionPostfix(Thing __instance, ref Thing.DelayedActionInstance __0, Interactable __1, Interaction __2, ref bool __result)
        {
            try
            {
                if (__result || (!Guard.Enforcing && RemoteView.Count == 0))
                {
                    return;
                }
                Area area = BlockedUse(HumanOf(__2.SourceThing), __instance);
                if (area == null)
                {
                    return;
                }
                __0 = new Thing.DelayedActionInstance
                {
                    Duration = 0f,
                    ActionMessage = __1 != null ? __1.ContextualName : Patches.BlockedAction
                }.Fail(Guard.DeniedText(area));
                __result = true;
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
            }
        }

        /// <summary>Host recebendo uma interacao de um cliente: recusa e avisa o jogador.</summary>
        private static bool InteractionMessagePrefix(InteractionMessage __instance)
        {
            try
            {
                if (!NetworkManager.IsServer || !Plugin.ProtectInteractions.Value || AreaStore.Count == 0)
                {
                    return true;
                }
                if (__instance.InteractWith)
                {
                    Human actor = HumanOf(Thing.Find<Thing>(__instance.SourceId));
                    Area area = BlockedUse(actor, Thing.Find<Thing>(__instance.DestinationId));
                    if (area == null)
                    {
                        return true;
                    }
                    Guard.NotifyDenied(actor, area);
                    return false;
                }
                return AllowAnonymousInteraction(Thing.Find<Thing>(__instance.SourceId));
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
                return true;
            }
        }

        private static bool RequestInteractionPrefix(RequestInteractionToServer __instance)
        {
            try
            {
                if (!NetworkManager.IsServer || !Plugin.ProtectInteractions.Value || AreaStore.Count == 0)
                {
                    return true;
                }
                return AllowAnonymousInteraction(Thing.Find<Thing>(__instance.InteractThingId));
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
                return true;
            }
        }

        /// <summary>Pedido de cliente que nao informa o autor: decide pelos jogadores proximos do alvo.</summary>
        private static bool AllowAnonymousInteraction(Thing target)
        {
            if (!Guard.IsProtectable(target))
            {
                return true;
            }
            Area area = Guard.FindBlockingByProximity(target.Position, out Human culprit);
            if (area == null)
            {
                return true;
            }
            Guard.NotifyDenied(culprit, area);
            return false;
        }

        // ---------------------------------------------------------------- itens em recipientes

        /// <summary>
        /// Um jogador mexendo em um recipiente que nao e dele (armario, maquina, caixa no chao):
        /// retorna a area que bloqueia, ou null.
        /// </summary>
        private static Area BlockedContainer(Human actor, Thing container)
        {
            if ((object)container == null || (object)actor == null)
            {
                return null;
            }
            if (ReferenceEquals(container, actor) || ReferenceEquals(container.RootParentHuman, actor))
            {
                return null; // o proprio inventario
            }
            return BlockedUse(actor, container);
        }

        private static bool MoveToSlotPrefix(MoveToSlotMessage __instance)
        {
            try
            {
                if (!NetworkManager.IsServer || __instance.Drag || !Plugin.ProtectInteractions.Value || AreaStore.Count == 0)
                {
                    return true;
                }
                DynamicThing child = Thing.Find<DynamicThing>(__instance.ChildId);
                Thing destination = Thing.Find<Thing>(__instance.ParentId);
                if ((object)child == null || (object)destination == null)
                {
                    return true;
                }
                Thing source = child.ParentSlot != null ? child.ParentSlot.Parent : null;
                Human actor = HumanOf(destination) ?? HumanOf(source);
                Area area = BlockedContainer(actor, source) ?? BlockedContainer(actor, destination);
                if (area == null)
                {
                    return true;
                }
                Guard.NotifyDenied(actor, area);
                return false;
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
                return true;
            }
        }

        private static bool SwapSlotsPrefix(SwapSlotsMessage __instance)
        {
            try
            {
                if (!NetworkManager.IsServer || !Plugin.ProtectInteractions.Value || AreaStore.Count == 0)
                {
                    return true;
                }
                Thing first = Thing.Find<Thing>(__instance.ThingId1);
                Thing second = Thing.Find<Thing>(__instance.ThingId2);
                Human actor = HumanOf(second) ?? HumanOf(first);
                Area area = BlockedContainer(actor, first) ?? BlockedContainer(actor, second);
                if (area == null)
                {
                    return true;
                }
                Guard.NotifyDenied(actor, area);
                return false;
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
                return true;
            }
        }

        // ---------------------------------------------------------------- dano

        /// <summary>Pode ser chamado de threads do jogo: usa apenas dados imutaveis/volateis.</summary>
        private static bool DamagePrefix(IndestructableDamageState __instance, ChangeDamageType __0)
        {
            try
            {
                if (__0 != ChangeDamageType.Increment || !GameManager.RunSimulation)
                {
                    return true;
                }
                Structure structure = __instance.Parent as Structure;
                if ((object)structure == null)
                {
                    return true;
                }
                return !AreaStore.IsDamageProtected(structure.Position);
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
                return true;
            }
        }

        // ---------------------------------------------------------------- explosoes

        /// <summary>
        /// O objeto que esta explodindo agora (granada, explosivo, canister, fornalha...). Explosion.Explode nao
        /// recebe quem explodiu, entao os metodos que a chamam sao interceptados para preencher este campo.
        /// </summary>
        internal static Thing ExplosionSource;

        private static readonly System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo> ThisFields =
            new System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo>();

        /// <summary>Metodos assincronos do jogo que terminam em explosao: (classe, metodo).</summary>
        private static readonly string[][] AsyncSources =
        {
            new[] { "Grenade", "GrenadeOperation" },
            new[] { "Dynamite", "DynamiteOperation" },
            new[] { "ItemExplosive", "ExplosionCountdown" },
            new[] { "DeviceInputOutputImportCircuit", "HaltAndCatchFire" },
            new[] { "DeviceInputOutputCircuit", "HaltAndCatchFire" },
            new[] { "DeviceInputOutputImportExportCircuit", "HaltAndCatchFire" },
            new[] { "CircuitHousing", "HaltAndCatchFire" },
            new[] { "RobotMining", "HaltAndCatchFire" },
            new[] { "SurvivalToolbelt", "HaltAndCatchFire" },
            new[] { "AdvancedTablet", "HaltAndCatchFire" },
            new[] { "Laptop", "HaltAndCatchFire" },
            new[] { "AdvancedSuit", "HaltAndCatchFire" },
            new[] { "SuitBase", "HaltAndCatchFire" },
        };

        /// <summary>Metodos comuns do jogo que chamam Explosion.Explode: (classe, metodo).</summary>
        private static readonly string[][] DirectSources =
        {
            new[] { "GasCanister", "OnDamageDestroyed" },
            new[] { "DynamicGasCanister", "OnDamageDestroyed" },
            new[] { "ItemExplosive", "OnDestroy" },
            new[] { "FurnaceBase", "Explode" },
            new[] { "StirlingEngine", "Explode" },
        };

        private const System.Reflection.BindingFlags DeclaredAny =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;

        internal static void ApplyExplosionSources(Harmony harmony)
        {
            Type self = typeof(ProtectionPatches);
            HarmonyMethod finalizer = new HarmonyMethod(self.GetMethod(nameof(SourceFinalizer), Static));
            HarmonyMethod direct = new HarmonyMethod(self.GetMethod(nameof(DirectSourcePrefix), Static));
            HarmonyMethod machine = new HarmonyMethod(self.GetMethod(nameof(StateMachineSourcePrefix), Static));
            int patched = 0;
            int expected = AsyncSources.Length + DirectSources.Length;

            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Thing).Assembly))
            {
                if (type == null || type.IsNested)
                {
                    continue;
                }
                foreach (string[] source in DirectSources)
                {
                    if (type.Name != source[0])
                    {
                        continue;
                    }
                    try
                    {
                        System.Reflection.MethodInfo method = type.GetMethod(source[1], DeclaredAny);
                        if (method != null)
                        {
                            harmony.Patch(method, prefix: direct, finalizer: finalizer);
                            patched++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Could not track explosions of {type.Name}.{source[1]}: {ex.Message}");
                    }
                }
                foreach (string[] source in AsyncSources)
                {
                    if (type.Name != source[0])
                    {
                        continue;
                    }
                    foreach (Type nested in type.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (!nested.Name.StartsWith("<" + source[1] + ">d__", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        try
                        {
                            System.Reflection.MethodInfo moveNext = nested.GetMethod("MoveNext", DeclaredAny);
                            System.Reflection.FieldInfo thisField = nested.GetField("<>4__this", DeclaredAny);
                            if (moveNext != null && thisField != null)
                            {
                                ThisFields[nested] = thisField;
                                // Maquinas de estado "struct" precisam receber a instancia por referencia.
                                HarmonyMethod prefix = nested.IsValueType
                                    ? new HarmonyMethod(self.GetMethod(nameof(StructMachineSourcePrefix), Static).MakeGenericMethod(nested))
                                    : machine;
                                harmony.Patch(moveNext, prefix: prefix, finalizer: finalizer);
                                patched++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"Could not track explosions of {type.Name}.{source[1]}: {ex.Message}");
                        }
                    }
                }
            }
            Patches.PatchSingle(harmony, AccessTools.Method(typeof(Thing), "OnChildEnterInventory"), nameof(TrackOwnerPrefix), self);
            Plugin.Log.LogInfo($"Explosion sources tracked: {patched} of {expected}");
        }

        private static void DirectSourcePrefix(object __instance, out Thing __state)
        {
            __state = ExplosionSource;
            ExplosionSource = __instance as Thing;
        }

        private static void StateMachineSourcePrefix(object __instance, out Thing __state)
        {
            __state = ExplosionSource;
            try
            {
                if (__instance != null && ThisFields.TryGetValue(__instance.GetType(), out System.Reflection.FieldInfo field))
                {
                    ExplosionSource = field.GetValue(__instance) as Thing;
                }
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
            }
        }

        private static void StructMachineSourcePrefix<T>(ref T __instance, out Thing __state) where T : struct
        {
            __state = ExplosionSource;
            try
            {
                if (ThisFields.TryGetValue(typeof(T), out System.Reflection.FieldInfo field))
                {
                    ExplosionSource = field.GetValue(__instance) as Thing;
                }
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
            }
        }

        private static void SourceFinalizer(Thing __state)
        {
            ExplosionSource = __state;
        }

        /// <summary>
        /// Registra em cada objeto quem o carregou por ultimo (fica salvo junto com o mundo).
        /// E esse "dono" que decide se uma explosao perto de uma area e permitida.
        /// </summary>
        private static void TrackOwnerPrefix(Thing __instance, DynamicThing __0)
        {
            try
            {
                if (!GameManager.RunSimulation || (object)__0 == null || __0 is Entity)
                {
                    return;
                }
                ulong id = Guard.IdOf(HumanOf(__instance));
                if (id >= Guard.MinSteamId && __0.OwnerClientId != id)
                {
                    __0.OwnerClientId = id;
                }
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
            }
        }

        /// <summary>Dono do objeto que explode: quem construiu (estruturas) ou quem carregou por ultimo (objetos).</summary>
        internal static ulong OwnerOf(Thing source)
        {
            if ((object)source == null)
            {
                return 0UL;
            }
            ulong owner = source.OwnerClientId;
            if (owner >= Guard.MinSteamId)
            {
                return owner;
            }
            return Guard.IdOf(HumanOf(source)); // ainda na mao/mochila de alguem
        }

        /// <summary>
        /// Area que anula uma explosao nessa posicao: qualquer area a ate N blocos de distancia
        /// em que o dono do objeto nao e autorizado. Retorna null se a explosao pode acontecer.
        /// </summary>
        internal static Area ExplosionBlockedBy(Vector3 position, Thing source, out ulong owner)
        {
            owner = OwnerOf(source);
            float blocks = Plugin.ExplosionBufferBlocks.Value;
            if (blocks < 0f)
            {
                return null;
            }
            float buffer = blocks * BlockSize;
            ulong ownerId = owner;
            bool admin = Plugin.AdminsBypass.Value && Guard.IsAdminId(ownerId);
            if (admin)
            {
                return null;
            }
            return AreaStore.FirstNear(position, buffer, ownerId);
        }

        /// <summary>Um bloco = uma celula do grid grande de construcao (2 m).</summary>
        public const float BlockSize = 2f;

        private static bool ExplodePrefix(Vector3 __1, float __2, ref bool __4)
        {
            try
            {
                if (!GameManager.RunSimulation)
                {
                    return true;
                }
                Area blocking = ExplosionBlockedBy(__1, ExplosionSource, out ulong owner);
                if (blocking != null)
                {
                    Guard.NotifyDenied(Human.Find(owner), blocking,
                        $"Explosives have no effect within {Plugin.ExplosionBufferBlocks.Value:0} blocks of protected area '{blocking.Name}'.");
                    return false; // sem dano, sem escavar terreno
                }
                // Explosao permitida encostando em uma area imune (dono offline): nao escava o terreno.
                if (__4 && AreaStore.AnyDamageProtectedIntersecting(__1, __2))
                {
                    __4 = false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
                return true;
            }
        }
    }
}
