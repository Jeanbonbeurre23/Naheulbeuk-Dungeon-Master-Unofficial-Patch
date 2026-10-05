using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Management
{
    // A third page, "Assigned rooms", in the character sheet of the Minions window.
    //
    // The sheet is MinionDetailsTooltip (prefab UI_NPCManagement, docked to the right of the Minions window). In
    // MANAGEMENT mode it pages between ContentManagement_1 (identity, gauges, moods) and ContentManagement_2 (rules,
    // traits, alterations) with the arrows of UI_Paging, through NextContent, PreviousContent and the checks
    // NextContentEnableCheck and PreviousContentEnableCheck, which the sheet's buttons read every frame.
    //
    // The page is built once per sheet from the game's own pieces: title and body texts cloned from ContentManagement_2,
    // arrows drawn with the sprites of the paging arrows. It sits over the content area of the sheet. While it is shown,
    // the game's current page is made transparent through its CanvasGroup (restored when the page closes), so the
    // game's own page animations are never touched. Past the last game page, the right arrow opens this page; the left
    // arrow closes it.
    //
    // Below the slots, the Forbidden rooms section lists the rooms forbidden to the character, each with links to show
    // it and to lift the ban, and a link to forbid one more with a click in the dungeon. It shows at most BanLines
    // lines; when there are more bans, the last line counts the ones not shown.
    internal sealed class RoomsPage
    {
        private static readonly Dictionary<IntPtr, RoomsPage> Pages = new();
        private static readonly Regex Number = new(@"\d+");
        private const int BanLines = 4;

        private sealed class BanLine
        {
            public GameObject Line;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI Show;
            public TextMeshProUGUI Lift;
            public int Room = -1;
            public string Name;
            public Color Normal;
        }

        private sealed class Row
        {
            public Slot Slot;
            public TextMeshProUGUI Value;
            public TextMeshProUGUI Note;
            public TextMeshProUGUI Pick;
            public TextMeshProUGUI Show;
            public GameObject Left;
            public GameObject Right;
            public string Message;
            public float MessageUntil;
            public List<int> Ids = new() { -1 };
            public List<string> Labels = new() { "Game's choice" };
        }

        private readonly MinionDetailsTooltip _tooltip;
        private readonly Dictionary<Slot, Row> _rows = new();
        private readonly List<BanLine> _bans = new();
        private TextMeshProUGUI _banNote;
        private TextMeshProUGUI _banPick;
        private string _banMessage;
        private float _banMessageUntil;
        private GameObject _root;
        private TextMeshProUGUI _header;
        private bool _buildFailed;
        private bool _shown;
        private bool _eligible;
        private MinionInfo _minion;
        private List<RoomInfo> _rooms = new();
        private CanvasGroup _hiddenGroup;
        private float _hiddenAlpha;
        private bool _hiddenRaycasts;
        private bool _hiddenInteractable;
        private string _pageTextBefore;
        private string _maxWritten;
        private string _maxBase;

        private RoomsPage(MinionDetailsTooltip tooltip) => _tooltip = tooltip;

        internal static RoomsPage Get(MinionDetailsTooltip tooltip)
        {
            if (!Pages.TryGetValue(tooltip.Pointer, out var page))
            {
                page = new RoomsPage(tooltip);
                Pages[tooltip.Pointer] = page;
            }
            return page;
        }

        internal bool Shown => _shown;
        internal bool Eligible => _eligible;

        // ---- Eligibility and page numbers --------------------------------------------------------------------

        // Only for a player minion (not unique, not a VIP) shown in MANAGEMENT mode.
        internal void Reevaluate()
        {
            bool before = _eligible;
            _eligible = false;
            try
            {
                if (_tooltip.m_currentMode == MinionDetailsTooltip.EMode.MANAGEMENT && GameContext.Ready)
                {
                    int target = _tooltip.m_targetEntity.Id;
                    var m = GameContext.ListMinions().FirstOrDefault(x => x.Entity == target);
                    if (m.Name != null) { _minion = m; _eligible = true; }
                }
            }
            catch (Exception e) { CharacterManager.ReportError("checking the character sheet", e); }
            if (!_eligible && _shown) Hide(true);
            if (before && !_eligible) RestoreMaxText();
        }

        // The sheet writes "current" and "last" page numbers into two texts; with this page, the last is one more.
        internal void AdjustMaxText()
        {
            if (!_eligible) return;
            var max = _tooltip.m_maxPageText;
            if (max == null) return;
            string text = max.text ?? "";
            if (text == _maxWritten) return;
            var match = Number.Match(text);
            if (!match.Success) return;
            _maxBase = text;
            _maxWritten = Number.Replace(text, (int.Parse(match.Value) + 1).ToString(), 1);
            max.text = _maxWritten;
        }

        private void RestoreMaxText()
        {
            var max = _tooltip.m_maxPageText;
            if (max != null && _maxWritten != null && max.text == _maxWritten && _maxBase != null) max.text = _maxBase;
            _maxWritten = null;
        }

        // ---- Showing and hiding ------------------------------------------------------------------------------

        internal void Show()
        {
            if (_shown || !_eligible) return;
            if (!Build()) return;
            try
            {
                var group = CurrentGamePage();
                if (group != null)
                {
                    _hiddenGroup = group;
                    _hiddenAlpha = group.alpha;
                    _hiddenRaycasts = group.blocksRaycasts;
                    _hiddenInteractable = group.interactable;
                }
                Refresh();
                _root.transform.SetAsLastSibling();
                _root.SetActive(true);
                _shown = true;
                var current = _tooltip.m_currentPageText;
                var max = _tooltip.m_maxPageText;
                if (current != null && max != null)
                {
                    _pageTextBefore = current.text;
                    var last = Number.Match(max.text ?? "");
                    if (last.Success) current.text = Number.Replace(current.text ?? "", last.Value, 1);
                }
            }
            catch (Exception e) { CharacterManager.ReportError("opening the rooms page", e); }
        }

        // The CanvasGroup of the game's page now shown. The sheet keeps its pages in m_contents, indexed by EContent; if
        // that lookup fails, the page is the visible content with the highest opacity.
        private CanvasGroup CurrentGamePage()
        {
            try
            {
                var pages = _tooltip.m_contents?.m_internalArray;
                int index = (int)_tooltip.m_currentContent;
                if (pages != null && index >= 0 && index < pages.Length && pages[index] != null)
                {
                    var group = pages[index].GetComponent<CanvasGroup>();
                    if (group != null) return group;
                }
            }
            catch (Exception e) { CharacterManager.ReportError("finding the sheet's current page", e); }

            CanvasGroup best = null;
            var contents = _root.transform.parent;
            for (int i = 0; i < contents.childCount; i++)
            {
                var child = contents.GetChild(i).gameObject;
                if (child == _root || !child.activeInHierarchy || !child.name.StartsWith("Content") || child.name == "Contentdown") continue;
                var group = child.GetComponent<CanvasGroup>();
                if (group != null && (best == null || group.alpha > best.alpha)) best = group;
            }
            return best;
        }

        internal void Hide(bool restoreGamePage)
        {
            RoomPicker.Cancel(this);
            if (!_shown) return;
            _shown = false;
            try
            {
                if (_root != null) _root.SetActive(false);
                if (_hiddenGroup != null)
                {
                    _hiddenGroup.alpha = _hiddenAlpha;
                    _hiddenGroup.blocksRaycasts = _hiddenRaycasts;
                    _hiddenGroup.interactable = _hiddenInteractable;
                }
                _hiddenGroup = null;
                var current = _tooltip.m_currentPageText;
                if (restoreGamePage && current != null && _pageTextBefore != null) current.text = _pageTextBefore;
            }
            catch (Exception e) { CharacterManager.ReportError("closing the rooms page", e); }
        }

        // After the Animator of the game's page has run, keep that page invisible while this one is shown.
        internal void LateUpdate()
        {
            if (!_shown || _hiddenGroup == null) return;
            _hiddenGroup.alpha = 0f;
            _hiddenGroup.blocksRaycasts = false;
            _hiddenGroup.interactable = false;
        }

        // ---- Building the page from the game's own pieces -----------------------------------------------------

        private static Transform FindDeep(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
                var found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private bool Build()
        {
            if (_root != null) return true;
            if (_buildFailed) return false;
            try
            {
                var sheet = _tooltip.transform;
                var content2 = FindDeep(sheet, "ContentManagement_2");
                var titleTemplate = content2 == null ? null : FindDeep(content2, "TitleRules")?.GetComponent<TextMeshProUGUI>();
                var bodyTemplate = content2 == null ? null : FindDeep(content2, "TextAlteration")?.GetComponent<TextMeshProUGUI>();
                var leftArrow = FindDeep(sheet, "UI_PagingArrowLeft")?.GetComponent<Image>();
                var rightArrow = FindDeep(sheet, "UI_PagingArrowRight")?.GetComponent<Image>();
                if (content2 == null || titleTemplate == null || bodyTemplate == null || leftArrow == null || rightArrow == null)
                    throw new InvalidOperationException("the sheet's layout differs from game 1.8");

                _root = new GameObject("NDMUnofficialPatch_RoomsPage");
                var rt = _root.AddComponent<RectTransform>();
                rt.SetParent(content2.parent, false);
                var model = content2.GetComponent<RectTransform>();
                rt.anchorMin = model.anchorMin;
                rt.anchorMax = model.anchorMax;
                rt.pivot = model.pivot;
                rt.offsetMin = model.offsetMin;
                rt.offsetMax = model.offsetMax;
                var layout = _root.AddComponent<VerticalLayoutGroup>();
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
                layout.spacing = 6f;
                _root.SetActive(false);

                _header = Text(titleTemplate, _root.transform, "Assigned rooms", 50f, 70f, TextAlignmentOptions.Left);
                Text(bodyTemplate, _root.transform,
                    "Arrows and Pick choose a room, Show moves the camera to it. An assigned room is a preference: when it is full, unfinished, below his prestige or not of the cleanliness his origin tries first, he goes elsewhere. He never looks for a prop in a forbidden room.",
                    26f, 130f, TextAlignmentOptions.TopLeft);

                foreach (var slot in CharacterManager.AllSlots)
                {
                    var row = new Row { Slot = slot };
                    var line = Container("Row" + slot, _root.transform, horizontal: true, height: 72f);
                    Text(titleTemplate, line.transform, CharacterManager.SlotName(slot), 32f, 72f, TextAlignmentOptions.Left, width: 330f);
                    var captured = row;
                    row.Left = Arrow(leftArrow, line.transform, () => Step(captured, -1));
                    row.Value = Text(bodyTemplate, line.transform, "", 32f, 72f, TextAlignmentOptions.Center, flexible: true);
                    row.Right = Arrow(rightArrow, line.transform, () => Step(captured, +1));
                    var under = Container("Links" + slot, _root.transform, horizontal: true, height: 36f);
                    row.Note = Text(bodyTemplate, under.transform, "", 22f, 36f, TextAlignmentOptions.Right, flexible: true);
                    row.Note.color = new Color(0.75f, 0.72f, 0.65f, 1f);
                    row.Show = Link(bodyTemplate, under.transform, "Show", 90f, () => OnShow(captured));
                    row.Pick = Link(bodyTemplate, under.transform, "Pick in dungeon", 220f, () => OnPick(captured));
                    _rows[slot] = row;
                }

                var banHead = Container("ForbiddenHead", _root.transform, horizontal: true, height: 56f);
                Text(titleTemplate, banHead.transform, "Forbidden rooms", 32f, 56f, TextAlignmentOptions.Left, width: 260f);
                _banNote = Text(bodyTemplate, banHead.transform, "", 22f, 56f, TextAlignmentOptions.Right, flexible: true);
                _banNote.color = new Color(0.75f, 0.72f, 0.65f, 1f);
                _banPick = Link(bodyTemplate, banHead.transform, "Forbid a room", 200f, OnForbid);
                for (int i = 0; i < BanLines; i++)
                {
                    var ban = new BanLine { Line = Container("Forbidden" + i, _root.transform, horizontal: true, height: 36f) };
                    ban.Label = Text(bodyTemplate, ban.Line.transform, "", 24f, 36f, TextAlignmentOptions.Left, flexible: true);
                    ban.Label.enableWordWrapping = false;
                    ban.Label.overflowMode = TextOverflowModes.Ellipsis;
                    ban.Normal = ban.Label.color;
                    var captured = ban;
                    ban.Show = Link(bodyTemplate, ban.Line.transform, "Show", 90f, () => OnShowBan(captured));
                    ban.Lift = Link(bodyTemplate, ban.Line.transform, "Lift", 90f, () => OnLift(captured));
                    _bans.Add(ban);
                }
                Plugin.Logger.LogInfo("[Manager] rooms page added to the character sheet");
                return true;
            }
            catch (Exception e)
            {
                _buildFailed = true;
                Plugin.Logger.LogWarning($"[Manager] the rooms page could not be built: {e.Message}");
                if (_root != null) Object.Destroy(_root);
                _root = null;
                return false;
            }
        }

        private static GameObject Container(string name, Transform parent, bool horizontal, float height)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>().SetParent(parent, false);
            if (horizontal)
            {
                var h = go.AddComponent<HorizontalLayoutGroup>();
                h.childControlWidth = true;
                h.childControlHeight = true;
                h.childForceExpandWidth = false;
                h.childForceExpandHeight = true;
                h.spacing = 8f;
                h.childAlignment = TextAnchor.MiddleLeft;
            }
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return go;
        }

        private static TextMeshProUGUI Text(TextMeshProUGUI template, Transform parent, string text, float size, float height,
            TextAlignmentOptions alignment, float width = -1f, bool flexible = false)
        {
            var go = Object.Instantiate(template.gameObject, parent, false).Cast<GameObject>();
            go.name = "Text";
            // A LocalizedText component would overwrite the text with the game's translation.
            foreach (var localized in go.GetComponents<Aube.LocalizedText>()) Object.DestroyImmediate(localized);
            // A size fitter on the template would fight the layout groups of this page.
            foreach (var fitter in go.GetComponents<ContentSizeFitter>()) Object.DestroyImmediate(fitter);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.enableAutoSizing = false;
            tmp.fontSize = size;
            tmp.enableWordWrapping = true;
            tmp.alignment = alignment;
            tmp.text = text;
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            if (width > 0) { le.minWidth = width; le.preferredWidth = width; }
            if (flexible) le.flexibleWidth = 1f;
            return tmp;
        }

        // A text that acts as a button, drawn like the sheet's own texts and underlined.
        private static TextMeshProUGUI Link(TextMeshProUGUI template, Transform parent, string text, float width, Action onClick)
        {
            var tmp = Text(template, parent, text, 24f, 36f, TextAlignmentOptions.Right, width: width);
            tmp.fontStyle = FontStyles.Underline;
            tmp.raycastTarget = true;
            var button = tmp.gameObject.AddComponent<Button>();
            button.targetGraphic = tmp;
            button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(onClick));
            return tmp;
        }

        private static GameObject Arrow(Image template, Transform parent, Action onClick)
        {
            var go = new GameObject("Arrow");
            go.AddComponent<RectTransform>().SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = template.sprite;
            image.color = template.color;
            image.preserveAspect = true;
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(onClick));
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 64f;
            le.preferredWidth = 64f;
            le.minHeight = 64f;
            le.preferredHeight = 64f;
            return go;
        }

        // ---- Contents ----------------------------------------------------------------------------------------

        internal void Refresh()
        {
            if (_root == null || !_eligible) return;
            try
            {
                _rooms = GameContext.ListRooms();
                _header.text = "Assigned rooms: " + _minion.Name;
                CharacterManager.ByMinion.TryGetValue(_minion.Entity, out var assignment);
                foreach (var row in _rows.Values)
                {
                    var types = CharacterManager.SlotTypes(row.Slot, _minion);
                    row.Ids = new List<int> { -1 };
                    row.Labels = new List<string> { "Game's choice" };
                    // A forbidden room is not offered for a slot; the ban has to be lifted first.
                    foreach (var r in _rooms.Where(r => types.Contains(r.Type) && !CharacterManager.IsForbidden(_minion, r)))
                    {
                        row.Ids.Add(r.Entity);
                        row.Labels.Add(r.Label);
                    }
                    bool available = types.Length > 0;
                    bool picking = RoomPicker.IsPicking(this, row.Slot);
                    row.Left.SetActive(available && !picking);
                    row.Right.SetActive(available && !picking);
                    int index = 0;
                    if (assignment != null && assignment.Rooms.TryGetValue(row.Slot, out var chosen))
                        index = Math.Max(0, row.Ids.IndexOf(chosen.Entity));
                    row.Value.fontStyle = picking ? FontStyles.Italic : FontStyles.Normal;
                    row.Value.text = !available ? "Not available for this job yet"
                        : picking ? "Click a " + string.Join(" or ", types.Select(t => Names.Room(t).ToLowerInvariant())) + " in the dungeon"
                        : row.Labels[index];
                    row.Pick.gameObject.SetActive(available);
                    row.Pick.text = picking ? "Cancel" : "Pick in dungeon";
                    row.Show.gameObject.SetActive(available && !picking && index > 0);
                    string note = null;
                    assignment?.LastNote.TryGetValue(row.Slot, out note);
                    if (row.Message != null && Time.unscaledTime < row.MessageUntil) row.Note.text = row.Message;
                    else row.Note.text = note == null ? "" : "Last search: " + note;
                }
                RefreshBans();
            }
            catch (Exception e) { CharacterManager.ReportError("filling the rooms page", e); }
        }

        private void RefreshBans()
        {
            var a = CharacterManager.Of(_minion);
            var bans = a == null ? new List<RoomRef>() : a.Forbidden;
            bool forbidding = RoomPicker.IsForbidding(this);
            _banPick.text = forbidding ? "Cancel" : "Forbid a room";
            _banNote.fontStyle = FontStyles.Normal;
            if (_banMessage != null && Time.unscaledTime < _banMessageUntil) _banNote.text = _banMessage;
            else if (forbidding) { _banNote.text = "Click the room in the dungeon"; _banNote.fontStyle = FontStyles.Italic; }
            else _banNote.text = "";

            for (int i = 0; i < _bans.Count; i++)
            {
                var line = _bans[i];
                line.Room = -1;
                line.Name = null;
                line.Show.gameObject.SetActive(false);
                line.Lift.gameObject.SetActive(false);
                line.Label.color = new Color(0.75f, 0.72f, 0.65f, 1f);
                if (bans.Count == 0)
                {
                    line.Line.SetActive(i == 0);
                    line.Label.text = "None";
                    continue;
                }
                if (i >= bans.Count) { line.Line.SetActive(false); continue; }
                line.Line.SetActive(true);
                if (bans.Count > _bans.Count && i == _bans.Count - 1)
                {
                    line.Label.text = $"and {bans.Count - i} more (lift one above to see the next)";
                    continue;
                }
                var ban = bans[i];
                var room = _rooms.FirstOrDefault(r => r.Entity == ban.Entity && r.Gen == ban.Gen);
                string floor = ban.Floor < 0 ? "?" : (ban.Floor + 1).ToString();
                line.Room = ban.Entity;
                line.Name = room.Label ?? $"{Names.Room(ban.Type)}, floor {floor}, no longer built";
                string skipped = a.LastSkipped.TryGetValue(ban.Entity, out var at) ? $" (last left out {at:HH:mm:ss})" : "";
                line.Label.text = line.Name + skipped;
                line.Label.color = line.Normal;
                line.Show.gameObject.SetActive(room.Label != null && !forbidding);
                line.Lift.gameObject.SetActive(!forbidding);
            }
        }

        private void OnForbid()
        {
            try
            {
                if (RoomPicker.IsForbidding(this)) RoomPicker.Cancel(this);
                else RoomPicker.StartForbid(this, _minion);
                _banMessage = null;
                Refresh();
            }
            catch (Exception e) { CharacterManager.ReportError("starting to forbid a room", e); }
        }

        private void OnLift(BanLine line)
        {
            try
            {
                if (line.Room < 0) return;
                CharacterManager.Lift(_minion, line.Room, line.Name);
                _banMessage = "Ban lifted";
                _banMessageUntil = Time.unscaledTime + 6f;
                Refresh();
            }
            catch (Exception e) { CharacterManager.ReportError("lifting a ban", e); }
        }

        private void OnShowBan(BanLine line)
        {
            try
            {
                if (line.Room < 0) return;
                var room = GameContext.ListRooms().FirstOrDefault(r => r.Entity == line.Room);
                if (room.Label == null) return;
                RoomPicker.Show(_tooltip, room);
            }
            catch (Exception e) { CharacterManager.ReportError("showing a forbidden room", e); }
        }

        // Called by RoomPicker after a click in the dungeon made to forbid a room.
        internal void ForbidRejected(string message)
        {
            _banMessage = message;
            _banMessageUntil = Time.unscaledTime + 6f;
            Refresh();
        }

        internal void ForbidAccepted(RoomInfo room, List<Slot> cleared)
        {
            string text = "Forbidden.";
            if (cleared.Count > 0) text += $" {CharacterManager.SlotName(cleared[0])} assignment cleared.";
            else if (room.Type == RoomType.TREASURE_ROOM) text += " He will not collect his salary there either.";
            _banMessage = text;
            _banMessageUntil = Time.unscaledTime + 8f;
            Refresh();
        }

        private void OnPick(Row row)
        {
            try
            {
                if (RoomPicker.IsPicking(this, row.Slot)) RoomPicker.Cancel(this);
                else RoomPicker.Start(this, row.Slot, _minion);
                row.Message = null;
                Refresh();
            }
            catch (Exception e) { CharacterManager.ReportError("starting a pick", e); }
        }

        private void OnShow(Row row)
        {
            try
            {
                CharacterManager.ByMinion.TryGetValue(_minion.Entity, out var assignment);
                if (assignment == null || !assignment.Rooms.TryGetValue(row.Slot, out var chosen)) return;
                var room = GameContext.ListRooms().FirstOrDefault(r => r.Entity == chosen.Entity);
                if (room.Label == null) return;
                RoomPicker.Show(_tooltip, room);
            }
            catch (Exception e) { CharacterManager.ReportError("showing a room", e); }
        }

        // Called by RoomPicker after a click in the dungeon.
        internal void PickRejected(Slot slot, string message)
        {
            if (!_rows.TryGetValue(slot, out var row)) return;
            row.Message = message;
            row.MessageUntil = Time.unscaledTime + 6f;
            Refresh();
        }

        internal void PickAccepted(Slot slot, RoomInfo room)
        {
            if (!_rows.TryGetValue(slot, out var row)) return;
            row.Message = "Picked in the dungeon";
            row.MessageUntil = Time.unscaledTime + 6f;
            Refresh();
        }

        private void Step(Row row, int direction)
        {
            try
            {
                if (row.Ids.Count <= 1) return;
                CharacterManager.ByMinion.TryGetValue(_minion.Entity, out var assignment);
                int index = 0;
                if (assignment != null && assignment.Rooms.TryGetValue(row.Slot, out var chosen))
                    index = Math.Max(0, row.Ids.IndexOf(chosen.Entity));
                index = (index + direction + row.Ids.Count) % row.Ids.Count;
                if (row.Ids[index] < 0) CharacterManager.Assign(_minion, row.Slot, null);
                else CharacterManager.Assign(_minion, row.Slot, _rooms.First(r => r.Entity == row.Ids[index]));
                Refresh();
            }
            catch (Exception e) { CharacterManager.ReportError("changing an assignment", e); }
        }

        // ---- Called from ManagerBehaviour ---------------------------------------------------------------------

        internal static void TickAll()
        {
            foreach (var kv in Pages.ToList())
            {
                var page = kv.Value;
                if (page._tooltip == null || page._tooltip.WasCollected || !page._tooltip)
                {
                    Pages.Remove(kv.Key);
                    continue;
                }
                // Sheets that are closed, or that belong to another window, cost nothing until they open.
                if (!page._tooltip.gameObject.activeInHierarchy) continue;
                page.Reevaluate();
                if (page._shown) page.Refresh();
            }
        }

        internal static void LateUpdateAll()
        {
            foreach (var page in Pages.Values) page.LateUpdate();
        }
    }

    // ---- Hooks on the sheet's paging --------------------------------------------------------------------------

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.NextContentEnableCheck))]
    internal static class RoomsPageNextCheck
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Postfix(MinionDetailsTooltip __instance, ref bool __result)
        {
            var page = RoomsPage.Get(__instance);
            page.AdjustMaxText();
            if (page.Shown) __result = false;
            else if (!__result && page.Eligible) __result = true;
        }
    }

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.PreviousContentEnableCheck))]
    internal static class RoomsPagePreviousCheck
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Postfix(MinionDetailsTooltip __instance, ref bool __result)
        {
            if (RoomsPage.Get(__instance).Shown) __result = true;
        }
    }

    // The right arrow on the last game page opens the rooms page instead of doing nothing.
    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.NextContent))]
    internal static class RoomsPageNext
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static bool Prefix(MinionDetailsTooltip __instance)
        {
            var page = RoomsPage.Get(__instance);
            if (page.Shown) return false;
            if (!page.Eligible) return true;
            // The game moves to its own next page when there is one; the rooms page comes after the last.
            int count = __instance.m_enableContents?.Count ?? 0;
            int index = -1;
            for (int i = 0; i < count; i++)
                if (__instance.m_enableContents[i] == __instance.m_currentContent) { index = i; break; }
            if (index >= 0 && index < count - 1) return true;
            page.Show();
            return false;
        }
    }

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.PreviousContent))]
    internal static class RoomsPagePrevious
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static bool Prefix(MinionDetailsTooltip __instance)
        {
            var page = RoomsPage.Get(__instance);
            if (!page.Shown) return true;
            page.Hide(true);
            return false;
        }
    }

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.SwitchContent), new[] { typeof(MinionDetailsTooltip.EContent), typeof(bool) })]
    internal static class RoomsPageSwitch
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Prefix(MinionDetailsTooltip __instance) => RoomsPage.Get(__instance).Hide(false);
    }

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.SetTarget))]
    internal static class RoomsPageTarget
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Postfix(MinionDetailsTooltip __instance)
        {
            var page = RoomsPage.Get(__instance);
            page.Hide(true);
            page.Reevaluate();
        }
    }

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.SetMode))]
    internal static class RoomsPageMode
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Postfix(MinionDetailsTooltip __instance)
        {
            var page = RoomsPage.Get(__instance);
            page.Hide(true);
            page.Reevaluate();
        }
    }

    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.OnDisable))]
    internal static class RoomsPageDisable
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Postfix(MinionDetailsTooltip __instance) => RoomsPage.Get(__instance).Hide(true);
    }
}
