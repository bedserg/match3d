using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages the 7-slot tray that sits at the bottom of the play field.
///
/// Placement rule: when an object is tapped it flies to the tray.
/// <list type="bullet">
///   <item>If the tray already contains the same <see cref="ObjectType"/>, the new object
///         is inserted directly after the last object of that type, and every object to the
///         right is animated one slot to the right.</item>
///   <item>If no matching type exists in the tray, the object is placed in the first empty slot.</item>
/// </list>
///
/// Match-3 removal: after every successful placement the tray scans all occupied
/// slots for any ObjectType that appears 3 or more times. Because same-type objects
/// are always adjacent after insertion, the 3 matching objects are already grouped.
/// They gather into tray slots 0-1-2, then disappear, and the remaining objects
/// compact left so the tray stays gapless.
///
/// Lose condition: if the tray is full and cannot accept another object, or if a
/// placement fills the last slot without triggering a match-3 removal,
/// <see cref="OnTrayFull"/> is fired so the game can show a fail state.
///
/// Placement is driven exclusively by <see cref="TryAutoPlaceObject"/>, called by
/// <see cref="DraggableObject"/> at the end of its fly-in animation.
/// Physics-based trigger entry is intentionally ignored.
/// </summary>
[RequireComponent(typeof(Collider))]
public class TrayController : MonoBehaviour
{
    private const int    SlotCount = 7;
    private const string LogPrefix = "[TrayController]";

    [Tooltip("World-space anchors for each tray slot, ordered left to right. Must contain exactly 7 entries.")]
    [SerializeField] private Transform[] _slotAnchors;

    [Tooltip("Reference to the ObjectSpawner so it can be notified when objects are removed by a match-3.")]
    [SerializeField] private ObjectSpawner _objectSpawner;

    [Tooltip("Reference to the UIManager. Auto-resolved via FindFirstObjectByType if left empty.")]
    [SerializeField] private UIManager _uiManager;

    [Tooltip("Reference to the LevelObjectiveManager. Auto-resolved via FindFirstObjectByType if left empty.")]
    [SerializeField] private LevelObjectiveManager _levelObjectiveManager;

    [Tooltip("Duration in seconds for surviving objects to slide into compacted positions after a match-3 removal.")]
    [SerializeField] private float _compactDuration = 0.2f;

    [Header("Match-3 Merge Animation")]
    [Tooltip("Duration in seconds for the left and right objects to slide into the middle object's position.")]
    [SerializeField] private float _mergeMoveDuration = 0.2f;

    [Tooltip("Duration in seconds for the middle object to scale up to the pop peak scale.")]
    [SerializeField] private float _popUpDuration = 0.12f;

    [Tooltip("Duration in seconds for the middle object to shrink from peak scale down to finalShrinkScale.")]
    [SerializeField] private float _shrinkDuration = 0.15f;

    [Tooltip("Peak scale multiplier applied to the middle object's tray scale during the pop. " +
             "For example 1.25 makes the object 25% larger at peak before shrinking away.")]
    [SerializeField] private float _popScaleMultiplier = 1.25f;

    [Tooltip("Final local scale the middle object shrinks to before it is destroyed. " +
             "Use Vector3.zero to make it disappear completely.")]
    [SerializeField] private Vector3 _finalShrinkScale = Vector3.zero;

    [Tooltip("Seconds to wait after the shrink completes before the middle object is destroyed. " +
             "A tiny pause lets the last frame of the shrink register visually.")]
    [SerializeField] private float _destroyDelayAfterShrink = 0.03f;

    [Header("Booster – Remove Last Object")]
    [Tooltip("Empty GameObject placed in the middle of the gameplay area. " +
             "The removed tray object flies back to this position. Must be assigned.")]
    [SerializeField] private Transform _boosterReturnPoint;
    [Header("Booster – Shuffle Objects")]

    [Tooltip("Tracks remaining uses for the shuffle booster. Assign the BoosterAmountManager on the Shuffle Booster UI object.")]
    [SerializeField] private BoosterAmountManager _shuffleBoosterAmount;

    [Tooltip("Horizontal impulse strength applied to board objects when shuffle booster is used.")]
    [SerializeField] private float _shuffleHorizontalForce = 2f;

    [Tooltip("Small upward impulse applied to board objects when shuffle booster is used.")]
    [SerializeField] private float _shuffleUpwardForce = 0.5f;
    [Tooltip("Seconds to wait for the removed object's return animation before compacting. " +
             "Should match the DraggableObject._returnDuration on your prefabs (default 0.25 s).")]
    [SerializeField] private float _boosterReturnDuration = 0.25f;

    [Tooltip("Tracks remaining uses for the remove-last-object booster. " +
             "Assign the BoosterAmountManager on the Booster 1 UI object.")]
    [SerializeField] private BoosterAmountManager _removeLastBoosterAmount;

    [Header("Booster – Collect Objective Triple")]

    [Tooltip("Tracks remaining uses for the collect-triple booster. " +
             "Assign the BoosterAmountManager on the Booster 2 UI object.")]
    [SerializeField] private BoosterAmountManager _collectTripleBoosterAmount;

    [Header("Booster – Pause Timer")]

    [Tooltip("Tracks remaining uses for the pause-timer booster. " +
             "Assign the BoosterAmountManager on the Booster 3 UI object.")]
    [SerializeField] private BoosterAmountManager _pauseTimerBoosterAmount;
    [Tooltip("How many seconds the timer should stop when this booster is used.")]
    [SerializeField] private float _pauseTimerDuration = 5f;

    private readonly DraggableObject[] _slots = new DraggableObject[SlotCount];

    // True while any tray animation (shift, match gather, compaction) is running.
    // Suppresses new placements and scene-object taps during animation.
    private bool _isMatchAnimating;

    // True after the fail condition has been triggered. Permanently blocks new placements.
    private bool _isFailed;

