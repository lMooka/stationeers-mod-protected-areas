#if SELFTEST
// Autoteste de desenvolvimento. So entra na DLL quando compilada com -p:DefineConstants=SELFTEST
// e so roda se existir o arquivo %TEMP%\protectedareas_selftest.flag. Usar apenas em um mundo descartavel:
// ele cria estruturas e uma area chamada "selftest", registra o resultado no log e fecha o jogo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Serialization;
using UnityEngine;

namespace ProtectedAreas
{
    public static class SelfTest
    {
        private static readonly bool Enabled = File.Exists(Path.Combine(Path.GetTempPath(), "protectedareas_selftest.flag"));
        private static float _quitAt = -1f;
        private static float _nextStatus;
        private static bool _started;
        private static float _runAt = -1f;
        private static bool _done;
        private static int _pass;
        private static int _fail;

        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }
            if (_done)
            {
                // Alguns segundos depois, com os limites ja desenhados: confere, limpa e sai.
                if (_quitAt > 0f && Time.unscaledTime >= _quitAt)
                {
                    _quitAt = -1f;
                    Check("limites desenhados", Plugin.ShowAreas && Plugin.LineCount == 16, $"show={Plugin.ShowAreas} linhas={Plugin.LineCount}");
                    Commands.Execute(Human.LocalHuman, new[] { "remove", "selftest" });
                    Check("remover", AreaStore.Find("selftest") == null);
                    Guard.TestNoAdmin = false;
                    Plugin.Log.LogInfo($"SELFTEST DONE pass={_pass} fail={_fail}");
                    Application.Quit();
                }
                return;
            }
            if (Time.unscaledTime >= _nextStatus)
            {
                _nextStatus = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo($"SELFTEST status state={GameManager.GameState} init={GameManager.IsInitialized} human={(object)Human.LocalHuman != null} t={Time.unscaledTime:0}");
            }
            if (GameManager.GameState != GameState.Running || (object)Human.LocalHuman == null)
            {
                _runAt = -1f;
                if (!_started && GameManager.IsInitialized && GameManager.GameState == GameState.None && Time.unscaledTime > 25f)
                {
                    _started = true;
                    StartWorld();
                }
                return;
            }
            if (_runAt < 0f)
            {
                _runAt = Time.unscaledTime + 10f;
                return;
            }
            if (Time.unscaledTime < _runAt)
            {
                return;
            }
            _done = true;
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SELFTEST EXCEPTION: {ex}");
            }
            _quitAt = Time.unscaledTime + 3f;
        }

        // Mesmo caminho do comando de console "new <mundo>", com log de cada etapa.
        private static async void StartWorld()
        {
            try
            {
                Plugin.Log.LogInfo("SELFTEST mundos: " + string.Join(", ", WorldSetting.AllWorldSettings.Select(w => w.Id + (w.IsTutorial ? "(tutorial)" : "") + (w.IsDeprecated ? "(deprecated)" : ""))));
                Plugin.Log.LogInfo("SELFTEST dificuldades: " + string.Join(", ", DifficultySetting.AllSettings.Select(d => d.Id)));
                WorldSetting world = WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated && w.Id.IndexOf("Lunar", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated);
                DifficultySetting difficulty = DifficultySetting.Find("Normal") ?? DifficultySetting.AllSettings.FirstOrDefault();
                StartConditionData start = world.Data.StartConditionDatas.FirstOrDefault(s => s.IsDefault) ?? world.Data.StartConditionDatas.FirstOrDefault();
                Plugin.Log.LogInfo($"SELFTEST iniciando mundo={world.Id} dificuldade={difficulty?.Id} inicio={start?.Id}");
                WorldSetting.SetCurrent(world, start);
                DifficultySetting.SetCurrent(difficulty);
                Assets.Scripts.UI.ImGuiLoadingScreen.WorldName = world.Id;
                await World.StartNewWorld(world.Id);
                Plugin.Log.LogInfo("SELFTEST StartNewWorld retornou");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SELFTEST falha ao iniciar mundo: {ex}");
            }
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Plugin.Log.LogInfo($"SELFTEST {(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        private static HashSet<int> StructureIds()
        {
            return new HashSet<int>(UnityEngine.Object.FindObjectsOfType<Structure>().Select(s => s.GetInstanceID()));
        }

        private static Structure NewStructure(HashSet<int> before)
        {
            return UnityEngine.Object.FindObjectsOfType<Structure>().FirstOrDefault(s => !before.Contains(s.GetInstanceID()));
        }

        /// <summary>
        /// Multiplayer sem um segundo jogador: forca o papel de rede (host / cliente) e entrega as mesmas
        /// mensagens que um cliente enviaria, pelos mesmos metodos Process que o jogo chama ao recebe-las.
        /// </summary>
        private static void NetworkTests(Human human, Area area, Structure built, MultiConstructor multiKit, Grid3 freeCell, Grid3 freeCell2)
        {
            ulong id = Guard.IdOf(human);
            string me = human.DisplayName;
            Vector3 pos = human.Position;
            NetworkRole oldRole = NetworkManager.NetworkRole;
            bool oldShow = Plugin.ShowAreas;
            try
            {
                // ---------------- host recebendo mensagens de um cliente nao autorizado
                Commands.Execute(human, new[] { "del", me });
                NetworkManager.NetworkRole = NetworkRole.Server;

                int denied = Guard.DeniedCount;
                new MineAsteroidMessage
                {
                    ParentBrainId = human.ReferenceId,
                    WorldVoxelPosition = pos + Vector3.down,
                    Amount = 0.1,
                    ToolId = 0L
                }.Process(0L);
                Check("net: mensagem de minerar bloqueada", Guard.DeniedCount > denied);

                Slot hand = human.LeftHandSlot;
                bool hasTool = built && hand.Occupant;
                if (hasTool)
                {
                    int state = built.CurrentBuildStateIndex;
                    denied = Guard.DeniedCount;
                    new AttackWithMessage
                    {
                        AttackParentId = human.ReferenceId,
                        TargetId = built.ReferenceId,
                        ActiveHandSlotId = (byte)hand.SlotIndex,
                        OffHandSlotId = (byte)human.RightHandSlot.SlotIndex,
                        AttackPosition = built.Position,
                        CompletionRatio = 1f
                    }.Process(0L);
                    Check("net: mensagem de ferramenta bloqueada", Guard.DeniedCount > denied && built && built.CurrentBuildStateIndex == state);
                }

                // Clientes enviam o ReferenceId do cerebro em CreatorSteamId (nao o SteamID).
                CreateStructureMessage BuildMessage(Grid3 cell) => new CreateStructureMessage
                {
                    ConstructorId = 0L,
                    OffhandOccupantReferenceId = 0L,
                    LocalPosition = cell,
                    Rotation = Quaternion.identity,
                    CreatorSteamId = (ulong)human.OrganBrain.ReferenceId,
                    OptionIndex = 0,
                    PrefabHash = multiKit.PrefabHash,
                    AuthoringMode = true
                };
                if (multiKit)
                {
                    HashSet<int> before = StructureIds();
                    denied = Guard.DeniedCount;
                    BuildMessage(freeCell).Process(0L);
                    Check("net: mensagem de construir bloqueada", NewStructure(before) == null && Guard.DeniedCount > denied);
                }

                // Comando vindo pelo chat de um cliente.
                new ChatMessage { HumanId = human.ReferenceId, DisplayName = me, ChatText = "/area add " + me }.Process(0L);
                Check("net: comando pelo chat", area.IsMember(id));
                if (multiKit)
                {
                    HashSet<int> before = StructureIds();
                    BuildMessage(freeCell).Process(0L);
                    Structure s = NewStructure(before);
                    Check("net: mensagem de construir liberada apos add", s != null, s ? s.name : "");
                }
                new ChatMessage { HumanId = human.ReferenceId, DisplayName = me, ChatText = "/area sync" }.Process(0L);
                Check("net: host nao se inscreve em si mesmo", Sync.SubscriberCount == 0);

                // ---------------- cliente com o mod recebendo a copia das areas
                Commands.Execute(human, new[] { "del", me });
                List<string> messages = Sync.BuildMessages(AreaStore.Snapshot(), id);
                int expected = AreaStore.Count;
                NetworkManager.NetworkRole = NetworkRole.Client;
                Sync.HostHasMod = false;
                new ChatMessage { HumanId = human.ReferenceId, DisplayName = "Alguem", ChatText = "oi" }.Process(0L);
                Check("net: chat comum nao dispara pedido", !Sync.HostHasMod);
                new ChatMessage { HumanId = -1L, DisplayName = Guard.Tag, ChatText = "aviso" }.Process(0L);
                Check("net: aviso do host habilita pedido", Sync.HostHasMod);
                foreach (string payload in messages)
                {
                    new ChatMessage { HumanId = -1L, DisplayName = Sync.Tag, ChatText = payload }.Process(0L);
                }
                Check("net: cliente recebeu as areas", RemoteView.Count == expected && expected > 0, $"{RemoteView.Count}/{expected}");
                Check("net: cliente ve bloqueio", RemoteView.FirstBlocking(pos) != null && RemoteView.FirstBlocking(pos + new Vector3(500f, 0f, 0f)) == null);

                if (hasTool)
                {
                    Attack attack = new Attack(hand, human.RightHandSlot, built.Position, built, 1f, null, false, false);
                    Thing.DelayedActionInstance preview = built.AttackWith(attack, false);
                    Check("net: cliente tooltip bloqueado", preview != null && preview.IsDisabled && preview.ActionMessage == Patches.BlockedAction);
                }
                var cursors = (Dictionary<string, Structure>)HarmonyLib.AccessTools.Field(typeof(InventoryManager), "_constructionCursors").GetValue(null);
                if (cursors.TryGetValue("StructureFrame", out Structure cursor))
                {
                    Structure oldCursor = InventoryManager.ConstructionCursor;
                    Vector3 oldPosition = cursor.ThingTransformPosition;
                    cursor.ThingTransformPosition = GridController.World.LocalToWorld(freeCell2);
                    InventoryManager.ConstructionCursor = cursor;
                    CanConstructInfo info = cursor.CanConstruct();
                    InventoryManager.ConstructionCursor = oldCursor;
                    cursor.ThingTransformPosition = oldPosition;
                    Check("net: cliente cursor bloqueado", !info.CanConstruct && info.ErrorMessage.Contains("selftest"), info.ErrorMessage);
                }

                Plugin.ShowAreas = false;
                Check("net: cliente 'show' local", Commands.TryRunClientSide("/area show on") && Plugin.ShowAreas);
                Check("net: cliente repassa outros comandos", !Commands.TryRunClientSide("/area list"));

                // Autorizado: a copia do cliente deixa de bloquear.
                NetworkManager.NetworkRole = oldRole;
                Commands.Execute(human, new[] { "add", me });
                messages = Sync.BuildMessages(AreaStore.Snapshot(), id);
                NetworkManager.NetworkRole = NetworkRole.Client;
                foreach (string payload in messages)
                {
                    new ChatMessage { HumanId = -1L, DisplayName = Sync.Tag, ChatText = payload }.Process(0L);
                }
                Check("net: cliente autorizado nao ve bloqueio", RemoteView.Count == expected && RemoteView.FirstBlocking(pos) == null);

                // Muitas areas: o envio e dividido em varias mensagens e remontado.
                List<Area> many = new List<Area>();
                for (int i = 0; i < 80; i++)
                {
                    many.Add(new Area { Name = "area|com espaco " + i, OwnerName = "Dono %|" + i, X = i * 50f, Y = 1.5f, Z = -i, Radius = 10f + i });
                }
                List<string> chunks = Sync.BuildMessages(many, 1UL);
                foreach (string payload in chunks)
                {
                    RemoteView.Apply(payload);
                }
                List<RemoteView.Entry> view = RemoteView.Snapshot();
                Check("net: sincronizacao em partes", chunks.Count > 1 && chunks.All(c => c.Length <= 1000) && view.Count == 80
                    && view[79].Area.Name == many[79].Name && view[79].Area.OwnerName == many[79].OwnerName && view[79].Area.Radius == many[79].Radius,
                    $"mensagens={chunks.Count} areas={view.Count}");
            }
            catch (Exception ex)
            {
                Check("net: sem excecao", false, ex.ToString());
            }
            finally
            {
                NetworkManager.NetworkRole = oldRole;
                RemoteView.Clear();
                Plugin.ShowAreas = oldShow;
            }
            if (!area.IsMember(id))
            {
                Commands.Execute(human, new[] { "add", me });
            }
        }

        private static Structure _second;

        /// <summary>True enquanto o autoteste controla o relogio de invasao (o Plugin nao chama Trespass.Tick).</summary>
        public static bool Active => Enabled;

        /// <summary>Aviso de invasao: 30 s para sair, depois e removido. Usa um relogio falso.</summary>
        private static void TrespassTests(Human human, Area area)
        {
            string me = human.DisplayName;
            Vector3 start = human.Transform.position;
            try
            {
                float limit = Plugin.TrespassKickSeconds.Value;
                Trespass.Reset();
                Trespass.Tick(1000f);
                Check("invasao: autorizado nao e acompanhado", Trespass.TrackedCount == 0);

                Commands.Execute(human, new[] { "del", me });
                Trespass.Tick(1000f);
                Check("invasao: nao autorizado recebe o aviso", Trespass.TrackedCount == 1, $"limite={limit:0}s");
                Trespass.Tick(1000f + limit - 8f);
                Check("invasao: ainda dentro do prazo nao e removido", Trespass.TrackedCount == 1 && area.Contains(human.Transform.position));

                // Autorizado no meio da contagem: para de ser acompanhado.
                Commands.Execute(human, new[] { "add", me });
                Trespass.Tick(1000f + limit - 5f);
                Check("invasao: autorizar cancela a contagem", Trespass.TrackedCount == 0);

                Commands.Execute(human, new[] { "del", me });
                Trespass.Tick(2000f);
                Trespass.Tick(2000f + limit + 1f);
                Check("invasao: fim do prazo remove da area", Trespass.TrackedCount == 0 && !area.Contains(human.Transform.position),
                    $"{start} -> {human.Transform.position}");
            }
            catch (Exception ex)
            {
                Check("invasao: sem excecao", false, ex.ToString());
            }
            Trespass.Reset();
            if (!area.IsMember(Guard.IdOf(human)))
            {
                Commands.Execute(human, new[] { "add", "selftest", me });
            }
            Guard.TeleportLocal(start);
        }

        /// <summary>Estruturas alheias, interacoes e dano com dono offline.</summary>
        private static void ProtectionTests(Human human, Area area, Structure built)
        {
            ulong id = Guard.IdOf(human);
            string me = human.DisplayName;
            try
            {
                // ---------------- estruturas de outro jogador
                if (built)
                {
                    Plugin.Log.LogInfo($"SELFTEST dono gravado na estrutura={built.OwnerClientId} meuId={id}");
                    int mine = Commands.CountForeignStructures(human, id, area.Center, area.Radius, out _);
                    int foreign = Commands.CountForeignStructures(null, id + 1UL, area.Center, area.Radius, out string owner);
                    Check("regra: minhas estruturas nao contam como alheias", mine == 0, $"{mine}");
                    Check("regra: estruturas de outro jogador sao detectadas", foreign >= 1, $"{foreign} dono={owner}");
                }

                // ---------------- dano com dono offline
                if (built)
                {
                    area.OwnerOnline = true;
                    Check("dano: dono online nao protege", !AreaStore.IsDamageProtected(built.Position));
                    area.OwnerOnline = false;
                    Check("dano: dono offline protege", AreaStore.IsDamageProtected(built.Position));
                    float before = built.DamageState.Total;
                    built.DamageState.Damage(ChangeDamageType.Increment, 5f, DamageUpdateType.Brute);
                    Check("dano: estrutura imune com dono offline", built.DamageState.Total == before, $"{before} -> {built.DamageState.Total}");
                    area.OwnerOnline = true;
                    built.DamageState.Damage(ChangeDamageType.Increment, 5f, DamageUpdateType.Brute);
                    Check("dano: estrutura sofre dano com dono online", built.DamageState.Total > before, $"{before} -> {built.DamageState.Total}");
                    built.DamageState.HealAll();
                }

                // ---------------- interacoes (botoes, portas, alavancas)
                Thing device = null;
                Interactable button = null;
                foreach (Structure candidate in new[] { _second, built })
                {
                    if (!candidate || candidate.Interactables == null)
                    {
                        continue;
                    }
                    foreach (Interactable interactable in candidate.Interactables)
                    {
                        if (interactable != null && interactable.Slot == null
                            && (interactable.Action == InteractableType.OnOff || interactable.Action == InteractableType.Open || interactable.Action == InteractableType.Button1))
                        {
                            device = candidate;
                            button = interactable;
                            break;
                        }
                    }
                    if (button != null)
                    {
                        break;
                    }
                }
                Plugin.Log.LogInfo($"SELFTEST interacao: alvo={(device ? device.name : "nenhum")} acao={(button != null ? button.Action.ToString() : "-")}");
                if (button != null)
                {
                    Interaction interaction = new Interaction(human, human.LeftHandSlot, device, false);
                    bool baseline = device.PreventInteraction(out _, button, interaction); // membro
                    Commands.Execute(human, new[] { "del", me });
                    bool blocked = device.PreventInteraction(out Thing.DelayedActionInstance fail, button, interaction);
                    Check("interacao: nao autorizado bloqueado", blocked && fail != null && fail.IsDisabled, $"baseline={baseline}");

                    NetworkRole oldRole = NetworkManager.NetworkRole;
                    NetworkManager.NetworkRole = NetworkRole.Server;
                    try
                    {
                        int state = button.State;
                        int denied = Guard.DeniedCount;
                        new InteractionMessage
                        {
                            InteractWith = true,
                            DestinationId = device.ReferenceId,
                            SourceId = human.ReferenceId,
                            SourceSlotId = human.LeftHandSlot.SlotIndex,
                            InteractionId = button.InteractableId,
                            State = state
                        }.Process(0L);
                        Check("interacao: mensagem do cliente bloqueada", Guard.DeniedCount > denied && button.State == state);

                        denied = Guard.DeniedCount;
                        new RequestInteractionToServer
                        {
                            InteractThingId = device.ReferenceId,
                            InteractionId = button.InteractableId,
                            NewState = state == 0 ? 1 : 0
                        }.Process(0L);
                        Check("interacao: pedido sem autor bloqueado por proximidade", Guard.DeniedCount > denied && button.State == state,
                            $"dist={(human.Position - device.Position).magnitude:0.0}");
                    }
                    finally
                    {
                        NetworkManager.NetworkRole = oldRole;
                    }
                    Commands.Execute(human, new[] { "add", me });
                    bool after = device.PreventInteraction(out _, button, interaction);
                    Check("interacao: liberada apos add", after == baseline);
                }
            }
            catch (Exception ex)
            {
                Check("protecao: sem excecao", false, ex.ToString());
            }
            if (!area.IsMember(id))
            {
                Commands.Execute(human, new[] { "add", me });
            }
            area.OwnerOnline = true;
        }

        /// <summary>Expulsao: o dono tira da area um jogador nao autorizado (aqui, ele mesmo).</summary>
        private static void KickTests(Human human, Area area)
        {
            string me = human.DisplayName;
            try
            {
                // Pedido de teleporte como um cliente com o mod o receberia.
                NetworkRole oldRole = NetworkManager.NetworkRole;
                Vector3 start = human.Transform.position;
                Vector3 nudge = start + new Vector3(0.5f, 0.5f, 0f);
                NetworkManager.NetworkRole = NetworkRole.Client;
                try
                {
                    new ChatMessage
                    {
                        HumanId = -1L,
                        DisplayName = Sync.Tag,
                        ChatText = FormattableString.Invariant($"T|{nudge.x:0.###}|{nudge.y:0.###}|{nudge.z:0.###}")
                    }.Process(0L);
                }
                finally
                {
                    NetworkManager.NetworkRole = oldRole;
                }
                Check("kick: cliente com o mod obedece ao teleporte", (human.Transform.position - nudge).magnitude < 0.05f,
                    $"{human.Transform.position} esperado {nudge}");

                Commands.Execute(human, new[] { "kick" });
                Check("kick: autorizado nao e movido", area.Contains(human.Transform.position));

                Commands.Execute(human, new[] { "del", me });
                Commands.Execute(human, new[] { "kick", "selftest" });
                Vector3 now = human.Transform.position;
                Check("kick: nao autorizado sai da area", !area.Contains(now), $"{start} -> {now}");
                Commands.Execute(human, new[] { "add", "selftest", me });
                Guard.TeleportLocal(start);
            }
            catch (Exception ex)
            {
                Check("kick: sem excecao", false, ex.ToString());
            }
        }

        private static void Run()
        {
            Human human = Human.LocalHuman;
            ulong id = Guard.IdOf(human);
            string me = human.DisplayName;
            Vector3 pos = human.Position;
            Plugin.Log.LogInfo($"SELFTEST start human='{me}' id={id} pos={pos} station='{XmlSaveLoad.Instance?.CurrentStationName}' runSim={GameManager.RunSimulation}");

            // --- comandos e regra de permissao
            // --- regras de criacao (jogador comum x admin)
            Guard.TestNoAdmin = true;
            List<Vector3> spawns = Commands.WorldSpawnPositions();
            float nearest = spawns.Count == 0 ? -1f : spawns.Min(s => Vector2.Distance(new Vector2(s.x, s.z), new Vector2(pos.x, pos.z)));
            Plugin.Log.LogInfo($"SELFTEST spawns={spawns.Count} maisProximo={nearest:0}m");
            Commands.Execute(human, new[] { "create", "toobig", "16" });
            Check("regra: raio maximo 15", AreaStore.Find("toobig") == null);
            if (nearest >= 0f && nearest < 80f)
            {
                Commands.Execute(human, new[] { "create", "selftest", "10" });
                Check("regra: perto do spawn bloqueia jogador comum", AreaStore.Find("selftest") == null);
            }
            Guard.TestNoAdmin = false;
            Commands.Execute(human, new[] { "create", "selftest", "10" });
            Check("regra: admin ignora spawn", AreaStore.Find("selftest") != null);
            Commands.Execute(human, new[] { "create", "selftest2", "5" });
            Check("regra: sobreposicao bloqueia ate admin", AreaStore.Find("selftest2") == null);
            Check("regra: admin nao e bloqueado sem ser membro", Guard.FindBlocking(human, pos + new Vector3(1f, 0f, 0f)) == null);
            Guard.TestNoAdmin = true;

            Area area = AreaStore.Find("selftest");
            Check("criar", area != null && area.IsMember(id) && area.Contains(pos));
            Check("autorizado nao e bloqueado", Guard.FindBlocking(human, pos) == null);
            Check("fora da area nao e bloqueado", Guard.FindBlocking(human, pos + new Vector3(500f, 0f, 0f)) == null);

            // --- colocar estrutura (Constructor) estando autorizado
            Constructor kit = Resources.FindObjectsOfTypeAll<Constructor>()
                .Where(c => c.ReferenceId == 0 && c.BuildStructure != null)
                .OrderBy(c => c.name.Contains("Wall") ? 0 : 1).FirstOrDefault();
            MultiConstructor multiKit = Resources.FindObjectsOfTypeAll<MultiConstructor>()
                .Where(c => c.ReferenceId == 0 && c.Constructables.Count > 0 && c.GetType() == typeof(MultiConstructor))
                .OrderBy(c => c.name == "ItemSteelFrames" ? 0 : 1).FirstOrDefault();
            Plugin.Log.LogInfo($"SELFTEST kits: constructor={(kit ? kit.name : "null")} multi={(multiKit ? multiKit.name : "null")}");

            Grid3 cellA = GridController.World.WorldToLocal(pos + new Vector3(0f, 6f, 0f));
            Grid3 cellB = GridController.World.WorldToLocal(pos + new Vector3(4f, 6f, 0f));
            Grid3 cellC = GridController.World.WorldToLocal(pos + new Vector3(-4f, 6f, 0f));
            Grid3 cellD = GridController.World.WorldToLocal(pos + new Vector3(0f, 6f, 4f));

            Structure built = null;
            if (multiKit)
            {
                HashSet<int> before = StructureIds();
                multiKit.Construct(cellA, Quaternion.identity, 0, null, true, id, 0);
                built = NewStructure(before);
                Check("multi: autorizado constroi", built != null, built ? built.name : "");
            }
            if (kit)
            {
                HashSet<int> before = StructureIds();
                kit.Construct(cellB, Quaternion.identity, true, id);
                Structure s = NewStructure(before);
                Check("constructor: autorizado constroi", s != null, s ? s.name : "");
                if (!built) built = s;
                _second = s;
            }

            // --- tira a autorizacao de si mesmo
            Commands.Execute(human, new[] { "del", me });
            Check("del", !area.IsMember(id));
            Area blocking = Guard.FindBlocking(human, pos);
            Check("nao autorizado e bloqueado", blocking == area);

            int denied = Guard.DeniedCount;
            if (multiKit)
            {
                HashSet<int> before = StructureIds();
                multiKit.Construct(cellC, Quaternion.identity, 0, null, true, id, 0);
                Check("multi: bloqueado nao constroi", NewStructure(before) == null && Guard.DeniedCount > denied);
            }
            denied = Guard.DeniedCount;
            if (kit)
            {
                HashSet<int> before = StructureIds();
                kit.Construct(cellD, Quaternion.identity, true, id);
                Check("constructor: bloqueado nao constroi", NewStructure(before) == null && Guard.DeniedCount > denied);
            }

            // --- minerar bloqueado (o prefixo deve barrar antes do codigo original)
            denied = Guard.DeniedCount;
            try
            {
                OnServer.MineAsteroid(human.ReferenceId, pos + Vector3.down, 0.1f, 0L);
                Check("minerar bloqueado", Guard.DeniedCount > denied);
            }
            catch (Exception ex)
            {
                Check("minerar bloqueado", false, ex.GetType().Name);
            }

            // --- ferramenta em estrutura
            if (built)
            {
                Slot hand = human.LeftHandSlot;
                if (!hand.Occupant)
                {
                    Item tool = Resources.FindObjectsOfTypeAll<Wrench>().FirstOrDefault(w => w.ReferenceId == 0);
                    if (tool)
                    {
                        OnServer.Create<DynamicThing>(tool, hand);
                    }
                }
                Plugin.Log.LogInfo($"SELFTEST hand={(hand.Occupant ? hand.Occupant.name : "vazia")} alvo={built.name} tipo={built.GetType().Name}");
                if (hand.Occupant)
                {
                    Attack attack = new Attack(hand, human.RightHandSlot, built.Position, built, 1f, null, false, false);
                    Thing.DelayedActionInstance preview = built.AttackWith(attack, false);
                    Check("ferramenta: tooltip bloqueado", preview != null && preview.IsDisabled && preview.ActionMessage == Patches.BlockedAction);
                    denied = Guard.DeniedCount;
                    Thing.DelayedActionInstance action = built.AttackWith(attack, true);
                    Check("ferramenta: acao bloqueada", action == null && Guard.DeniedCount > denied);

                    // Cursor de construcao: usa o prefab da estrutura posicionado em uma celula livre dentro da area.
                    var cursors = (Dictionary<string, Structure>)HarmonyLib.AccessTools.Field(typeof(InventoryManager), "_constructionCursors").GetValue(null);
                    Structure cursor;
                    if (!cursors.TryGetValue(built.PrefabName ?? "", out cursor) && !cursors.TryGetValue("StructureFrame", out cursor))
                    {
                        cursor = cursors.Values.First();
                    }
                    Plugin.Log.LogInfo($"SELFTEST cursor={cursor.name} de {cursors.Count}");
                    Structure oldCursor = InventoryManager.ConstructionCursor;
                    Vector3 oldPosition = cursor.ThingTransformPosition;
                    cursor.ThingTransformPosition = GridController.World.LocalToWorld(cellC);
                    InventoryManager.ConstructionCursor = cursor;
                    CanConstructInfo info = cursor.CanConstruct();
                    Check("cursor: bloqueado", !info.CanConstruct && info.ErrorMessage.Contains("selftest"), info.ErrorMessage);

                    Commands.Execute(human, new[] { "add", me });
                    CanConstructInfo infoAllowed = cursor.CanConstruct();
                    Check("cursor: liberado apos add", infoAllowed.CanConstruct, infoAllowed.ErrorMessage);
                    InventoryManager.ConstructionCursor = oldCursor;
                    cursor.ThingTransformPosition = oldPosition;

                    Commands.Execute(human, new[] { "add", me });
                    Check("add", area.IsMember(id));
                    Thing.DelayedActionInstance allowed = built.AttackWith(attack, false);
                    Check("ferramenta: liberada apos add", allowed == null || allowed.ActionMessage != Patches.BlockedAction,
                        allowed == null ? "null" : allowed.ActionMessage);
                }
            }

            ProtectionTests(human, area, built);
            NetworkTests(human, area, built, multiKit, cellC, cellD);

            // --- persistencia
            XmlSerializer serializer = new XmlSerializer(typeof(AreaFile));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.Serialize(stream, new AreaFile { Areas = AreaStore.Snapshot() });
                stream.Position = 0;
                AreaFile back = (AreaFile)serializer.Deserialize(stream);
                Area again = back.Areas.FirstOrDefault(a => a.Name == "selftest");
                Check("xml ida e volta", again != null && again.Radius == area.Radius && again.OwnerId == id && again.Members.Count == area.Members.Count);
            }
            string station = XmlSaveLoad.Instance?.CurrentStationName;
            if (!string.IsNullOrEmpty(station))
            {
                string file = Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, station, AreaStore.FileName);
                Plugin.Log.LogInfo($"SELFTEST arquivo {file} existe={File.Exists(file)} pastaExiste={Directory.Exists(Path.GetDirectoryName(file))}");
            }

            Commands.Execute(human, new[] { "list" });
            Commands.Execute(human, new[] { "info" });
            KickTests(human, area);
            TrespassTests(human, area);
        }
    }
}
#endif
