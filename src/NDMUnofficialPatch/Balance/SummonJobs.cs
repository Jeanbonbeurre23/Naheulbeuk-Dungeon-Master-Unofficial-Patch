using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // Summoned undead and demons get a job.
    //
    // Game 1.8, from the method bodies, the configs bundle and the behaviour trees, read on 8 and 9 October 2026.
    // Undead (AS_Undead, AS_Troll_Undead) and demons (AS_Demon, AS_CursedWarrior) carry the same JobPracticedComponent,
    // GradeComponent and origin component as minions, with the job UNDEAD or DEMON and the origins SKELETON, ZOMBIE,
    // DEMON or CURSED_KNIGHT, whose Minion.OriginInfoConfig has no need decay, no salary and no craftable resource. They
    // have no MinionTag, no ReservedPlacesComponent (the list of furniture places a character has booked) and no craft
    // components. Their BehaviourTreeOwnerConfigComponent names UndeadBehaviourConfig (BORED, DEATH, COMBAT, INVOKED) or
    // DemonBehaviourConfig (BORED, RESIGN, DEATH, COMBAT, ENEMY_COMBAT, DEMON_FOLLOW_CULTIST); job minions use
    // MinionBehaviourConfig, which maps CLEAN, RELOAD_TRAP, CRAFT_RESOURCES, TRAIN, COACH, INCARCERATE_ENTITY,
    // TORTURE_ENTITY, MAGIC_TRAINING, ENCHANT_TRAP and the rest to their behaviour trees. A minion's job components come
    // from WorldConfig.MinionJobComponents[job] (domestic: clean and reload-trap scores; artisan: craft score and success
    // rate; guard: GuardTag, train and coach scores, IoAttachedTo; torturer: incarcerate and torture scores; sorcerer:
    // MageTag, MagicLearning, enchant, magic-training and craft scores). AISwitchBehaviourSystem.Run calls
    // BehaviourTreeUtility.FillAvailablesBehaviours for a character whose BehaviourTreeOwnerComponent has no list of
    // available behaviours yet (the native array at offset 0x10); that method builds the list from the behaviour config
    // named by the character's BehaviourTreeOwnerConfigComponent, disposing the arrays it replaces. A guard's idle
    // behaviour (BT_Bored_1, HasMinionJobCondition GUARD) uses a guard table one time in ten (FindGuardSpotTask: the
    // tables, PropType ARMORY_TABLE, of the room of the furniture named by his IoAttachedToComponent, his locker) and
    // otherwise walks into that room (GetRoomAttachedToTask). An artisan or a crafting mage makes what his origin's
    // m_craftableResources allows: goblins 4 tools, dwarves 4 weapons, humans 4 astral energy.
    //
    // The summons themselves get the jobs, automatically, in proportions set in the settings. A guard is not hired by
    // a locker and is never recruitable, and "the Library" for demons means producing astral energy and learning
    // spells, the sorcerer's work.
    //
    // Every 5 seconds, each summon still on his own job (UNDEAD or DEMON), alive, in the dungeon, not rising
    // (behaviour INVOKED) and, for a demon, linked to a living cultist, gets the job furthest below its share among the
    // summons of his kind, or keeps none when the share of none is not met. The patch then adds the job's components
    // and the minion components a job needs (ReservedPlaces, CraftSpeedMultiplier, CraftQualityModifier,
    // DamageOnCraftPercentage, TrapTriggerModifier), each through its own component config
    // (AComponentConfig.AddComponents and SetupComponents), taken from the job's config and from an ordinary minion;
    // sets JobPracticed; points the BehaviourTreeOwnerConfigComponent at an ordinary minion's config, which names
    // MinionBehaviourConfig; and empties the list of available behaviours so that the game builds it again. A summon
    // guard's IoAttachedToComponent is set to a guard table, so that his idle behaviour finds the guard room. Skeletons
    // and zombies are given the goblins' tools, demons and cursed warriors the dwarves' weapons and the humans' astral
    // energy, in memory only. The look of a character is chosen once, when he is created, and is kept.
    internal static unsafe class SummonJobs
    {
        private const JobType NoJob = (JobType)(-1);
        private const PropType GuardTable = PropType.ARMORY_TABLE;

        private enum Kind { Undead, Demon }

        private static readonly JobType[] UndeadAllowed = { JobType.DOMESTIC, JobType.GUARD, JobType.ARTISAN };
        private static readonly JobType[] DemonAllowed = { JobType.DOMESTIC, JobType.GUARD, JobType.TORTURER, JobType.ARTISAN, JobType.SORCERER };

        private static Dictionary<JobType, float> _undeadShares, _demonShares;
        private static IntPtr _world;
        private static bool _broken, _originsDone;
        private static float _nextPass, _nextSummary;
        private static IntPtr _minionOwnerConfig;
        private static readonly List<AComponentConfig> MinionConfigs = new();
        private static int _ownerOffset = -1, _availOffset, _currentOffset, _linkOffset, _idOffset, _genOffset, _attachedOffset, _attachedPacked;
        private static int _converted, _logged, _pass, _tablesPass = -1;
        private static readonly List<int> Tables = new();
        private static readonly Dictionary<int, int> Load = new();
        private static bool _noTableLogged, _noTemplateLogged;

        // Up to three summons per job are described a few seconds after they get it, then every minute for 5 minutes.
        private sealed class Watch { internal int Entity; internal JobType Job; internal float Next; internal int Left; }
        private static readonly List<Watch> Watches = new();
        private static readonly Dictionary<JobType, int> Watched = new();

        internal static void Update()
        {
            if (_broken || !GameContext.Ready || !GameContext.TryWorld(out var world, out int size)) return;
            if (GameContext.WorldPointer != _world)
            {
                _world = GameContext.WorldPointer;
                _minionOwnerConfig = IntPtr.Zero;
                MinionConfigs.Clear();
                Watches.Clear();
                Watched.Clear();
                _nextPass = Time.unscaledTime + 3f;
            }
            float now = Time.unscaledTime;
            try
            {
                if (now >= _nextPass)
                {
                    _nextPass = now + 5f;
                    Pass(world, size);
                }
                if (Watches.Count > 0) Describe(world, size, now);
                if (now >= _nextSummary)
                {
                    _nextSummary = now + 300f;
                    Summary(world, size);
                }
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.Logger.LogWarning($"[SummonJobs] failed, no further summon gets a job in this session: {e}");
            }
        }

        // ---- Settings ----

        internal static Dictionary<JobType, float> ParseShares(string text, JobType[] allowed, string setting)
        {
            var shares = new Dictionary<JobType, float>();
            foreach (string part in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split(':');
                string name = kv[0].Trim().ToUpperInvariant();
                if (kv.Length != 2 || !float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || value < 0f)
                {
                    Plugin.Logger.LogWarning($"[SummonJobs] {setting}: '{part}' ignored, expected JOB:share");
                    continue;
                }
                JobType job;
                if (name == "NONE") job = NoJob;
                else if (name == "LIBRARY") job = JobType.SORCERER;
                else if (!Enum.TryParse(name, out job) || Array.IndexOf(allowed, job) < 0)
                {
                    Plugin.Logger.LogWarning($"[SummonJobs] {setting}: job '{name}' ignored; allowed: NONE, " + string.Join(", ", allowed.Select(Label)));
                    continue;
                }
                shares[job] = shares.TryGetValue(job, out float v) ? v + value : value;
            }
            float total = shares.Values.Sum();
            if (total <= 0f) return new Dictionary<JobType, float>();
            return shares.ToDictionary(kv => kv.Key, kv => kv.Value / total);
        }

        private static string Label(JobType job) => job == NoJob ? "NONE" : job == JobType.SORCERER ? "LIBRARY" : job.ToString();

        // ---- Layout and templates ----

        private static void Layout()
        {
            if (_ownerOffset >= 0) return;
            _availOffset = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("AvailableBehavioursIndex");
            if (_availOffset != 0x10) throw new InvalidOperationException($"BehaviourTreeOwnerComponent.AvailableBehavioursIndex at {_availOffset}, expected 16");
            _currentOffset = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("CurrentBehaviour");
            _linkOffset = Il2CppRaw.ValueFieldOffset<Demon.CultistLinkComponent>("Value");
            _idOffset = Il2CppRaw.ValueFieldOffset<EcsPackedEntity>("Id");
            _genOffset = Il2CppRaw.ValueFieldOffset<EcsPackedEntity>("Gen");
            _attachedOffset = Il2CppRaw.ValueFieldOffset<IoAttachedToComponent>("IoAttached");
            _attachedPacked = Il2CppRaw.ValueFieldOffset<IoAttachedToComponent>("IoAttachedPackedEntity");
            _ownerOffset = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerConfigComponent>("m_config");
        }

        private static IntPtr ConfigOf<T>(EcsWorld world, int entity)
        {
            IntPtr item = RawPool.Of<T>(world, -1).Item(entity);
            return item == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(item + Il2CppRaw.ValueFieldOffset<T>("m_config"));
        }

        // An ordinary minion's behaviour config and minion component configs, read once per world.
        private static bool Templates(EcsWorld world, int size)
        {
            if (_minionOwnerConfig != IntPtr.Zero) return true;
            var undead = RawPool.Of<Undead.UndeadTag>(world, -1);
            var demons = RawPool.Of<Demon.DemonTag>(world, -1);
            var dead = RawPool.Of<DeathComponent>(world, -1);
            foreach (int e in RawPool.Of<MinionTag>(world, -1).Entities())
            {
                if (!world.IsEntityAlive(e, size) || undead.Has(e) || demons.Has(e) || dead.Has(e)) continue;
                IntPtr owner = ConfigOf<BehaviourTreeOwnerConfigComponent>(world, e);
                if (owner == IntPtr.Zero || new BehaviourTreeOwnerConfig(owner).BehaviourConfig?.name != "MinionBehaviourConfig") continue;
                var configs = new[]
                {
                    ConfigOf<ReservedPlacesConfigComponent>(world, e),
                    ConfigOf<CraftSpeedMultiplierConfigComponent>(world, e),
                    ConfigOf<CraftQualityModifierConfigComponent>(world, e),
                    ConfigOf<DamageOnCraftPercentageConfigComponent>(world, e),
                    ConfigOf<TrapTriggerModifierConfigComponent>(world, e),
                };
                if (configs.Any(c => c == IntPtr.Zero)) continue;
                MinionConfigs.Clear();
                MinionConfigs.AddRange(configs.Select(c => new AComponentConfig(c)));
                _minionOwnerConfig = owner;
                Plugin.Logger.LogInfo($"[SummonJobs] templates taken from {Name(e)} (entity {e}): MinionBehaviourConfig and " + string.Join(", ", MinionConfigs.Select(c => c.name)));
                return true;
            }
            if (!_noTemplateLogged) Plugin.Logger.LogInfo("[SummonJobs] no ordinary minion to copy the job set-up from yet; summons get no job until one exists");
            _noTemplateLogged = true;
            return false;
        }

        // Skeletons and zombies craft tools as goblins do; demons and cursed warriors weapons as dwarves do and astral
        // energy as humans do. In memory only.
        private static void Origins()
        {
            if (_originsDone) return;
            _originsDone = true;
            var origins = GameContext.GameData.WorldConfig.OriginsConfig;
            int Amount(OriginType o, ResourceType r) => origins.GetMinionConfig(o)?.m_craftableResources?.m_internalArray?[(int)r] ?? 4;
            int tools = Amount(OriginType.GOBLIN, ResourceType.TOOLS), weapons = Amount(OriginType.DWARF, ResourceType.ARMAMENT), magic = Amount(OriginType.HUMAN, ResourceType.MAGIC);
            void Give(OriginType o, (ResourceType Resource, int Amount)[] crafts, ResourceType[] priorities)
            {
                var info = origins.GetMinionConfig(o);
                if (info == null) { Plugin.Logger.LogInfo($"[SummonJobs] no minion origin config for {o}"); return; }
                var array = info.m_craftableResources?.m_internalArray;
                if (array != null) foreach (var c in crafts) if (array[(int)c.Resource] <= 0) array[(int)c.Resource] = c.Amount;
                var list = info.m_resourcePriorities;
                if (list != null && list.Count == 0) foreach (var p in priorities) list.Add(p);
                Plugin.Logger.LogInfo($"[SummonJobs] {o} can craft " + string.Join(", ", crafts.Select(c => $"{c.Amount} {c.Resource}")));
            }
            var undeadOrder = new[] { ResourceType.TOOLS, ResourceType.ARMAMENT, ResourceType.INTEL, ResourceType.CORPSES, ResourceType.MAGIC };
            var demonOrder = new[] { ResourceType.ARMAMENT, ResourceType.MAGIC, ResourceType.TOOLS, ResourceType.INTEL, ResourceType.CORPSES };
            foreach (var o in new[] { OriginType.SKELETON, OriginType.ZOMBIE }) Give(o, new[] { (ResourceType.TOOLS, tools) }, undeadOrder);
            foreach (var o in new[] { OriginType.DEMON, OriginType.CURSED_KNIGHT }) Give(o, new[] { (ResourceType.ARMAMENT, weapons), (ResourceType.MAGIC, magic) }, demonOrder);
        }

        // ---- The pass ----

        private struct Summon { internal int Entity; internal Kind Kind; internal JobType Job; }

        private static void Pass(EcsWorld world, int size)
        {
            _pass++;
            Layout();
            if (!Templates(world, size)) return;
            Origins();
            _undeadShares ??= ParseShares(Settings.UndeadJobs.Value, UndeadAllowed, "UndeadJobs");
            _demonShares ??= ParseShares(Settings.DemonJobs.Value, DemonAllowed, "DemonJobs");

            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var outside = RawPool.Of<OutsideDungeonTag>(world, -1);
            var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, 152);
            var links = RawPool.Of<Demon.CultistLinkComponent>(world, -1);
            var summons = new List<Summon>();
            void Collect(RawPool tags, Kind kind)
            {
                foreach (int e in tags.Entities())
                {
                    if (!world.IsEntityAlive(e, size) || dead.Has(e) || outside.Has(e)) continue;
                    IntPtr j = jobs.Item(e);
                    if (j == IntPtr.Zero || !brains.Has(e)) continue;
                    summons.Add(new Summon { Entity = e, Kind = kind, Job = ((JobPracticedComponent*)j)->JobPracticed });
                }
            }
            Collect(RawPool.Of<Undead.UndeadTag>(world, -1), Kind.Undead);
            Collect(RawPool.Of<Demon.DemonTag>(world, -1), Kind.Demon);
            if (summons.Count == 0) return;

            // Summons already given a job: the behaviour config is pointed again after a load, and guards keep a table.
            foreach (var s in summons)
                if (s.Job != Base(s.Kind)) Keep(world, size, s.Entity, s.Job);

            int budget = 30;
            foreach (Kind kind in new[] { Kind.Undead, Kind.Demon })
            {
                var shares = kind == Kind.Undead ? _undeadShares : _demonShares;
                if (shares.Count == 0) continue;
                var mine = summons.Where(s => s.Kind == kind).ToList();
                var counts = new Dictionary<JobType, int>();
                foreach (var s in mine)
                {
                    var j = s.Job == Base(kind) ? NoJob : s.Job;
                    counts[j] = counts.TryGetValue(j, out int c) ? c + 1 : 1;
                }
                int total = mine.Count;
                foreach (var s in mine)
                {
                    if (budget <= 0) break;
                    if (s.Job != Base(kind) || !Ready(world, size, s, brains, links, dead)) continue;
                    // The job furthest below its share; none when no job is below.
                    JobType best = NoJob;
                    float bestGap = 0f;
                    foreach (var kv in shares)
                    {
                        if (kv.Key == NoJob) continue;
                        float gap = kv.Value * total - (counts.TryGetValue(kv.Key, out int c) ? c : 0);
                        if (gap > bestGap + 0.0001f) { bestGap = gap; best = kv.Key; }
                    }
                    if (best == NoJob || bestGap < 0.5f) continue;
                    if (!Give(world, size, s.Entity, kind, best)) continue;
                    counts[best] = (counts.TryGetValue(best, out int b) ? b : 0) + 1;
                    counts[NoJob] = Math.Max(0, (counts.TryGetValue(NoJob, out int n) ? n : 0) - 1);
                    budget--;
                }
            }
        }

        private static JobType Base(Kind kind) => kind == Kind.Undead ? JobType.UNDEAD : JobType.DEMON;

        private static bool Ready(EcsWorld world, int size, Summon s, RawPool brains, RawPool links, RawPool dead)
        {
            IntPtr b = brains.Item(s.Entity);
            if (b == IntPtr.Zero || *(EBehaviourType*)(b + _currentOffset) == EBehaviourType.INVOKED) return false;
            if (s.Kind == Kind.Undead) return true;
            IntPtr l = links.Item(s.Entity);
            if (l == IntPtr.Zero) return false;
            int cultist = *(int*)(l + _linkOffset + _idOffset);
            short gen = *(short*)(l + _linkOffset + _genOffset);
            return world.IsEntityAlive(cultist, size) && world.GetEntityGen(cultist) == gen && !dead.Has(cultist);
        }

        private static string Name(int e)
        {
            try { string n = GameContext.Minions.GetMinionFullName(e); if (!string.IsNullOrWhiteSpace(n)) return n; }
            catch { }
            return $"entity {e}";
        }

        // ---- Giving a job ----

        private static bool Give(EcsWorld world, int size, int e, Kind kind, JobType job)
        {
            var jobConfig = GameContext.GameData.WorldConfig.MinionJobComponents.m_internalArray[(int)job];
            if (jobConfig == null) { Plugin.Logger.LogWarning($"[SummonJobs] the job {job} has no component config; not given"); return false; }
            var added = new List<AComponentConfig>();
            var names = new List<string>();
            var all = new List<AComponentConfig>();
            var jobConfigs = jobConfig.ComponentConfigs;
            if (jobConfigs != null) for (int i = 0; i < jobConfigs.Length; i++) all.Add(jobConfigs[i]);
            all.AddRange(MinionConfigs);
            foreach (var config in all)
            {
                if (config == null) continue;
                if (Add(world, e, config)) { added.Add(config); names.Add(config.name); }
            }
            var gameData = GameContext.GameData;
            foreach (var config in added) config.SetupComponents(world, gameData, e, false);
            ((JobPracticedComponent*)RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent)).Item(e))->JobPracticed = job;
            Keep(world, size, e, job);
            _converted++;
            _logged++;
            if (_logged <= 60)
                Plugin.Logger.LogInfo($"[SummonJobs] {Name(e)} ({kind.ToString().ToLowerInvariant()}, entity {e}) becomes {Label(job)}; components added: {string.Join(", ", names)}");
            else if (_logged == 61)
                Plugin.Logger.LogInfo("[SummonJobs] further jobs given are counted in the 5-minute summary only");
            if (!Watched.TryGetValue(job, out int w) || w < 3)
            {
                Watched[job] = w + 1;
                Watches.Add(new Watch { Entity = e, Job = job, Next = Time.unscaledTime + 4f, Left = 6 });
            }
            return true;
        }

        // Adds a config's components unless the entity already has its config component.
        private static bool Add(EcsWorld world, int e, AComponentConfig config)
        {
            Il2CppSystem.Type type = null;
            try { type = config.ConfigComponentType; } catch { }
            if (type != null)
            {
                IEcsPool pool = world.GetPoolByType(type);
                if (pool != null && pool.Has(e)) return false;
            }
            return config.AddComponents(world, e, false);
        }

        // Points the behaviour config at the minions' one, and keeps a guard on a guard table.
        private static void Keep(EcsWorld world, int size, int e, JobType job)
        {
            IntPtr oc = RawPool.Of<BehaviourTreeOwnerConfigComponent>(world, -1).Item(e);
            if (oc != IntPtr.Zero && *(IntPtr*)(oc + _ownerOffset) != _minionOwnerConfig)
            {
                *(IntPtr*)(oc + _ownerOffset) = _minionOwnerConfig;
                IntPtr b = RawPool.Of<BehaviourTreeOwnerComponent>(world, 152).Item(e);
                if (b != IntPtr.Zero)
                {
                    // The game builds the list again, from the new config, the next time it chooses a behaviour.
                    *(IntPtr*)(b + _availOffset) = IntPtr.Zero;
                    *(int*)(b + _availOffset + IntPtr.Size) = 0;
                }
            }
            if (job == JobType.GUARD) KeepTable(world, size, e);
        }

        private static void KeepTable(EcsWorld world, int size, int guard)
        {
            var attached = RawPool.Of<IoAttachedToComponent>(world, -1);
            IntPtr a = attached.Item(guard);
            if (a == IntPtr.Zero) return;
            var identities = RawPool.Of<PropIdentityComponent>(world, 4);
            int current = *(int*)(a + _attachedPacked + _idOffset);
            short currentGen = *(short*)(a + _attachedPacked + _genOffset);
            bool IsTable(int p) => world.IsEntityAlive(p, size) && identities.Item(p) is var i && i != IntPtr.Zero && *(PropType*)i == GuardTable;
            if (current > 0 && IsTable(current) && world.GetEntityGen(current) == currentGen) return;

            // The guard table with the fewest summon guards, on the guard's floor when there is one; the tables and
            // their load are listed once per pass.
            if (_tablesPass != _pass)
            {
                _tablesPass = _pass;
                Tables.Clear();
                Load.Clear();
                Tables.AddRange(identities.Entities().Where(p => IsTable(p) && GameContext.RoomOfProp(world, p) >= 0));
                foreach (int g in attached.Entities())
                {
                    int t = *(int*)(attached.Item(g) + _attachedPacked + _idOffset);
                    if (Tables.Contains(t)) Load[t] = (Load.TryGetValue(t, out int c) ? c : 0) + 1;
                }
            }
            var tables = Tables;
            var load = Load;
            if (tables.Count == 0)
            {
                if (!_noTableLogged) Plugin.Logger.LogInfo("[SummonJobs] no guard table in a guard room yet; summon guards wait for one");
                _noTableLogged = true;
                return;
            }
            var floors = RawPool.Of<GridFloorComponent>(world, 4);
            int Floor(int ent) { IntPtr f = floors.Item(ent); return f == IntPtr.Zero ? int.MinValue : *(int*)f; }
            int floor = Floor(guard);
            int best = tables.OrderBy(t => Floor(t) == floor ? 0 : 1).ThenBy(t => load.TryGetValue(t, out int c) ? c : 0).First();
            *(int*)(a + _attachedOffset) = best;
            *(int*)(a + _attachedPacked + _idOffset) = best;
            *(short*)(a + _attachedPacked + _genOffset) = world.GetEntityGen(best);
            load[best] = (load.TryGetValue(best, out int n) ? n : 0) + 1;
            _noTableLogged = false;
        }

        // ---- What the summons do ----

        private static List<EBehaviourType> Available(IntPtr brain)
        {
            var list = new List<EBehaviourType>();
            IntPtr data = *(IntPtr*)(brain + _availOffset);
            int length = *(int*)(brain + _availOffset + IntPtr.Size);
            if (data == IntPtr.Zero || length <= 0 || length > 128) return list;
            for (int i = 0; i < length; i++) list.Add(*(EBehaviourType*)(data + i * 4));
            return list;
        }

        private static void Describe(EcsWorld world, int size, float now)
        {
            Layout();
            var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, 152);
            for (int i = Watches.Count - 1; i >= 0; i--)
            {
                var w = Watches[i];
                if (now < w.Next) continue;
                w.Left--;
                w.Next = now + 60f;
                IntPtr b = world.IsEntityAlive(w.Entity, size) ? brains.Item(w.Entity) : IntPtr.Zero;
                if (b == IntPtr.Zero) { Watches.RemoveAt(i); continue; }
                Plugin.Logger.LogInfo($"[SummonJobs] {Name(w.Entity)} ({Label(w.Job)}, entity {w.Entity}): doing {*(EBehaviourType*)(b + _currentOffset)}; available: {string.Join(", ", Available(b))}");
                if (w.Left <= 0) Watches.RemoveAt(i);
            }
        }

        private static void Summary(EcsWorld world, int size)
        {
            // The first summary can come before the first pass, which reads the field offsets.
            Layout();
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, 152);
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var sb = new StringBuilder();
            foreach (var (tags, kind) in new[] { (RawPool.Of<Undead.UndeadTag>(world, -1), "undead"), (RawPool.Of<Demon.DemonTag>(world, -1), "demons") })
            {
                var byJob = new SortedDictionary<string, Dictionary<string, int>>();
                foreach (int e in tags.Entities())
                {
                    if (!world.IsEntityAlive(e, size) || dead.Has(e)) continue;
                    IntPtr j = jobs.Item(e), b = brains.Item(e);
                    if (j == IntPtr.Zero || b == IntPtr.Zero) continue;
                    var job = ((JobPracticedComponent*)j)->JobPracticed;
                    string key = job == JobType.UNDEAD || job == JobType.DEMON ? "NONE" : Label(job);
                    if (!byJob.TryGetValue(key, out var doing)) byJob[key] = doing = new Dictionary<string, int>();
                    string what = (*(EBehaviourType*)(b + _currentOffset)).ToString();
                    doing[what] = (doing.TryGetValue(what, out int c) ? c : 0) + 1;
                }
                if (byJob.Count == 0) continue;
                sb.Append(sb.Length > 0 ? " | " : "").Append(kind).Append(": ");
                sb.Append(string.Join("; ", byJob.Select(kv => $"{kv.Key} {kv.Value.Values.Sum()} ({string.Join(", ", kv.Value.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))})")));
            }
            if (sb.Length > 0) Plugin.Logger.LogInfo($"[SummonJobs] {_converted} job(s) given this session; {sb}");
        }
    }
}
