using System.Collections;
using UnityEngine;

/// <summary>
/// Attach to the root GameObject of an object prefab.
/// Requires a Rigidbody and a Collider on the same GameObject.
/// On click/tap the object automatically flies to the assigned drop zone.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class DraggableObject : MonoBehaviour
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string LogPrefix = "[DraggableObject]";

    // ── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("Which object type this instance represents. Set in the prefab Inspector.")]
    [SerializeField] private ObjectType _objectType;

    [Tooltip("Maximum horizontal speed (m/s) a settled, non-moving object can reach from a collision impulse. " +
             "Lower values keep nearby objects from flying when something moves past them.")]
    [SerializeField] private float _maxIdleSpeed = 1.5f;

    [Tooltip("Duration in seconds for the smooth return animation when a rejected object snaps back.")]
    [SerializeField] private float _returnDuration = 0.25f;

    [Tooltip("Duration in seconds for the smooth auto-move animation when a tapped object flies to a drop zone.")]
    [SerializeField] private float _autoMoveDuration = 0.25f;

    [Tooltip("The tray this object flies into on a tap. Auto-resolved at runtime if left empty.")]
    [SerializeField] private TrayController _tray;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Which object type this instance represents.</summary>
    public ObjectType ObjectType => _objectType;

    /// <summary>
    /// Marks the object as unsettled so clicks are ignored until
    /// <see cref="OnSettled"/> is called by <see cref="ObjectSpawner"/>.
    /// </summary>
    public void MarkUnsettled() => _isSettled = false;

    /// <summary>True while the object is locked inside a slot (kinematic, immovable).</summary>
    public bool IsLocked => _isLocked;

    /// <summary>True once the object has finished falling and is ready to be clicked.</summary>
    public bool IsSettled => _isSettled;

    /// <summary>
    /// Called by <see cref="ObjectSpawner"/> after the fall settle coroutine completes.
    /// Enables click interaction for this object.
    /// </summary>
    public void OnSettled() => _isSettled = true;

    /// <summary>
    /// Locks the object at <paramref name="worldPosition"/>, making it kinematic so that
    /// other Rigidbody objects cannot push it out of its slot.
    /// </summary>
    /// <param name="worldPosition">Exact world-space position to snap the object to.</param>
    public void Lock(Vector3 worldPosition)
    {
        _isLocked           = true;
        _rb.isKinematic     = true;
        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.position        = worldPosition;
    }

    /// <summary>
    /// Restores dynamic physics so the object can be clicked or pushed again.
    /// Called when an object is removed from a slot.
    /// </summary>
    public void Unlock()
    {
        _isLocked       = false;
        _rb.isKinematic = false;
    }

    /// <summary>
    /// Cancels any active auto-move and applies an instant impulse to the Rigidbody.
    /// Used by <see cref="TrayController"/> to physically push a rejected object out of the trigger.
    /// </summary>
    /// <param name="impulse">World-space impulse vector (direction × force).</param>
    public void Bounce(Vector3 impulse)
    {
        StopMotion();
        _rb.AddForce(impulse, ForceMode.Impulse);
    }

    /// <summary>
    /// Smoothly moves the object to <paramref name="targetPosition"/>, then re-enables physics.
    /// Used for programmatic repositioning when placement is rejected.
    /// </summary>
    /// <param name="targetPosition">World-space position to animate the object toward.</param>
    public void ReturnToPosition(Vector3 targetPosition)
    {
        Debug.Log($"{LogPrefix} ReturnToPosition on '{name}' — target={targetPosition}");
        StopAllCoroutines();
        StartCoroutine(ReturnCoroutine(targetPosition));
    }

    /// <summary>
    /// Smoothly slides an already-locked (kinematic) object to a new tray slot position.
    /// The object remains locked throughout; no physics state is changed.
    /// Called by <see cref="TrayController"/> during tray compaction after a match-3 removal.
    /// </summary>
    /// <param name="targetPosition">World-space destination anchor of the new tray slot.</param>
    /// <param name="duration">Slide duration in seconds.</param>
    public void MoveToSlot(Vector3 targetPosition, float duration)
    {
        StopAllCoroutines();
        StartCoroutine(MoveToSlotCoroutine(targetPosition, duration));
    }

    /// <summary>
    /// Smoothly flies the object into <paramref name="tray"/> and attempts to place it
    /// in the first empty tray slot. If the tray is full or placement fails, the object
    /// animates back to its original position via <see cref="ReturnToPosition"/>.
    /// No-ops when the object is locked, not yet settled, or already flying.
    /// </summary>
    /// <param name="tray">The tray to attempt placement into.</param>
    public void AutoMoveToDropZone(TrayController tray)
    {
        if (tray == null)      return;
        if (_isLocked)         return;
        if (!_isSettled)       return;
        if (_isAutoMoving)     return;

        StopAllCoroutines();
        StartCoroutine(AutoMoveCoroutine(tray));
    }

    // ── Private state ────────────────────────────────────────────────────────

    private Rigidbody _rb;
    private bool      _isLocked;
    private bool      _isSettled;
    private bool      _isAutoMoving;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        if (_tray == null)
            _tray = FindFirstObjectByType<TrayController>();

        if (_tray == null)
            Debug.LogWarning($"{LogPrefix} No TrayController found in the scene. " +
                             $"Tap-to-place will not work on '{name}'.", this);

        // Objects placed directly in the scene (not via ObjectSpawner) are considered
        // already settled so they can be clicked immediately.
        _isSettled = true;

        Debug.Log($"{LogPrefix} Awake on '{name}' — type={_objectType}, " +
                  $"rb.isKinematic={_rb.isKinematic}");
    }

    private void OnMouseUp()
    {
        if (_isLocked)     return;
        if (!_isSettled)   return;
        if (_isAutoMoving) return;

        Debug.Log($"{LogPrefix} Click on '{name}' (type={_objectType}) — flying to tray.");
        StopMotion();
        AutoMoveToDropZone(_tray);
    }

    private void FixedUpdate()
    {
        if (_isAutoMoving || _isLocked) return;

        // Clamp horizontal velocity so collision impulses cannot propel this object
        // far across the board.
        if (_isSettled)
        {
            Vector3 vel = _rb.linearVelocity;
            float   sqr = vel.x * vel.x + vel.z * vel.z;
            if (sqr > _maxIdleSpeed * _maxIdleSpeed)
            {
                float scale = _maxIdleSpeed / Mathf.Sqrt(sqr);
                vel.x      *= scale;
                vel.z      *= scale;
                _rb.linearVelocity = vel;
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private IEnumerator AutoMoveCoroutine(TrayController tray)
    {
        Vector3 originalPosition = _rb.position;

        _isAutoMoving   = true;
        _rb.isKinematic = true;
        StopMotion();

        Debug.Log($"{LogPrefix} Flying '{name}' toward tray '{tray.name}'.");

        Vector3 start       = _rb.position;
        Vector3 destination = tray.GetPreviewAutoSlotPosition(this);
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

        bool placed = tray.TryAutoPlaceObject(this);
        Debug.Log($"{LogPrefix} TryAutoPlaceObject returned {placed} for '{name}'.");

        if (!placed)
        {
            _rb.isKinematic = false;
            ReturnToPosition(originalPosition);
        }
        // On success, TrayController.Lock() has already made the Rigidbody kinematic
        // and snapped the object to the tray slot anchor; nothing more to do here.

        _isAutoMoving = false;
    }

    private IEnumerator MoveToSlotCoroutine(Vector3 target, float duration)
    {
        Vector3 start   = _rb.position;
        float   elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t      = Mathf.Clamp01(elapsed / duration);
            float smooth = t * t * (3f - 2f * t);
            _rb.MovePosition(Vector3.Lerp(start, target, smooth));
            yield return null;
        }

        _rb.MovePosition(target);
        Debug.Log($"{LogPrefix} MoveToSlot complete on '{name}' — now at {target}");
    }

    private IEnumerator ReturnCoroutine(Vector3 target)
    {
        _rb.isKinematic = true;

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

    /// <summary>Zeroes out all velocity to prevent drifting on pick-up or release.</summary>
    private void StopMotion()
    {
        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }
}
