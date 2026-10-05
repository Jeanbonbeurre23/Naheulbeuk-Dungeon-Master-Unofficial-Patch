using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Economy
{
    // "Bilan" in the resource counter at the bottom right of the dungeon screen, right of "Dépenses": the gold earned
    // over the last decade of game time minus the "Dépenses" figure.
    //
    // "Dépenses" is the counter's charges text (ResourceBar.m_chargesText, element UI_SalaryCost), whose value
    // ResourceBar.GetCharges takes from EconomyUtility.GetChargesCost: the decade's salaries
    // (SalaryManagerComponent.CurrentSalarialCosts), the tavern's costs (TavernUtility.CalculateTavernCosts) and the
    // floors' decade costs (FloorUtility.GetTotalFloorsDecadeCosts). It is the fixed cost of a decade.
    //
    // Gold earned is every EconomyUtility.GiveGoldAmount over the last decade (tavern takings, resource sales, raid
    // loot, quest rewards, gold recovered from adventurers), measured in the buckets of ResourceFlows. Gold given
    // back by construction is left out, as construction spending is: refunds while building or deleting rooms and
    // furniture (Builder.ValidateRoom, ValidatePropsInCorridor, DeleteRoom, UpdateDeletedPropCost, CancelRoom;
    // BuilderHistory.Undo, UndoAll, UndoForWallsBlueprint), furniture sold from its panel (PropDetailsPage.OnButtonSell),
    // and the starting gold of a new game (GameInitSystem.Init).
    //
    // The element is a copy of the Dépenses element, with its tooltip trigger and tutorial identifier removed, the
    // gold icon of the counter, and the game's own signed, green or red number display (ResourceText.ForceSign and
    // UseGreenRedColor). While less than a decade has been observed since the game or the save started, it is
    // dimmed, and the resource bar at the top says how many days are covered.
    internal static class Bilan
    {
        internal static int ExcludedDepth;

        private static ResourceBar _bar;
        private static GameObject _element;
        private static ResourceText _text;
        private static CanvasGroup _group;
        private static float _nextSearch;
        private static float _nextRefresh;
        private static int _shown = int.MinValue;
        private static readonly double[] Produced = new double[ResourceFlows.Kinds];
        private static readonly double[] Consumed = new double[ResourceFlows.Kinds];
        private static bool _errorLogged;

        internal static void RecordIncome(uint amount)
        {
            if (ExcludedDepth > 0 || amount == 0) return;
            ResourceFlows.Add(ResourceFlows.GoldIncome, true, amount);
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == name) return parent.GetChild(i);
            return null;
        }

        // Every frame, from ResourceBarBehaviour.
        internal static void Update()
        {
            try
            {
                if (_element == null || _element.WasCollected || _bar == null || _bar.WasCollected)
                {
                    _element = null;
                    if (Time.unscaledTime < _nextSearch) return;
                    _nextSearch = Time.unscaledTime + 2f;
                    if (!Build()) return;
                }
                if (Time.unscaledTime < _nextRefresh) return;
                _nextRefresh = Time.unscaledTime + 0.5f;
                Refresh();
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[Bilan] update failed, further errors are not logged: {e.Message}");
            }
        }

        private static bool Build()
        {
            var page = Object.FindObjectOfType<DungeonPage>();
            if (page == null) return false;
            var bar = page.GetComponentInChildren<ResourceBar>(true);
            if (bar == null) return false;
            var row = bar.transform;
            var charges = FindChild(row, "UI_SalaryCost");
            if (charges == null)
            {
                Plugin.Logger.LogWarning("[Bilan] the Dépenses element (UI_SalaryCost) was not found; no Bilan");
                _nextSearch = float.MaxValue;
                return false;
            }
            // An element left by an earlier build (the HUD kept, the reference lost) is reused.
            var existing = FindChild(row, "NDMUnofficialPatch_Bilan");
            GameObject element;
            if (existing != null) element = existing.gameObject;
            else
            {
                int index = charges.GetSiblingIndex();
                Transform separator = null;
                for (int i = index + 1; i < row.childCount; i++)
                    if (row.GetChild(i).name == "Separator") { separator = row.GetChild(i); break; }
                if (separator != null)
                {
                    var sep = Object.Instantiate(separator.gameObject, row, false).Cast<GameObject>();
                    sep.name = "NDMUnofficialPatch_BilanSeparator";
                    sep.transform.SetSiblingIndex(index + 1);
                }
                element = Object.Instantiate(charges.gameObject, row, false).Cast<GameObject>();
                element.name = "NDMUnofficialPatch_Bilan";
                element.transform.SetSiblingIndex(index + (separator != null ? 2 : 1));
                foreach (var trigger in element.GetComponents<EventTrigger>()) Object.DestroyImmediate(trigger);
                foreach (var id in element.GetComponentsInChildren<RectTransformIdentifier>(true)) Object.DestroyImmediate(id);
                var goldIcon = FindChild(row, "UI_Cost")?.Find("IcnValue")?.GetComponent<UnityEngine.UI.Image>();
                var icon = element.transform.Find("IcnSalary")?.GetComponent<UnityEngine.UI.Image>();
                if (goldIcon != null && icon != null) icon.sprite = goldIcon.sprite;
            }
            _text = element.GetComponent<ResourceText>();
            if (_text == null)
            {
                Plugin.Logger.LogWarning("[Bilan] the copied element has no ResourceText; no Bilan");
                Object.Destroy(element);
                _nextSearch = float.MaxValue;
                return false;
            }
            _text.ForceSign = true;
            _text.UseGreenRedColor = true;
            _group = element.GetComponent<CanvasGroup>() ?? element.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = true;
            _bar = bar;
            _element = element;
            _shown = int.MinValue;
            Plugin.Logger.LogInfo("[Bilan] Bilan added right of Dépenses in the resource counter");
            return true;
        }

        private static void Refresh()
        {
            if (!ResourceFlows.TrySums(Produced, Consumed, out double observedDays, out int daysPerDecade))
            {
                _element.SetActive(false);
                return;
            }
            _element.SetActive(true);
            int income = (int)Math.Round(Produced[ResourceFlows.GoldIncome]);
            int charges = _bar.GetCharges();
            int value = income - charges;
            if (value != _shown)
            {
                if (_shown == int.MinValue) _text.Init(value);
                else _text.SetValue(value);
                _shown = value;
            }
            _group.alpha = observedDays < daysPerDecade - 0.05 ? 0.6f : 1f;
        }
    }

    [HarmonyPatch(typeof(EconomyUtility), nameof(EconomyUtility.GiveGoldAmount))]
    internal static class GoldIncomePatch
    {
        private static bool Prepare() => Settings.Bilan.Value;
        private static void Prefix(uint __0) => Bilan.RecordIncome(__0);
    }

    // Gold given during these calls is construction money coming back, or the starting gold, not income.
    internal static class ExcludedFromIncome
    {
        internal static bool Prepare() => Settings.Bilan.Value;
        internal static void Enter() => Bilan.ExcludedDepth++;
        internal static void Leave() { if (Bilan.ExcludedDepth > 0) Bilan.ExcludedDepth--; }
    }

    [HarmonyPatch(typeof(Builder), nameof(Builder.ValidateRoom))]
    internal static class ExcludeValidateRoom
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(Builder), nameof(Builder.ValidatePropsInCorridor))]
    internal static class ExcludeValidatePropsInCorridor
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(Builder), nameof(Builder.DeleteRoom))]
    internal static class ExcludeDeleteRoom
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(Builder), nameof(Builder.UpdateDeletedPropCost))]
    internal static class ExcludeUpdateDeletedPropCost
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(Builder), nameof(Builder.CancelRoom))]
    internal static class ExcludeCancelRoom
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(BuilderHistory), nameof(BuilderHistory.Undo))]
    internal static class ExcludeUndo
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(BuilderHistory), nameof(BuilderHistory.UndoAll))]
    internal static class ExcludeUndoAll
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(BuilderHistory), nameof(BuilderHistory.UndoForWallsBlueprint))]
    internal static class ExcludeUndoForWallsBlueprint
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(PropDetailsPage), nameof(PropDetailsPage.OnButtonSell))]
    internal static class ExcludePropSale
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }

    [HarmonyPatch(typeof(GameInitSystem), nameof(GameInitSystem.Init))]
    internal static class ExcludeStartingGold
    {
        private static bool Prepare() => ExcludedFromIncome.Prepare();
        private static void Prefix() => ExcludedFromIncome.Enter();
        private static void Postfix() => ExcludedFromIncome.Leave();
    }
}
