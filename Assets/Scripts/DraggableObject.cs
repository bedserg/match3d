using System.Collections;
using UnityEngine;

/// <summary>
/// Attach to the root GameObject of an object prefab.
/// Requires a Rigidbody and a Collider on the same GameObject.
/// Handles mouse dragging via MovePosition so dragged objects push other Rigidbody objects.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class DraggableObject : MonoBehaviour
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string LogPrefix     = "[DraggableObject]";

    /// <summary>
    /// Name of the physics layer assigned to an object while it is being dragged.
    /// DropZone only accepts objects on this layer, preventing physics-driven entry.
    /// </summary>
    private const string DraggingLayer = "Dragging";

    // ── Private cached layer indices ─────────────────────────────────────────

    private int _defaultLayer;
    private int _draggingLayer;

    // ── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("Which object type this instance represents. Set in the prefab Inspector.")]
    [SerializeField] private ObjectType _objectType;

    [Header("Drag Feel")]
    [Tooltip("How fast the object follows the cursor. Higher = snappier, lower = floatier.")]
    [SerializeField] private float _followSpeed = 20f;

    [Tooltip("Maximum horizontal speed (m/s) a settled, non-dragged object can reach from a collision impulse. " +
             "Lower values keep nearby objects from flying when something is dragged past them.")]
    [SerializeField] private float _maxIdleSpeed = 1.5f;

    [Tooltip("Duration in seconds for the smooth return animation when a dragged object is rejected.")]
    [SerializeField] private float _returnDuration = 0.25f;

    [Header("Tap Detection")]
    [Tooltip("Maximum finger/cursor movement in pixels for an interaction to count as a tap.")]
    [SerializeField] private float _tapMoveThresholdPixels = 15f;

    [Tooltip("Maximum duration in seconds for an interaction to count as a tap.")]
    [SerializeField] private float _tapMaxDuration = 0.25f;

    [Tooltip("Duration in seconds for the smooth auto-move animation when a tapped object flies to a drop zone.")]
    [SerializeField] private float _autoMoveDuration = 0.25f;

    [Tooltip("Drop zone this object flies to on a tap. Auto-resolved at runtime if left empty.")]
    [SerializeField] private DropZone _dropZone;

    [Header("Drag Bounds")]
    [Tooltip("When enabled, dragging is clamped to the rectangle defined below.")]
    [SerializeField] private bool _useDragBounds = true;

    [Tooltip("Minimum world-space X the object can be dragged to.")]
    [SerializeField] private float _minX = -3.4f;

    [Tooltip("Maximum world-space X the object can be dragged to.")]
    [SerializeField] private float _maxX = 3.4f;

    [Tooltip("Minimum world-space Z the object can be dragged to.")]
    [SerializeField] private float _minZ = -5.9f;

    [Tooltip("Maximum world-space Z the object can be dragged to.")]
    [SerializeField] private float _maxZ = 5.9f;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Which object type this instance represents.</summary>
    public ObjectType ObjectType => _objectType;

    /// <summary>
    /// Marks the object as unsettled so dragging is gated until
    /// <see cref="OnSettled"/> is called by <see cref="ObjectSpawner"/>.
    /// </summary>
    public void MarkUnsettled() => _isSettled = false;

    /// <summary>True while the player is actively dragging this object.</summary>
    public bool IsDragging => _isDragging;

    /// <summary>True while the object is locked inside a slot (kinematic, immovable).</summary>
    public bool IsLocked => _isLocked;

    /// <summary>True once the object has finished falling and is ready to be dragged.</summary>
    public bool IsSettled => _isSettled;

    /// <summary>
    /// World-space position where the player first grabbed this object (recorded on
    /// <see cref="OnMouseDown"/>). Used by <see cref="ReturnToDragStart"/> to
    /// smoothly send rejected objects back to a safe resting spot.
    /// </summary>
    public Vector3 DragStartPosition => _dragStartPosition;

    /// <summary>
    /// Called by <see cref="ObjectSpawner"/> after the fall settle coroutine completes.
    /// Enables dragging for this object.
    /// </summary>
    public void OnSettled() => _isSettled = true;

    /// <summary>
    /// Locks the object at <paramref name="worldPosition"/>, making it kinematic so that
    /// other Rigidbody objects cannot push it out of its slot.
    /// Also cancels any active drag so the player cannot immediately re-grab it.
    /// </summary>
    /// <param name="worldPosition">Exact world-space position to snap the object to.</param>
    public void Lock(Vector3 worldPosition)
    {
        _isDragging         = false;
        _isLocked           = true;
        _rb.isKinematic     = true;
        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.position        = worldPosition;

        // Object is seated — restore the default layer so it no longer triggers
        // the DropZone's dragging-only acceptance gate.
        gameObject.layer = _defaultLayer;
    }

    /// <summary>
    /// Restores dynamic physics so the object can be dragged or pushed again.
    /// Called when an object is removed from a slot (player drag-out or rejection).
    /// </summary>
    public void Unlock()
    {
        _isLocked       = false;
        _rb.isKinematic = false;
    }

    /// <summary>
    /// Cancels any active drag and applies an instant impulse to the Rigidbody.
    /// Used by <see cref="DropZone"/> to physically push a rejected object out of the trigger.
    /// </summary>
    /// <param name="impulse">World-space impulse vector (direction × force).</param>
    public void Bounce(Vector3 impulse)
    {
        _isDragging = false;
        StopMotion();
        _rb.AddForce(impulse, ForceMode.Impulse);
    }

    /// <summary>
    /// Smoothly moves the object back to the position it was at when the player first
    /// clicked it this drag, then re-enables physics.
    /// </summary>
    public void ReturnToDragStart()
    {
        Debug.Log($"{LogPrefix} ReturnToDragStart on '{name}' — target={_dragStartPosition}");
        StopAllCoroutines();
        StartCoroutine(ReturnCoroutine(_dragStartPosition));
    }

    /// <summary>
    /// Smoothly moves the object to <paramref name="targetPosition"/>, then re-enables physics.
    /// Use this for programmatic repositioning (e.g. tap-to-place rejection) where the
    /// destination is known at call time rather than recorded from a drag.
    /// </summary>
    /// <param name="targetPosition">World-space position to animate the object toward.</param>
    public void ReturnToPosition(Vector3 targetPosition)
    {
        Debug.Log($"{LogPrefix} ReturnToPosition on '{name}' — target={targetPosition}");
        StopAllCoroutines();
        StartCoroutine(ReturnCoroutine(targetPosition));
    }

    /// <summary>
    /// Smoothly flies the object to <paramref name="dropZone"/> and attempts to place it there.
    /// If placement succeeds, <see cref="DropZone"/> handles locking and destruction.
    /// If placement fails (zone full, wrong type, etc.), the object animates back to its
    /// current position via <see cref="ReturnToPosition"/>.
    /// No-ops when the object is null, locked, or not yet settled.
    /// </summary>
    /// <param name="dropZone">The drop zone to attempt placement into.</param>
    public void AutoMoveToDropZone(DropZone dropZone)
    {
        if (dropZone == null)   return;
        if (_isLocked)          return;
        if (!_isSettled)        return;

        StopAllCoroutines();
        StartCoroutine(AutoMoveCoroutine(dropZone));
    }

    // ── Private state ────────────────────────────────────────────────────────

    private Rigidbody _rb;
    private Camera    _cam;
    private Collider  _col;
    private bool      _isDragging;
    private bool      _isLocked;
    private bool      _isSettled;
    private bool      _isAutoMoving;

    private Vector3 _dragStartPosition;
    private Plane _dragPlane;
    private Vector3 _grabOffset;
    private Quaternion _dragRotation = Quaternion.identity;

    private Vector2 _mouseDownScreenPos;
    private float   _mouseDownTime;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        _rb  = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();
        _cam = Camera.main;

        _dragPlane = new Plane(Vector3.up, transform.position);

        _defaultLayer  = gameObject.layer;
        _draggingLayer = LayerMask.NameToLayer(DraggingLayer);

        if (_draggingLayer < 0)
            Debug.LogError($"{LogPrefix} Layer '{DraggingLayer}' not found. " +
                           "Add it in Project Settings > Tags & Layers.", this);

        if (_dropZone == null)
            _dropZone = FindFirstObjectByType<DropZone>();

        if (_dropZone == null)
            Debug.LogWarning($"{LogPrefix} No DropZone found in the scene. " +
                             "Tap-to-place will not work on '{name}'.", this);

        // Objects placed directly in the scene (not via ObjectSpawner) are considered
        // already settled so they can be dragged immediately.
        _isSettled = true;

        Debug.Log($"{LogPrefix} Awake on '{name}' — type={_objectType}, " +
                  $"collider={_col.GetType().Name} isTrigger={_col.isTrigger} enabled={_col.enabled}, " +
                  $"rb.isKinematic={_rb.isKinematic}, " +
                  $"camera={(_cam == null ? "NULL — OnMouseDown will not fire!" : _cam.name)}");
    }

    private void OnMouseDown()
    {
        if (_isLocked)   return;
        if (!_isSettled) return;

        Debug.Log($"{LogPrefix} OnMouseDown fired on '{name}' (type={_objectType})");

        _dragStartPosition = transform.position;
        _dragPlane         = new Plane(Vector3.up, transform.position);
        _isDragging = true;
        _dragRotation = Quaternion.Euler(0f, 0f, 0f);
        _rb.MoveRotation(_dragRotation);
        transform.rotation = _dragRotation;

        // Record tap-detection state.
        _mouseDownScreenPos = Input.mousePosition;
        _mouseDownTime      = Time.unscaledTime;

        // Switch to the Dragging layer so DropZone can distinguish intentional drags
        // from physics-driven overlaps.
        if (_draggingLayer >= 0)
            gameObject.layer = _draggingLayer;

        Vector3 hitPoint = ScreenToPlane(Input.mousePosition);
        _grabOffset       = transform.position - hitPoint;
        _grabOffset.y     = 0f;

        StopMotion();
        Debug.Log($"{LogPrefix} Drag started — grabOffset={_grabOffset}");
    }

    private void OnMouseUp()
    {
        Debug.Log($"{LogPrefix} OnMouseUp on '{name}' — was dragging={_isDragging}");

        float movedPixels = Vector2.Distance(Input.mousePosition, _mouseDownScreenPos);
        float heldSeconds = Time.unscaledTime - _mouseDownTime;
        bool  isTap       = movedPixels <= _tapMoveThresholdPixels && heldSeconds <= _tapMaxDuration;

        _isDragging = false;

        if (isTap)
        {
            Debug.Log($"{LogPrefix} Tap detected on object — auto-moving to drop zone.");

            _isDragging      = false;
            gameObject.layer = _defaultLayer;
            StopMotion();
            AutoMoveToDropZone(_dropZone);
            return;
        }

        // Drag release — keep existing behavior.
        gameObject.layer = _defaultLayer;
        StopMotion();
    }

    private void OnMouseDrag()
    {
        if (!_isDragging)
            Debug.LogWarning($"{LogPrefix} OnMouseDrag fired but _isDragging is false on '{name}'");
    }

    private void FixedUpdate()
    {
        if (_isAutoMoving) return;

        if (_isDragging)
        {
            Vector3 cursorWorld = ScreenToPlane(Input.mousePosition);
            if (cursorWorld == Vector3.zero)
            {
                Debug.LogWarning($"{LogPrefix} ScreenToPlane returned zero — ray missed the drag plane.");
                return;
            }

            Vector3 target = cursorWorld + _grabOffset;
            target.y = _rb.position.y;

            if (_useDragBounds)
            {
                target.x = Mathf.Clamp(target.x, _minX, _maxX);
                target.z = Mathf.Clamp(target.z, _minZ, _maxZ);
            }

            Vector3 next = Vector3.Lerp(_rb.position, target, _followSpeed * Time.fixedDeltaTime);
            _rb.MovePosition(next);
            _rb.MoveRotation(_dragRotation);
        }
        else if (_isSettled && !_isLocked)
        {
            // Clamp horizontal velocity so collision impulses from a nearby dragged
            // object cannot propel this one far across the board.
            Vector3 vel = _rb.linearVelocity;
            float   sqr = vel.x * vel.x + vel.z * vel.z;
            if (sqr > _maxIdleSpeed * _maxIdleSpeed)
            {
                float scale    = _maxIdleSpeed / Mathf.Sqrt(sqr);
                vel.x         *= scale;
                vel.z         *= scale;
                _rb.linearVelocity = vel;
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private IEnumerator AutoMoveCoroutine(DropZone dropZone)
    {
        Vector3 originalPosition = _rb.position;

        _isDragging    = false;
        _isAutoMoving  = true;
        _rb.isKinematic = true;
        StopMotion();

        Debug.Log($"{LogPrefix} AutoMove started on '{name}' — target zone='{dropZone.name}'");

        Vector3 start       = _rb.position;
        Vector3 destination = dropZone.transform.position;
        float   elapsed     = 0f;

        while (elapsed < _autoMoveDuration)
        {
            elapsed += Time.deltaTime;
            float t      = Mathf.Clamp01(elapsed / _autoMoveDuration);
            float smooth = t * t * (3f - 2f * t);
            _rb.MovePosition(Vector3.Lerp(start, destination, smooth));
            yield return null;
        }

        _rb.MovePosition(destination);

        bool placed = dropZone.TryAutoPlaceObject(this);
        Debug.Log($"{LogPrefix} AutoMove — TryAutoPlaceObject returned {placed} on '{name}'");

        if (!placed)
        {
            // DropZone rejected the object — undo kinematic so ReturnToPosition can animate.
            _rb.isKinematic = false;
            ReturnToPosition(originalPosition);
        }
        // On success, DropZone.Lock() has already made the Rigidbody kinematic and
        // positioned the object at the slot anchor; nothing more to do here.

        _isAutoMoving = false;
    }

    private IEnumerator ReturnCoroutine(Vector3 target)
    {
        _rb.isKinematic = true;
        _isDragging     = false;

        Vector3 start   = _rb.position;
        float   elapsed = 0f;

        while (elapsed < _returnDuration)
        {
            elapsed += Time.deltaTime;
            float t      = Mathf.Clamp01(elapsed / _returnDuration);
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
        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!_useDragBounds) return;

        float centerX = (_minX + _maxX) * 0.5f;
        float centerZ = (_minZ + _maxZ) * 0.5f;
        float sizeX   = _maxX - _minX;
        float sizeZ   = _maxZ - _minZ;
        float gizmoY  = transform.position.y;

        UnityEditor.Handles.color = new Color(0f, 1f, 0.4f, 0.9f);
        UnityEditor.Handles.DrawWireCube(
            new Vector3(centerX, gizmoY, centerZ),
            new Vector3(sizeX,   0f,     sizeZ));
    }
#endif
}
