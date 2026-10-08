using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppInterop.Runtime;
using NDMUnofficialPatch.Common;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Management
{
    // A tab at the top left of the dungeon screen that opens a panel of room needs (RoomNeeds.Compute), one row per room
    // type: three segments for the kinds of prop the minions looked for there in the last 3 minutes, and a bar for
    // prestige.
    //
    // A segment names a kind of prop (the three with the most minions who found none, then the most searched) with the
    // number of minions who found one over the number who looked for one. It is green when all found one, amber when
    // some did not, red when none did. A click on a segment shows one of the minions whose last search for that kind
    // found none, the most recent first, and the next one at each further click. The status gives the minions whose most
    // recent search in that room type found nothing; the tab counts the room types that have such minions. Version 0.11.0
    // to 0.12.2 showed three dirtiness bands instead, on the reading that a minion refuses rooms outside his band; the
    // game only tries those rooms last (RoomNeeds.cs).
    // The bar shows the prestige level preferred by the most demanding minion whose need that room type does not meet,
    // on a scale of the type's prestige levels, with a mark at the level of the best room of that type. A click shows
    // that minion. Prestige is information only: it never makes a search fail. Showing a minion does what a click on
    // him in the Minions window does (ObservationController.SimulateClickEntity, reached through DungeonGameMode.Player's
    // state machine as NotificationUtility.SelectEntity reaches it), and also moves the camera to him
    // (CameraController.SetEntityCoords), on his floor when he carries a GridFloorComponent.
    //
    // The tab and the panel are children of the dungeon HUD (DungeonPage), so they scale and hide with it; they take
    // their background from the game's resource counter and their text style from its decade label. The tab is placed
    // below the lowest element the HUD already shows in its top left corner. The panel is closed at first; its rows
    // refresh every 2 seconds, and the tab counts the room types with a problem while the panel is closed.
    internal sealed class RoomNeedsPanel
    {
        private static RoomNeedsPanel _current;
        private static float _nextSearch;
        private static bool _open;

        private const int Slots = 3;
        private static readonly Color Red = new(0.93f, 0.40f, 0.33f, 1f);
        private static readonly Color Amber = new(0.95f, 0.72f, 0.30f, 1f);
        private static readonly Color Lit = new(0.56f, 0.86f, 0.47f, 1f);
        private static readonly Color Spare = new(0.62f, 0.60f, 0.55f, 1f);
        private static readonly Color Empty = new(0.18f, 0.17f, 0.16f, 0.85f);
        private static readonly Color BarBack = new(0.12f, 0.11f, 0.10f, 0.9f);

        private sealed class RowUi
        {
            public RoomType Type;
            public GameObject Root;
            public CanvasGroup Group;
            public TextMeshProUGUI Name;
            public readonly Image[] Segments = new Image[Slots];
            public readonly TextMeshProUGUI[] Counts = new TextMeshProUGUI[Slots];
            public readonly PropType?[] SlotProp = new PropType?[Slots];
            public readonly int[] Cursor = new int[Slots];
            public RectTransform BarArea;
            public RectTransform Fill;
            public RectTransform Mark;
            public TextMeshProUGUI Status;
            public RoomNeeds.Row Data;
        }

        private readonly DungeonPage _page;
        private ResourceBar _gameBar;
        private TextMeshProUGUI _template;
        private GameObject _tab;
        private TextMeshProUGUI _tabText;
        private GameObject _panel;
        private TextMeshProUGUI _error;
        private readonly Dictionary<RoomType, RowUi> _rows = new();
        private float _nextRefresh;
        private int _problems;
        private int _loggedProblems = -1;
        private float _nextLog;

        private float _panelTop;
        private Transform _resourceBar;
        private float _nextBarLookup;
        private bool _barError;

        private int _focusEntity = -1;
        private int _focusFloor;
        private float _focusUntil;

        private RoomNeedsPanel(DungeonPage page) => _page = page;

        private bool Alive => _page != null && !_page.WasCollected && _tab != null && !_tab.WasCollected;

        // Every frame, from ManagerBehaviour.
        internal static void Update()
        {
            if (_current != null && !_current.Alive) _current = null;
            if (_current == null)
            {
                if (Time.unscaledTime < _nextSearch) return;
                _nextSearch = Time.unscaledTime + 2f;
                var page = Object.FindObjectOfType<DungeonPage>();
                if (page == null) return;
                var panel = new RoomNeedsPanel(page);
                if (!panel.Build()) return;
                _current = panel;
            }
            _current.Tick();
        }

        // ---- Building ----

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
            try
            {
                _gameBar = _page.GetComponentInChildren<ResourceBar>(true);
                Transform barTransform = _gameBar == null ? _page.transform : _gameBar.transform;
                _template = FindDeep(barTransform, "TextDecade")?.GetComponent<TextMeshProUGUI>()
                    ?? _page.GetComponentInChildren<TextMeshProUGUI>(true);
                if (_template == null) throw new InvalidOperationException("no text of the dungeon HUD to copy");
                float top = FreeTopLeft();

                _tab = Box("NDMUnofficialPatch_RoomNeedsTab", _page.transform);
                var tabRt = _tab.GetComponent<RectTransform>();
                tabRt.anchorMin = tabRt.anchorMax = new Vector2(0f, 1f);
                tabRt.pivot = new Vector2(0f, 1f);
                tabRt.anchoredPosition = new Vector2(24f, top);
                var tabRow = _tab.AddComponent<HorizontalLayoutGroup>();
                tabRow.padding = new RectOffset(28, 28, 12, 12);
                tabRow.childControlWidth = tabRow.childControlHeight = true;
                tabRow.childForceExpandWidth = tabRow.childForceExpandHeight = false;
                Fit(_tab);
                _tabText = Text(_tab.transform, "Room needs", 32f, TextAlignmentOptions.Left);
                var tabButton = _tab.AddComponent<Button>();
                tabButton.targetGraphic = _tab.GetComponent<Image>();
                tabButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(Toggle)));

                _panel = Box("NDMUnofficialPatch_RoomNeedsPanel", _page.transform);
                var panelRt = _panel.GetComponent<RectTransform>();
                panelRt.anchorMin = panelRt.anchorMax = new Vector2(0f, 1f);
                panelRt.pivot = new Vector2(0f, 1f);
                _panelTop = top - 76f;
                panelRt.anchoredPosition = new Vector2(24f, _panelTop);
                var column = _panel.AddComponent<VerticalLayoutGroup>();
                column.padding = new RectOffset(28, 28, 18, 18);
                column.spacing = 10f;
                column.childControlWidth = column.childControlHeight = true;
                column.childForceExpandWidth = column.childForceExpandHeight = false;
                Fit(_panel);
                Header();
                _error = Text(_panel.transform, "", 26f, TextAlignmentOptions.Left);
                _error.color = Red;
                _error.gameObject.SetActive(false);
                _panel.SetActive(_open);
                Plugin.Logger.LogInfo($"[RoomNeeds] panel added to the dungeon HUD, top edge at {top.ToString("0", CultureInfo.InvariantCulture)}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[RoomNeeds] the panel could not be built: {e.Message}");
                if (_tab != null) Object.Destroy(_tab);
                if (_panel != null) Object.Destroy(_panel);
                _tab = _panel = null;
                return false;
            }
        }

        // The vertical position, from the top of the HUD, just below the lowest element the HUD shows in its top left
        // corner (the left quarter and the top quarter of the screen), ignoring elements larger than 40 % of the screen.
        private float FreeTopLeft()
        {
            var pageRt = _page.GetComponent<RectTransform>();
            Rect page = pageRt.rect;
            float lowest = 0f;
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            var names = new List<string>();
            foreach (var graphic in _page.GetComponentsInChildren<Graphic>(false))
            {
                if (graphic == null || !graphic.enabled || graphic.color.a <= 0.01f) continue;
                var rt = graphic.rectTransform;
                rt.GetWorldCorners(corners);
                Vector3 a = pageRt.InverseTransformPoint(corners[0]); // bottom left
                Vector3 b = pageRt.InverseTransformPoint(corners[2]); // top right
                float width = b.x - a.x, height = b.y - a.y;
                if (width <= 0f || height <= 0f || width > page.width * 0.4f || height > page.height * 0.4f) continue;
                if (a.x > page.xMin + page.width * 0.25f || b.y < page.yMax - page.height * 0.25f) continue;
                float below = a.y - page.yMax; // negative: distance of the element's bottom from the top edge
                if (below < lowest)
                {
                    lowest = below;
                    if (names.Count < 12) names.Add(graphic.gameObject.name);
                }
            }
            if (names.Count > 0) Plugin.Logger.LogInfo("[RoomNeeds] top left of the HUD already holds: " + string.Join(", ", names));
            return lowest - 16f;
        }

        private GameObject Box(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>().SetParent(parent, false);
            var image = go.AddComponent<Image>();
            var gameBackground = _gameBar == null ? null : _gameBar.GetComponent<Image>();
            if (gameBackground != null && gameBackground.sprite != null)
            {
                image.sprite = gameBackground.sprite;
                image.type = gameBackground.type;
                image.color = gameBackground.color;
            }
            else image.color = new Color(0f, 0f, 0f, 0.7f);
            image.raycastTarget = true;
            return go;
        }

        private static void Fit(GameObject go)
        {
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private TextMeshProUGUI Text(Transform parent, string text, float size, TextAlignmentOptions alignment, float width = -1f)
        {
            var go = Object.Instantiate(_template.gameObject, parent, false).Cast<GameObject>();
            go.name = "Text";
            foreach (var localized in go.GetComponents<Aube.LocalizedText>()) Object.DestroyImmediate(localized);
            foreach (var fit in go.GetComponents<ContentSizeFitter>()) Object.DestroyImmediate(fit);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.enableAutoSizing = false;
            tmp.fontSize = size;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.alignment = alignment;
            tmp.raycastTarget = false;
            tmp.text = text;
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = size * 1.3f;
            le.minWidth = width;
            le.preferredWidth = width;
            le.flexibleWidth = -1f;
            return tmp;
        }

        private static GameObject Child(Transform parent, string name, float width, float height)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>().SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = width;
            le.minHeight = le.preferredHeight = height;
            return go;
        }

        private static HorizontalLayoutGroup Line(GameObject go, float spacing)
        {
            var line = go.AddComponent<HorizontalLayoutGroup>();
            line.spacing = spacing;
            line.childAlignment = TextAnchor.MiddleLeft;
            line.childControlWidth = line.childControlHeight = true;
            line.childForceExpandWidth = line.childForceExpandHeight = false;
            return line;
        }

        private const float NameWidth = 230f;
        private const float SegmentWidth = 200f;
        private const float BarWidth = 220f;
        private const float StatusWidth = 460f;
        private const float RowHeight = 44f;

        private void Header()
        {
            var go = new GameObject("Header");
            go.AddComponent<RectTransform>().SetParent(_panel.transform, false);
            Line(go, 8f);
            Text(go.transform, "Room", 26f, TextAlignmentOptions.Left, NameWidth);
            Text(go.transform, "Free props, last 3 min (found / looked)", 26f, TextAlignmentOptions.Left, SegmentWidth * Slots + 8f * (Slots - 1));
            Child(go.transform, "Gap", 16f, 10f);
            Text(go.transform, "Prestige preferred", 26f, TextAlignmentOptions.Left, BarWidth);
            Text(go.transform, "", 26f, TextAlignmentOptions.Left, StatusWidth);
        }

        private RowUi MakeRow(RoomType type)
        {
            var row = new RowUi { Type = type };
            row.Root = new GameObject("Row_" + type);
            row.Root.AddComponent<RectTransform>().SetParent(_panel.transform, false);
            row.Group = row.Root.AddComponent<CanvasGroup>();
            Line(row.Root, 8f);
            row.Name = Text(row.Root.transform, Names.Room(type), 30f, TextAlignmentOptions.Left, NameWidth);

            for (int b = 0; b < Slots; b++)
            {
                int slot = b;
                var segment = Child(row.Root.transform, "Segment_" + b, SegmentWidth, RowHeight);
                row.Segments[b] = segment.AddComponent<Image>();
                row.Segments[b].color = Empty;
                var button = segment.AddComponent<Button>();
                button.targetGraphic = row.Segments[b];
                button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => OnSegment(row, slot))));
                row.Counts[b] = Text(segment.transform, "", 22f, TextAlignmentOptions.Center);
                var countRt = row.Counts[b].rectTransform;
                countRt.anchorMin = Vector2.zero;
                countRt.anchorMax = Vector2.one;
                countRt.offsetMin = countRt.offsetMax = Vector2.zero;
                Object.DestroyImmediate(row.Counts[b].GetComponent<LayoutElement>());
                row.Counts[b].color = Color.black;
            }
            Child(row.Root.transform, "Gap", 16f, 10f);

            var bar = Child(row.Root.transform, "PrestigeBar", BarWidth, RowHeight - 12f);
            var back = bar.AddComponent<Image>();
            back.color = BarBack;
            var barButton = bar.AddComponent<Button>();
            barButton.targetGraphic = back;
            barButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => OnBar(row))));
            row.BarArea = bar.GetComponent<RectTransform>();

            var fill = new GameObject("Fill");
            row.Fill = fill.AddComponent<RectTransform>();
            row.Fill.SetParent(bar.transform, false);
            row.Fill.anchorMin = new Vector2(0f, 0f);
            row.Fill.anchorMax = new Vector2(0f, 1f);
            row.Fill.pivot = new Vector2(0f, 0.5f);
            row.Fill.offsetMin = row.Fill.offsetMax = Vector2.zero;
            var fillImage = fill.AddComponent<Image>();
            fillImage.color = Red;
            fillImage.raycastTarget = false;

            var mark = new GameObject("BestRoom");
            row.Mark = mark.AddComponent<RectTransform>();
            row.Mark.SetParent(bar.transform, false);
            row.Mark.anchorMin = new Vector2(0f, 0f);
            row.Mark.anchorMax = new Vector2(0f, 1f);
            row.Mark.pivot = new Vector2(0.5f, 0.5f);
            row.Mark.sizeDelta = new Vector2(6f, 10f);
            var markImage = mark.AddComponent<Image>();
            markImage.color = new Color(0.98f, 0.92f, 0.75f, 1f);
            markImage.raycastTarget = false;

            row.Status = Text(row.Root.transform, "", 26f, TextAlignmentOptions.Left, StatusWidth);
            return row;
        }

        // ---- Refreshing ----

        private static void Toggle()
        {
            _open = !_open;
            if (_current == null) return;
            _current._panel.SetActive(_open);
            _current._nextRefresh = 0f;
            _current.UpdateTab();
        }

        // The resource bar (Economy/ResourceBarOverlay.cs) sits at the top centre of the same HUD. When the open panel is
        // wide enough to reach under it, the panel starts below the bar; otherwise it keeps its place under the tab. Both
        // are children of the HUD, so their edges compare in its space. Before 0.24.5 the panel's header and first row
        // ran under the bar.
        private void KeepBelowResourceBar()
        {
            if (_barError) return;
            try
            {
                if (Time.unscaledTime >= _nextBarLookup)
                {
                    _nextBarLookup = Time.unscaledTime + 2f;
                    _resourceBar = _page.transform.Find("NDMUnofficialPatch_ResourceBar");
                }
                var panelRt = _panel.GetComponent<RectTransform>();
                float y = _panelTop;
                if (_resourceBar != null && !_resourceBar.WasCollected && _resourceBar.gameObject.activeInHierarchy)
                {
                    var barRt = _resourceBar.GetComponent<RectTransform>();
                    Rect bar = barRt.rect, panel = panelRt.rect;
                    Vector3 barAt = barRt.localPosition, panelAt = panelRt.localPosition;
                    float barLeft = barAt.x + bar.xMin, barRight = barAt.x + bar.xMax, barBottom = barAt.y + bar.yMin;
                    float panelLeft = panelAt.x + panel.xMin, panelRight = panelAt.x + panel.xMax;
                    // The panel's top edge where it would stand at its own place under the tab.
                    float ownTop = panelAt.y + panel.yMax - (panelRt.anchoredPosition.y - _panelTop);
                    float limit = barBottom - 12f;
                    if (panelRight > barLeft && panelLeft < barRight && ownTop > limit) y = _panelTop - (ownTop - limit);
                }
                if (Math.Abs(panelRt.anchoredPosition.y - y) > 0.5f) panelRt.anchoredPosition = new Vector2(panelRt.anchoredPosition.x, y);
            }
            catch (Exception e)
            {
                _barError = true;
                Plugin.Logger.LogWarning($"[RoomNeeds] placing the panel below the resource bar failed, the panel stays under its tab: {e.Message}");
            }
        }

        private void UpdateTab()
        {
            _tabText.text = "Room needs" + (_problems > 0 ? $" ({_problems})" : "") + (_open ? "  -" : "  +");
            _tabText.color = _problems > 0 ? Red : _template.color;
        }

        private void Tick()
        {
            FollowFocus();
            if (_open) KeepBelowResourceBar();
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 2f;
            try
            {
                var rows = RoomNeeds.Compute();
                _problems = 0;
                foreach (var r in rows) if (r.Problem) _problems++;
                UpdateTab();
                // The rows go to the log when the number of problems changes, at most once a minute.
                if (rows.Count > 0 && _problems != _loggedProblems && Time.unscaledTime >= _nextLog)
                {
                    _loggedProblems = _problems;
                    _nextLog = Time.unscaledTime + 60f;
                    Plugin.Logger.LogInfo($"[RoomNeeds] {rows.Count} room type(s), {_problems} with a problem");
                    foreach (var r in rows) Plugin.Logger.LogInfo("[RoomNeeds] " + r);
                }
                if (!_open) return;
                var seen = new HashSet<RoomType>();
                for (int i = 0; i < rows.Count; i++)
                {
                    var data = rows[i];
                    seen.Add(data.Type);
                    if (!_rows.TryGetValue(data.Type, out var ui)) _rows[data.Type] = ui = MakeRow(data.Type);
                    ui.Root.transform.SetSiblingIndex(i + 2);
                    Show(ui, data);
                }
                foreach (var pair in _rows) pair.Value.Root.SetActive(seen.Contains(pair.Key));
                _error.gameObject.SetActive(rows.Count == 0);
                if (rows.Count == 0) _error.text = "No data yet: the game's systems are not running.";
            }
            catch (Exception e)
            {
                RoomNeeds.ReportError("refreshing the panel", e);
                // An empty panel always says why.
                _error.text = "Refresh failed: " + e.Message;
                _error.gameObject.SetActive(_open);
            }
        }

        private void Show(RowUi ui, RoomNeeds.Row data)
        {
            ui.Data = data;
            ui.Root.SetActive(true);
            ui.Group.alpha = data.Problem ? 1f : 0.5f;
            ui.Name.text = Names.Room(data.Type) + (data.Rooms == 0 ? " (none)" : "");

            for (int b = 0; b < Slots; b++)
            {
                var kind = b < data.Kinds.Count ? data.Kinds[b] : null;
                if (kind == null)
                {
                    ui.Segments[b].color = Empty;
                    ui.Counts[b].text = "";
                    ui.SlotProp[b] = null;
                    continue;
                }
                ui.Segments[b].color = kind.Found == 0 ? Red : kind.Without.Count > 0 ? Amber : Lit;
                ui.Counts[b].text = $"{RoomNeeds.PropName(kind.Prop)} {kind.Found}/{kind.Searched}";
                if (ui.SlotProp[b] != kind.Prop) ui.Cursor[b] = 0;
                ui.SlotProp[b] = kind.Prop;
                if (ui.Cursor[b] >= kind.Without.Count) ui.Cursor[b] = 0;
            }

            int scale = Math.Max(1, Math.Max(data.LevelCount, Math.Max(data.PrestigeRequired, data.BestRoomLevel)));
            float width = ui.BarArea.rect.width > 0f ? ui.BarArea.rect.width : BarWidth;
            ui.Fill.sizeDelta = new Vector2(data.PrestigeShort ? width * data.PrestigeRequired / scale : 0f, 0f);
            ui.Mark.gameObject.SetActive(data.BestRoomLevel >= 0);
            ui.Mark.anchoredPosition = new Vector2(width * Math.Max(0, data.BestRoomLevel) / scale, 0f);

            string status;
            if (data.Problem)
                status = $"{data.Unmet.Count} found nothing on their last try: " + string.Join(", ", data.Unmet.Take(3).Select(data.Name)) + (data.Unmet.Count > 3 ? ", ..." : "");
            else if (data.Kinds.Count > 0)
                status = "all found a free prop";
            else
                status = data.Rooms == 0 ? "" : "no search in the last 3 minutes";
            ui.Status.text = status;
        }

        // ---- Clicks ----

        private static void OnSegment(RowUi ui, int slot)
        {
            try
            {
                var kinds = ui.Data?.Kinds;
                if (kinds == null || slot >= kinds.Count) return;
                var list = kinds[slot].Without;
                if (list.Count == 0) return;
                if (ui.Cursor[slot] >= list.Count) ui.Cursor[slot] = 0;
                int minion = list[ui.Cursor[slot]];
                ui.Cursor[slot] = (ui.Cursor[slot] + 1) % list.Count;
                _current?.Focus(minion);
            }
            catch (Exception e) { RoomNeeds.ReportError("showing a minion", e); }
        }

        private static void OnBar(RowUi ui)
        {
            try
            {
                if (ui.Data == null || !ui.Data.PrestigeShort) return;
                _current?.Focus(ui.Data.PrestigeMinion);
            }
            catch (Exception e) { RoomNeeds.ReportError("showing a minion", e); }
        }

        private unsafe void Focus(int minion)
        {
            var mode = DungeonGameMode.Instance;
            var state = mode == null ? null : mode.Player?.m_states?.CurrentState;
            var observation = state == null ? null : state.TryCast<ObservationController>();
            if (observation != null) observation.SimulateClickEntity(minion, true);
            var camera = CameraController.Instance;
            if (camera == null) return;
            camera.SetEntityCoords(minion);
            if (!GameContext.TryWorld(out var world, out _)) return;
            IntPtr f = RawPool.Of<GridFloorComponent>(world, sizeof(GridFloorComponent)).Item(minion);
            if (f == IntPtr.Zero) return;
            int floor = ((GridFloorComponent*)f)->Floor;
            if (floor < 0 || _page.m_currentFloor == floor) return;
            _page.SetDesiredFloor(floor);
            _focusEntity = minion;
            _focusFloor = floor;
            _focusUntil = Time.unscaledTime + 4f;
        }

        // Moves the camera to the minion again once the floor asked for is displayed.
        private void FollowFocus()
        {
            if (_focusEntity < 0) return;
            try
            {
                if (Time.unscaledTime > _focusUntil) { _focusEntity = -1; return; }
                if (_page.m_currentFloor != _focusFloor) return;
                CameraController.Instance?.SetEntityCoords(_focusEntity);
                _focusEntity = -1;
            }
            catch (Exception e)
            {
                _focusEntity = -1;
                RoomNeeds.ReportError("moving the camera to a minion", e);
            }
        }
    }
}