    // Held true for the entire duration of the collect-triple booster sequence.
    // Prevents InsertAndPlaceCoroutine and MergeSequenceCoroutine from calling
    // SetGlobalInputBlocked(false) between per-object dispatches, which would otherwise
    // release player-tap input in the gaps between objects 1→2 and 2→3.
    private bool _boosterInputLock;

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when the tray is full and cannot accept another object.
    /// Triggered in two cases:
    /// <list type="bullet">
    ///   <item>A tapped object is rejected because all 7 tray slots are occupied.</item>
    ///   <item>A placement fills the last tray slot and no match-3 removal frees any slots.</item>
    /// </list>
    /// Subscribe here to show a fail popup or trigger a game-over sequence.
    /// </summary>
    public event Action OnTrayFull;

    // ── Public state ─────────────────────────────────────────────────────────

    /// <summary>
    /// True while any tray animation is running — shift, merge, or compaction.
    /// External systems (e.g. boosters) can poll this to know when the tray is
    /// ready to accept the next object.
    /// </summary>
    public bool IsBusy => _isMatchAnimating;

    /// <summary>True when every tray slot is empty.</summary>
    public bool IsEmpty
    {
        get
        {
            for (int i = 0; i < SlotCount; i++)
                if (_slots[i] != null) return false;
            return true;
        }
    }

    /// <summary>True when all 7 tray slots are occupied.</summary>
    public bool IsFull
    {
        get
        {
            for (int i = 0; i < SlotCount; i++)
                if (_slots[i] == null) return false;
            return true;
        }
    }

