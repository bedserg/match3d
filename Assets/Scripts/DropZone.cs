using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Represents a hole with two logical slots (left = index 0, right = index 1).
///
/// Empty-hole rule   : An empty hole accepts any ObjectType.
/// Type-locking rule : Once the first object is placed, the hole locks to that
///                     ObjectType and rejects all others until it is cleared.
/// Two-slot rule     : The hole holds at most 2 objects. A third is always
///                     rejected regardless of type.
/// Match rule        : When both slots are filled (always a matching pair thanks
///                     to type-locking), both objects are destroyed and the hole
///                     resets to the empty state.
/// Wrong-object rule : An object of the wrong type may physically enter the trigger
///                     while being dragged, but it is never stored in a slot. If
///                     released inside the trigger it is smoothly returned to the
///                     position it was grabbed from.
/// </summary>
[RequireComponent(typeof(Collider))]
public class DropZone : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("World-space anchor for the left slot (index 0). Assign a child Transform.")]
    [SerializeField] private Transform _leftSlotAnchor;

    [Tooltip("World-space anchor for the right slot (index 1). Assign a child Transform.")]
    [SerializeField] private Transform _rightSlotAnchor;

    [Tooltip("Reference to the ObjectSpawner so it can be notified when a pair is destroyed.")]
    [SerializeField] private ObjectSpawner _objectSpawner;

    // ── Constants ────────────────────────────────────────────────────────────

    private const string ObjectTag    = "Object"; // keep the existing Unity tag; rename in Project Settings if desired
    private const int    SlotCount    = 2;
    private const string DraggingLayerName = "Dragging";

    // ── Private state ─────────────────────────────────────────────────────────

    /// <summary>Cached index of the Dragging physics layer. -1 if the layer does not exist.</summary>
    private int _draggingLayerIndex = -1;

    /// <summary>
    /// Fixed-size slot array. Index 0 = left slot, index 1 = right slot.
    /// A null entry means the slot is empty.
    /// </summary>
    private readonly DraggableObject[] _slots = new DraggableObject[SlotCount];

    /// <summary>
    /// The ObjectType this hole is locked to, or null when the hole is empty
    /// and therefore accepts any object.
    /// </summary>
    private ObjectType? _lockedType;

    /// <summary>
    /// Objects currently overlapping the trigger that are of the wrong type while
    /// the zone is locked. Tracked so we can detect when the player releases them
    /// inside the zone and return them to their drag-start position.
    /// </summary>
    private readonly HashSet<DraggableObject> _wrongObjectsInside = new HashSet<DraggableObject>();

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>True when both slots are empty.</summary>
    public bool IsEmpty => _slots[0] == null && _slots[1] == null;

    /// <summary>True when both slots are occupied.</summary>
    public bool IsFull => _slots[0] != null && _slots[1] != null;

    /// <summary>The locked ObjectType, or null when the hole is empty.</summary>
    public ObjectType? LockedType => _lockedType;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        Collider col = GetComponent<Collider>();
        if (!col.isTrigger)
        {
            col.isTrigger = true;
            Debug.LogWarning($"[DropZone] Collider on '{name}' was not a trigger — fixed automatically.", this);
        }

        if (_leftSlotAnchor == null || _rightSlotAnchor == null)
            Debug.LogWarning($"[DropZone] '{name}' is missing one or both slot anchors. " +
                             "Assign LeftSlotAnchor and RightSlotAnchor in the Inspector.", this);

        if (_objectSpawner == null)
            _objectSpawner = FindFirstObjectByType<ObjectSpawner>();

        _draggingLayerIndex = LayerMask.NameToLayer(DraggingLayerName);
        if (_draggingLayerIndex < 0)
            Debug.LogWarning($"[DropZone] Layer '{DraggingLayerName}' not found. " +
                             "Objects will fall back to IsDragging check only.", this);
    }

    private void OnTriggerEnter(Collider other)
    {
        TryAcceptObject(other);
    }

    private void OnTriggerStay(Collider other)
    {
        // An object may have settled while already overlapping the trigger (e.g. it fell
        // through, bounced, and came to rest inside the zone before the player drags it).
        // Re-evaluate on every stay tick so it gets accepted once the player drags it in.
        TryAcceptObject(other);

        // ── Wrong-object release detection ────────────────────────────────────
        if (!other.CompareTag(ObjectTag)) return;
        if (!other.TryGetComponent(out DraggableObject wrongObject)) return;
        if (!_wrongObjectsInside.Contains(wrongObject)) return;

        if (!wrongObject.IsDragging)
        {
            Debug.Log($"[DropZone] Wrong object '{wrongObject.name}' released inside '{name}' — returning to drag start.");
            _wrongObjectsInside.Remove(wrongObject);
            wrongObject.ReturnToDragStart();
        }
    }

    /// <summary>
    /// Core acceptance logic, shared between <see cref="OnTriggerEnter"/> and
    /// <see cref="OnTriggerStay"/>. An object is accepted only when it is settled
    /// AND the player is actively dragging it, preventing physics-driven auto-acceptance.
    /// </summary>
    private void TryAcceptObject(Collider other)
    {
        if (!other.CompareTag(ObjectTag)) return;
        if (!other.TryGetComponent(out DraggableObject obj)) return;

        // Still falling — not ready to be placed yet.
        if (!obj.IsSettled) return;

        // Hard gate: only accept objects that are on the Dragging layer.
        // DraggableObject switches to this layer on OnMouseDown and restores
        // the original layer on OnMouseUp / Lock, so a physics-driven overlap
        // from a non-dragged object can never reach this point.
        if (_draggingLayerIndex >= 0 && other.gameObject.layer != _draggingLayerIndex) return;

        // Fallback check (also covers edge cases when the layer was not created).
        if (!obj.IsDragging) return;

        // Both slots already occupied — let the player drag it away.
        if (IsFull) return;

        // Wrong type while locked — track but never store; ReturnToDragStart on release.
        if (_lockedType.HasValue && obj.ObjectType != _lockedType.Value)
        {
            _wrongObjectsInside.Add(obj);
            return;
        }

        // Already in a slot (accepted on a previous tick) — do not double-place.
        if (FindSlotIndex(obj) >= 0) return;

        // Place in the first empty slot (left before right).
        int slotIndex = FirstEmptySlotIndex();
        _slots[slotIndex] = obj;

        // If it was previously tracked as wrong (shouldn't happen, but be safe), remove it.
        _wrongObjectsInside.Remove(obj);

        Debug.Log($"[DropZone] '{obj.ObjectType}' accepted into slot {slotIndex} of '{name}'. " +
                  $"slots[0]={_slots[0]?.name ?? "empty"}  slots[1]={_slots[1]?.name ?? "empty"}");

        // Lock the hole to this object's type on first placement.
        if (!_lockedType.HasValue)
        {
            _lockedType = obj.ObjectType;
            Debug.Log($"[DropZone] '{name}' locked to type '{_lockedType}'.");
        }

        // Snap and freeze the object at its slot anchor so physics cannot displace it.
        obj.Lock(SlotAnchorPosition(slotIndex));

        OnObjectEntered(obj, slotIndex);

        Debug.Log($"[DropZone] IsFull={IsFull}  (slots[0]={_slots[0]?.name ?? "null"}, slots[1]={_slots[1]?.name ?? "null"})");

        // Both slots are now filled — type-lock guarantees a valid pair.
        if (IsFull)
        {
            Debug.Log($"[DropZone] Both slots filled — calling DestroyMatchedPair() for type '{_lockedType}'.");
            DestroyMatchedPair();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(ObjectTag)) return;
        if (!other.TryGetComponent(out DraggableObject obj)) return;

        // A wrong object has left the trigger on its own — no longer needs tracking.
        if (_wrongObjectsInside.Remove(obj))
        {
            Debug.Log($"[DropZone] Wrong object '{obj.name}' exited '{name}' — removed from tracking.");
            return;
        }

        // obj.Lock() teleports the object to the slot anchor position.
        // If that anchor sits outside the trigger collider, Unity fires OnTriggerExit
        // immediately after placement. Ignore the exit for any locked object so it
        // stays in its slot and IsFull can become true.
        if (obj.IsLocked) return;

        int slotIndex = FindSlotIndex(obj);
        if (slotIndex < 0) return;

        _slots[slotIndex] = null;

        obj.Unlock();

        if (IsEmpty)
            _lockedType = null;

        OnObjectExited(obj, slotIndex);
    }

    /// <summary>
    /// Places <paramref name="obj"/> into this hole programmatically, bypassing the
    /// drag/trigger requirements of the normal flow. Intended for tap-to-place logic.
    ///
    /// Applies the same slot rules as the drag path:
    /// empty hole accepts any type; a partially-filled hole only accepts the locked type;
    /// a full hole rejects immediately.
    /// </summary>
    /// <param name="obj">The object to place.</param>
    /// <returns>True when the object was accepted and locked into a slot.</returns>
    public bool TryAutoPlaceObject(DraggableObject obj)
    {
        if (obj == null)
        {
            Debug.LogWarning("[DropZone] TryAutoPlaceObject called with a null object.");
            return false;
        }

        if (!obj.IsSettled)
        {
            Debug.Log($"[DropZone] TryAutoPlaceObject rejected '{obj.name}' — not yet settled.");
            return false;
        }

        if (IsFull)
        {
            Debug.Log($"[DropZone] TryAutoPlaceObject rejected '{obj.name}' — '{name}' is full.");
            return false;
        }

        if (_lockedType.HasValue && obj.ObjectType != _lockedType.Value)
        {
            Debug.Log($"[DropZone] TryAutoPlaceObject rejected '{obj.name}' " +
                      $"(type={obj.ObjectType}) — '{name}' is locked to '{_lockedType}'.");
            return false;
        }

        // Already in a slot — do not double-place.
        if (FindSlotIndex(obj) >= 0)
        {
            Debug.Log($"[DropZone] TryAutoPlaceObject skipped '{obj.name}' — already in a slot.");
            return false;
        }

        int slotIndex = FirstEmptySlotIndex();
        _slots[slotIndex] = obj;

        if (!_lockedType.HasValue)
        {
            _lockedType = obj.ObjectType;
            Debug.Log($"[DropZone] '{name}' locked to type '{_lockedType}' via TryAutoPlaceObject.");
        }

        obj.Lock(SlotAnchorPosition(slotIndex));
        OnObjectEntered(obj, slotIndex);

        Debug.Log($"[DropZone] TryAutoPlaceObject placed '{obj.name}' into slot {slotIndex} of '{name}'.");

        if (IsFull)
        {
            Debug.Log($"[DropZone] Both slots filled via auto-place — calling DestroyMatchedPair().");
            DestroyMatchedPair();
        }

        return true;
    }

    /// <summary>
    /// Returns the world-space position the object should visually fly toward during
    /// a tap auto-move animation. This is a preview only — actual slot assignment is
    /// decided by <see cref="TryAutoPlaceObject"/> after the animation completes.
    /// </summary>
    /// <param name="obj">The object about to be auto-moved.</param>
    public Vector3 GetPreviewAutoSlotPosition(DraggableObject obj)
    {
        if (obj == null)
            return transform.position;

        if (IsEmpty)
            return SlotAnchorPosition(0);

        if (IsFull)
            return transform.position;

        // One slot occupied — always preview the right slot, regardless of type match.
        // A wrong-type object will animate there and then return, which gives clear
        // visual feedback that the zone rejected it.
        return SlotAnchorPosition(1);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the world-space position of the anchor for the given slot index.
    /// Falls back to the zone's own position when an anchor Transform is not assigned.
    /// </summary>
    private Vector3 SlotAnchorPosition(int slotIndex)
    {
        Transform anchor = slotIndex == 0 ? _leftSlotAnchor : _rightSlotAnchor;
        return anchor != null ? anchor.position : transform.position;
    }

    /// <summary>
    /// Returns the index of the first null slot (left before right),
    /// or -1 if all slots are occupied.
    /// </summary>
    private int FirstEmptySlotIndex()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (_slots[i] == null) return i;
        }
        return -1;
    }

    /// <summary>
    /// Returns the slot index containing <paramref name="obj"/>,
    /// or -1 if it is not in any slot.
    /// </summary>
    private int FindSlotIndex(DraggableObject obj)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (_slots[i] == obj) return i;
        }
        return -1;
    }

    /// <summary>
    /// Destroys both slotted objects and resets the hole to an empty state.
    /// The type-lock guarantees both objects share the same ObjectType, so no
    /// additional type check is needed here.
    /// </summary>
    private void DestroyMatchedPair()
    {
        DraggableObject left  = _slots[0];
        DraggableObject right = _slots[1];
        ObjectType      type  = _lockedType.Value;

        Debug.Log($"[DropZone] Matched pair of '{type}' in '{name}' — destroying both.");

        // Clear state before destruction so any callbacks see a clean, empty hole.
        _slots[0]   = null;
        _slots[1]   = null;
        _lockedType = null;

        ExecutePairDestruction(left, right, type);
    }

    // ── Protected hooks ──────────────────────────────────────────────────────

    /// <summary>
    /// Performs the actual destruction of a matched pair.
    /// Override in a subclass to play a match animation before destroying —
    /// state has already been cleared so the hole is logically empty at this point.
    /// </summary>
    /// <param name="left">The object in the left slot.</param>
    /// <param name="right">The object in the right slot.</param>
    /// <param name="objectType">The shared ObjectType of the pair.</param>
    protected virtual void ExecutePairDestruction(DraggableObject left, DraggableObject right, ObjectType objectType)
    {
        _objectSpawner?.OnObjectsDestroyed(left, right);

        Destroy(left.gameObject);
        Destroy(right.gameObject);
        OnMatchedPairDestroyed(objectType);
    }

    /// <summary>
    /// Called when an object is successfully accepted into a slot.
    /// Override to update slot visuals, highlight indicators, etc.
    /// </summary>
    /// <param name="obj">The object that was placed.</param>
    /// <param name="slotIndex">0 = left slot, 1 = right slot.</param>
    protected virtual void OnObjectEntered(DraggableObject obj, int slotIndex)
    {
        Debug.Log($"[DropZone] '{obj.ObjectType}' placed in slot {slotIndex} of '{name}'. " +
                  $"Locked type: {_lockedType}");
    }

    /// <summary>
    /// Called when an object leaves the zone without being part of a matched pair
    /// (e.g. the player dragged it back out).
    /// Override to revert slot visuals.
    /// </summary>
    /// <param name="obj">The object that exited.</param>
    /// <param name="slotIndex">The slot it previously occupied.</param>
    protected virtual void OnObjectExited(DraggableObject obj, int slotIndex)
    {
        Debug.Log($"[DropZone] '{obj.ObjectType}' removed from slot {slotIndex} of '{name}'. " +
                  $"Hole is now {(IsEmpty ? "empty" : "partially filled")}.");
    }

    /// <summary>
    /// Called after a matched pair has been destroyed and the hole has fully reset.
    /// Override to trigger scoring, particle effects, audio, etc.
    /// </summary>
    /// <param name="objectType">The type of the pair that was cleared.</param>
    protected virtual void OnMatchedPairDestroyed(ObjectType objectType)
    {
        Debug.Log($"[DropZone] Pair of '{objectType}' cleared from '{name}' — hole is now empty.");
    }
}
