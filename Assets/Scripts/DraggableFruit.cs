using System.Collections;
using UnityEngine;

/// <summary>
/// Attach to the root GameObject of the fruit prefab.
/// Requires a Rigidbody and a Collider on the same GameObject.
/// Handles mouse dragging via MovePosition so dragged fruits push other Rigidbody fruits.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class DraggableFruit : MonoBehaviour
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string LogPrefix = "[DraggableFruit]";

    /// <summary>Duration of the smooth return-to-start animation in seconds.</summary>
    private const float ReturnDuration = 0.25f;

    /// <summary>
    /// Maximum horizontal speed (m/s) for a settled, non-dragged fruit.
    /// Prevents collision impulses from a dragged neighbour launching this fruit far.
    /// </summary>
    private const float MaxIdleSpeed = 1.5f;

    /// <summary>
    /// Name of the physics layer assigned to a fruit while it is being dragged.
    /// DropZone only accepts objects on this layer, preventing physics-driven entry.
    /// </summary>
    private const string DraggingLayer = "Dragging";

    // ── Private cached layer indices ─────────────────────────────────────────

    private int _defaultLayer;
    private int _draggingLayer;

    // ── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("Which fruit this object represents.")]
    [SerializeField] private FruitType _fruitType;

    [Tooltip("How fast the fruit follows the cursor. Higher = snappier.")]
    [SerializeField] private float _followSpeed = 20f;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Which fruit type this instance represents.</summary>
    public FruitType FruitType => _fruitType;

    /// <summary>Assigns the fruit type at runtime (called by <see cref="FruitSpawner"/>).</summary>
    public void SetFruitType(FruitType type)
    {
        _fruitType = type;
        // Mark as unsettled — FruitSpawner will call OnSettled() once the fall completes.
        _isSettled = false;
    }

    /// <summary>True while the player is actively dragging this fruit.</summary>
    public bool IsDragging => _isDragging;

    /// <summary>True while the fruit is locked inside a slot (kinematic, immovable).</summary>
    public bool IsLocked => _isLocked;

    /// <summary>True once the fruit has finished falling and is ready to be dragged.</summary>
    public bool IsSettled => _isSettled;

    /// <summary>
    /// World-space position where the player first grabbed this fruit (recorded on
    /// <see cref="OnMouseDown"/>). Used by <see cref="ReturnToDragStart"/> to
    /// smoothly send rejected fruits back to a safe resting spot.
    /// </summary>
    public Vector3 DragStartPosition => _dragStartPosition;

    /// <summary>
    /// Called by <see cref="FruitSpawner"/> after the fall settle coroutine completes.
    /// Enables dragging for this fruit.
    /// </summary>
    public void OnSettled() => _isSettled = true;

    /// <summary>
    /// Locks the fruit at <paramref name="worldPosition"/>, making it kinematic so that
    /// other Rigidbody fruits cannot push it out of its slot.
    /// Also cancels any active drag so the player cannot immediately re-grab it.
    /// </summary>
    /// <param name="worldPosition">Exact world-space position to snap the fruit to.</param>
    public void Lock(Vector3 worldPosition)
    {
        _isDragging        = false;
        _isLocked          = true;
        _rb.isKinematic    = true;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.position       = worldPosition;

        // Fruit is seated — restore the default layer so it no longer triggers
        // the DropZone's dragging-only acceptance gate.
        gameObject.layer = _defaultLayer;
    }

    /// <summary>
    /// Restores dynamic physics so the fruit can be dragged or pushed again.
    /// Called when a fruit is removed from a slot (player drag-out or rejection).
    /// </summary>
    public void Unlock()
    {
        _isLocked       = false;
        _rb.isKinematic = false;
    }

    /// <summary>
    /// Cancels any active drag and applies an instant impulse to the Rigidbody.
    /// Used by <see cref="DropZone"/> to physically push a rejected fruit out of the trigger.
    /// Stopping the drag first ensures <see cref="FixedUpdate"/> won't override the impulse
    /// with <c>MovePosition</c> on the same frame.
    /// </summary>
    /// <param name="impulse">World-space impulse vector (direction × force).</param>
    public void Bounce(Vector3 impulse)
    {
        _isDragging = false;
        StopMotion();
        _rb.AddForce(impulse, ForceMode.Impulse);
    }

    /// <summary>
    /// Smoothly moves the fruit back to the position it was at when the player first
    /// clicked it this drag, then re-enables physics. Call this to reject a fruit that
    /// was released inside a locked <see cref="DropZone"/> with a non-matching type.
    /// </summary>
    public void ReturnToDragStart()
    {
        Debug.Log($"{LogPrefix} ReturnToDragStart on '{name}' — target={_dragStartPosition}");
        StopAllCoroutines();
        StartCoroutine(ReturnCoroutine(_dragStartPosition));
    }

    // ── Private state ────────────────────────────────────────────────────────

    private Rigidbody _rb;
    private Camera    _cam;
    private Collider  _col;
    private bool      _isDragging;
    private bool      _isLocked;    // true while fruit is kinematic inside a slot
    private bool      _isSettled;   // false until FruitSpawner settle coroutine completes

    // World-space position at the moment the player pressed the mouse button.
    // ReturnToDragStart() uses this as the return target.
    private Vector3 _dragStartPosition;

    // Plane at the fruit's Y level used to project the screen cursor to world space.
    private Plane   _dragPlane;

    // Offset between the fruit centre and the grab point so it does not snap to the cursor.
    private Vector3 _grabOffset;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        _rb  = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();
        _cam = Camera.main;

        // Safe default so _dragPlane is never uninitialized.
        _dragPlane = new Plane(Vector3.up, transform.position);

        _defaultLayer  = gameObject.layer;
        _draggingLayer = LayerMask.NameToLayer(DraggingLayer);

        if (_draggingLayer < 0)
            Debug.LogError($"{LogPrefix} Layer '{DraggingLayer}' not found. " +
                           "Add it in Project Settings > Tags & Layers.", this);

        // Fruits placed directly in the scene (not via FruitSpawner) are considered
        // already settled so they can be dragged immediately.
        _isSettled = true;

        Debug.Log($"{LogPrefix} Awake on '{name}' — type={_fruitType}, " +
                  $"collider={_col.GetType().Name} isTrigger={_col.isTrigger} enabled={_col.enabled}, " +
                  $"rb.isKinematic={_rb.isKinematic}, " +
                  $"camera={(_cam == null ? "NULL — OnMouseDown will not fire!" : _cam.name)}");
    }

    private void OnMouseDown()
    {
        // A locked fruit is seated inside a slot — do not allow picking it up.
        if (_isLocked) return;

        // Still falling — not ready to be dragged yet.
        if (!_isSettled) return;

        Debug.Log($"{LogPrefix} OnMouseDown fired on '{name}' (type={_fruitType})");

        // Record position before any movement so ReturnToDragStart() has a safe target.
        _dragStartPosition = transform.position;

        _dragPlane  = new Plane(Vector3.up, transform.position);
        _isDragging = true;

        // Switch to the Dragging layer so DropZone can distinguish intentional drags
        // from physics-driven overlaps.
        if (_draggingLayer >= 0)
            gameObject.layer = _draggingLayer;

        Vector3 hitPoint = ScreenToPlane(Input.mousePosition);
        Debug.Log($"{LogPrefix} Grab hit point={hitPoint}, fruit pos={transform.position}");

        _grabOffset   = transform.position - hitPoint;
        _grabOffset.y = 0f;

        StopMotion();
        Debug.Log($"{LogPrefix} Drag started — grabOffset={_grabOffset}");
    }

    private void OnMouseUp()
    {
        Debug.Log($"{LogPrefix} OnMouseUp on '{name}' — was dragging={_isDragging}");
        _isDragging = false;

        // Restore the original layer so the fruit is no longer treated as "being dragged".
        gameObject.layer = _defaultLayer;

        StopMotion();
    }

    private void OnMouseDrag()
    {
        // Movement is handled in FixedUpdate for physics accuracy.
        // OnMouseDrag here just confirms Unity receives continuous input.
        if (!_isDragging)
            Debug.LogWarning($"{LogPrefix} OnMouseDrag fired but _isDragging is false on '{name}'");
    }

    private void FixedUpdate()
    {
        if (_isDragging)
        {
            Vector3 cursorWorld = ScreenToPlane(Input.mousePosition);
            if (cursorWorld == Vector3.zero)
            {
                Debug.LogWarning($"{LogPrefix} ScreenToPlane returned zero — ray missed the drag plane.");
                return;
            }

            Vector3 target = cursorWorld + _grabOffset;
            target.y = _rb.position.y; // keep the fruit on its original Y level

            Vector3 next = Vector3.Lerp(_rb.position, target, _followSpeed * Time.fixedDeltaTime);
            _rb.MovePosition(next);
        }
        else if (_isSettled && !_isLocked)
        {
            // Clamp horizontal velocity to prevent a collision impulse from a nearby
            // dragged fruit from sending this one flying off the board.
            Vector3 vel = _rb.linearVelocity;
            float   sqr = vel.x * vel.x + vel.z * vel.z;
            if (sqr > MaxIdleSpeed * MaxIdleSpeed)
            {
                float scale    = MaxIdleSpeed / Mathf.Sqrt(sqr);
                vel.x         *= scale;
                vel.z         *= scale;
                _rb.linearVelocity = vel;
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Smoothly lerps the fruit from its current position to <paramref name="target"/>
    /// over <see cref="ReturnDuration"/> seconds using kinematic MovePosition, then
    /// restores dynamic physics.
    /// </summary>
    private System.Collections.IEnumerator ReturnCoroutine(Vector3 target)
    {
        // Go kinematic during the animation so physics does not fight the movement.
        _rb.isKinematic = true;
        _isDragging     = false;

        Vector3 start   = _rb.position;
        float   elapsed = 0f;

        while (elapsed < ReturnDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.Clamp01(elapsed / ReturnDuration);
            // Smoothstep easing for a more natural feel.
            float smooth = t * t * (3f - 2f * t);
            _rb.MovePosition(Vector3.Lerp(start, target, smooth));
            yield return null;
        }

        _rb.MovePosition(target);
        _rb.isKinematic = false;
        StopMotion();

        Debug.Log($"{LogPrefix} ReturnCoroutine complete on '{name}' — now at {target}");
    }

    /// <summary>Projects a screen-space position onto the drag plane and returns the world-space hit point.</summary>
    private Vector3 ScreenToPlane(Vector3 screenPos)
    {
        Ray ray = _cam.ScreenPointToRay(screenPos);
        if (_dragPlane.Raycast(ray, out float distance))
            return ray.GetPoint(distance);

        Debug.LogWarning($"{LogPrefix} Plane raycast missed. " +
                         $"Ray origin={ray.origin} dir={ray.direction}, " +
                         $"Plane normal={_dragPlane.normal} dist={_dragPlane.distance}");
        return Vector3.zero;
    }

    /// <summary>Zeroes out all velocity to prevent drifting on pick-up or release.</summary>
    private void StopMotion()
    {
#if UNITY_6000_0_OR_NEWER
        _rb.linearVelocity  = Vector3.zero;
#else
        _rb.velocity        = Vector3.zero;
#endif
        _rb.angularVelocity = Vector3.zero;
    }
}
