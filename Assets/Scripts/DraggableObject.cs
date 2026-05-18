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
    private const float  ReturnDuration = 0.25f;

    // ── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("Which object type this instance represents. Set in the prefab Inspector.")]
    [SerializeField] private ObjectType _objectType;

    [Tooltip("How fast the object follows the cursor. Higher = snappier.")]
    [SerializeField] private float _followSpeed = 20f;

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

    // ── Private state ────────────────────────────────────────────────────────

    private Rigidbody _rb;
    private Camera    _cam;
    private Collider  _col;
    private bool      _isDragging;
    private bool      _isLocked;
    private bool      _isSettled;

    private Vector3 _dragStartPosition;
    private Plane   _dragPlane;
    private Vector3 _grabOffset;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        _rb  = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();
        _cam = Camera.main;

        _dragPlane = new Plane(Vector3.up, transform.position);

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
        _isDragging        = true;

        Vector3 hitPoint = ScreenToPlane(Input.mousePosition);
        _grabOffset       = transform.position - hitPoint;
        _grabOffset.y     = 0f;

        StopMotion();
        Debug.Log($"{LogPrefix} Drag started — grabOffset={_grabOffset}");
    }

    private void OnMouseUp()
    {
        Debug.Log($"{LogPrefix} OnMouseUp on '{name}' — was dragging={_isDragging}");
        _isDragging = false;
        StopMotion();
    }

    private void OnMouseDrag()
    {
        if (!_isDragging)
            Debug.LogWarning($"{LogPrefix} OnMouseDrag fired but _isDragging is false on '{name}'");
    }

    private void FixedUpdate()
    {
        if (!_isDragging) return;

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
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private IEnumerator ReturnCoroutine(Vector3 target)
    {
        _rb.isKinematic = true;
        _isDragging     = false;

        Vector3 start   = _rb.position;
        float   elapsed = 0f;

        while (elapsed < ReturnDuration)
        {
            elapsed += Time.deltaTime;
            float t      = Mathf.Clamp01(elapsed / ReturnDuration);
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
