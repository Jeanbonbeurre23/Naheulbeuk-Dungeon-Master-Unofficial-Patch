using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Economy
{
    // A bar at the top of the dungeon screen: one icon per workshop resource and per food type, and under each icon
    // what was produced and consumed over the last decade of game time (ResourceFlows), as "produced / consumed".
    //
    // The bar is a child of the dungeon HUD (DungeonPage, prefab DungeonHUD, reference resolution 3840 by 2160), so
    // it scales and hides with the game's own HUD. Its background, separator and text style are taken from the
    // game's resource counter at the bottom right (ResourceBar, UI_ResourcesInfos). Nothing in it is a raycast
    // target: it never takes a click from the dungeon. Workshop icons come from UIGameConfig.ResourceIcons, food
    // icons from the sprites of the game's food page (ICN_Food_Meat, ICN_Food_Soup, ICN_Food_Sweet,
    // ICN_Food_Waste). A workshop resource the game has not unlocked (ResourceUtility.IsResourceUnlocked) is not
    // shown, nor is cheese, which has no line on the game's food page, unless something was measured for them.
    internal sealed class ResourceBarOverlay
    {
        private static ResourceBarOverlay _current;
        private static float _nextSearch;

        private static readonly string[] KindNames =
            { "Weapons", "Magic", "Tools", "Intel", "Corpses", "Meat", "Soup", "Sweets", "Cheese", "Leftovers" };
        private static readonly string[] FoodSprites =
            { "ICN_Food_Meat", "ICN_Food_Soup", "ICN_Food_Sweet", "ICN_Food_Cheese", "ICN_Food_Waste" };
        private static readonly Color Surplus = new(0.56f, 0.86f, 0.47f, 1f);
        private static readonly Color Deficit = new(0.93f, 0.47f, 0.38f, 1f);

        private sealed class Cell
        {
            public int Kind;
            public GameObject Root;
            public Image Icon;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Value;
        }

        private readonly DungeonPage _page;
        private ResourceBar _gameBar;
        private GameObject _root;
        private GameObject _separator;
        private TextMeshProUGUI _caption;
        private readonly List<Cell> _cells = new();
        private Color _baseColor = new(0.88f, 0.79f, 0.59f, 1f);
        private float _nextRefresh;
        private float _nextIconSearch;
        private int _iconSearches;
        private readonly double[] _produced = new double[ResourceFlows.Kinds];
        private readonly double[] _consumed = new double[ResourceFlows.Kinds];

        private ResourceBarOverlay(DungeonPage page) => _page = page;

        private bool Alive => _page != null && !_page.WasCollected && _root != null && !_root.WasCollected;

        // Every frame, from ResourceBarBehaviour.
        internal static void Update()
        {
            if (_current != null && !_current.Alive) _current = null;
            if (_current == null)
            {
                if (Time.unscaledTime < _nextSearch) return;
                _nextSearch = Time.unscaledTime + 2f;
                var page = Object.FindObjectOfType<DungeonPage>();
                if (page == null) return;
                var overlay = new ResourceBarOverlay(page);
                if (!overlay.Build()) return;
                _current = overlay;
            }
            _current.Tick();
        }

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
                var textTemplate = FindDeep(barTransform, "TextDecade")?.GetComponent<TextMeshProUGUI>()
                    ?? _page.GetComponentInChildren<TextMeshProUGUI>(true);
                if (textTemplate == null) throw new InvalidOperationException("no text of the dungeon HUD to copy");
                _baseColor = textTemplate.color;

                _root = new GameObject("NDMUnofficialPatch_ResourceBar");
                var rt = _root.AddComponent<RectTransform>();
                rt.SetParent(_page.transform, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -16f);

                var background = _root.AddComponent<Image>();
                var gameBackground = _gameBar == null ? null : _gameBar.GetComponent<Image>();
                if (gameBackground != null && gameBackground.sprite != null)
                {
                    background.sprite = gameBackground.sprite;
                    background.type = gameBackground.type;
                    background.color = gameBackground.color;
                }
                else background.color = new Color(0f, 0f, 0f, 0.6f);
                background.raycastTarget = false;

                var row = _root.AddComponent<HorizontalLayoutGroup>();
                row.padding = new RectOffset(56, 56, 18, 18);
                row.spacing = 44f;
                row.childAlignment = TextAnchor.MiddleCenter;
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = false;
                row.childForceExpandHeight = false;
                var fitter = _root.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var separatorTemplate = FindDeep(barTransform, "Separator")?.GetComponent<Image>();
                for (int kind = 0; kind < ResourceFlows.ResourceKinds; kind++)
                {
                    if (kind == ResourceFlows.WorkshopKinds) _separator = Separator(separatorTemplate);
                    _cells.Add(MakeCell(kind, textTemplate));
                }
                _caption = Text(textTemplate, _root.transform, "", 30f);
                _caption.color = new Color(0.75f, 0.72f, 0.65f, 1f);
                FindIcons();
                Plugin.Logger.LogInfo("[ResourceBar] resource bar added to the dungeon HUD");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[ResourceBar] the resource bar could not be built: {e.Message}");
                if (_root != null) Object.Destroy(_root);
                _root = null;
                return false;
            }
        }

        private GameObject Separator(Image template)
        {
            var go = new GameObject("Separator");
            go.AddComponent<RectTransform>().SetParent(_root.transform, false);
            var image = go.AddComponent<Image>();
            if (template != null && template.sprite != null)
            {
                image.sprite = template.sprite;
                image.type = template.type;
                image.color = template.color;
            }
            else image.color = new Color(_baseColor.r, _baseColor.g, _baseColor.b, 0.5f);
            image.raycastTarget = false;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = 6f;
            le.minHeight = le.preferredHeight = 130f;
            return go;
        }

        private Cell MakeCell(int kind, TextMeshProUGUI template)
        {
            var cell = new Cell { Kind = kind };
            cell.Root = new GameObject("Cell_" + KindNames[kind]);
            cell.Root.AddComponent<RectTransform>().SetParent(_root.transform, false);
            var column = cell.Root.AddComponent<VerticalLayoutGroup>();
            column.spacing = 4f;
            column.childAlignment = TextAnchor.UpperCenter;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = false;
            column.childForceExpandHeight = false;
            var le = cell.Root.AddComponent<LayoutElement>();
            le.minWidth = 150f;

            var iconGo = new GameObject("Icon");
            iconGo.AddComponent<RectTransform>().SetParent(cell.Root.transform, false);
            cell.Icon = iconGo.AddComponent<Image>();
            cell.Icon.preserveAspect = true;
            cell.Icon.raycastTarget = false;
            var iconLe = iconGo.AddComponent<LayoutElement>();
            iconLe.minWidth = iconLe.preferredWidth = 88f;
            iconLe.minHeight = iconLe.preferredHeight = 88f;
            iconGo.SetActive(false);

            cell.Name = Text(template, cell.Root.transform, KindNames[kind], 30f);
            cell.Name.GetComponent<LayoutElement>().minHeight = 88f;
            cell.Value = Text(template, cell.Root.transform, "0 / 0", 44f);
            return cell;
        }

        private static TextMeshProUGUI Text(TextMeshProUGUI template, Transform parent, string text, float size)
        {
            var go = Object.Instantiate(template.gameObject, parent, false).Cast<GameObject>();
            go.name = "Text";
            foreach (var localized in go.GetComponents<Aube.LocalizedText>()) Object.DestroyImmediate(localized);
            foreach (var fit in go.GetComponents<ContentSizeFitter>()) Object.DestroyImmediate(fit);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.enableAutoSizing = false;
            tmp.fontSize = size;
            tmp.enableWordWrapping = false;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.text = text;
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = size * 1.3f;
            le.minWidth = -1f;
            le.preferredWidth = -1f;
            le.flexibleWidth = -1f;
            return tmp;
        }

        // Icons: workshop resources from UIGameConfig.ResourceIcons, food from the sprites of the food page. The food
        // sprites are in memory once the game has loaded its interface; the search is retried a few times.
        private void FindIcons()
        {
            try
            {
                var icons = UIManager.GameConfig?.ResourceIcons?.m_internalArray;
                for (int t = 0; t < ResourceFlows.WorkshopKinds; t++)
                    if (icons != null && t < icons.Length && icons[t] != null) SetIcon(_cells[t], icons[t]);
            }
            catch (Exception e) { Plugin.Logger.LogInfo($"[ResourceBar] workshop icons not found: {e.Message}"); }

            bool missing = false;
            for (int f = 0; f < ResourceFlows.FoodKinds; f++)
                if (_cells[ResourceFlows.WorkshopKinds + f].Icon.sprite == null) missing = true;
            if (!missing) return;
            try
            {
                var wanted = new Dictionary<string, int>();
                for (int f = 0; f < ResourceFlows.FoodKinds; f++) wanted[FoodSprites[f]] = ResourceFlows.WorkshopKinds + f;
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (sprite != null && wanted.TryGetValue(sprite.name, out int kind) && _cells[kind].Icon.sprite == null)
                        SetIcon(_cells[kind], sprite);
            }
            catch (Exception e) { Plugin.Logger.LogInfo($"[ResourceBar] food icons not found: {e.Message}"); }
            _iconSearches++;
            _nextIconSearch = Time.unscaledTime + 10f;
        }

        private static void SetIcon(Cell cell, Sprite sprite)
        {
            cell.Icon.sprite = sprite;
            cell.Icon.gameObject.SetActive(true);
            cell.Name.gameObject.SetActive(false);
        }

        private void Tick()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            try
            {
                if (_iconSearches < 6 && Time.unscaledTime >= _nextIconSearch && _cells.Exists(c => c.Icon.sprite == null)) FindIcons();
                if (!ResourceFlows.TrySums(_produced, _consumed, out double observedDays, out int daysPerDecade))
                {
                    _root.SetActive(false);
                    return;
                }
                _root.SetActive(true);
                bool anyWorkshop = false, anyFood = false;
                foreach (var cell in _cells)
                {
                    double p = _produced[cell.Kind], c = _consumed[cell.Kind];
                    bool visible = p > 0 || c > 0 || Available(cell.Kind);
                    cell.Root.SetActive(visible);
                    if (!visible) continue;
                    if (cell.Kind < ResourceFlows.WorkshopKinds) anyWorkshop = true; else anyFood = true;
                    cell.Value.text = Format(p) + " / " + Format(c);
                    cell.Value.color = p == 0 && c == 0 ? _baseColor : p >= c ? Surplus : Deficit;
                }
                _separator.SetActive(anyWorkshop && anyFood);
                bool partial = observedDays < daysPerDecade - 0.05;
                _caption.gameObject.SetActive(partial);
                if (partial)
                    _caption.text = "last " + observedDays.ToString("0.0", CultureInfo.InvariantCulture) + " of " + daysPerDecade + " days";
            }
            catch (Exception e) { ResourceFlows.ReportError(e); }
        }

        private bool Available(int kind)
        {
            if (kind >= ResourceFlows.WorkshopKinds)
                return kind != ResourceFlows.Food(FoodType.CHEESE);
            try
            {
                if (_gameBar == null) return true;
                // ResourceBar.m_resourceUtility is an EcsUtilityInject<ResourceUtility>, a struct whose only field
                // is the utility, read in place as GameContext reads the utilities of the systems it captures.
                IntPtr utility = Common.Il2CppRaw.ReadObject(_gameBar.Pointer, "m_resourceUtility", "ResourceUtility");
                return utility == IntPtr.Zero || new ResourceUtility(utility).IsResourceUnlocked((ResourceType)kind);
            }
            catch { return true; }
        }

        private static string Format(double v)
        {
            if (v >= 10) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
            double r = Math.Round(v, 1);
            return r == Math.Floor(r) ? r.ToString("0", CultureInfo.InvariantCulture) : r.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
