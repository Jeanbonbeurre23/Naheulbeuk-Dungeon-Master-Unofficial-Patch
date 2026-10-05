using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Management
{
    // Recruitment targets: how many minions of each job and origin the dungeon keeps.
    //
    // Game 1.8, from the method bodies and the configs bundle, read on 2 October 2026. The recruitment list
    // (RecruitmentComponent.EntitiesAvailableForRecruitment, on the player entity) holds the candidates, entities with
    // CandidateToRecruitTagComponent. With the recruitment roll activated (RecruitmentComponent.IsRollActivated),
    // RecruitmentUtility.DrawCandidates, run every decade and by the paid reroll, deletes them and creates one candidate
    // per recruitable job (MinionJobsUtility.IsJobRecruitable; MinionJobInfoConfig.m_isRecruitable is set for domestic,
    // cook, artisan, sorcerer, pharmagician, cultist, banker, torturer, necromancer and spy) for every origin available
    // for that job (CreateAllNewCandidatesForJob, OriginsUtility.GetAllOriginsAvailablesForJob; each recruitable
    // OriginInfoConfig lists its m_availableJobs), then adds unique characters.
    // RecruitCandidateAtIndex turns a candidate into a minion arriving by the teleporter and, with the roll activated,
    // creates a new candidate of the same job and origin. It takes no gold; the minion costs his salary every decade.
    // The candidate's sheet (MinionDetailsTooltip in RECRUITMENT mode) recruits with OnButtonRecruit, which refuses
    // when MinionUtility.HasEnoughPlaceForMinions(1) is true: despite its name, that call is true when one more minion
    // would go over the maximum. Guards and barmen never come through this list: the game hires them for each guard
    // locker and tavern counter.
    //
    // In that sheet, a row "Keep - N +" under or beside the Recruit button sets the target for the candidate's job
    // and origin (Shift+click: 5 at a time). Every 3 seconds of game time, for each target above the number of
    // minions of that job and origin, the patch recruits one listed candidate of that job and origin: through the
    // sheet's own OnButtonRecruit when that candidate is the one on screen, so the game shows its feedback and selects
    // the next candidate, otherwise through RecruitCandidateAtIndex, the call the button makes. A target is a minimum:
    // nobody is fired. A minion counts when he is alive, not a candidate, not unique, not a VIP, and not resigning or
    // leaving the dungeon, unless he leaves it for a raid. Targets are saved per save file, next to the room
    // assignments, in NDMUnofficialPatch-data\<save>.recruitment.tsv.
    internal static unsafe class RecruitTargets
    {
        internal static readonly SortedDictionary<(JobType Job, OriginType Origin), int> Targets = new();
        private const float TickSeconds = 3f;
        private const int MaxTarget = 999;

        private static IntPtr _system, _recruitment;
        private static float _nextTick, _nextLabel;
        private static readonly Dictionary<string, string> Notes = new();
        private static readonly HashSet<string> ErrorsLogged = new();
        private static readonly Dictionary<IntPtr, Row> Rows = new();
        private static string _pendingLoadPath;
        private static DateTime _pendingLoadTime;

        private sealed class Row
        {
            public MinionDetailsTooltip Tooltip;
            public GameObject Root;
            public TextMeshProUGUI Value, Have;
            public bool Failed;
            public (JobType, OriginType)? Key;
        }

        internal static void ReportError(string what, Exception e)
        {
            if (!ErrorsLogged.Add(what + e.Message)) return;
            Plugin.Logger.LogWarning($"[Recruit] error while {what}: {e.Message}");
        }

        // A line logged when it differs from the last one under the same key, so a state is reported once.
        private static void Note(string key, string text)
        {
            if (Notes.TryGetValue(key, out var last) && last == text) return;
            Notes[key] = text;
            if (text.Length > 0) Plugin.Logger.LogInfo("[Recruit] " + text);
        }

        internal static string Label((JobType Job, OriginType Origin) key) => $"{Names.Job(key.Job)}, {Origin(key.Origin)}";
        private static string Origin(OriginType o)
        {
            string s = o.ToString().ToLowerInvariant();
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // ---- Capture of the game's RecruitmentUtility ---------------------------------------------------------

        internal static void CaptureSystem(IntPtr system)
        {
            if (system == _system) return;
            _system = system;
            _recruitment = Il2CppRaw.ReadObject(system, "m_recruitmentUtility", "RecruitmentUtility");
        }

        private static bool TryUtility(out RecruitmentUtility utility)
        {
            utility = null;
            if (_recruitment == IntPtr.Zero || !GameContext.Ready) return false;
            if (Il2CppRaw.ReadPointer(_recruitment, "m_world") != GameContext.WorldPointer) return false;
            utility = new RecruitmentUtility(_recruitment);
            return true;
        }

        // ---- Counts ----------------------------------------------------------------------------------------------

        private static Dictionary<(JobType, OriginType), int> CountMinions(EcsWorld world, int size)
        {
            var counts = new Dictionary<(JobType, OriginType), int>();
            var job = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var origin = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent));
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var candidate = RawPool.Of<CandidateToRecruitTagComponent>(world, -1);
            var unique = RawPool.Of<UniqueComponent>(world, -1);
            var vip = RawPool.Of<VipTag>(world, -1);
            var resign = RawPool.Of<ResignTagComponent>(world, -1);
            var leave = RawPool.Of<LeaveDungeonTag>(world, -1);
            var exit = RawPool.Of<ExitDungeonTagComponent>(world, -1);
            var raid = RawPool.Of<Raid.RaidParticipantTagComponent>(world, -1);
            foreach (int e in RawPool.Of<MinionTag>(world, -1).Entities())
            {
                if (!world.IsEntityAlive(e, size) || dead.Has(e) || candidate.Has(e) || unique.Has(e) || vip.Has(e)) continue;
                if (!raid.Has(e) && (resign.Has(e) || leave.Has(e) || exit.Has(e))) continue;
                IntPtr j = job.Item(e), o = origin.Item(e);
                if (j == IntPtr.Zero || o == IntPtr.Zero) continue;
                var key = (((JobPracticedComponent*)j)->JobPracticed, ((OriginComponent*)o)->Origin);
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            return counts;
        }

        // The job and origin of a listed, non-unique candidate, or null.
        private static (JobType, OriginType)? CandidateKey(EcsWorld world, int size, int entity)
        {
            if (!world.IsEntityAlive(entity, size)) return null;
            if (!RawPool.Of<CandidateToRecruitTagComponent>(world, -1).Has(entity)) return null;
            if (RawPool.Of<UniqueComponent>(world, -1).Has(entity) || RawPool.Of<MinionUniqueToRecruitConfigComponent>(world, -1).Has(entity)) return null;
            IntPtr j = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent)).Item(entity);
            IntPtr o = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent)).Item(entity);
            if (j == IntPtr.Zero || o == IntPtr.Zero) return null;
            return (((JobPracticedComponent*)j)->JobPracticed, ((OriginComponent*)o)->Origin);
        }

        private static int FindCandidate(EcsWorld world, int size, (JobType, OriginType) key)
        {
            foreach (int e in RawPool.Of<CandidateToRecruitTagComponent>(world, -1).Entities())
                if (CandidateKey(world, size, e) is { } k && k.Equals(key)) return e;
            return -1;
        }

        // ---- The top-up, every TickSeconds of game time ----------------------------------------------------

        private static void Tick()
        {
            if (Targets.Count == 0 || !TryUtility(out var utility) || !GameContext.TryWorld(out var world, out int size)) return;
            if (!utility.IsRecruitmentAvailable())
            {
                Note("available", "recruitment is not available in this game yet; the targets wait");
                return;
            }
            Note("available", "");
            var minions = GameContext.Minions;
            var counts = CountMinions(world, size);
            foreach (var kv in Targets.ToList())
            {
                int have = counts.TryGetValue(kv.Key, out int n) ? n : 0;
                string label = Label(kv.Key);
                if (have >= kv.Value) { Note(label, ""); continue; }
                if (minions.HasEnoughPlaceForMinions(1))
                {
                    Note("full", "the dungeon has reached its maximum number of minions; the targets wait");
                    return;
                }
                Note("full", "");
                int candidate = FindCandidate(world, size, kv.Key);
                if (candidate < 0)
                {
                    Note(label, $"{label}: no candidate of this job and origin in the recruitment list; the target ({have} of {kv.Value}) waits");
                    continue;
                }
                string name = Describe(minions, candidate);
                short gen = world.GetEntityGen(candidate);
                var shown = ShownOn(candidate);
                if (shown != null)
                    shown.OnButtonRecruit();
                else
                {
                    int index = utility.GetCandidateIndex(candidate);
                    if (index < 0)
                    {
                        Note(label, $"{label}: the candidate found is not in the recruitment list; the target waits");
                        continue;
                    }
                    utility.RecruitCandidateAtIndex(index);
                }
                // The recruited candidate is deleted; the game may give his id to the new candidate it creates, with a
                // new generation.
                bool done = !world.IsEntityAlive(candidate, size) || world.GetEntityGen(candidate) != gen
                    || !RawPool.Of<CandidateToRecruitTagComponent>(world, -1).Has(candidate);
                Note(label, "");
                Plugin.Logger.LogInfo(done
                    ? $"[Recruit] {name} recruited for the target {label}: {have + 1} of {kv.Value}"
                    : $"[Recruit] {name} could not be recruited for the target {label} (the game refused); the target waits");
            }
        }

        private static string Describe(MinionUtility minions, int entity)
        {
            string name = null, grade = null;
            try { name = minions.GetMinionFullName(entity); } catch { }
            try { grade = minions.GetMinionGradeType(entity).ToString(); } catch { }
            return (string.IsNullOrWhiteSpace(name) ? $"candidate {entity}" : name) + (grade == null ? "" : $" ({grade})");
        }

        // The sheet showing this candidate in RECRUITMENT mode, if one is open on him.
        private static MinionDetailsTooltip ShownOn(int candidate)
        {
            foreach (var row in Rows.Values)
            {
                var t = row.Tooltip;
                if (t == null || t.WasCollected || !t || !t.gameObject.activeInHierarchy) continue;
                if (t.m_currentMode == MinionDetailsTooltip.EMode.RECRUITMENT && t.m_targetEntity.Id == candidate) return t;
            }
            return null;
        }

        // ---- Called every frame from ManagerBehaviour ------------------------------------------------------------

        internal static void Update()
        {
            try { UpdateRows(); }
            catch (Exception e) { ReportError("updating the recruitment sheet", e); }
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickSeconds;
            try { Tick(); }
            catch (Exception e) { ReportError("recruiting for the targets", e); }
        }

        internal static void OnTarget(MinionDetailsTooltip tooltip)
        {
            if (!Rows.ContainsKey(tooltip.Pointer)) Rows[tooltip.Pointer] = new Row { Tooltip = tooltip };
            _nextLabel = 0f;
        }

        private static void UpdateRows()
        {
            bool refresh = Time.unscaledTime >= _nextLabel;
            if (refresh) _nextLabel = Time.unscaledTime + 0.5f;
            foreach (var kv in Rows.ToList())
            {
                var row = kv.Value;
                var t = row.Tooltip;
                if (t == null || t.WasCollected || !t)
                {
                    Rows.Remove(kv.Key);
                    continue;
                }
                if (!refresh) continue;
                (JobType, OriginType)? key = null;
                if (t.gameObject.activeInHierarchy && t.m_currentMode == MinionDetailsTooltip.EMode.RECRUITMENT
                    && GameContext.TryWorld(out var world, out int size))
                    key = CandidateKey(world, size, t.m_targetEntity.Id);
                row.Key = key;
                if (key == null)
                {
                    if (row.Root != null && row.Root.activeSelf) row.Root.SetActive(false);
                    continue;
                }
                if (row.Root == null && !row.Failed) Build(row);
                if (row.Root == null) continue;
                if (!row.Root.activeSelf) row.Root.SetActive(true);
                Show(row);
            }
        }

        private static void Show(Row row)
        {
            if (row.Key is not { } key || !GameContext.TryWorld(out var world, out int size)) return;
            int keep = Targets.TryGetValue(key, out int k) ? k : 0;
            int have = CountMinions(world, size).TryGetValue(key, out int n) ? n : 0;
            row.Value.text = keep.ToString(CultureInfo.InvariantCulture);
            string state = keep == 0 ? "" : have >= keep ? ", kept" : GameContext.Minions.HasEnoughPlaceForMinions(1) ? ", dungeon full" : ", recruiting";
            row.Have.text = $"you have {have}{state}";
        }

        private static void Change(Row row, int direction)
        {
            try
            {
                if (row.Key is not { } key) return;
                var kb = Keyboard.current;
                int step = kb != null && kb.shiftKey.isPressed ? 5 : 1;
                int before = Targets.TryGetValue(key, out int k) ? k : 0;
                int after = Math.Clamp(before + direction * step, 0, MaxTarget);
                if (after == before) return;
                if (after == 0) Targets.Remove(key);
                else Targets[key] = after;
                Plugin.Logger.LogInfo($"[Recruit] target {Label(key)}: keep {after}" + (after == 0 ? " (target removed)" : ""));
                Show(row);
                _nextTick = Math.Min(_nextTick, Time.time + 0.5f); // the first recruit follows the click
            }
            catch (Exception e) { ReportError("changing a target", e); }
        }

        // ---- The row in the sheet --------------------------------------------------------------------------------

        private static void Build(Row row)
        {
            try
            {
                var t = row.Tooltip;
                var recruit = t.m_buttons[MinionDetailsTooltip.EButton.RECRUIT]?.Button;
                if (recruit == null) throw new InvalidOperationException("the sheet has no Recruit button");
                var recruitRect = recruit.GetComponent<RectTransform>();
                var label = recruit.GetComponentInChildren<TextMeshProUGUI>(true) ?? t.m_minionNameText;
                if (label == null) throw new InvalidOperationException("no text to copy the style from");
                Transform parent = recruitRect.parent;
                float height = Math.Max(40f, recruitRect.rect.height);
                float size = Math.Max(20f, label.fontSize);

                var root = new GameObject("NDMUP_RecruitTarget");
                var rect = root.AddComponent<RectTransform>();
                rect.SetParent(parent, false);
                var h = root.AddComponent<HorizontalLayoutGroup>();
                h.childControlWidth = true;
                h.childControlHeight = true;
                h.childForceExpandWidth = false;
                h.childForceExpandHeight = true;
                h.spacing = 10f;
                h.childAlignment = TextAnchor.MiddleCenter;
                var le = root.AddComponent<LayoutElement>();
                le.minHeight = height;
                le.preferredHeight = height;
                le.preferredWidth = 420f;

                Text(label, root.transform, "Keep", size, height, 90f);
                Link(label, root.transform, "−", size * 1.3f, height, 44f, () => Change(row, -1));
                row.Value = Text(label, root.transform, "0", size, height, 60f);
                Link(label, root.transform, "+", size * 1.3f, height, 44f, () => Change(row, +1));
                row.Have = Text(label, root.transform, "", size * 0.8f, height, 200f);

                var group = parent.GetComponent<LayoutGroup>();
                rect.SetSiblingIndex(recruitRect.GetSiblingIndex() + 1);
                if (group == null)
                {
                    // No layout to place it: under the button, same anchors.
                    rect.anchorMin = recruitRect.anchorMin;
                    rect.anchorMax = recruitRect.anchorMax;
                    rect.pivot = recruitRect.pivot;
                    rect.sizeDelta = new Vector2(420f, height);
                    rect.anchoredPosition = recruitRect.anchoredPosition + new Vector2(0f, -(height + 8f));
                }
                row.Root = root;
                Plugin.Logger.LogInfo($"[Recruit] Keep row added to the recruitment sheet; {Hierarchy(recruitRect)}");
            }
            catch (Exception e)
            {
                row.Failed = true;
                ReportError("building the Keep row of the recruitment sheet", e);
            }
        }

        // Where the row went, for the log: the Recruit button, its parent's layout and its siblings.
        private static string Hierarchy(RectTransform button)
        {
            var sb = new StringBuilder("Recruit button ");
            sb.Append(button.name).Append(' ').Append(button.rect.width.ToString("0")).Append('x').Append(button.rect.height.ToString("0"));
            var parent = button.parent;
            if (parent == null) return sb.ToString();
            var group = parent.GetComponent<LayoutGroup>();
            sb.Append(" in ").Append(parent.name).Append(" (").Append(group == null ? "no layout group" : group.GetIl2CppType().Name).Append("), siblings:");
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                sb.Append(' ').Append(c.name).Append(c.gameObject.activeSelf ? "" : "(hidden)");
            }
            return sb.ToString();
        }

        private static TextMeshProUGUI Text(TextMeshProUGUI template, Transform parent, string text, float size, float height, float width)
        {
            var go = Object.Instantiate(template.gameObject, parent, false).Cast<GameObject>();
            go.name = "Text";
            foreach (var localized in go.GetComponents<Aube.LocalizedText>()) Object.DestroyImmediate(localized);
            foreach (var fitter in go.GetComponents<ContentSizeFitter>()) Object.DestroyImmediate(fitter);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.enableAutoSizing = false;
            tmp.fontSize = size;
            tmp.enableWordWrapping = false;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.text = text;
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.minWidth = width;
            le.preferredWidth = width;
            return tmp;
        }

        private static void Link(TextMeshProUGUI template, Transform parent, string text, float size, float height, float width, Action onClick)
        {
            var tmp = Text(template, parent, text, size, height, width);
            tmp.raycastTarget = true;
            var button = tmp.gameObject.AddComponent<Button>();
            button.targetGraphic = tmp;
            button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(onClick));
        }

        // ---- Persistence -----------------------------------------------------------------------------------------

        private static string DataPath(string savePath)
        {
            string saveDir = Path.GetDirectoryName(savePath);
            string root = Path.GetDirectoryName(saveDir) ?? saveDir;
            return Path.Combine(root, "NDMUnofficialPatch-data", Path.GetFileNameWithoutExtension(savePath) + ".recruitment.tsv");
        }

        private static bool IsProfile(string name) => name != null && name.StartsWith("Player_Profile", StringComparison.OrdinalIgnoreCase);

        internal static void OnSave(string filename, string savePath)
        {
            if (string.IsNullOrEmpty(savePath) || IsProfile(filename)) return;
            try
            {
                string path = DataPath(savePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string> { "# NDM Unofficial Patch, recruitment targets: job, origin, number of minions to keep" };
                foreach (var kv in Targets) lines.Add($"{kv.Key.Job}\t{kv.Key.Origin}\t{kv.Value.ToString(CultureInfo.InvariantCulture)}");
                File.WriteAllLines(path, lines);
                Plugin.Logger.LogInfo($"[Recruit] {Targets.Count} target(s) written to {path}");
            }
            catch (Exception e) { ReportError("writing the targets", e); }
        }

        internal static void OnLoad(string filename, string savePath)
        {
            if (IsProfile(filename)) return;
            // The main menu also reads every save to list it, so only the last read before a new world counts.
            _pendingLoadPath = string.IsNullOrEmpty(savePath) ? null : DataPath(savePath);
            _pendingLoadTime = DateTime.Now;
        }

        // A new game starts: the save the main menu read last was only listed, and its targets are not restored.
        internal static void OnNewGame()
        {
            if (_pendingLoadPath != null) Plugin.Logger.LogInfo("[Recruit] new game: no saved target restored");
            _pendingLoadPath = null;
        }

        internal static void OnWorldChanged()
        {
            Targets.Clear();
            Notes.Clear();
            if (_pendingLoadPath == null) return;
            string path = _pendingLoadPath;
            _pendingLoadPath = null;
            if ((DateTime.Now - _pendingLoadTime).TotalMinutes > 3) return; // a save listed in the menu, then a new game
            try
            {
                if (!File.Exists(path)) return;
                int dropped = 0;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split('\t');
                    if (p.Length >= 3 && Enum.TryParse(p[0], out JobType job) && Enum.TryParse(p[1], out OriginType origin)
                        && int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                        Targets[(job, origin)] = Math.Min(n, MaxTarget);
                    else dropped++;
                }
                Plugin.Logger.LogInfo($"[Recruit] targets restored from {path}: " + (Targets.Count == 0 ? "none" : string.Join("; ", Targets.Select(kv => $"{Label(kv.Key)} {kv.Value}")))
                    + (dropped > 0 ? $"; {dropped} line(s) not understood" : ""));
            }
            catch (Exception e) { ReportError("reading the targets", e); }
        }
    }

    [HarmonyPatch(typeof(UpdateRecruitmentProcessSystem), nameof(UpdateRecruitmentProcessSystem.Run))]
    internal static class RecruitTargetsCapturePatch
    {
        private static bool Prepare() => Settings.RecruitTargets.Value;
        private static void Postfix(UpdateRecruitmentProcessSystem __instance)
        {
            try { RecruitTargets.CaptureSystem(__instance.Pointer); }
            catch (Exception e) { RecruitTargets.ReportError("capturing the recruitment utility", e); }
        }
    }

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.SetTarget))]
    internal static class RecruitTargetsSheetPatch
    {
        private static bool Prepare() => Settings.RecruitTargets.Value;
        private static void Postfix(MinionDetailsTooltip __instance)
        {
            try { RecruitTargets.OnTarget(__instance); }
            catch (Exception e) { RecruitTargets.ReportError("following the recruitment sheet", e); }
        }
    }

    [HarmonyPatch(typeof(Aube.SaveManagerStandalone), nameof(Aube.SaveManagerStandalone.Save))]
    internal static class RecruitTargetsSavePatch
    {
        private static bool Prepare() => Settings.RecruitTargets.Value;
        private static void Prefix(Aube.SaveManagerStandalone __instance, Aube.SaveManager.SaveRequest request)
        {
            try
            {
                string name = request?.Filename;
                if (!string.IsNullOrEmpty(name)) RecruitTargets.OnSave(name, __instance.CreatePath(name));
            }
            catch (Exception e) { RecruitTargets.ReportError("saving the targets", e); }
        }
    }

    [HarmonyPatch(typeof(Aube.SaveManagerStandalone), nameof(Aube.SaveManagerStandalone.Load))]
    internal static class RecruitTargetsLoadPatch
    {
        private static bool Prepare() => Settings.RecruitTargets.Value;
        private static void Prefix(Aube.SaveManagerStandalone __instance, Aube.SaveManager.LoadRequest request)
        {
            try
            {
                string name = request?.Filename;
                if (!string.IsNullOrEmpty(name)) RecruitTargets.OnLoad(name, __instance.CreatePath(name));
            }
            catch (Exception e) { RecruitTargets.ReportError("noting the save being loaded", e); }
        }
    }
}
