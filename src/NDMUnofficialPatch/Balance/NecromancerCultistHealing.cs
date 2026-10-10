using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // Necromancers heal undead and vampires, cultists heal their demons.
    //
    // Game 1.8, from the method bodies, the configs bundle and the behaviour trees, read on 8 October 2026. A
    // pharmagician heals a minion who lies in an infirmary bed. The minion's REQUEST_HEAL behaviour (BT_RequestHeal) takes
    // him to a bed, where he carries a RequestHealComponent (RequestedHeal, MinionHealingEntity) and a
    // RequestToBeServedComponent. The pharmagician's HEAL behaviour (BT_Heal) runs FindMinionToHealTask, which takes the
    // first minion of m_minionsRequestingHealFilter (Inc<RequestHealComponent, RequestToBeServedComponent>,
    // Exc<DeathComponent>) whose MinionHealingEntity is -1, writes the healer there, reserves the bed, walks to it and
    // works (HealActionSystem) for the healer's DurationComponent, 10 seconds from the pharmagician job's HealDuration,
    // at the pace of his ProgressionComponent. At the end (OnProgressReachedDuration) the patient's life is set to the
    // gauge's maximum and every state of his carrying a CanBeHealedTagComponent is removed (bleeding, poison, Broken,
    // Frozen, Dynamo, four curses, the hurt emote). The HEAL score comes from AIComputeHealScoreJob: 1 for every entity
    // with an AIComputeHealScoreComponent as soon as one minion requests a heal. The three components come from the
    // pharmagician job's components (AS_Components_Pharmawizard: ComputeHealScore, HealDuration, HealProgression);
    // necromancers and cultists use the same minion behaviour config, which maps HEAL to BT_Heal, but their job's
    // components hold none of the three. MinionUtility.IsTherePharmagicianInDungeon, which ends a patient's wait in
    // bed when it returns false, counts the entities with an AIComputeHealScoreComponent. Undead (UndeadBehaviourConfig:
    // BORED, DEATH, COMBAT, INVOKED) and demons (DemonBehaviourConfig: BORED, FLEE, DEATH, COMBAT, ENEMY_COMBAT,
    // DEMON_FOLLOW_CULTIST) have no REQUEST_HEAL and never lie in a bed.
    //
    // Since undead and demons cannot lie in a bed, the patch gives undead, vampires and demons a heal at close range,
    // adds the game's bed heal of vampires by necromancers, and heals no one during a fight.
    //
    // Close range. Twice a second, each necromancer and each cultist who is not fighting tends one patient at a time:
    // for a necromancer a wounded undead (Undead.UndeadTag) or a wounded vampire minion (OriginType.VAMPIRE) who is not
    // asking for a bed heal, for a cultist a wounded demon whose CultistLinkComponent names him. The patient stands on
    // the same floor within 5 squares and is not fighting. After 10 seconds of game time without a break, and without a
    // loss of life meanwhile (more than 0.25 between two checks), the patient gets the result of
    // OnProgressReachedDuration: life at its maximum and his states carrying CanBeHealedTagComponent removed with
    // StatesUtility.RemoveState. Fighting means a current behaviour of COMBAT, ENEMY_COMBAT or USE_SKILL.
    //
    // Bed heal. Every 5 seconds, a necromancer without an AIComputeHealScoreComponent gets the three components of the
    // pharmagician job, added by the job's own component configs (AComponentConfig.AddComponents and SetupComponents).
    // A prefix on AISwitchBehaviourSystem.Run sets a necromancer's HEAL score to 0 unless a vampire waits in bed for no
    // healer or for him. A prefix on FindMinionToHealTask.OnExecute, for a necromancer, marks every other waiting patient
    // as taken for the time of the call (MinionHealingEntity -2), and the postfix gives -1 back. A postfix on
    // IsTherePharmagicianInDungeon counts a necromancer only for a vampire. The components are kept in the save; with
    // the setting off, the patch removes them from the necromancers at load.
    //
    // Call. The heal at close range seldom happens on its own, since necromancers stay at their work and undead
    // wander. Twice a second, each patient whose life is below
    // Balance.HealCompulsionBelow percent calls a healer: for an undead or a vampire the nearest necromancer who is not
    // fighting and has no patient, for a demon his own cultist. Until the patient is healed, the healer's work scores
    // (invoke, carry corpses, enchant trap, heal, discuss, curse item, magic training, craft resources, sacrifice,
    // enchant item, mix potion, release anger) are set to 0 in a prefix on AISwitchBehaviourSystem.Run, so the game
    // gives him BORED (or a need). In his BORED tree (BT_Bored_1), a prefix on Discussion.FindEntityToDiscussWithTask
    // ends that task as failed, and a prefix on FindRandomPositionTask writes the patient's position
    // (TransformComponent.Position) into the task's position variable instead of a random one, so the following
    // MoveTowardsTask walks him up to 9 units towards his patient at each turn of the tree. Once within 5 squares, the
    // heal at close range takes its 10 seconds. A call ends with the heal, when either one dies or leaves the dungeon,
    // when the healer fights, or after 120 seconds of game time, after which the patient waits 60 seconds before
    // calling again.
    internal static unsafe class NecromancerCultistHealing
    {
        private const int Radius = 5;
        private const float HealSeconds = 10f;
        // A loss of life larger than this between two checks (half a second) counts as a hit and restarts the heal;
        // the undead's own decay, when Balance.UndeadNoDecay is off, takes at most 0.034 in that time.
        private const float LossTolerance = 0.25f;

        // The behaviour needs a world in both states of the setting: on it heals, off it removes the components it gave.
        internal static bool Needed => true;

        private sealed class Care
        {
            internal int Patient;
            internal short Gen;
            internal float Elapsed;
            internal float LastLife;
        }

        private struct Body
        {
            internal int Entity;
            internal int Floor, X, Y;
        }

        private static readonly Dictionary<int, Care> Cares = new();

        // A healer called to a patient: healer -> call.
        private sealed class Call
        {
            internal int Patient;
            internal short Gen;
            internal float Since;
            internal string Kind;
        }
        private static readonly Dictionary<int, Call> Calls = new();
        private static readonly Dictionary<int, float> CallCooldown = new();
        private static readonly List<(RawPool Pool, int Offset)> WorkScores = new();
        private const float CallTimeout = 120f, CallCooldownSeconds = 60f;
        private static int _callsLogged, _callsMade, _callsDone, _callsTimedOut;
        private static readonly HashSet<int> Tended = new();
        private static readonly List<int> Hidden = new();
        private static readonly List<int> NecroHealers = new();     // necromancers with the heal score component
        private static readonly List<int> OtherHealers = new();     // every other entity with it (pharmagicians)
        private static readonly HashSet<int> NecroAllowed = new();  // necromancers whose HEAL score is left to the game
        private static bool _freeVampire;

        private static IntPtr _world;
        private static GameData _gameData;
        private static AComponentConfig _scoreConfig, _durationConfig, _progressionConfig;
        private static float _gameTime, _lastTick, _nextTick, _nextGrant, _nextSummary;
        private static bool _stripped, _broken, _layout;
        private static int _granted, _logged, _healedUndead, _healedVampires, _healedDemons;

        private static int _gauge, _value, _percentage, _max, _computed, _current, _healer, _link, _id, _gen, _states, _score, _base, _final;

        private static void Layout()
        {
            if (_layout) return;
            Il2CppRaw.ExpectValueFieldOffset<GridCoordinatesComponent>("Coordinates", 0);
            Il2CppRaw.ExpectValueFieldOffset<GridFloorComponent>("Floor", 0);
            _gauge = Il2CppRaw.ValueFieldOffset<LifePointsComponent>("Gauge");
            _value = Il2CppRaw.ValueFieldOffset<Gauge>("CurrentValue");
            _percentage = Il2CppRaw.ValueFieldOffset<Gauge>("CurrentPercentage");
            _max = Il2CppRaw.ValueFieldOffset<Gauge>("MaxValue");
            _computed = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_computedValue");
            _current = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("CurrentBehaviour");
            _healer = Il2CppRaw.ValueFieldOffset<RequestHealComponent>("MinionHealingEntity");
            _link = Il2CppRaw.ValueFieldOffset<Demon.CultistLinkComponent>("Value");
            _id = Il2CppRaw.ValueFieldOffset<EcsPackedEntity>("Id");
            _gen = Il2CppRaw.ValueFieldOffset<EcsPackedEntity>("Gen");
            _states = Il2CppRaw.ValueFieldOffset<EntityStateHandlerComponent>("CurrentStates");
            _score = Il2CppRaw.ValueFieldOffset<AIComputeHealScoreComponent>("ScoreData");
            _base = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("BaseScore");
            _final = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("FinalScore");
            _layout = true;
        }

        private static void Report(string what, Exception e)
        {
            if (_broken) return;
            _broken = true;
            Plugin.Logger.LogWarning($"[Healing] {what} failed; necromancers and cultists stop healing for the rest of the session: {e.Message}");
        }

        private static string Name(int e, string kind)
        {
            try { string n = GameContext.Minions.GetMinionFullName(e); if (!string.IsNullOrWhiteSpace(n)) return n; }
            catch { }
            return $"{kind} {e}";
        }

        // ---- Every frame, from FixesBehaviour ----

        internal static void Update()
        {
            if (_broken || !GameContext.TryWorld(out var world, out int size)) return;
            if (GameContext.WorldPointer != _world)
            {
                _world = GameContext.WorldPointer;
                Cares.Clear();
                Tended.Clear();
                Calls.Clear();
                CallCooldown.Clear();
                WorkScores.Clear();
                NecroHealers.Clear();
                OtherHealers.Clear();
                NecroAllowed.Clear();
                _freeVampire = false;
                _gameData = null;
                _stripped = false;
                _gameTime = _lastTick = 0f;
                _nextGrant = _nextTick = 0f;
            }
            if (!Settings.NecromancersAndCultistsHeal.Value)
            {
                if (_stripped) return;
                _stripped = true;
                try { Strip(world, size); }
                catch (Exception e) { Report("removing the heal components from necromancers", e); }
                return;
            }
            if (!GameContext.Ready) return;
            try
            {
                Layout();
                _gameData ??= GameContext.GameData;
                _gameTime += Math.Max(0f, _gameData.DeltaTime);
                float now = Time.unscaledTime;
                if (now >= _nextGrant)
                {
                    _nextGrant = now + 5f;
                    Grant(world, size);
                }
                if (now >= _nextTick)
                {
                    _nextTick = now + 0.5f;
                    float dt = _gameTime - _lastTick;
                    _lastTick = _gameTime;
                    Restore();
                    Refresh(world, size);
                    Tick(world, size, dt);
                }
                if (now >= _nextSummary)
                {
                    if (_healedUndead + _healedVampires + _healedDemons + _callsMade > 0)
                        Plugin.Logger.LogInfo($"[Healing] in the last 5 minutes, healed at close range: {_healedUndead} undead, {_healedVampires} vampire(s), {_healedDemons} demon(s); healers called: {_callsMade}, of whom {_callsDone} healed their patient and {_callsTimedOut} gave up after {CallTimeout:0} s; {Calls.Count} call(s) under way");
                    _healedUndead = _healedVampires = _healedDemons = 0;
                    _callsMade = _callsDone = _callsTimedOut = 0;
                    _nextSummary = now + 300f;
                }
            }
            catch (Exception e) { Report("healing by necromancers and cultists", e); }
        }

        // ---- The pharmagician's heal components, given to necromancers ----

        private static bool ResolveConfigs()
        {
            if (_scoreConfig != null) return true;
            var jobs = GameContext.GameData.WorldConfig.MinionJobComponents.m_internalArray;
            var pharmagician = jobs[(int)JobType.PHARMAWIZARD];
            if (pharmagician == null) throw new InvalidOperationException("the pharmagician job has no component config");
            foreach (var c in pharmagician.ComponentConfigs)
            {
                if (c == null) continue;
                if (c.TryCast<AIComputeHealScoreConfig>() != null) _scoreConfig = c;
                else if (c.TryCast<DurationConfig>() != null && c.name == "HealDuration") _durationConfig = c;
                else if (c.TryCast<ProgressionConfig>() != null && c.name == "HealProgression") _progressionConfig = c;
            }
            if (_scoreConfig == null || _durationConfig == null || _progressionConfig == null)
            {
                _scoreConfig = null;
                throw new InvalidOperationException("ComputeHealScore, HealDuration or HealProgression not found in the pharmagician job's components");
            }
            return true;
        }

        private static bool IsNecromancer(RawPool jobs, int e)
        {
            IntPtr j = jobs.Item(e);
            return j != IntPtr.Zero && ((JobPracticedComponent*)j)->JobPracticed == JobType.NECROMANCER;
        }

        internal static bool IsNecromancer(EcsWorld world, int e)
            => IsNecromancer(RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent)), e);

        private static void Grant(EcsWorld world, int size)
        {
            ResolveConfigs();
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var minion = RawPool.Of<MinionTag>(world, -1);
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var score = RawPool.Of<AIComputeHealScoreComponent>(world, -1);
            var pairs = new (AComponentConfig Config, RawPool Component, RawPool ConfigComponent)[]
            {
                (_scoreConfig, score, RawPool.Of<AIComputeHealScoreConfigComponent>(world, -1)),
                (_durationConfig, RawPool.Of<DurationComponent>(world, -1), RawPool.Of<DurationConfigComponent>(world, -1)),
                (_progressionConfig, RawPool.Of<ProgressionComponent>(world, -1), RawPool.Of<ProgressionConfigComponent>(world, -1)),
            };
            var added = new List<AComponentConfig>(3);
            foreach (int e in jobs.Entities())
            {
                if (!IsNecromancer(jobs, e) || !world.IsEntityAlive(e, size) || !minion.Has(e) || dead.Has(e) || score.Has(e)) continue;
                added.Clear();
                foreach (var p in pairs)
                    if (!p.Component.Has(e) && !p.ConfigComponent.Has(e) && p.Config.AddComponents(world, e, false)) added.Add(p.Config);
                foreach (var c in added) c.SetupComponents(world, _gameData, e, false);
                _granted++;
                if (_granted <= 20) Plugin.Logger.LogInfo($"[Healing] {Name(e, "necromancer")} can now heal vampires lying in an infirmary bed ({added.Count} component(s) of the pharmagician job added)");
                else if (_granted == 21) Plugin.Logger.LogInfo("[Healing] further necromancers given the heal are not logged one by one");
            }
        }

        private static void Strip(EcsWorld world, int size)
        {
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var score = RawPool.Of<AIComputeHealScoreComponent>(world, -1);
            int count = 0;
            foreach (int e in score.Entities())
            {
                if (!world.IsEntityAlive(e, size) || !IsNecromancer(jobs, e)) continue;
                Del<AIComputeHealScoreComponent>(world, e);
                Del<AIComputeHealScoreConfigComponent>(world, e);
                Del<DurationComponent>(world, e);
                Del<DurationConfigComponent>(world, e);
                Del<ProgressionComponent>(world, e);
                Del<ProgressionConfigComponent>(world, e);
                count++;
            }
            if (count > 0) Plugin.Logger.LogInfo($"[Healing] setting off: the pharmagician's heal components removed from {count} necromancer(s)");
        }

        private static void Del<T>(EcsWorld world, int e)
        {
            IEcsPool pool = world.GetPoolByType(Il2CppType.Of<T>());
            if (pool != null && pool.Has(e)) pool.Del(e, false);
        }

        // ---- The caches the hooks read, twice a second ----

        private static bool IsVampireMinion(RawPool origins, RawPool minions, int e)
        {
            if (!minions.Has(e)) return false;
            IntPtr o = origins.Item(e);
            return o != IntPtr.Zero && ((OriginComponent*)o)->Origin == OriginType.VAMPIRE;
        }

        private static void Refresh(EcsWorld world, int size)
        {
            NecroHealers.Clear();
            OtherHealers.Clear();
            NecroAllowed.Clear();
            _freeVampire = false;
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var outside = RawPool.Of<OutsideDungeonTag>(world, -1);
            foreach (int e in RawPool.Of<AIComputeHealScoreComponent>(world, -1).Entities())
            {
                if (!world.IsEntityAlive(e, size) || dead.Has(e) || outside.Has(e)) continue;
                (IsNecromancer(jobs, e) ? NecroHealers : OtherHealers).Add(e);
            }
            if (NecroHealers.Count == 0) return;
            var requests = RawPool.Of<RequestHealComponent>(world, 8);
            var origins = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent));
            var minions = RawPool.Of<MinionTag>(world, -1);
            foreach (int e in RawPool.Of<RequestToBeServedComponent>(world, -1).Entities())
            {
                IntPtr r = requests.Item(e);
                if (r == IntPtr.Zero || dead.Has(e) || !IsVampireMinion(origins, minions, e)) continue;
                int healer = *(int*)(r + _healer);
                if (healer == -1) _freeVampire = true;
                else NecroAllowed.Add(healer);
            }
        }

        // A necromancer's HEAL score, just before the behaviours are chosen.
        internal static void BeforeSwitch()
        {
            if (_broken || (NecroHealers.Count == 0 && Calls.Count == 0) || !GameContext.TryWorld(out var world, out _)) return;
            try
            {
                if (NecroHealers.Count > 0 && !_freeVampire)
                {
                    var score = RawPool.Of<AIComputeHealScoreComponent>(world, -1);
                    foreach (int e in NecroHealers)
                    {
                        if (NecroAllowed.Contains(e)) continue;
                        IntPtr s = score.Item(e);
                        if (s == IntPtr.Zero) continue;
                        *(float*)(s + _score + _base) = 0f;
                        *(float*)(s + _score + _final) = 0f;
                    }
                }
                if (Calls.Count > 0) StopWork(world);
            }
            catch (Exception e) { Report("setting the necromancers' heal score", e); }
        }

        // The work scores of every called healer, set to 0 so that the game gives him BORED.
        private static void StopWork(EcsWorld world)
        {
            if (WorkScores.Count == 0)
            {
                void Add<T>()
                {
                    var pool = RawPool.Of<T>(world, -1);
                    if (!pool.IsNull) WorkScores.Add((pool, Il2CppRaw.ValueFieldOffset<T>("ScoreData")));
                }
                Add<AIComputeInvokeScoreComponent>();
                Add<AIComputeCarryCorpsesScoreComponent>();
                Add<AIComputeEnchantTrapScoreComponent>();
                Add<AIComputeHealScoreComponent>();
                Add<Discussion.AIComputeDiscussScoreComponent>();
                Add<AIComputeCurseItemScoreComponent>();
                Add<AIComputeMagicTrainingScoreComponent>();
                Add<AIComputeCraftResourcesScoreComponent>();
                Add<AIComputeSacrificeScoreComponent>();
                Add<AIComputeEnchantItemScoreComponent>();
                Add<AIComputeMixPotionScoreComponent>();
                Add<AIComputeReleaseAngerScoreComponent>();
            }
            foreach (int h in Calls.Keys)
                foreach (var (pool, offset) in WorkScores)
                {
                    IntPtr s = pool.Item(h);
                    if (s == IntPtr.Zero) continue;
                    *(float*)(s + offset + _base) = 0f;
                    *(float*)(s + offset + _final) = 0f;
                }
        }

        internal static bool IsCalled(IntPtr task)
        {
            if (_broken || Calls.Count == 0) return false;
            try { return Calls.ContainsKey(*(int*)(task + Il2CppRaw.FieldOffset(task, "m_entity"))); }
            catch { return false; }
        }

        // A called healer's next walk goes towards his patient.
        internal static bool WalkToPatient(FindRandomPositionTask task)
        {
            if (_broken || Calls.Count == 0) return false;
            try
            {
                int agent = *(int*)(task.Pointer + Il2CppRaw.FieldOffset(task.Pointer, "m_entity"));
                if (!Calls.TryGetValue(agent, out var call) || !GameContext.TryWorld(out var world, out _)) return false;
                IntPtr t = RawPool.Of<TransformComponent>(world, -1).Item(call.Patient);
                if (t == IntPtr.Zero) return false;
                float* p = (float*)(t + Il2CppRaw.ValueFieldOffset<TransformComponent>("Position"));
                task.PositionVariable.value = new Vector3(p[0], p[1], p[2]);
                task.EndAction(true);
                return true;
            }
            catch (Exception e) { Report("walking a healer to his patient", e); return false; }
        }

        // For a necromancer looking for a patient, every waiting patient but the vampires is marked as taken.
        internal static void BeforeFind(IntPtr task)
        {
            Restore();
            if (_broken || NecroHealers.Count == 0 || !GameContext.TryWorld(out var world, out _)) return;
            try
            {
                int agent = *(int*)(task + Il2CppRaw.FieldOffset(task, "m_entity"));
                if (!NecroHealers.Contains(agent)) return;
                var filter = new EcsFilter(Il2CppRaw.ReadObject(task, "m_minionsRequestingHealFilter", "EcsFilter"));
                var requests = RawPool.From(Il2CppRaw.ReadPointer(task, "m_requestHealPool"), 8, "RequestHealComponent");
                var origins = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent));
                var minions = RawPool.Of<MinionTag>(world, -1);
                int count = filter.GetEntitiesCount();
                var ids = filter.GetRawEntities();
                for (int i = 0; i < count; i++)
                {
                    int e = ids[i];
                    IntPtr r = requests.Item(e);
                    if (r == IntPtr.Zero || *(int*)(r + _healer) != -1 || IsVampireMinion(origins, minions, e)) continue;
                    *(int*)(r + _healer) = -2;
                    Hidden.Add(e);
                }
            }
            catch (Exception e) { Restore(); Report("choosing a necromancer's patient", e); }
        }

        internal static void Restore()
        {
            if (Hidden.Count == 0) return;
            try
            {
                if (GameContext.TryWorld(out var world, out _))
                {
                    var requests = RawPool.Of<RequestHealComponent>(world, 8);
                    foreach (int e in Hidden)
                    {
                        IntPtr r = requests.Item(e);
                        if (r != IntPtr.Zero && *(int*)(r + _healer) == -2) *(int*)(r + _healer) = -1;
                    }
                }
            }
            finally { Hidden.Clear(); }
        }

        // Whether a healer other than the minion exists for him: a necromancer counts only for a vampire.
        internal static bool AnyHealerFor(int minion)
        {
            if (_broken || NecroHealers.Count == 0) return true;
            foreach (int e in OtherHealers) if (e != minion) return true;
            if (!GameContext.TryWorld(out var world, out _)) return true;
            if (!IsVampireMinion(RawPool.Of<OriginComponent>(world, sizeof(OriginComponent)), RawPool.Of<MinionTag>(world, -1), minion)) return false;
            foreach (int e in NecroHealers) if (e != minion) return true;
            return false;
        }

        // ---- Heal at close range ----

        private static bool Fighting(RawPool brains, int e)
        {
            IntPtr b = brains.Item(e);
            if (b == IntPtr.Zero) return false;
            var current = *(EBehaviourType*)(b + _current);
            return current == EBehaviourType.COMBAT || current == EBehaviourType.ENEMY_COMBAT || current == EBehaviourType.USE_SKILL;
        }

        private static bool Place(RawPool coords, RawPool floors, int e, out Body body)
        {
            body = new Body { Entity = e };
            IntPtr c = coords.Item(e), f = floors.Item(e);
            if (c == IntPtr.Zero || f == IntPtr.Zero) return false;
            body.X = *(int*)c;
            body.Y = *(int*)(c + 4);
            body.Floor = *(int*)f;
            return true;
        }

        private static bool Near(Body a, Body b) => a.Floor == b.Floor && Math.Abs(a.X - b.X) <= Radius && Math.Abs(a.Y - b.Y) <= Radius;

        // Life and maximum of a life gauge; false when the entity has none.
        private static bool Life(RawPool lives, int e, out float life, out float max)
        {
            life = max = 0f;
            IntPtr l = lives.Item(e);
            if (l == IntPtr.Zero) return false;
            life = *(float*)(l + _gauge + _value);
            max = *(float*)(l + _gauge + _max + _computed);
            return max > 0f;
        }

        private static void Tick(EcsWorld world, int size, float dt)
        {
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var minions = RawPool.Of<MinionTag>(world, -1);
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var outside = RawPool.Of<OutsideDungeonTag>(world, -1);
            var coords = RawPool.Of<GridCoordinatesComponent>(world, 8);
            var floors = RawPool.Of<GridFloorComponent>(world, 4);
            var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, -1);
            var lives = RawPool.Of<LifePointsComponent>(world, 120);

            var healers = new List<(Body Body, JobType Job)>();
            foreach (int e in jobs.Entities())
            {
                var job = ((JobPracticedComponent*)jobs.Item(e))->JobPracticed;
                if (job != JobType.NECROMANCER && job != JobType.CULTIST) continue;
                if (!world.IsEntityAlive(e, size) || !minions.Has(e) || dead.Has(e) || outside.Has(e) || Fighting(brains, e)) continue;
                if (Place(coords, floors, e, out var body)) healers.Add((body, job));
            }
            var present = new HashSet<int>();
            foreach (var h in healers) present.Add(h.Body.Entity);
            var gone = new List<int>();
            foreach (var kv in Cares) if (!present.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (int e in gone) { Tended.Remove(Cares[e].Patient); Cares.Remove(e); }
            if (healers.Count == 0) return;

            // The wounded who may be healed at close range, with their kind.
            var undead = new List<Body>();
            var vampires = new List<Body>();
            var demons = new Dictionary<int, List<Body>>(); // by cultist
            bool Candidate(int e) => world.IsEntityAlive(e, size) && !dead.Has(e) && !outside.Has(e) && !Fighting(brains, e)
                                     && Life(lives, e, out float life, out float max) && life < max - 0.01f;
            foreach (int e in RawPool.Of<Undead.UndeadTag>(world, -1).Entities())
                if (Candidate(e) && Place(coords, floors, e, out var b)) undead.Add(b);
            var origins = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent));
            var requests = RawPool.Of<RequestHealComponent>(world, 8);
            foreach (int e in origins.Entities())
                if (IsVampireMinion(origins, minions, e) && !requests.Has(e) && Candidate(e) && Place(coords, floors, e, out var b)) vampires.Add(b);
            var links = RawPool.Of<Demon.CultistLinkComponent>(world, -1);
            foreach (int e in RawPool.Of<Demon.DemonTag>(world, -1).Entities())
            {
                IntPtr l = links.Item(e);
                if (l == IntPtr.Zero || !Candidate(e) || !Place(coords, floors, e, out var b)) continue;
                int cultist = *(int*)(l + _link + _id);
                short gen = *(short*)(l + _link + _gen);
                if (!world.IsEntityAlive(cultist, size) || world.GetEntityGen(cultist) != gen) continue;
                if (!demons.TryGetValue(cultist, out var list)) demons[cultist] = list = new List<Body>();
                list.Add(b);
            }

            UpdateCalls(world, size, healers, lives, undead, vampires, demons);

            foreach (var (healer, job) in healers)
            {
                int h = healer.Entity;
                if (Cares.TryGetValue(h, out var care))
                {
                    float life = 0f;
                    bool keep = world.IsEntityAlive(care.Patient, size) && world.GetEntityGen(care.Patient) == care.Gen
                                && IsListed(care.Patient, job, h, undead, vampires, demons, out var pb) && Near(healer, pb)
                                && Life(lives, care.Patient, out life, out _) && life >= care.LastLife - LossTolerance;
                    if (keep)
                    {
                        care.Elapsed += dt;
                        care.LastLife = life;
                        if (care.Elapsed < HealSeconds) continue;
                        Heal(world, lives, care.Patient, h, job, undead.Exists(u => u.Entity == care.Patient));
                    }
                    Tended.Remove(care.Patient);
                    Cares.Remove(h);
                    if (keep) continue;
                }
                // A new patient: the most wounded within reach that no one else tends.
                int best = -1;
                float bestRatio = 2f;
                void Consider(List<Body> list)
                {
                    if (list == null) return;
                    foreach (var p in list)
                    {
                        if (p.Entity == h || Tended.Contains(p.Entity) || !Near(healer, p)) continue;
                        if (!Life(lives, p.Entity, out float life, out float max)) continue;
                        float ratio = life / max;
                        if (ratio < bestRatio) { bestRatio = ratio; best = p.Entity; }
                    }
                }
                if (Calls.TryGetValue(h, out var called) && !Tended.Contains(called.Patient)
                    && IsListed(called.Patient, job, h, undead, vampires, demons, out var cb) && Near(healer, cb))
                    best = called.Patient;
                else if (job == JobType.NECROMANCER) { Consider(undead); Consider(vampires); }
                else if (demons.TryGetValue(h, out var own)) Consider(own);
                if (best < 0) continue;
                Life(lives, best, out float start, out _);
                Cares[h] = new Care { Patient = best, Gen = world.GetEntityGen(best), Elapsed = 0f, LastLife = start };
                Tended.Add(best);
            }
        }

        private static void UpdateCalls(EcsWorld world, int size, List<(Body Body, JobType Job)> healers, RawPool lives,
            List<Body> undead, List<Body> vampires, Dictionary<int, List<Body>> demons)
        {
            float below = Settings.HealCompulsionBelow.Value / 100f;
            var free = new Dictionary<int, (Body Body, JobType Job)>();
            foreach (var h in healers) free[h.Body.Entity] = h;

            // Calls under way: kept while the healer is free to walk and the patient is still wounded.
            foreach (int h in Calls.Keys.ToList())
            {
                var call = Calls[h];
                bool patientOk = world.IsEntityAlive(call.Patient, size) && world.GetEntityGen(call.Patient) == call.Gen
                                 && free.TryGetValue(h, out var hb) && IsListed(call.Patient, hb.Job, h, undead, vampires, demons, out _);
                if (patientOk && _gameTime - call.Since <= CallTimeout) continue;
                if (patientOk)
                {
                    _callsTimedOut++;
                    CallCooldown[call.Patient] = _gameTime + CallCooldownSeconds;
                }
                Calls.Remove(h);
            }
            if (below <= 0f) return;
            foreach (int h in Calls.Keys) free.Remove(h);
            foreach (int h in Cares.Keys) free.Remove(h);
            if (free.Count == 0) return;
            var calledPatients = new HashSet<int>(Calls.Values.Select(c => c.Patient));

            // The patients below the threshold, most wounded first.
            var patients = new List<(Body Body, float Ratio, string Kind, int Cultist)>();
            void Collect(List<Body> list, string kind, int cultist)
            {
                foreach (var b in list)
                {
                    if (calledPatients.Contains(b.Entity) || Tended.Contains(b.Entity)) continue;
                    if (CallCooldown.TryGetValue(b.Entity, out float until) && _gameTime < until) continue;
                    if (!Life(lives, b.Entity, out float life, out float max) || life / max >= below) continue;
                    patients.Add((b, life / max, kind, cultist));
                }
            }
            Collect(undead, "undead", -1);
            Collect(vampires, "vampire", -1);
            foreach (var kv in demons) Collect(kv.Value, "demon", kv.Key);

            static int Distance(Body a, Body b) => a.Floor == b.Floor ? Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) : 1000 + 100 * Math.Abs(a.Floor - b.Floor);
            foreach (var p in patients.OrderBy(x => x.Ratio))
            {
                int chosen = -1;
                if (p.Kind == "demon")
                {
                    if (free.ContainsKey(p.Cultist)) chosen = p.Cultist;
                }
                else
                {
                    int bestDistance = int.MaxValue;
                    foreach (var kv in free)
                    {
                        if (kv.Value.Job != JobType.NECROMANCER) continue;
                        int d = Distance(kv.Value.Body, p.Body);
                        if (d < bestDistance) { bestDistance = d; chosen = kv.Key; }
                    }
                }
                if (chosen < 0) continue;
                Calls[chosen] = new Call { Patient = p.Body.Entity, Gen = world.GetEntityGen(p.Body.Entity), Since = _gameTime, Kind = p.Kind };
                free.Remove(chosen);
                _callsMade++;
                _callsLogged++;
                if (_callsLogged <= 40)
                    Plugin.Logger.LogInfo($"[Healing] {Name(chosen, p.Kind == "demon" ? "cultist" : "necromancer")} leaves his work to heal {Name(p.Body.Entity, p.Kind)} ({p.Kind}, life {p.Ratio * 100f:0} %)");
                else if (_callsLogged == 41)
                    Plugin.Logger.LogInfo("[Healing] further calls are counted in the 5-minute summary only");
                if (free.Count == 0) break;
            }
        }

        private static bool IsListed(int e, JobType job, int healer, List<Body> undead, List<Body> vampires, Dictionary<int, List<Body>> demons, out Body body)
        {
            body = default;
            List<Body>[] lists = job == JobType.NECROMANCER
                ? new[] { undead, vampires }
                : new[] { demons.TryGetValue(healer, out var own) ? own : null };
            foreach (var list in lists)
            {
                if (list == null) continue;
                int i = list.FindIndex(b => b.Entity == e);
                if (i >= 0) { body = list[i]; return true; }
            }
            return false;
        }

        // The result of a pharmagician's heal: life at its maximum, healable states removed.
        private static void Heal(EcsWorld world, RawPool lives, int patient, int healer, JobType job, bool isUndead)
        {
            IntPtr l = lives.Item(patient);
            if (l == IntPtr.Zero) return;
            float max = *(float*)(l + _gauge + _max + _computed);
            *(float*)(l + _gauge + _value) = max;
            *(float*)(l + _gauge + _percentage) = 100f;

            int removed = 0;
            IntPtr handler = RawPool.Of<EntityStateHandlerComponent>(world, 16).Item(patient);
            if (handler != IntPtr.Zero)
            {
                IntPtr list = *(IntPtr*)(handler + _states);
                if (list != IntPtr.Zero)
                {
                    IntPtr data = *(IntPtr*)list;
                    int length = *(int*)(list + IntPtr.Size);
                    var healable = RawPool.Of<CanBeHealedTagComponent>(world, -1);
                    var states = new List<int>();
                    if (data != IntPtr.Zero && length > 0 && length <= 512)
                        for (int i = 0; i < length; i++)
                        {
                            int s = *(int*)(data + i * 4);
                            if (healable.Has(s)) states.Add(s);
                        }
                    if (states.Count > 0)
                    {
                        var utility = new StatesUtility(Il2CppRaw.ReadObject(GameContext.Minions.Pointer, "m_statesUtility", "StatesUtility"));
                        foreach (int s in states) { utility.RemoveState(patient, s); removed++; }
                    }
                }
            }

            if (Calls.TryGetValue(healer, out var call) && call.Patient == patient)
            {
                Calls.Remove(healer);
                _callsDone++;
            }
            string kind = job == JobType.CULTIST ? "demon" : isUndead ? "undead" : "vampire";
            if (kind == "demon") _healedDemons++;
            else if (kind == "undead") _healedUndead++;
            else _healedVampires++;
            _logged++;
            if (_logged <= 40)
                Plugin.Logger.LogInfo($"[Healing] {Name(healer, job == JobType.CULTIST ? "cultist" : "necromancer")} healed {Name(patient, kind)} ({kind}) at close range: life {max:0.#}" + (removed > 0 ? $", {removed} healable state(s) removed" : ""));
            else if (_logged == 41)
                Plugin.Logger.LogInfo("[Healing] further heals at close range are counted in the 5-minute summary only");
        }
    }

    [HarmonyPatch(typeof(AISwitchBehaviourSystem), nameof(AISwitchBehaviourSystem.Run))]
    internal static class NecromancerHealScores
    {
        private static bool Prepare() => Settings.NecromancersAndCultistsHeal.Value;
        private static void Prefix() => NecromancerCultistHealing.BeforeSwitch();
    }

    // A called healer walks towards his patient instead of a random place, and does not stop to chat.
    [HarmonyPatch(typeof(FindRandomPositionTask), "OnExecute")]
    internal static class HealCallWalk
    {
        private static bool Prepare() => Settings.NecromancersAndCultistsHeal.Value && Settings.HealCompulsionBelow.Value > 0;
        private static bool Prefix(FindRandomPositionTask __instance) => !NecromancerCultistHealing.WalkToPatient(__instance);
    }

    [HarmonyPatch(typeof(Discussion.FindEntityToDiscussWithTask), "OnExecute")]
    internal static class HealCallNoChat
    {
        private static bool Prepare() => Settings.NecromancersAndCultistsHeal.Value && Settings.HealCompulsionBelow.Value > 0;
        private static bool Prefix(Discussion.FindEntityToDiscussWithTask __instance)
        {
            if (!NecromancerCultistHealing.IsCalled(__instance.Pointer)) return true;
            __instance.EndAction(false);
            return false;
        }
    }

    [HarmonyPatch(typeof(FindMinionToHealTask), "OnExecute")]
    internal static class NecromancerHealTargets
    {
        private static bool Prepare() => Settings.NecromancersAndCultistsHeal.Value;
        private static void Prefix(FindMinionToHealTask __instance) => NecromancerCultistHealing.BeforeFind(__instance.Pointer);
        private static void Postfix() => NecromancerCultistHealing.Restore();
    }

    [HarmonyPatch(typeof(MinionUtility), nameof(MinionUtility.IsTherePharmagicianInDungeon))]
    internal static class NecromancerHealWait
    {
        private static bool Prepare() => Settings.NecromancersAndCultistsHeal.Value;
        private static void Postfix(int __0, ref bool __result)
        {
            if (__result && __0 >= 0) __result = NecromancerCultistHealing.AnyHealerFor(__0);
        }
    }
}
