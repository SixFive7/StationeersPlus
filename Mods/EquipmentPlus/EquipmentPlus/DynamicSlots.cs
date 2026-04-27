using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.UI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace EquipmentPlus
{
    /// <summary>
    /// Shared logic for dynamic slot management. Ensures exactly one empty
    /// unlocked slot of the requested type is visible at all times. Slots grow
    /// when filled, shrink when emptied. Works for any item type.
    ///
    /// Only slots that this mod itself added are touched. Vanilla slots (like
    /// the tablet's Battery, ProgrammableChip, and its original Cartridge
    /// slots) are never blocked, unblocked, or retyped.
    /// </summary>
    internal static class DynamicSlots
    {
        // Per-prefab vanilla slot count at the moment AddBlockedSlots was
        // called. Slot indices >= this value are the slots we added and are
        // the only ones we manage. Keyed by PrefabName so instances share the
        // boundary with their prefab.
        private static readonly Dictionary<string, int> _vanillaSlotCount =
            new Dictionary<string, int>();

        // Per-activeType fallback SlotTypeIcon, captured from the first
        // vanilla slot of that type at prefab mutation. Slot.Initialize will
        // overwrite a new slot's SlotTypeIcon with Slot.GetSlotTypeSprite()
        // at Thing.Awake, but that table is a Dictionary lookup that may
        // not have an entry for every Slot.Class (e.g. SensorProcessingUnit
        // isn't guaranteed to be registered). This cache lets Unlock fall
        // back to the designer-baked sprite from the vanilla prefab when
        // the runtime lookup misses.
        private static readonly Dictionary<Slot.Class, Sprite> _templateSprite =
            new Dictionary<Slot.Class, Sprite>();

        /// <summary>
        /// Adds <paramref name="count"/> Blocked (invisible) slots to a prefab,
        /// cloning presentation from an existing slot of <paramref name="activeType"/>
        /// so UI behaviour matches once unlocked. Unlocks the first added slot
        /// so one empty slot of activeType is visible from the start.
        /// Records the vanilla slot boundary so RefreshSlots can leave vanilla
        /// slots untouched.
        /// </summary>
        internal static void AddBlockedSlots(Thing prefab, int count, Slot.Class activeType)
        {
            if (prefab.Slots == null || prefab.Slots.Count == 0)
                return;

            int vanillaCount = prefab.Slots.Count;
            _vanillaSlotCount[prefab.PrefabName] = vanillaCount;

            // Prefer an existing slot of the active type as template so clone
            // properties (HidesOccupant, IsSwappable, etc.) match vanilla.
            Slot template = null;
            foreach (var s in prefab.Slots)
            {
                if (s.Type == activeType) { template = s; break; }
            }
            if (template == null)
                template = prefab.Slots[0];

            // Remember the template's editor-baked icon for this active type
            // so Unlock can use it as a fallback if Slot.GetSlotTypeSprite
            // returns null at runtime.
            if (template.SlotTypeIcon != null)
                _templateSprite[activeType] = template.SlotTypeIcon;

            for (int i = 0; i < count; i++)
            {
                prefab.Slots.Add(new Slot
                {
                    Type = Slot.Class.Blocked,
                    IsInteractable = false,
                    IsSwappable = template.IsSwappable,
                    Interactable = template.Interactable,
                    HidesOccupant = template.HidesOccupant,
                    IsHiddenInSeat = template.IsHiddenInSeat,
                    OccupantCastsShadows = template.OccupantCastsShadows,
                    SlotTypeIcon = template.SlotTypeIcon,
                    // StringKey/StringHash drive Slot.DisplayName via the
                    // Localization table. Without them the slot label is
                    // empty. Cloning from the template gives our dynamic
                    // slots the same label vanilla uses on the first slot
                    // of this type (e.g. "Sensor Processing Unit" for
                    // lenses, "Cartridge" for the tablet).
                    StringKey = template.StringKey,
                    StringHash = template.StringHash,
                });
            }

            // Unlock one of the added slots so the player sees an insert target
            // from the moment they open the item. (First added slot is at
            // index vanillaCount.)
            if (vanillaCount < prefab.Slots.Count)
                Unlock(prefab.Slots[vanillaCount], activeType);
        }

        /// <summary>
        /// Variant for call-sites that are already inside an
        /// InventoryWindow rebuild (e.g. our HandleOccupantChange prefix).
        /// Skips the embedded rebuild call to avoid recursion while still
        /// normalising Slot state and the CartridgeSlots cache.
        /// </summary>
        internal static void RefreshSlotsNoRebuild(Thing thing, Slot.Class activeType)
        {
            RefreshSlotsCore(thing, activeType, rebuildUi: false);
        }

        /// <summary>
        /// Reconciles slot state so exactly one empty unlocked slot of
        /// <paramref name="activeType"/> is visible among the slots we added.
        /// Vanilla slots (indices below the recorded vanilla count) are never
        /// modified regardless of occupancy.
        ///
        /// Also handles the save-load case where vanilla's MoveToSlot placed
        /// a chip into one of our Blocked slots: such a slot is unlocked so
        /// the occupant becomes visible and interactable.
        ///
        /// If any slot state changed, the live InventoryWindow UI for this
        /// thing is rebuilt so drop-target widgets exist for newly-unlocked
        /// slots and vanished widgets are removed for newly-blocked ones.
        /// </summary>
        internal static void RefreshSlots(Thing thing, Slot.Class activeType)
        {
            RefreshSlotsCore(thing, activeType, rebuildUi: true);
        }

        private static void RefreshSlotsCore(Thing thing, Slot.Class activeType, bool rebuildUi)
        {
            if (thing.Slots == null) return;
            if (!_vanillaSlotCount.TryGetValue(thing.PrefabName, out int vanillaCount))
                return; // prefab wasn't mutated by us; nothing to manage

            bool changed = false;

            // Pass 1: any added slot that became Blocked but holds an occupant
            // must be unlocked, otherwise the chip is invisible to the UI.
            // This happens on save load when MoveToSlot targets one of our
            // Blocked indices.
            for (int i = vanillaCount; i < thing.Slots.Count; i++)
            {
                var slot = thing.Slots[i];
                if (slot.Type == Slot.Class.Blocked && slot.Get() != null)
                {
                    Unlock(slot, activeType);
                    changed = true;
                }
            }

            // Pass 2: among our added slots only, count unlocked empty ones
            // and track the first blocked (for growth) + last unlocked empty
            // (for shrink).
            int unlockedEmpty = 0;
            int firstBlocked = -1;
            int lastUnlockedEmpty = -1;

            for (int i = vanillaCount; i < thing.Slots.Count; i++)
            {
                var slot = thing.Slots[i];
                bool blocked = slot.Type == Slot.Class.Blocked;
                bool empty = slot.Get() == null;

                if (!blocked && empty)
                {
                    unlockedEmpty++;
                    lastUnlockedEmpty = i;
                }
                else if (blocked && firstBlocked == -1)
                {
                    firstBlocked = i;
                }
            }

            // Grow: unlock more of our Blocked slots until we have one empty.
            while (unlockedEmpty < 1 && firstBlocked >= 0)
            {
                Unlock(thing.Slots[firstBlocked], activeType);
                changed = true;
                unlockedEmpty++;
                firstBlocked = FindNextBlocked(thing, firstBlocked + 1);
            }

            // Shrink: re-block our excess empty slots, keeping exactly 1.
            while (unlockedEmpty > 1 && lastUnlockedEmpty >= 0)
            {
                Block(thing.Slots[lastUnlockedEmpty]);
                changed = true;
                unlockedEmpty--;
                lastUnlockedEmpty = FindLastUnlockedEmpty(thing, vanillaCount);
            }

            if (changed)
            {
                SyncTabletCartridgeSlots(thing);
                if (rebuildUi)
                    RebuildInventoryWindowFor(thing);
            }
        }

        /// <summary>
        /// AdvancedTablet caches a `CartridgeSlots` list built once in Awake()
        /// by scanning Slots for Type==Cartridge. Vanilla's Next/Prev/Mode
        /// cycling, the Cartridge getter, GetExtendedText, and InteractWith
        /// all iterate the cache rather than Slots. Runtime Type changes
        /// don't touch the cache, which orphans our unlocked slots from
        /// vanilla's cartridge machinery.
        ///
        /// Rebuild the cache from scratch after every slot-state change,
        /// preserving Mode by re-indexing the currently-displayed Cartridge
        /// into the new list. If that cartridge is no longer in a Cartridge
        /// slot (shouldn't happen, but defensive), Mode falls back to 0.
        /// </summary>
        private static void SyncTabletCartridgeSlots(Thing thing)
        {
            if (!(thing is AdvancedTablet tablet)) return;
            if (tablet.CartridgeSlots == null) return;

            var prevCartridge = tablet.Cartridge;

            // Only occupied Cartridge slots go into the cache. Vanilla's
            // cycle via GetCartridge indexes CartridgeSlots[Mode] and sets
            // tablet.Cartridge = that slot's Occupant; an empty slot in the
            // list would flip the display to null each time Next lands on
            // it. Skipping empties gives the user a "cycle through actual
            // cartridges" experience, which is what they want and strictly
            // better than vanilla's behaviour with empty slots too.
            tablet.CartridgeSlots.Clear();
            foreach (var s in tablet.Slots)
            {
                if (s.Type == Slot.Class.Cartridge && s.Get() != null)
                    tablet.CartridgeSlots.Add(s);
            }

            if (prevCartridge != null)
            {
                for (int i = 0; i < tablet.CartridgeSlots.Count; i++)
                {
                    if (tablet.CartridgeSlots[i].Get() == prevCartridge)
                    {
                        tablet.Mode = i;
                        return;
                    }
                }
            }
            if (tablet.Mode >= tablet.CartridgeSlots.Count)
                tablet.Mode = 0;
        }

        /// <summary>
        /// Finds any visible InventoryWindow whose parent is <paramref name="thing"/>
        /// and forces a full slot-widget rebuild. Required because SetSlots
        /// only instantiates SlotDisplayButton widgets for slots where
        /// IsInteractable == true at the time SetSlots runs; runtime changes
        /// to IsInteractable after that point don't create or remove widgets
        /// on their own, so drop targets go stale.
        /// </summary>
        private static void RebuildInventoryWindowFor(Thing thing)
        {
            try
            {
                var windows = UnityEngine.Object.FindObjectsOfType<InventoryWindow>();
                if (windows == null) return;
                foreach (var w in windows)
                {
                    if (w != null && (object)w.Parent == thing)
                        w.HandleOccupantChange();
                }
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log?.LogWarning(
                    $"EquipmentPlus: InventoryWindow rebuild failed: {e.Message}");
            }
        }

        private static void Unlock(Slot slot, Slot.Class activeType)
        {
            slot.Type = activeType;
            slot.IsInteractable = true;
            // Slot.Initialize runs once at instance Awake and baked the
            // Blocked-type sprite into SlotTypeIcon; re-derive it here so
            // the runtime-unlocked slot shows the correct hint icon.
            // If the runtime lookup misses this type, fall back to the
            // editor-baked template sprite captured in AddBlockedSlots.
            var icon = Slot.GetSlotTypeSprite(activeType);
            if (icon == null && _templateSprite.TryGetValue(activeType, out var cached))
                icon = cached;
            slot.SlotTypeIcon = icon;
        }

        private static void Block(Slot slot)
        {
            slot.Type = Slot.Class.Blocked;
            slot.IsInteractable = false;
            slot.SlotTypeIcon = Slot.GetSlotTypeSprite(Slot.Class.Blocked);
        }

        private static int FindNextBlocked(Thing thing, int startFrom)
        {
            for (int i = startFrom; i < thing.Slots.Count; i++)
            {
                if (thing.Slots[i].Type == Slot.Class.Blocked)
                    return i;
            }
            return -1;
        }

        private static int FindLastUnlockedEmpty(Thing thing, int minIndex)
        {
            for (int i = thing.Slots.Count - 1; i >= minIndex; i--)
            {
                var slot = thing.Slots[i];
                if (slot.Type != Slot.Class.Blocked && slot.Get() == null)
                    return i;
            }
            return -1;
        }
    }
}