    /// <summary>Number of tray slots currently holding an object.</summary>
    public int OccupiedCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < SlotCount; i++)
                if (_slots[i] != null) count++;
            return count;
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the world-space position of the tray slot the object will be inserted into.
    /// Uses the same smart-insert rule as <see cref="TryAutoPlaceObject"/> so the fly-in
    /// animation aims at the correct destination.
    /// </summary>
    public Vector3 GetPreviewAutoSlotPosition(DraggableObject obj)
    {
        int insertIndex = FindInsertSlotIndex(obj.ObjectType);
        return insertIndex >= 0 ? SlotAnchorPosition(insertIndex) : transform.position;
    }

    /// <summary>
    /// Attempts to place <paramref name="obj"/> into the tray using the smart-insert rule:
    /// <list type="number">
    ///   <item>If the tray already contains the same <see cref="ObjectType"/>, insert
    ///         directly after the last object of that type and shift everything right.</item>
    ///   <item>Otherwise place in the first empty slot.</item>
    /// </list>
    /// Rejects the object silently when the tray is already full before placement.
    /// Also rejects silently while a shift or match-3 animation is running, or after a fail.
    /// After a successful placement, checks for a match-3 removal first.
    /// Only if no match is found and the tray is still full does it fire <see cref="OnTrayFull"/>.
    /// </summary>
    /// <returns>True when the object was accepted and its insertion coroutine was started.</returns>
    public bool TryAutoPlaceObject(DraggableObject obj)
    {
        if (obj == null)
        {
            Debug.LogWarning(LogPrefix + " TryAutoPlaceObject called with a null object.");
            return false;
        }

        if (_isFailed)
        {
            Debug.Log(LogPrefix + " Rejected '" + obj.name + "' - game has already failed.");
            return false;
        }

        if (_isMatchAnimating)
        {
            Debug.Log(LogPrefix + " Rejected '" + obj.name + "' - animation is in progress.");
            return false;
        }

        if (!obj.IsSettled)
        {
            Debug.Log(LogPrefix + " Rejected '" + obj.name + "' - object is not yet settled.");
            return false;
        }

        if (IsFull)
        {
            // Tray is full before this object even lands — silently reject it back to the board.
            // Do NOT fire OnTrayFull here; the fail is only triggered after a placed object
            // fails to create a match-3 (handled inside InsertAndPlaceCoroutine).
            Debug.Log(LogPrefix + " Tray is full - rejected '" + obj.name + "' silently.");
            return false;
        }

        if (FindSlotIndex(obj) >= 0)
        {
            Debug.Log(LogPrefix + " Skipped '" + obj.name + "' - already seated in a tray slot.");
            return false;
        }

        int insertIndex = FindInsertSlotIndex(obj.ObjectType);
        StartCoroutine(InsertAndPlaceCoroutine(obj, insertIndex));
        return true;
    }
    /// <summary>
    /// UI entry point for the shuffle booster.
    /// Connect this to the Shuffle booster button's OnClick event.
    /// </summary>
    public void UseShuffleBoosterButton()
    {
        if (_isFailed)
        {
            Debug.Log(LogPrefix + " Shuffle booster ignored — game has already failed.");
            return;
        }

        if (_uiManager != null && _uiManager.IsGameOver)
        {
            Debug.Log(LogPrefix + " Shuffle booster ignored — game is over.");
            return;
        }

        if (_isMatchAnimating)
        {
            Debug.Log(LogPrefix + " Shuffle booster ignored — tray animation is in progress.");
            return;
        }

        if (_shuffleBoosterAmount != null && !_shuffleBoosterAmount.TryConsumeBooster())
            return;

        DraggableObject[] allObjects = FindObjectsByType<DraggableObject>(FindObjectsSortMode.None);

        int shuffledCount = 0;

        foreach (DraggableObject obj in allObjects)
        {
            if (obj == null) continue;
            if (obj.IsLocked) continue;
            if (!obj.IsSettled) continue;
            if (obj.IsAutoMoving) continue;

            obj.ApplyShuffleShake(_shuffleHorizontalForce, _shuffleUpwardForce);
            shuffledCount++;
        }

        Debug.Log(LogPrefix + " Shuffle booster used. Shuffled objects: " + shuffledCount);
    }
    /// <summary>
    /// UI entry point for the pause-timer booster.
    /// Connect this to the Pause Timer booster button's OnClick event.
    /// </summary>
    public void UsePauseTimerBoosterButton()
    {
        if (_isFailed)
        {
            Debug.Log(LogPrefix + " Pause-timer booster ignored — game has already failed.");
            return;
        }

        if (_uiManager != null && _uiManager.IsGameOver)
        {
            Debug.Log(LogPrefix + " Pause-timer booster ignored — game is over.");
            return;
        }

        if (_uiManager == null)
        {
            Debug.LogWarning(LogPrefix + " Pause-timer booster cancelled — UIManager is not assigned.", this);
            return;
        }

        if (_pauseTimerBoosterAmount != null && !_pauseTimerBoosterAmount.TryConsumeBooster())
            return;

        _uiManager.PauseTimerForSeconds(_pauseTimerDuration);

        Debug.Log(LogPrefix + " Pause-timer booster used for " + _pauseTimerDuration + " seconds.");
    }

    /// <summary>
    /// UI entry point for the remove-last-object booster. Connect this to a Unity Button OnClick event.
    /// Validates preconditions via <see cref="CanUseRemoveLastTrayObjectBooster"/> before consuming
    /// a use, so the count is never decremented when the booster cannot actually run.
    /// </summary>

    public void UseRemoveLastTrayObjectBoosterButton()
    {
        int lastIndex;
        if (!CanUseRemoveLastTrayObjectBooster(out lastIndex))
            return;

        if (_removeLastBoosterAmount != null && !_removeLastBoosterAmount.TryConsumeBooster())
            return;

        StartCoroutine(RemoveLastTrayObjectBoosterCoroutine(lastIndex));
    }

    /// <summary>
    /// Checks every precondition required to run the remove-last-object booster.
    /// No state is mutated; this method is purely a query.
    /// </summary>
    /// <param name="lastIndex">
    /// When the method returns <c>true</c>, contains the index of the rightmost occupied tray slot.
    /// Set to <c>-1</c> on failure.
    /// </param>
    /// <returns><c>true</c> when all preconditions are satisfied; <c>false</c> otherwise.</returns>
    private bool CanUseRemoveLastTrayObjectBooster(out int lastIndex)
    {
        lastIndex = -1;

        if (_isFailed)
        {
            Debug.Log(LogPrefix + " Booster 1 ignored — game has already failed.");
            return false;
        }

        if (_uiManager != null && _uiManager.IsGameOver)
        {
            Debug.Log(LogPrefix + " Booster 1 ignored — game is over.");
            return false;
        }

        if (_isMatchAnimating)
        {
            Debug.Log(LogPrefix + " Booster 1 ignored — animation is in progress.");
            return false;
        }

        if (IsEmpty)
        {
            Debug.Log(LogPrefix + " Booster 1 ignored — tray is empty.");
            return false;
        }

        lastIndex = LastOccupiedSlotIndex();
        if (lastIndex < 0)
        {
            Debug.Log(LogPrefix + " Booster 1 ignored — no occupied slot found.");
            return false;
        }

        if (_boosterReturnPoint == null)
        {
            Debug.LogWarning(LogPrefix + " Booster 1 cancelled — _boosterReturnPoint is not assigned.", this);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Drives the booster animation sequence. Intentionally does not check for match-3,
    /// register objectives, or destroy any objects — it only removes one object and
    /// returns it to the board.
    /// <list type="number">
    ///   <item>Sets <see cref="_isMatchAnimating"/> and blocks global input.</item>
    ///   <item>Clears <paramref name="slotIndex"/> from the slot array.</item>
    ///   <item>Flies the object back to <see cref="_boosterReturnPoint"/> via
    ///         <see cref="DraggableObject.MoveBackToBoardFromTray"/>.</item>
    ///   <item>Waits for <see cref="_boosterReturnDuration"/>.</item>
    ///   <item>Compacts remaining tray objects left.</item>
    ///   <item>Clears <see cref="_isMatchAnimating"/> and restores global input.</item>
    /// </list>
    /// </summary>
    private IEnumerator RemoveLastTrayObjectBoosterCoroutine(int slotIndex)
    {
        _isMatchAnimating = true;
        SetGlobalInputBlocked(true);

        DraggableObject obj = _slots[slotIndex];
        _slots[slotIndex] = null;

        if (obj == null)
        {
            Debug.LogWarning(LogPrefix + " Booster coroutine found a null object at slot "
                             + slotIndex + " — aborting.", this);
            _isMatchAnimating = false;
            SetGlobalInputBlocked(false);
            yield break;
        }

        Debug.Log(LogPrefix + " Booster removing '" + obj.name
                  + "' from tray slot " + slotIndex + ".");

        // If this object was counted toward the objective, reverse that contribution
        // and clear the flag so it can be counted again if re-inserted.
        if (obj.IsCountedForObjective)
        {
            _levelObjectiveManager?.UnregisterPlacedObject(obj.ObjectType);
            obj.ClearCountedForObjective();
            Debug.Log(LogPrefix + " Objective count restored for '" + obj.name + "' — returned to board.");
        }

        obj.Unlock();
        obj.MoveBackToBoardFromTray(_boosterReturnPoint.position, _boosterReturnDuration);

        yield return new WaitForSeconds(_boosterReturnDuration);

        yield return StartCoroutine(CompactSlots());

        _isMatchAnimating = false;
        SetGlobalInputBlocked(false);

        Debug.Log(LogPrefix + " Booster animation complete — input restored.");
    }

    // ── Booster – Collect Objective Triple to Slot 6 ─────────────────────────

    /// <summary>
    /// UI entry point for the slot-6 collect-and-merge booster.
    /// Connect this to a Unity Button OnClick event.
    /// Validates preconditions via <see cref="CanUseCollectTripleToSlot6Booster"/> before consuming
    /// a use, so the count is never decremented when the booster cannot actually run.
    /// </summary>
    public void Button_CollectObjectiveTripleToSlot6Booster()
    {
        ObjectType        objectiveType;
        DraggableObject[] selectedObjects;

        if (!CanUseCollectTripleToSlot6Booster(out objectiveType, out selectedObjects))
            return;

        if (_collectTripleBoosterAmount != null && !_collectTripleBoosterAmount.TryConsumeBooster())
            return;

        StartCoroutine(CollectObjectiveTripleToSlot6BoosterCoroutine(objectiveType, selectedObjects));
    }

    /// <summary>
    /// Checks every precondition required to run the slot-6 collect-and-merge booster and,
    /// when all pass, resolves the 3 eligible objects that the coroutine will animate.
    /// No state is mutated; this method is purely a query.
    /// </summary>
    /// <param name="objectiveType">
    /// When the method returns <c>true</c>, contains the <see cref="ObjectType"/> that must be collected.
    /// </param>
    /// <param name="selectedObjects">
    /// When the method returns <c>true</c>, contains exactly 3 <see cref="DraggableObject"/> instances
    /// selected for the merge sequence (tray-priority order).
    /// </param>
    /// <returns><c>true</c> when all preconditions are satisfied; <c>false</c> otherwise.</returns>
    private bool CanUseCollectTripleToSlot6Booster(out ObjectType objectiveType, out DraggableObject[] selectedObjects)
    {
        objectiveType   = default;
        selectedObjects = null;

        if (_isFailed)
        {
            Debug.Log(LogPrefix + " Slot-6 merge booster ignored — game has already failed.");
            return false;
        }

        if (_uiManager != null && _uiManager.IsGameOver)
        {
            Debug.Log(LogPrefix + " Slot-6 merge booster ignored — game is over.");
            return false;
        }

        if (_isMatchAnimating)
        {
            Debug.Log(LogPrefix + " Slot-6 merge booster ignored — animation is in progress.");
            return false;
        }

        if (_levelObjectiveManager == null)
        {
            Debug.LogWarning(LogPrefix + " Slot-6 merge booster cancelled — LevelObjectiveManager not found.", this);
            return false;
        }

        if (!_levelObjectiveManager.TryGetCurrentNeededObjectType(out objectiveType))
        {
            Debug.Log(LogPrefix + " Slot-6 merge booster cancelled — no needed object type (objective complete or not loaded).");
            return false;
        }

        if (_objectSpawner == null)
        {
            Debug.LogWarning(LogPrefix + " Slot-6 merge booster cancelled — ObjectSpawner not found.", this);
            return false;
        }

        const int Required = 3;

        DraggableObject[] candidates = new DraggableObject[Required];
        int               found      = 0;

        // ── Priority 1: tray slots, left to right ─────────────────────────────
        // Prefer objects already seated in the tray. They are already at tray scale
        // and orientation, so moving them to slot 6 is visually coherent.
        // Skip slot 6 itself — it is the merge destination and must stay free.
        for (int i = 0; i < SlotCount - 1 && found < Required; i++)
        {
            DraggableObject trayObj = _slots[i];
            if (trayObj == null)                              continue;
            if (trayObj.ObjectType != objectiveType)          continue;
            if (!trayObj.CanBeSelectedFromTrayByBooster())    continue;

            candidates[found] = trayObj;
            found++;
        }

        // ── Priority 2: live board objects ─────────────────────────────────────
        // Fill remaining slots from ObjectSpawner.LiveObjects. Objects already in
        // the tray (found in any slot) are excluded via FindSlotIndex.
        var live = _objectSpawner.LiveObjects;
        for (int i = 0; i < live.Count && found < Required; i++)
        {
            DraggableObject candidate = live[i];
            if (candidate == null)                     continue;
            if (!candidate.CanBeCollectedByBooster())  continue;
            if (candidate.ObjectType != objectiveType) continue;
            if (FindSlotIndex(candidate) >= 0)         continue; // already seated in a tray slot

            candidates[found] = candidate;
            found++;
        }

        if (found < Required)
        {
            Debug.LogWarning(LogPrefix + " Slot-6 merge booster cancelled — only " + found
                             + " eligible object(s) of type " + objectiveType
                             + " (tray + board, need " + Required + ").", this);
            return false;
        }

        // ── Slot-6 safety check ────────────────────────────────────────────────
        // Slot 6 is the exclusive merge destination. It must be empty, or it must
        // contain one of the 3 already-selected candidates (tray-priority pass above
        // may have picked it). Any other occupant makes the destination unsafe.
        const int MergeSlot = SlotCount - 1; // index 6
        DraggableObject slot6Occupant = _slots[MergeSlot];
        if (slot6Occupant != null)
        {
            bool occupantIsSelected = false;
            for (int i = 0; i < Required; i++)
            {
                if (candidates[i] == slot6Occupant)
                {
                    occupantIsSelected = true;
                    break;
                }
            }

            if (!occupantIsSelected)
            {
                Debug.LogWarning(LogPrefix + " Slot-6 merge booster cancelled — slot 6 is occupied by '"
                                 + slot6Occupant.name + "' which is not part of the booster selection."
                                 + " Clear slot 6 before activating this booster.", this);
                return false;
            }
        }

        selectedObjects = candidates;
        return true;
    }

    /// <summary>
    /// Drives the slot-6 collect-and-merge booster sequence.
    /// <list type="number">
    ///   <item>Acquires <see cref="_boosterInputLock"/>, sets <see cref="_isMatchAnimating"/>,
    ///         and blocks global input.</item>
    ///   <item>Clears the original tray slot for every selected object that is already seated
    ///         in the tray, so <c>_slots[]</c> is consistent before any animation starts.</item>
    ///   <item>Moves each of the 3 selected objects one by one to slot 6 via
    ///         <see cref="DraggableObject.BoosterMoveToExactSlotAndWait"/>.
    ///         Registers one objective progress tick immediately after each object arrives.</item>
    ///   <item>Hides objects [0] and [1] instantly, then pops and shrinks object [2].</item>
    ///   <item>Destroys all 3 objects and notifies <see cref="ObjectSpawner"/>.</item>
    ///   <item>Compacts remaining tray objects, then releases the lock and restores input.</item>
    /// </list>
    /// Never calls <see cref="TryAutoPlaceObject"/>, <see cref="CheckForTripleMatch"/>,
    /// or fires <see cref="OnTrayFull"/>.
    /// </summary>
    private IEnumerator CollectObjectiveTripleToSlot6BoosterCoroutine(ObjectType neededType, DraggableObject[] selectedObjects)
    {
        _boosterInputLock = true;
        _isMatchAnimating = true;
        SetGlobalInputBlocked(true);

        Vector3 mergePos = BoosterSlot6Position();

        Debug.Log(LogPrefix + " Collect-triple booster starting — merging 3x " + neededType + " at " + mergePos + ".");

        // ── Step 1: clear original tray slots for all selected objects ────────
        // Done upfront so _slots[] is consistent before any object starts moving.
        for (int i = 0; i < selectedObjects.Length; i++)
        {
            if (selectedObjects[i] == null) continue;

            int oldSlot = FindSlotIndex(selectedObjects[i]);
            if (oldSlot >= 0)
            {
                _slots[oldSlot] = null;
                Debug.Log(LogPrefix + " Cleared tray slot " + oldSlot
                          + " for '" + selectedObjects[i].name + "' before booster move.");
            }
        }

        // ── Step 2: move each object to slot 6 one by one ────────────────────
        for (int i = 0; i < selectedObjects.Length; i++)
        {
            DraggableObject obj = selectedObjects[i];

            if (obj == null)
            {
                Debug.LogWarning(LogPrefix + " Collect-triple booster: object at index " + i
                                 + " is null — aborting sequence.", this);
                _boosterInputLock = false;
                _isMatchAnimating = false;
                SetGlobalInputBlocked(false);
                yield break;
            }

            Debug.Log(LogPrefix + " Collect-triple booster moving object " + (i + 1)
                      + "/3: '" + obj.name + "' to slot 6.");

            yield return StartCoroutine(obj.BoosterMoveToExactSlotAndWait(mergePos, obj.AutoMoveDuration));

            // Only register the objective tick if this object has not already been counted.
            // Tray-priority objects (already in a slot) may have IsCountedForObjective = true.
            if (!obj.IsCountedForObjective)
            {
                bool counted = _levelObjectiveManager != null
                    && _levelObjectiveManager.RegisterPlacedObject(obj.ObjectType);
                if (counted)
                {
                    obj.MarkCountedForObjective();
                    Debug.Log(LogPrefix + " Collect-triple booster object " + (i + 1) + "/3 arrived — objective registered.");
                }
                else
                {
                    Debug.Log(LogPrefix + " Collect-triple booster object " + (i + 1) + "/3 arrived — registration returned false, not marked.");
                }
            }
            else
            {
                Debug.Log(LogPrefix + " Collect-triple booster object " + (i + 1) + "/3 arrived — already counted, skipping registration.");
            }
        }

        // ── Step 3: merge animation at slot 6 ────────────────────────────────
        // All 3 objects are at mergePos. Hide [0] and [1], pop-and-shrink [2].
        Debug.Log(LogPrefix + " Collect-triple booster running merge animation.");

        if (selectedObjects[0] != null) selectedObjects[0].HideInstant();
        if (selectedObjects[1] != null) selectedObjects[1].HideInstant();

        if (selectedObjects[2] != null)
        {
            Vector3 popPeakScale = selectedObjects[2].transform.localScale * _popScaleMultiplier;
            yield return StartCoroutine(selectedObjects[2].PopAndShrink(
                popPeakScale, _finalShrinkScale, _popUpDuration, _shrinkDuration));
        }

        if (_destroyDelayAfterShrink > 0f)
            yield return new WaitForSeconds(_destroyDelayAfterShrink);

        // ── Step 4: destroy and notify spawner ───────────────────────────────
        Debug.Log(LogPrefix + " Collect-triple booster destroying 3x " + neededType + ".");

        _objectSpawner?.OnObjectsDestroyed(selectedObjects[0], selectedObjects[1], selectedObjects[2]);

        for (int i = 0; i < selectedObjects.Length; i++)
        {
            if (selectedObjects[i] != null)
                Destroy(selectedObjects[i].gameObject);
        }

        // One frame for Unity to flush Destroy() before compaction reads _slots.
        yield return null;

        // ── Step 5: compact remaining tray objects ────────────────────────────
        yield return StartCoroutine(CompactSlots());

        // ── Step 6: release lock and restore input ────────────────────────────
        _isMatchAnimating = false;
        _boosterInputLock = false;
        SetGlobalInputBlocked(false);

        Debug.Log(LogPrefix + " Collect-triple booster complete — input restored.");
    }

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        Collider col = GetComponent<Collider>();
        if (!col.isTrigger)
        {
            col.isTrigger = true;
            Debug.LogWarning(LogPrefix + " Collider on '" + name + "' was not a trigger - fixed automatically.", this);
        }

        if (_slotAnchors == null || _slotAnchors.Length != SlotCount)
            Debug.LogWarning(LogPrefix + " '" + name + "' expects exactly " + SlotCount
                             + " tray slot anchors, found "
                             + (_slotAnchors != null ? _slotAnchors.Length : 0)
                             + ". Assign all entries in the Inspector.", this);

        if (_objectSpawner == null)
            _objectSpawner = FindFirstObjectByType<ObjectSpawner>();

        if (_objectSpawner == null)
            Debug.LogWarning(LogPrefix + " No ObjectSpawner found. Win tracking will not work.", this);

        if (_uiManager == null)
            _uiManager = FindFirstObjectByType<UIManager>();

        if (_levelObjectiveManager == null)
            _levelObjectiveManager = FindFirstObjectByType<LevelObjectiveManager>();

        // Route the tray-full event directly to UIManager so it can show the FailWindow.
        OnTrayFull += HandleTrayFull;
    }

    // ── Fail handling ─────────────────────────────────────────────────────────

    /// <summary>
    /// Permanently blocks the tray from accepting any further objects.
    /// Called externally (e.g. by <see cref="UIManager"/> on time-up) so that
    /// every game-over path converges on the same fail flag.
    /// </summary>
    public void SetFailed()
    {
        if (_isFailed) return;
        _isFailed = true;
        Debug.Log(LogPrefix + " SetFailed called — tray is now permanently blocked.");
    }

    /// <summary>
    /// Invoked when <see cref="OnTrayFull"/> fires. Sets the permanent fail flag,
    /// blocks all object input, and delegates UI to <see cref="UIManager"/>.
    ///
    /// Suppressed while <see cref="_boosterInputLock"/> is active: booster 2 temporarily
    /// stacks objects at slot 6 without using normal insertion, so <see cref="IsFull"/>
    /// can appear true mid-sequence even though the tray is not actually full.
    /// </summary>
    private void HandleTrayFull()
    {
        if (_boosterInputLock)
        {
            Debug.Log(LogPrefix + " HandleTrayFull suppressed — booster 2 merge is in progress.");
            return;
        }

        _isFailed = true;
        SetGlobalInputBlocked(true);
        _uiManager?.OnTrayFull();
        Debug.Log(LogPrefix + " Tray full — fail state set, input blocked.");
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.TryGetComponent(out DraggableObject obj)) return;

        // Locked objects are managed entirely by the tray; ignore physics exit events for them.
        if (obj.IsLocked) return;

        int slotIndex = FindSlotIndex(obj);
        if (slotIndex < 0) return;

        _slots[slotIndex] = null;
        obj.Unlock();

        Debug.Log(LogPrefix + " '" + obj.name + "' left tray slot " + slotIndex
                  + ". Occupied: " + OccupiedCount + "/" + SlotCount);
        OnObjectExited(obj, slotIndex);
    }

    // ── Insertion & shift logic ──────────────────────────────────────────────

    /// <summary>
    /// Drives the full insert-shift-lock sequence for a newly tapped object:
    /// <list type="number">
    ///   <item>Blocks input.</item>
    ///   <item>Shifts all objects at and to the right of <paramref name="insertIndex"/>
    ///         one slot to the right, animating each to its new anchor.</item>
    ///   <item>Locks the incoming object at <paramref name="insertIndex"/>.</item>
    ///   <item>Checks for a match-3 first. If found, starts the merge animation which
    ///         unblocks input when complete.</item>
    ///   <item>Only if no match-3 was found AND the tray is now full fires
    ///         <see cref="OnTrayFull"/> as the lose condition.</item>
    /// </list>
    /// </summary>
    private IEnumerator InsertAndPlaceCoroutine(DraggableObject obj, int insertIndex)
    {
        _isMatchAnimating = true;
        SetGlobalInputBlocked(true);

        // Right-shift every object from insertIndex onward, working right-to-left
        // to avoid overwriting neighbours before they are moved.
        for (int i = SlotCount - 1; i > insertIndex; i--)
        {
            if (_slots[i - 1] != null)
            {
                _slots[i] = _slots[i - 1];
                _slots[i].MoveToSlot(SlotAnchorPosition(i), _compactDuration);
                Debug.Log(LogPrefix + " Shifted '" + _slots[i].name
                          + "' from slot " + (i - 1) + " to slot " + i);
            }
        }

        // Clear the insert slot so the new object can occupy it.
        _slots[insertIndex] = null;

        // Wait for all shift animations to finish before placing the new object.
        yield return new WaitForSeconds(_compactDuration);

        // Lock the new object into its insert slot.
        _slots[insertIndex] = obj;
        obj.Lock(SlotAnchorPosition(insertIndex));

        // Notify objective manager now that the object is confirmed in a slot.
        // Skip registration entirely if already counted to prevent double-counting
        // when a tray object is repositioned by booster logic.
        if (_levelObjectiveManager != null && !obj.IsCountedForObjective)
        {
            bool counted = _levelObjectiveManager.RegisterPlacedObject(obj.ObjectType);
            if (counted)
                obj.MarkCountedForObjective();
        }

        Debug.Log(LogPrefix + " '" + obj.name + "' placed into tray slot " + insertIndex
                  + ". Occupied: " + OccupiedCount + "/" + SlotCount);
        OnObjectEntered(obj, insertIndex);

        // ── Step 1: check for match-3 first ───────────────────────────────────
        // If a match is found, MergeSequenceCoroutine takes ownership of input
        // unblocking. Do NOT check for tray-full here — the 7th object just
        // created a match, so the player should not fail.
        bool matchStarted = CheckForTripleMatch();

        if (!matchStarted)
        {
            // ── Step 2: no match — unblock input, then check tray-full ────────
            _isMatchAnimating = false;
            SetGlobalInputBlocked(false);

            if (IsFull)
            {
                Debug.Log(LogPrefix + " Tray is full after placement with no match-3 — fail condition.");
                OnTrayFull?.Invoke();
            }
        }
    }

    /// <summary>
    /// Determines the slot index at which a new object of <paramref name="type"/> should be inserted.
    /// Returns the index immediately after the last existing object of the same type.
    /// Falls back to <see cref="FirstEmptySlotIndex"/> when no matching type is present.
    /// Returns -1 if there is no valid slot to insert into.
    /// </summary>
    private int FindInsertSlotIndex(ObjectType type)
    {
        int lastMatchIndex = -1;
        for (int i = 0; i < SlotCount; i++)
        {
            if (_slots[i] != null && _slots[i].ObjectType == type)
                lastMatchIndex = i;
        }

        if (lastMatchIndex >= 0)
        {
            // Insert directly after the last matching object.
            // The slot at lastMatchIndex + 1 may currently be occupied — that is fine,
            // InsertAndPlaceCoroutine will shift it and everything to the right.
            int afterLast = lastMatchIndex + 1;
            if (afterLast < SlotCount)
                return afterLast;

            // The last match is in the rightmost slot and the tray is full;
            // the IsFull guard in TryAutoPlaceObject already handles this case.
            return -1;
        }

        return FirstEmptySlotIndex();
    }

    // ── Match-3 logic ────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all occupied tray slots and checks whether any ObjectType appears
    /// 3 or more times. Because same-type objects are always adjacent after smart
    /// insertion, the 3 matched objects will already be grouped.
    /// When a triple is found, exactly 3 matching slots are cleared and
    /// <see cref="OnTripleMatched"/> is called with those objects and the world-space
    /// position of the middle slot (used as the merge destination).
    /// Only one triple is resolved per call.
    /// </summary>
    /// <returns>True when a triple was found and a match animation was started.</returns>
    private bool CheckForTripleMatch()
    {
        int typeCount = Enum.GetValues(typeof(ObjectType)).Length;
        int[] counts  = new int[typeCount];

        for (int i = 0; i < SlotCount; i++)
            if (_slots[i] != null)
                counts[(int)_slots[i].ObjectType]++;

        for (int t = 0; t < typeCount; t++)
        {
            if (counts[t] < 3) continue;

            ObjectType matchedType = (ObjectType)t;
            Debug.Log(LogPrefix + " Match-3 found - type=" + matchedType
                      + ". Starting merge animation.");

            int cleared = 0;
            DraggableObject[] matched      = new DraggableObject[3];
            int[]             matchedSlots = new int[3];

            for (int i = 0; i < SlotCount && cleared < 3; i++)
            {
                if (_slots[i] != null && _slots[i].ObjectType == matchedType)
                {
                    matched[cleared]      = _slots[i];
                    matchedSlots[cleared] = i;
                    _slots[i]             = null;
                    cleared++;
                }
            }

            // Capture the middle slot's world position before the slots are cleared so the
            // merge coroutine has a stable destination even after the array is modified.
            Vector3 mergeCenterPos = SlotAnchorPosition(matchedSlots[1]);

            OnTripleMatched(matched, matchedType, mergeCenterPos);
            return true;
        }

        return false;
    }

    // ── Tray compaction ──────────────────────────────────────────────────────

    /// <summary>
    /// Left-packs the tray after a match-3 removal: shifts every surviving object
    /// to the lowest available slot index and slides it to the new anchor position.
    /// Objects remain locked (kinematic) throughout the slide animation.
    /// </summary>
    private IEnumerator CompactSlots()
    {
        // Gather surviving tray objects in their current left-to-right order.
        DraggableObject[] compacted = new DraggableObject[SlotCount];
        int writeIndex = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            if (_slots[i] != null)
                compacted[writeIndex++] = _slots[i];
        }

        // Commit the compacted layout; animate any object that shifted tray slot.
        for (int i = 0; i < SlotCount; i++)
        {
            bool hadObject      = _slots[i] != null;
            bool willHaveObject = i < writeIndex;

            _slots[i] = compacted[i]; // null for indices >= writeIndex

            if (willHaveObject)
            {
                Vector3 newPos = SlotAnchorPosition(i);

                if (!hadObject || _slots[i].transform.position != newPos)
                {
                    Debug.Log(LogPrefix + " Compacting '" + _slots[i].name
                              + "' to tray slot " + i);
                    _slots[i].MoveToSlot(newPos, _compactDuration);
                }
            }
        }

        // Wait for the compaction slides to finish before releasing input.
        yield return new WaitForSeconds(_compactDuration);
    }

    // ── Input blocking helpers ────────────────────────────────────────────────

    /// <summary>
    /// Sets <see cref="DraggableObject.IsInputBlocked"/> on every active
    /// <see cref="DraggableObject"/> in the scene to suppress taps during animation.
    ///
    /// When <see cref="_boosterInputLock"/> is active, calls with <c>blocked = false</c>
    /// are silently ignored. This prevents <see cref="InsertAndPlaceCoroutine"/> and
    /// <see cref="MergeSequenceCoroutine"/> — which each release input at their own end —
    /// from opening a gap between booster dispatches where a player tap could sneak through.
    /// The booster coroutine is the sole authority on releasing input for its sequence.
    /// </summary>
    private void SetGlobalInputBlocked(bool blocked)
    {
        if (!blocked && _boosterInputLock)
        {
            Debug.Log(LogPrefix + " SetGlobalInputBlocked(false) suppressed — booster input lock is active.");
            return;
        }

        DraggableObject[] all = FindObjectsByType<DraggableObject>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
            all[i].IsInputBlocked = blocked;
    }

    // ── Slot helpers ─────────────────────────────────────────────────────────

    private Vector3 SlotAnchorPosition(int slotIndex)
    {
        if (_slotAnchors != null && slotIndex >= 0 && slotIndex < _slotAnchors.Length
            && _slotAnchors[slotIndex] != null)
            return _slotAnchors[slotIndex].position;

        Debug.LogWarning(LogPrefix + " Tray slot anchor at index " + slotIndex
                         + " is not assigned - falling back to tray centre.", this);
        return transform.position;
    }

    /// <summary>
    /// Returns the world-space position of the fixed merge destination used by booster 2.
    /// All three selected objects move to this position one by one via
    /// <see cref="DraggableObject.BoosterMoveToExactSlotAndWait"/>.
    /// Normal slot insertion (<see cref="TryAutoPlaceObject"/>, <see cref="FindInsertSlotIndex"/>)
    /// is never invoked for this booster.
    /// </summary>
    private Vector3 BoosterSlot6Position()
    {
        return SlotAnchorPosition(6);
    }

    private int FirstEmptySlotIndex()
    {
        for (int i = 0; i < SlotCount; i++)
            if (_slots[i] == null) return i;
        return -1;
    }

    /// <summary>
    /// Returns the index of the rightmost occupied tray slot, or -1 if all slots are empty.
    /// </summary>
    private int LastOccupiedSlotIndex()
    {
        for (int i = SlotCount - 1; i >= 0; i--)
            if (_slots[i] != null) return i;
        return -1;
    }

    private int FindSlotIndex(DraggableObject obj)
    {
        for (int i = 0; i < SlotCount; i++)
            if (_slots[i] == obj) return i;
        return -1;
    }

    // ── Virtual hooks ────────────────────────────────────────────────────────

    /// <summary>
    /// Called when an object is successfully locked into a tray slot.
    /// Override to update slot visuals or play a placement sound.
    /// </summary>
    protected virtual void OnObjectEntered(DraggableObject obj, int slotIndex)
    {
        Debug.Log(LogPrefix + " Tray slot " + slotIndex + " filled - type=" + obj.ObjectType);
    }

    /// <summary>
    /// Called when a non-locked object leaves the tray collider unexpectedly.
    /// Override to revert slot visuals.
    /// </summary>
    protected virtual void OnObjectExited(DraggableObject obj, int slotIndex)
    {
        Debug.Log(LogPrefix + " Tray slot " + slotIndex + " vacated - type=" + obj.ObjectType);
    }

    /// <summary>
    /// Called after 3 matching objects are found and their tray slots are cleared.
    /// Runs the merge coroutine:
    /// <list type="number">
    ///   <item>Left and right objects slide into the middle object's position simultaneously.</item>
    ///   <item>Left and right are hidden instantly once they arrive.</item>
    ///   <item>Middle object pops up to <see cref="_popScaleMultiplier"/> × tray scale.</item>
    ///   <item>Middle object shrinks down to <see cref="_finalShrinkScale"/> and is destroyed.</item>
    ///   <item>Remaining tray objects compact left.</item>
    ///   <item>Tap input is restored.</item>
    /// </list>
    /// Override to customise the sequence (e.g. add particles or sound).
    /// </summary>
    /// <param name="matched">The 3 matched objects in tray-slot order: [0]=left, [1]=middle, [2]=right.</param>
    /// <param name="objectType">The shared ObjectType of the removed triple.</param>
    /// <param name="mergeCenterPos">World-space position of the middle slot, used as the merge destination.</param>
    protected virtual void OnTripleMatched(DraggableObject[] matched, ObjectType objectType, Vector3 mergeCenterPos)
    {
        StartCoroutine(MergeSequenceCoroutine(matched, objectType, mergeCenterPos));
    }

    /// <summary>
    /// Drives the full match-3 animation sequence:
    /// 1. Left and right objects slide to the middle position (parallel, no scale change during slide).
    /// 2. Left and right are instantly hidden.
    /// 3. Middle object pops up to peak scale then shrinks to <see cref="_finalShrinkScale"/>.
    /// 4. Middle object is destroyed after <see cref="_destroyDelayAfterShrink"/>.
    /// 5. Remaining objects compact left.
    /// 6. Input is restored.
    /// </summary>
    private IEnumerator MergeSequenceCoroutine(DraggableObject[] matched, ObjectType objectType, Vector3 mergeCenterPos)
    {
        _isMatchAnimating = true;
        Debug.Log(LogPrefix + " Merge animation started for 3x '" + objectType + "'. "
                  + "Left and right sliding into middle at " + mergeCenterPos);

        // ── Phase 1: left and right slide to the middle position in parallel ──
        Coroutine slideLeft  = StartCoroutine(matched[0].MergeToAndWait(mergeCenterPos, _mergeMoveDuration));
        Coroutine slideRight = StartCoroutine(matched[2].MergeToAndWait(mergeCenterPos, _mergeMoveDuration));

        yield return slideLeft;
        yield return slideRight;

        // ── Phase 2: instantly hide the left and right objects ─────────────────
        matched[0].HideInstant();
        matched[2].HideInstant();

        Debug.Log(LogPrefix + " Left and right hidden. Starting middle pop.");

        // ── Phase 3: middle object pops up then shrinks ────────────────────────
        Vector3 popPeakScale = matched[1].transform.localScale * _popScaleMultiplier;
        yield return StartCoroutine(matched[1].PopAndShrink(popPeakScale, _finalShrinkScale,
                                                            _popUpDuration, _shrinkDuration));

        if (_destroyDelayAfterShrink > 0f)
            yield return new WaitForSeconds(_destroyDelayAfterShrink);

        // ── Phase 4: destroy all 3 and notify the spawner ─────────────────────
        Debug.Log(LogPrefix + " Destroying 3x '" + objectType + "'.");
        _objectSpawner?.OnObjectsDestroyed(matched[0], matched[1], matched[2]);

        for (int i = 0; i < matched.Length; i++)
        {
            if (matched[i] != null)
                Destroy(matched[i].gameObject);
        }

        // Wait one frame so Unity flushes Destroy() before compaction reads _slots.
        yield return null;

        // ── Phase 5: compact surviving objects to the left ─────────────────────
        yield return StartCoroutine(CompactSlots());

        // ── Phase 6: restore input ─────────────────────────────────────────────
        _isMatchAnimating = false;
        SetGlobalInputBlocked(false);
        Debug.Log(LogPrefix + " Merge animation complete - input restored.");
    }
}
