using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Manages the 7-slot tray that sits at the bottom of the play field.
/// Objects tapped in the scene fly into the first empty tray slot one by one.
///
/// Match-3 removal: after every successful placement the tray scans all occupied
/// slots for any ObjectType that appears 3 or more times. When a triple is found,
/// those 3 objects are destroyed regardless of their slot positions, and the
/// remaining objects compact left so the tray stays gapless.
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

    private readonly DraggableObject[] _slots = new DraggableObject[SlotCount];

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
    /// Returns the world-space position of the first empty tray slot.
    /// Used by <see cref="DraggableObject"/> to aim its fly-in animation before
    /// placement is confirmed.
    /// </summary>
    public Vector3 GetPreviewAutoSlotPosition(DraggableObject obj)
    {
        int firstEmpty = FirstEmptySlotIndex();
        return firstEmpty >= 0 ? SlotAnchorPosition(firstEmpty) : transform.position;
    }

    /// <summary>
    /// Attempts to place <paramref name="obj"/> into the first empty tray slot.
    /// Rejects the object and fires <see cref="OnTrayFull"/> when the tray is full.
    /// After a successful placement, checks for a match-3 removal.
    /// If the tray is still full after the match check, fires <see cref="OnTrayFull"/>
    /// as the lose condition.
    /// </summary>
    /// <returns>True when the object was accepted and locked into a tray slot.</returns>
    public bool TryAutoPlaceObject(DraggableObject obj)
    {
        if (obj == null)
        {
            Debug.LogWarning(LogPrefix + " TryAutoPlaceObject called with a null object.");
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

        int slotIndex = FirstEmptySlotIndex();
        _slots[slotIndex] = obj;
        obj.Lock(SlotAnchorPosition(slotIndex));

        Debug.Log(LogPrefix + " '" + obj.name + "' placed into tray slot " + slotIndex
                  + ". Occupied: " + OccupiedCount + "/" + SlotCount);

        OnObjectEntered(obj, slotIndex);
        CheckForTripleMatch();

        // If the tray is still full after the match-3 check, no slots were freed —
        // the tray cannot accept any more objects: lose condition.
        if (IsFull)
        {
            Debug.Log(LogPrefix + " Tray is full after placement - no match-3 removal occurred. Lose condition.");
            OnTrayFull?.Invoke();
        }

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

    // ── Match-3 logic ────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all occupied tray slots and checks whether any ObjectType appears
    /// 3 or more times. Slot adjacency does not matter — matches are position-independent.
    /// When a triple is found, exactly 3 matching slots are cleared and
    /// <see cref="OnTripleMatched"/> is called with those objects.
    /// Only one triple is resolved per call.
    /// </summary>
    private void CheckForTripleMatch()
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
                      + ". Removing 3 objects from tray.");

            int cleared = 0;
            DraggableObject[] matched = new DraggableObject[3];

            for (int i = 0; i < SlotCount && cleared < 3; i++)
            {
                if (_slots[i] != null && _slots[i].ObjectType == matchedType)
                {
                    matched[cleared] = _slots[i];
                    _slots[i]        = null;
                    cleared++;
                }
            }

            OnTripleMatched(matched, matchedType);
            return;
        }
    }

    // ── Tray compaction ──────────────────────────────────────────────────────

    /// <summary>
    /// Left-packs the tray after a match-3 removal: shifts every surviving object
    /// to the lowest available slot index and slides it to the new anchor position.
    /// Objects remain locked (kinematic) throughout the slide animation.
    /// Runs one frame after destruction so Unity's Destroy() has been flushed first.
    /// </summary>
    private IEnumerator CompactSlots()
    {
        yield return null;

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
    /// Default behaviour: destroys all three GameObjects, notifies the spawner, then
    /// compacts the remaining tray contents.
    /// Override to play a match-3 animation before the objects are destroyed.
    /// </summary>
    /// <param name="matched">The 3 matched objects in tray-slot order (left to right).</param>
    /// <param name="objectType">The shared ObjectType of the removed triple.</param>
    protected virtual void OnTripleMatched(DraggableObject[] matched, ObjectType objectType)
    {
        Debug.Log(LogPrefix + " Match-3 removal - destroying 3x '" + objectType + "'.");
        _objectSpawner?.OnObjectsDestroyed(matched[0], matched[1], matched[2]);

        for (int i = 0; i < matched.Length; i++)
            Destroy(matched[i].gameObject);

        StartCoroutine(CompactSlots());
    }
}
