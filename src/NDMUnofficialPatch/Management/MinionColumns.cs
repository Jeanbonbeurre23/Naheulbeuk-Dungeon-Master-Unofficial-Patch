using System;
using System.Collections.Generic;
using System.Linq;
using NDMUnofficialPatch.Common;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Management
{
    // The management tab of the Minions window, widened and laid out in one column per group of origins:
    // Greenskins (green), Humans (blue), Elves (pink), Drows (black), and Others when a minion fits none of them.
    //
    // The window is MinionManagementPage (prefab UI_NPCManagementMenu). Its RootAnim is 1040 units wide out of the
    // 3840 of the reference resolution, and the character sheet (UI_NPCManagement, 1004 wide) hangs off its right
    // edge. The management tab (ManagementTab, 960 wide) shows the minions in one scrolling column built by the
    // game's CharacterList, a virtualised list (BaseEntityList) that only instantiates the slots in view and
    // recycles them while scrolling. That list cannot be split into columns without fighting its recycling, so the
    // columns are a separate view: the game's list keeps running, hidden, and provides the minions to show
    // (BaseEntityList.Entities, already filtered by the filter wheel and ordered by the sort toggles); each minion
    // gets a slot made from the list's own slot prefab and set up by the list itself (CharacterList.SetupSlot), so
    // it looks, updates and reacts to clicks exactly as the game's slots do, through the page's own callbacks
    // (BaseManagementPage.OnSlotSubmit selects the minion, opens his sheet and moves the camera to him).
    //
    // While the tab is shown the window is 2800 units wide, which leaves room for the sheet on the right of a
    // 16:9 screen. The other tabs (recruitment, necromancy, teleportation, VIP) keep the game's width and list.
    // During a room pick from the rooms page (RoomPicker), the window goes back to the game's width, so that the
    // dungeon is visible, and returns to the columns afterwards.
    internal sealed unsafe class MinionColumns
    {
        private const float WideWidth = 2800f;
        private const float SlotWidth = 800f;
        private const float SlotHeight = 190f;
        private const float ColumnSpacing = 24f;
        private const float ScrollbarWidth = 28f;

        private static MinionColumns _current;
        private static float _nextSearch;

        private sealed class Group
        {
            public string Name;
            public Color Color;
            public HashSet<OriginType> Origins = new();
        }

        private sealed class Column
        {
            public Group Group;
            public GameObject Root;
            public TextMeshProUGUI Header;
            public RectTransform Content;
            public ScrollRect Scroll;
        }

        private sealed class Entry
        {
            public GameObject Holder;
            public CharacterListSlot Slot;
            public bool Selected;
        }

        private readonly MinionManagementPage _page;
        private readonly RectTransform _rootAnim;
        private readonly RectTransform _tab;
        private readonly GameObject _gameScroll;
        private readonly CharacterList _list;
        private readonly float _narrowWidth;
        private readonly Vector2 _tabSize;
        private readonly Vector2 _tabPosition;
        private GameObject _view;
        private readonly List<Group> _groups;
        private readonly List<Column> _columns = new();
        private readonly Dictionary<int, Entry> _entries = new();
        private List<int> _shownOrder = new();
        private bool _wide;
        private bool _buildFailed;
        private float _nextSync;
        private float _slotScale = 1f;

        private MinionColumns(MinionManagementPage page, RectTransform rootAnim, RectTransform tab, GameObject gameScroll, CharacterList list)
        {
            _page = page;
            _rootAnim = rootAnim;
            _tab = tab;
            _gameScroll = gameScroll;
            _list = list;
            _narrowWidth = rootAnim.sizeDelta.x;
            _tabSize = tab.sizeDelta;
            _tabPosition = tab.anchoredPosition;
            _groups = ParseGroups(Settings.MinionColumnGroups.Value);
        }

        // Groups from the setting: "Name:ORIGIN,ORIGIN;Name:ORIGIN". Colours follow the order: green, blue, pink,
        // black, then grey.
        private static List<Group> ParseGroups(string text)
        {
            var colors = new[]
            {
                new Color(0.36f, 0.62f, 0.27f, 1f), new Color(0.29f, 0.47f, 0.80f, 1f),
                new Color(0.84f, 0.45f, 0.70f, 1f), new Color(0.13f, 0.12f, 0.16f, 1f),
            };
            var groups = new List<Group>();
            foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                var g = new Group { Name = part.Substring(0, colon).Trim() };
                g.Color = groups.Count < colors.Length ? colors[groups.Count] : new Color(0.4f, 0.4f, 0.4f, 1f);
                foreach (var o in part.Substring(colon + 1).Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (Enum.TryParse(o.Trim(), true, out OriginType origin)) g.Origins.Add(origin);
                groups.Add(g);
            }
            groups.Add(new Group { Name = "Others", Color = new Color(0.40f, 0.40f, 0.40f, 1f) });
            return groups;
        }

        private bool Alive => _page != null && !_page.WasCollected && _tab != null && !_tab.WasCollected;

        // Every frame, from ManagerBehaviour.
        internal static void Update()
        {
            if (_current != null && !_current.Alive) _current = null;
            if (_current == null)
            {
                if (Time.unscaledTime < _nextSearch) return;
                _nextSearch = Time.unscaledTime + 1f;
                _current = Find();
                if (_current == null) return;
            }
            _current.Tick();
        }

        // After each frame's animations, from ManagerBehaviour: the window's own animations must not undo the width.
        internal static void LateUpdate()
        {
            if (_current != null && _current.Alive && _current._wide) _current.ApplyWidth(true);
        }

        private static MinionColumns Find()
        {
            var page = Object.FindObjectOfType<MinionManagementPage>();
            if (page == null) return null;
            var rootAnim = page.transform.Find("RootAnim")?.Cast<RectTransform>();
            var tab = rootAnim?.Find("RootTabs/ManagementTab")?.Cast<RectTransform>();
            var gameScroll = tab?.Find("RootScrollView")?.gameObject;
            var list = tab?.GetComponent<CharacterList>();
            if (rootAnim == null || tab == null || gameScroll == null || list == null)
            {
                Plugin.Logger.LogWarning("[Columns] the Minions window's layout differs from game 1.8; no columns");
                _nextSearch = float.MaxValue;
                return null;
            }
            return new MinionColumns(page, rootAnim, tab, gameScroll, list);
        }

        private void Tick()
        {
            bool want = _page.gameObject.activeInHierarchy && _tab.gameObject.activeInHierarchy && !RoomPicker.Active;
            if (want != _wide)
            {
                _wide = want;
                try
                {
                    if (want && _view == null && (_buildFailed || !Build()))
                    {
                        _buildFailed = true;
                        _wide = false;
                        return;
                    }
                    ApplyWidth(want);
                    _gameScroll.SetActive(!want);
                    if (_view != null) _view.SetActive(want);
                    _nextSync = 0f;
                }
                catch (Exception e) { CharacterManager.ReportError("switching the Minions window's layout", e); }
            }
            if (!_wide || Time.unscaledTime < _nextSync) return;
            _nextSync = Time.unscaledTime + 0.25f;
            try { Sync(); }
            catch (Exception e) { CharacterManager.ReportError("filling the Minions window's columns", e); }
        }

        private void ApplyWidth(bool wide)
        {
            var size = _rootAnim.sizeDelta;
            float width = wide ? WideWidth : _narrowWidth;
            if (size.x != width) _rootAnim.sizeDelta = new Vector2(width, size.y);
            var tabSize = wide ? new Vector2(WideWidth - (_narrowWidth - _tabSize.x), _tabSize.y) : _tabSize;
            var tabPos = wide ? new Vector2(WideWidth / 2f, _tabPosition.y) : _tabPosition;
            if (_tab.sizeDelta != tabSize) _tab.sizeDelta = tabSize;
            if (_tab.anchoredPosition != tabPos) _tab.anchoredPosition = tabPos;
        }

        // ---- Building the view ------------------------------------------------------------------------------

        private bool Build()
        {
            try
            {
                var model = _gameScroll.GetComponent<RectTransform>();
                _view = new GameObject("NDMUnofficialPatch_MinionColumns");
                var rt = _view.AddComponent<RectTransform>();
                rt.SetParent(_tab, false);
                rt.anchorMin = model.anchorMin;
                rt.anchorMax = model.anchorMax;
                rt.pivot = model.pivot;
                rt.offsetMin = model.offsetMin;
                rt.offsetMax = model.offsetMax;
                var row = _view.AddComponent<HorizontalLayoutGroup>();
                row.spacing = ColumnSpacing;
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = true;
                row.childForceExpandHeight = true;

                var titleTemplate = _tab.Find("UI_MenuTitle/TextTitle")?.GetComponent<TextMeshProUGUI>();
                var scrollbarTemplate = _gameScroll.transform.Find("UI_NPCScrollView/UI_Scrollbar")?.gameObject;
                foreach (var g in _groups) _columns.Add(MakeColumn(g, titleTemplate, scrollbarTemplate));
                _view.SetActive(false);
                Plugin.Logger.LogInfo("[Columns] origin columns added to the Minions window: " + string.Join(", ", _groups.Select(g => g.Name + " (" + string.Join("/", g.Origins) + ")")));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Columns] the columns could not be built: {e.Message}");
                if (_view != null) Object.Destroy(_view);
                _view = null;
                return false;
            }
        }

        private Column MakeColumn(Group group, TextMeshProUGUI titleTemplate, GameObject scrollbarTemplate)
        {
            var column = new Column { Group = group };
            column.Root = new GameObject("Column_" + group.Name);
            column.Root.AddComponent<RectTransform>().SetParent(_view.transform, false);
            var stack = column.Root.AddComponent<VerticalLayoutGroup>();
            stack.spacing = 8f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;

            var header = new GameObject("Header");
            header.AddComponent<RectTransform>().SetParent(column.Root.transform, false);
            var headerImage = header.AddComponent<Image>();
            headerImage.color = group.Color;
            headerImage.raycastTarget = false;
            var headerLe = header.AddComponent<LayoutElement>();
            headerLe.minHeight = headerLe.preferredHeight = 84f;
            if (titleTemplate != null)
            {
                var textGo = Object.Instantiate(titleTemplate.gameObject, header.transform, false).Cast<GameObject>();
                foreach (var localized in textGo.GetComponents<Aube.LocalizedText>()) Object.DestroyImmediate(localized);
                var textRt = textGo.GetComponent<RectTransform>();
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = textRt.offsetMax = Vector2.zero;
                column.Header = textGo.GetComponent<TextMeshProUGUI>();
                column.Header.enableAutoSizing = false;
                column.Header.fontSize = 48f;
                column.Header.color = Color.white;
                column.Header.alignment = TextAlignmentOptions.Center;
                column.Header.raycastTarget = false;
                column.Header.text = group.Name;
            }

            var scrollGo = new GameObject("Scroll");
            scrollGo.AddComponent<RectTransform>().SetParent(column.Root.transform, false);
            var scrollLe = scrollGo.AddComponent<LayoutElement>();
            scrollLe.flexibleHeight = 1f;
            scrollLe.minHeight = 200f;
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 90f;

            var viewport = new GameObject("Viewport");
            var vrt = viewport.AddComponent<RectTransform>();
            vrt.SetParent(scrollGo.transform, false);
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.pivot = new Vector2(0f, 1f);
            vrt.offsetMin = Vector2.zero;
            vrt.offsetMax = new Vector2(-ScrollbarWidth, 0f);
            viewport.AddComponent<RectMask2D>();
            // A transparent image so that the mouse wheel over empty space still scrolls the column.
            var catcher = viewport.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);

            var content = new GameObject("Content");
            var crt = content.AddComponent<RectTransform>();
            crt.SetParent(viewport.transform, false);
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.offsetMin = crt.offsetMax = Vector2.zero;
            var list = content.AddComponent<VerticalLayoutGroup>();
            list.childControlWidth = true;
            list.childControlHeight = true;
            list.childForceExpandWidth = true;
            list.childForceExpandHeight = false;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = vrt;
            scroll.content = crt;
            if (scrollbarTemplate != null)
            {
                var bar = Object.Instantiate(scrollbarTemplate, scrollGo.transform, false).Cast<GameObject>();
                var brt = bar.GetComponent<RectTransform>();
                brt.anchorMin = new Vector2(1f, 0f);
                brt.anchorMax = new Vector2(1f, 1f);
                brt.pivot = new Vector2(1f, 0.5f);
                brt.anchoredPosition = Vector2.zero;
                brt.sizeDelta = new Vector2(16f, 0f);
                var scrollbar = bar.GetComponent<Scrollbar>();
                if (scrollbar != null)
                {
                    scroll.verticalScrollbar = scrollbar;
                    scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                }
            }
            column.Content = crt;
            column.Scroll = scroll;
            return column;
        }

        // ---- Filling the columns ---------------------------------------------------------------------------

        private int GroupOf(RawPool origins, int entity)
        {
            IntPtr p = origins.Item(entity);
            if (p != IntPtr.Zero)
            {
                var origin = *(OriginType*)p;
                for (int i = 0; i < _groups.Count - 1; i++)
                    if (_groups[i].Origins.Contains(origin)) return i;
            }
            return _groups.Count - 1;
        }

        private void Sync()
        {
            var entities = _list.Entities;
            var order = new List<int>(entities == null ? 0 : entities.Count);
            if (entities != null) for (int i = 0; i < entities.Count; i++) order.Add(entities[i]);

            if (!order.SequenceEqual(_shownOrder))
            {
                if (!GameContext.TryWorld(out var world, out _)) return;
                var origins = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent));
                var perColumn = _groups.Select(_ => new List<int>()).ToList();
                foreach (int e in order) perColumn[GroupOf(origins, e)].Add(e);

                _columns[_columns.Count - 1].Root.SetActive(perColumn[perColumn.Count - 1].Count > 0);
                int visibleColumns = _columns.Count(c => c.Root.activeSelf);
                float inner = _tab.sizeDelta.x - 10f; // the game's scroll view is 10 units narrower than the tab
                float columnWidth = (inner - ColumnSpacing * (visibleColumns - 1)) / visibleColumns - ScrollbarWidth;
                float scale = Mathf.Min(1f, columnWidth / SlotWidth);
                bool rescale = Math.Abs(scale - _slotScale) > 0.001f;
                _slotScale = scale;

                var keep = new HashSet<int>(order);
                foreach (var gone in _entries.Keys.Where(e => !keep.Contains(e)).ToList())
                {
                    Object.Destroy(_entries[gone].Holder);
                    _entries.Remove(gone);
                }
                for (int c = 0; c < perColumn.Count; c++)
                {
                    var column = _columns[c];
                    for (int i = 0; i < perColumn[c].Count; i++)
                    {
                        int e = perColumn[c][i];
                        if (!_entries.TryGetValue(e, out var entry))
                        {
                            entry = MakeEntry(e);
                            if (entry == null) continue;
                            _entries[e] = entry;
                        }
                        if (entry.Holder.transform.parent != column.Content) entry.Holder.transform.SetParent(column.Content, false);
                        entry.Holder.transform.SetSiblingIndex(i);
                        if (rescale) Scale(entry);
                    }
                    if (column.Header != null) column.Header.text = $"{column.Group.Name}  ({perColumn[c].Count})";
                }
                _shownOrder = order;
            }

            int selected = _page.SelectedEntity;
            foreach (var kv in _entries)
            {
                bool isSelected = kv.Key == selected;
                if (kv.Value.Selected == isSelected) continue;
                kv.Value.Selected = isSelected;
                kv.Value.Slot.SetSelected(isSelected);
            }
        }

        private Entry MakeEntry(int entity)
        {
            var prefab = _list.m_slotPrefab;
            if (prefab == null) return null;
            var holder = new GameObject("Minion_" + entity);
            var hrt = holder.AddComponent<RectTransform>();
            hrt.SetParent(_columns[0].Content, false);
            holder.AddComponent<LayoutElement>();
            var slotGo = Object.Instantiate(prefab.gameObject, holder.transform, false).Cast<GameObject>();
            var srt = slotGo.GetComponent<RectTransform>();
            srt.anchorMin = srt.anchorMax = new Vector2(0f, 1f);
            srt.pivot = new Vector2(0f, 1f);
            srt.anchoredPosition = Vector2.zero;
            srt.sizeDelta = new Vector2(SlotWidth, SlotHeight);
            var slot = slotGo.GetComponent<CharacterListSlot>();
            _list.SetupSlot(slot, entity);
            var entry = new Entry { Holder = holder, Slot = slot };
            Scale(entry);
            return entry;
        }

        private void Scale(Entry entry)
        {
            entry.Slot.transform.localScale = new Vector3(_slotScale, _slotScale, 1f);
            var le = entry.Holder.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = SlotHeight * _slotScale;
            le.minWidth = le.preferredWidth = SlotWidth * _slotScale;
        }
    }
}
