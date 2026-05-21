using System;
using System.Collections;
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

    [Tooltip("Duration in seconds for surviving objects to slide into compacted positions after a match-3 removal.")]
    [SerializeField] private float _compactDuration = 0.2f;

    [Header("Match-3 Merge Animation")]
    [Tooltip("Duration in seconds for the left and right matching objects to slide into the middle object.")]
    [SerializeField] private float _mergeDuration = 0.2f;

    [Tooltip("Seconds to wait after the merge is complete before destroying the 3 objects. " +
             "A small pause gives the player a moment to register the merge before objects vanish.")]
    [SerializeField] private float _destroyDelayAfterMerge = 0.05f;

    [Tooltip("All 3 merging objects scale to their current tray scale multiplied by this value during the merge, " +
             "creating a subtle squash effect. Set to 1 to disable. Recommended range: 0.6–0.9.")]
    [SerializeField] private float _mergeScaleMultiplier = 0.8f;

    private readonly DraggableObject[] _slots = new DraggableObject[SlotCount];

    // True while any tray animation (shift, match gather, compaction) is running.
    // Suppresses new placements and scene-object taps during animation.
    private bool _isMatchAnimating;

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
    /// Rejects the object and fires <see cref="OnTrayFull"/> when the tray is full.
    /// Also rejects silently while a shift or match-3 animation is running.
    /// After a successful placement, checks for a match-3 removal.
    /// If the tray is still full after the match check, fires <see cref="OnTrayFull"/>
    /// as the lose condition.
    /// </summary>
    /// <returns>True when the object was accepted and its insertion coroutine was started.</returns>
    public bool TryAutoPlaceObject(DraggableObject obj)
    {
        if (obj == null)
        {
            Debug.LogWarning(LogPrefix + " TryAutoPlaceObject called with a null object.");
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
            Debug.Log(LogPrefix + " Tray is full - rejected '" + obj.name + "'.");
            OnTrayFull?.Invoke();
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
    ///   <item>Runs the triple-match check.</item>
    ///   <item>Restores input (or hands off to the match animation coroutine).</item>
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

        Debug.Log(LogPrefix + " '" + obj.name + "' placed into tray slot " + insertIndex
                  + ". Occupied: " + OccupiedCount + "/" + SlotCount);
        OnObjectEntered(obj, insertIndex);

        // Check for a triple match. If a match is found, CheckForTripleMatch starts
        // MatchSequenceCoroutine which will unblock input when it finishes.
        bool matchStarted = CheckForTripleMatch();

        if (!matchStarted)
        {
            _isMatchAnimating = false;
            SetGlobalInputBlocked(false);

            // Tray is still full after placement with no match removal — lose condition.
            if (IsFull)
            {
                Debug.Log(LogPrefix + " Tray is full after placement - no match-3 removal. Lose condition.");
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
    /// </summary>
    private void SetGlobalInputBlocked(bool blocked)
    {
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

    private int FirstEmptySlotIndex()
    {
        for (int i = 0; i < SlotCount; i++)
            if (_slots[i] == null) return i;
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
    ///   <item>All 3 objects scale down by <see cref="_mergeScaleMultiplier"/> during the slide.</item>
    ///   <item>After <see cref="_destroyDelayAfterMerge"/>, all 3 are destroyed.</item>
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
    /// Drives the match-3 merge → destroy → compact sequence.
    /// The left and right objects animate to the middle object's slot position while
    /// all three simultaneously scale down, giving a satisfying squash-into-center feel.
    /// Input is already blocked by <see cref="InsertAndPlaceCoroutine"/>; this coroutine
    /// keeps it blocked and releases it when done.
    /// </summary>
    private IEnumerator MergeSequenceCoroutine(DraggableObject[] matched, ObjectType objectType, Vector3 mergeCenterPos)
    {
        _isMatchAnimating = true;
        Debug.Log(LogPrefix + " Merge animation started for 3x '" + objectType + "'. "
                  + "Left and right sliding into middle at " + mergeCenterPos);

        // Target scale: current tray scale * squash multiplier.
        // matched[1] (middle) is the reference; all three objects share the same tray scale.
        Vector3 mergeScale = matched[1].transform.localScale * _mergeScaleMultiplier;

        // Run all three merge animations in parallel:
        //   matched[0] = left  → slides to center + scales down
        //   matched[1] = middle → stays in place  + scales down
        //   matched[2] = right → slides to center + scales down
        Coroutine mergeLeft   = StartCoroutine(matched[0].MergeToAndWait(mergeCenterPos, mergeScale, _mergeDuration));
        Coroutine mergeMiddle = StartCoroutine(matched[1].MergeToAndWait(mergeCenterPos, mergeScale, _mergeDuration));
        Coroutine mergeRight  = StartCoroutine(matched[2].MergeToAndWait(mergeCenterPos, mergeScale, _mergeDuration));

        yield return mergeLeft;
        yield return mergeMiddle;
        yield return mergeRight;

        Debug.Log(LogPrefix + " Merge complete. Waiting " + _destroyDelayAfterMerge + "s before destroy.");

        if (_destroyDelayAfterMerge > 0f)
            yield return new WaitForSeconds(_destroyDelayAfterMerge);

        // Destroy all 3 and notify the spawner.
        Debug.Log(LogPrefix + " Destroying 3x '" + objectType + "'.");
        _objectSpawner?.OnObjectsDestroyed(matched[0], matched[1], matched[2]);

        for (int i = 0; i < matched.Length; i++)
        {
            if (matched[i] != null)
                Destroy(matched[i].gameObject);
        }

        // Wait one frame so Unity flushes Destroy() before compaction reads _slots.
        yield return null;

        // Compact surviving objects to the left and wait for the slide to finish.
        yield return StartCoroutine(CompactSlots());

        // Restore tap input.
        _isMatchAnimating = false;
        SetGlobalInputBlocked(false);
        Debug.Log(LogPrefix + " Merge animation complete - input restored.");

        // Check lose condition after the tray is fully settled.
        if (IsFull)
        {
            Debug.Log(LogPrefix + " Tray is full after match removal — lose condition.");
            OnTrayFull?.Invoke();
        }
    }
}
