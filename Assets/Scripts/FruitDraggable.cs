using UnityEngine;

/// <summary>
/// Attach to a fruit parent GameObject that has a Rigidbody and a Collider.
/// Handles mouse and single-touch dragging by moving the Rigidbody along the
/// XZ play plane, allowing full physics interaction with other fruits.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class FruitDraggable : MonoBehaviour
{
    // ── Inspector ───────────────────────────────────────────────────────────

    [Tooltip("How fast the fruit follows the pointer. Higher = snappier.")]
    [SerializeField] private float followSpeed = 20f;

    // ── Private state ───────────────────────────────────────────────────────

    private Rigidbody   _rb;
    private Camera      _cam;
    private bool        _isDragging;

    /// World-space offset between the fruit's position and the initial click
    /// point, so the fruit doesn't snap its centre to the cursor.
    private Vector3     _grabOffset;

    /// Horizontal plane at the fruit's Y height used for accurate world-space
    /// cursor projection from an orthographic top-down camera.
    private Plane       _dragPlane;

    // ── Constants ───────────────────────────────────────────────────────────

    private const int   MouseButton = 0;

    // ── Unity lifecycle ─────────────────────────────────────────────────────

    private void Awake()
    {
        _rb  = GetComponent<Rigidbody>();
        _cam = Camera.main;
    }

    private void OnMouseDown()
    {
        BeginDrag(Input.mousePosition);
    }

    private void OnMouseUp()
    {
        EndDrag();
    }

    private void Update()
    {
        // Single-touch fallback (works on mobile alongside OnMouseDown on PC)
        if (Input.touchCount == 1)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
                BeginDrag(touch.position);
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                EndDrag();
        }
    }

    private void FixedUpdate()
    {
        if (!_isDragging) return;

        Vector3 cursorWorld = GetCursorWorldPosition(GetCurrentScreenPosition());
        if (cursorWorld == Vector3.zero) return;

        // Target is cursor world position adjusted by the initial grab offset,
        // clamped back to the fruit's locked Y so it never leaves the plane.
        Vector3 target = cursorWorld + _grabOffset;
        target.y = _rb.position.y;

        // MovePosition keeps the Rigidbody in the physics simulation so it
        // can push other fruits during drag.
        Vector3 newPos = Vector3.Lerp(_rb.position, target, followSpeed * Time.fixedDeltaTime);
        _rb.MovePosition(newPos);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private void BeginDrag(Vector3 screenPos)
    {
        // Plane sits at the fruit's current world Y
        _dragPlane   = new Plane(Vector3.up, transform.position);
        _isDragging  = true;

        Vector3 hitPoint = GetCursorWorldPosition(screenPos);
        // Offset so the fruit doesn't jump to have its centre under the cursor
        _grabOffset  = transform.position - hitPoint;
        _grabOffset.y = 0f;

        // Kill any residual velocity so the fruit doesn't drift on pick-up
        _rb.linearVelocity        = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }

    private void EndDrag()
    {
        _isDragging = false;
        // Kill velocity so the fruit stops cleanly rather than continuing
        // to glide after the player releases.
        _rb.linearVelocity        = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// Projects a screen-space position onto <see cref="_dragPlane"/> and
    /// returns the world-space intersection point.
    /// Returns <see cref="Vector3.zero"/> if the ray misses the plane.
    /// </summary>
    private Vector3 GetCursorWorldPosition(Vector3 screenPos)
    {
        Ray ray = _cam.ScreenPointToRay(screenPos);
        if (_dragPlane.Raycast(ray, out float distance))
            return ray.GetPoint(distance);

        return Vector3.zero;
    }

    /// <summary>
    /// Returns the current pointer screen position, preferring touch over mouse.
    /// </summary>
    private Vector3 GetCurrentScreenPosition()
    {
        if (Input.touchCount > 0)
            return Input.GetTouch(0).position;

        return Input.mousePosition;
    }
}
