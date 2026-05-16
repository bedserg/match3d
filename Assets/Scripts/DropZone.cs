using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Represents a hole with two logical slots (left = index 0, right = index 1).
///
/// Empty-hole rule   : An empty hole accepts any FruitType.
/// Type-locking rule : Once the first fruit is placed, the hole locks to that
///                     FruitType and rejects all others until it is cleared.
/// Two-slot rule     : The hole holds at most 2 fruits. A third fruit is always
///                     rejected regardless of type.
/// Match rule        : When both slots are filled (always a matching pair thanks
///                     to type-locking), both fruits are destroyed and the hole
///                     resets to the empty state.
/// Wrong-fruit rule  : A fruit of the wrong type may physically enter the trigger
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

    [Tooltip("Reference to the FruitSpawner so it can be notified when a pair is destroyed.")]
    [SerializeField] private FruitSpawner _fruitSpawner;

    // ── Constants ────────────────────────────────────────────────────────────

    private const string FruitTag  = "Fruit";
    private const int    SlotCount = 2;

    // ── Private state ────────────────────────────────────────────────────────

    /// <summary>
    /// Fixed-size slot array. Index 0 = left slot, index 1 = right slot.
    /// A null entry means the slot is empty.
    /// </summary>
    private readonly DraggableFruit[] _slots = new DraggableFruit[SlotCount];

    /// <summary>
    /// The FruitType this hole is locked to, or null when the hole is empty
    /// and therefore accepts any fruit.
    /// </summary>
    private FruitType? _lockedType;

    /// <summary>
    /// Fruits currently overlapping the trigger that are of the wrong type while
    /// the zone is locked. Tracked so we can detect when the player releases them
    /// inside the zone and return them to their drag-start position.
    /// </summary>
    private readonly HashSet<DraggableFruit> _wrongFruitsInside = new HashSet<DraggableFruit>();

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>True when both slots are empty.</summary>
    public bool IsEmpty => _slots[0] == null && _slots[1] == null;

    /// <summary>True when both slots are occupied.</summary>
    public bool IsFull  => _slots[0] != null && _slots[1] != null;

    /// <summary>The locked FruitType, or null when the hole is empty.</summary>
    public FruitType? LockedType => _lockedType;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        // Enforce trigger mode so the hole collider does not block fruits.
        Collider col = GetComponent<Collider>();
        if (!col.isTrigger)
        {
            col.isTrigger = true;
            Debug.LogWarning($"[DropZone] Collider on '{name}' was not a trigger — fixed automatically.", this);
        }

        // Warn early if anchors are not assigned so the designer catches it in Edit Mode.
        if (_leftSlotAnchor == null || _rightSlotAnchor == null)
            Debug.LogWarning($"[DropZone] '{name}' is missing one or both slot anchors. " +
                             "Assign LeftSlotAnchor and RightSlotAnchor in the Inspector.", this);

        // Auto-resolve the spawner if not wired in the Inspector.
        if (_fruitSpawner == null)
            _fruitSpawner = FindFirstObjectByType<FruitSpawner>();
    }

    private void OnTriggerEnter(Collider other)
    {
        TryAcceptFruit(other);
    }

    private void OnTriggerStay(Collider other)
    {
        // A fruit may have settled while already overlapping the trigger (e.g. it fell
        // through, bounced, and came to rest inside the zone before the player drags it).
        // Re-evaluate on every stay tick so it gets accepted once the player drags it in.
        TryAcceptFruit(other);

        // ── Wrong-fruit release detection ──────────────────────────────────────
        // If a wrong fruit (tracked in _wrongFruitsInside) was just released by the
        // player while still overlapping this trigger, return it to its drag-start pos.
        if (!other.CompareTag(FruitTag)) return;
        if (!other.TryGetComponent(out DraggableFruit wrongFruit)) return;
        if (!_wrongFruitsInside.Contains(wrongFruit)) return;

        if (!wrongFruit.IsDragging)
        {
            Debug.Log($"[DropZone] Wrong fruit '{wrongFruit.name}' released inside '{name}' — returning to drag start.");
            _wrongFruitsInside.Remove(wrongFruit);
            wrongFruit.ReturnToDragStart();
        }
    }

    /// <summary>
    /// Core acceptance logic, shared between <see cref="OnTriggerEnter"/> and
    /// <see cref="OnTriggerStay"/>. A fruit is accepted only when it is settled
    /// AND the player is actively dragging it, preventing physics-driven auto-acceptance.
    /// </summary>
    private void TryAcceptFruit(Collider other)
    {
        if (!other.CompareTag(FruitTag)) return;
        if (!other.TryGetComponent(out DraggableFruit fruit)) return;

        // ── Ignore fruits that are still falling — not settled yet.
        if (!fruit.IsSettled) return;

        // ── Ignore fruits the player is not actively dragging.
        // This prevents physics collisions or rolling from auto-accepting a fruit.
        if (!fruit.IsDragging) return;

        // ── Ignore: both slots already occupied — let the player drag it away.
        if (IsFull) return;

        // ── Wrong type while locked — track but never store; ReturnToDragStart on release.
        if (_lockedType.HasValue && fruit.FruitType != _lockedType.Value)
        {
            _wrongFruitsInside.Add(fruit);
            return;
        }

        // ── Already in a slot (accepted on a previous tick) — do not double-place.
        if (FindSlotIndex(fruit) >= 0) return;

        // ── Place fruit in the first empty slot (left before right).
        int slotIndex = FirstEmptySlotIndex();
        _slots[slotIndex] = fruit;

        // ── If it was previously tracked as wrong (shouldn't happen, but be safe), remove it.
        _wrongFruitsInside.Remove(fruit);

        Debug.Log($"[DropZone] '{fruit.FruitType}' accepted into slot {slotIndex} of '{name}'. " +
                  $"slots[0]={_slots[0]?.name ?? "empty"}  slots[1]={_slots[1]?.name ?? "empty"}");

        // ── Lock the hole to this fruit's type on first placement.
        if (!_lockedType.HasValue)
        {
            _lockedType = fruit.FruitType;
            Debug.Log($"[DropZone] '{name}' locked to type '{_lockedType}'.");
        }

        // ── Snap and freeze the fruit at its slot anchor so physics cannot displace it.
        fruit.Lock(SlotAnchorPosition(slotIndex));

        OnFruitEntered(fruit, slotIndex);

        Debug.Log($"[DropZone] IsFull={IsFull}  (slots[0]={_slots[0]?.name ?? "null"}, slots[1]={_slots[1]?.name ?? "null"})");

        // ── Both slots are now filled — type-lock guarantees a valid pair.
        if (IsFull)
        {
            Debug.Log($"[DropZone] Both slots filled — calling DestroyMatchedPair() for type '{_lockedType}'.");
            DestroyMatchedPair();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(FruitTag)) return;
        if (!other.TryGetComponent(out DraggableFruit fruit)) return;

        // ── A wrong fruit has left the trigger on its own — no longer needs tracking.
        if (_wrongFruitsInside.Remove(fruit))
        {
            Debug.Log($"[DropZone] Wrong fruit '{fruit.name}' exited '{name}' — removed from tracking.");
            return;
        }

        // fruit.Lock() teleports the fruit to the slot anchor position.
        // If that anchor sits outside the trigger collider, Unity fires OnTriggerExit
        // immediately after placement. Ignore the exit for any locked fruit so it
        // stays in its slot and IsFull can become true.
        if (fruit.IsLocked) return;

        // Locate which slot held this fruit (it may not be in a slot if ignored).
        int slotIndex = FindSlotIndex(fruit);
        if (slotIndex < 0) return;

        // Free the slot.
        _slots[slotIndex] = null;

        // Restore dynamic physics so the fruit can be dragged or pushed again.
        fruit.Unlock();

        // Release the type-lock when the last fruit leaves.
        if (IsEmpty)
            _lockedType = null;

        OnFruitExited(fruit, slotIndex);
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
    /// Returns the slot index containing <paramref name="fruit"/>,
    /// or -1 if it is not in any slot.
    /// </summary>
    private int FindSlotIndex(DraggableFruit fruit)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (_slots[i] == fruit) return i;
        }
        return -1;
    }

    /// <summary>
    /// Destroys both slotted fruits and resets the hole to an empty state.
    /// The type-lock guarantees both fruits share the same FruitType, so no
    /// additional type check is needed here.
    /// </summary>
    private void DestroyMatchedPair()
    {
        DraggableFruit left  = _slots[0];
        DraggableFruit right = _slots[1];
        FruitType      type  = _lockedType.Value; // always set when a fruit is in a slot

        Debug.Log($"[DropZone] Matched pair of '{type}' in '{name}' — destroying both.");

        // Clear state before destruction so any callbacks see a clean, empty hole.
        _slots[0]   = null;
        _slots[1]   = null;
        _lockedType = null;

        // Delegate the actual Destroy calls to a virtual method so subclasses
        // can intercept here and play a match animation before destroying.
        ExecutePairDestruction(left, right, type);
    }

    // ── Protected hooks ──────────────────────────────────────────────────────

    /// <summary>
    /// Performs the actual destruction of a matched pair.
    /// Override in a subclass to play a match animation before destroying —
    /// state has already been cleared so the hole is logically empty at this point.
    /// Call <c>Destroy</c> on both GameObjects and <see cref="OnMatchedPairDestroyed"/>
    /// at the end of your animation.
    /// </summary>
    /// <param name="left">The fruit in the left slot.</param>
    /// <param name="right">The fruit in the right slot.</param>
    /// <param name="fruitType">The shared FruitType of the pair.</param>
    protected virtual void ExecutePairDestruction(DraggableFruit left, DraggableFruit right, FruitType fruitType)
    {
        // Notify the spawner before destroying so it can remove the references while they are still valid.
        _fruitSpawner?.OnFruitsDestroyed(left, right);

        Destroy(left.gameObject);
        Destroy(right.gameObject);
        OnMatchedPairDestroyed(fruitType);
    }

    /// <summary>
    /// Called when a fruit is successfully accepted into a slot.
    /// Override to update slot visuals, highlight indicators, etc.
    /// </summary>
    /// <param name="fruit">The fruit that was placed.</param>
    /// <param name="slotIndex">0 = left slot, 1 = right slot.</param>
    protected virtual void OnFruitEntered(DraggableFruit fruit, int slotIndex)
    {
        Debug.Log($"[DropZone] '{fruit.FruitType}' placed in slot {slotIndex} of '{name}'. " +
                  $"Locked type: {_lockedType}");
    }

    /// <summary>
    /// Called when a fruit leaves the zone without being part of a matched pair
    /// (e.g. the player dragged it back out).
    /// Override to revert slot visuals.
    /// </summary>
    /// <param name="fruit">The fruit that exited.</param>
    /// <param name="slotIndex">The slot it previously occupied.</param>
    protected virtual void OnFruitExited(DraggableFruit fruit, int slotIndex)
    {
        Debug.Log($"[DropZone] '{fruit.FruitType}' removed from slot {slotIndex} of '{name}'. " +
                  $"Hole is now {(IsEmpty ? "empty" : "partially filled")}.");
    }

    /// <summary>
    /// Called after a matched pair has been destroyed and the hole has fully reset.
    /// Override to trigger scoring, particle effects, audio, etc.
    /// </summary>
    /// <param name="fruitType">The type of the pair that was cleared.</param>
    protected virtual void OnMatchedPairDestroyed(FruitType fruitType)
    {
        Debug.Log($"[DropZone] Pair of '{fruitType}' cleared from '{name}' — hole is now empty.");
    }
}
