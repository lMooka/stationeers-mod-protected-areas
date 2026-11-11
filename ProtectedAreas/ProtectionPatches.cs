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

        /// <summary>Explosao encostando em uma area imune: ainda fere jogadores, mas nao escava o terreno.</summary>
        private static void ExplodePrefix(Vector3 __1, float __2, ref bool __4)
        {
            try
            {
                if (__4 && GameManager.RunSimulation && AreaStore.AnyDamageProtectedIntersecting(__1, __2))
                {
                    __4 = false;
                }
            }
            catch (Exception ex)
            {
                Patches.LogOnce(ex);
            }
        }
    }
}
