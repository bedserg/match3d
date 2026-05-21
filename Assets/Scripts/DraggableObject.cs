using System.Collections;
using UnityEngine;

/// <summary>
/// Attach to the root GameObject of an object prefab.
/// Requires a Rigidbody and a Collider on the same GameObject.
/// On click/tap the object automatically flies to the assigned drop zone.
///
/// While flying into the tray the object simultaneously animates its position,
/// scale, and rotation to the configured tray values so it fits the smaller
/// tray slots and faces front. On rejection it animates all three back to the
/// values captured just before the fly-in started.
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

    [Header("Tray Visual Transform")]
    [Tooltip("Target local scale applied while the object is seated in a tray slot. " +
             "Use a uniform value such as (0.55, 0.55, 0.55) to shrink the object to fit the slot.")]
    [SerializeField] private Vector3 _trayScale = new Vector3(0.55f, 0.55f, 0.55f);

    [Tooltip("Target world-space Euler rotation applied while the object is seated in a tray slot. " +
             "Default (0, 0, 0) shows the front face. The object animates to this rotation during the fly-in.")]
    [SerializeField] private Vector3 _trayRotation = Vector3.zero;

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
    /// When true, <see cref="OnMouseUp"/> silently ignores all tap input.
    /// Set by <see cref="TrayController"/> while a match-3 gather animation is running.
    /// </summary>
    public bool IsInputBlocked { get; set; }

    /// <summary>
    /// Called by <see cref="ObjectSpawner"/> after the fall settle coroutine completes.
    /// Enables click interaction for this object.
    /// </summary>
    public void OnSettled() => _isSettled = true;

    /// <summary>
    /// Locks the object at <paramref name="worldPosition"/>, making it kinematic so that
    /// other Rigidbody objects cannot push it out of its slot.
    /// Also snaps scale and rotation to the configured tray values as a hard guarantee
    /// in case the fly-in animation did not fully complete.
    /// </summary>
    /// <param name="worldPosition">Exact world-space position to snap the object to.</param>
    public void Lock(Vector3 worldPosition)
    {
        _isLocked               = true;
        _rb.isKinematic         = true;
        _rb.linearVelocity      = Vector3.zero;
        _rb.angularVelocity     = Vector3.zero;
        _rb.position            = worldPosition;
        _rb.rotation            = Quaternion.Euler(_trayRotation);
        transform.localScale    = _trayScale;
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
    /// Smoothly moves the object to <paramref name="targetPosition"/> and animates its
    /// scale and rotation back to the board values captured before the fly-in, then
    /// re-enables physics.
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
    /// Scale and rotation are already at tray values; only position is animated.
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
    /// Smoothly slides an already-locked (kinematic) object to a new tray slot position
    /// and yields until the animation is complete.
    /// Scale and rotation are already at tray values; only position is animated.
    /// The object remains locked throughout; no physics state is changed.
    /// Called by <see cref="TrayController"/> during the match-3 gather animation.
    /// </summary>
    /// <param name="targetPosition">World-space destination anchor of the target tray slot.</param>
    /// <param name="duration">Slide duration in seconds.</param>
    public IEnumerator MoveToSlotAndWait(Vector3 targetPosition, float duration)
    {
        StopAllCoroutines();
        yield return MoveToSlotCoroutine(targetPosition, duration);
    }

    /// <summary>
    /// Simultaneously animates position and scale of an already-locked (kinematic) object
    /// and yields until both animations are complete.
    /// Used by <see cref="TrayController"/> during the match-3 merge sequence:
    /// left and right objects slide to the middle slot position while all three scale down.
    /// The object remains locked throughout; no physics state is changed.
    /// </summary>
    /// <param name="targetPosition">World-space destination (the middle slot position for left/right, unchanged for middle).</param>
    /// <param name="targetScale">Local scale to animate toward (tray scale × merge multiplier).</param>
    /// <param name="duration">Animation duration in seconds.</param>
    public IEnumerator MergeToAndWait(Vector3 targetPosition, Vector3 targetScale, float duration)
    {
        // Stop any active MoveToSlot coroutine running on this object before starting.
        StopAllCoroutines();

        Vector3 startPos   = _rb.position;
        Vector3 startScale = transform.localScale;
        float   elapsed    = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t      = Mathf.Clamp01(elapsed / duration);
            float smooth = t * t * (3f - 2f * t);

            _rb.MovePosition(Vector3.Lerp(startPos, targetPosition, smooth));
            transform.localScale = Vector3.Lerp(startScale, targetScale, smooth);

            yield return null;
        }

        _rb.MovePosition(targetPosition);
        transform.localScale = targetScale;

        Debug.Log($"{LogPrefix} MergeToAndWait complete on '{name}' — pos={targetPosition}, scale={targetScale}");
    }

    /// <summary>
    /// Smoothly flies the object into <paramref name="tray"/> and attempts to place it
    /// in the correct tray slot. During the fly-in, position, scale, and rotation all
    /// animate simultaneously toward the configured tray values.
    /// If the tray is full or placement fails, the object animates back to its original
    /// board position, scale, and rotation.
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

    private Rigidbody  _rb;
    private bool       _isLocked;
    private bool       _isSettled;
    private bool       _isAutoMoving;

    // Board-state snapshot captured at the start of each fly-in animation.
    // Used to restore the object if placement is rejected.
    private Vector3    _boardScale;
    private Quaternion _boardRotation;

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
        if (_isLocked)       return;
        if (!_isSettled)     return;
        if (_isAutoMoving)   return;
        if (IsInputBlocked)  return;

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
                float spd = _maxIdleSpeed / Mathf.Sqrt(sqr);
                vel.x     *= spd;
                vel.z     *= spd;
                _rb.linearVelocity = vel;
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private IEnumerator AutoMoveCoroutine(TrayController tray)
    {
        // Capture board transform before any animation so we can restore it on rejection.
        Vector3    originalPosition = _rb.position;
        _boardScale                 = transform.localScale;
        _boardRotation              = _rb.rotation;

        _isAutoMoving   = true;
        _rb.isKinematic = true;
        StopMotion();

        Debug.Log($"{LogPrefix} Flying '{name}' toward tray '{tray.name}'.");

        Vector3    startPos   = _rb.position;
        Vector3    destPos    = tray.GetPreviewAutoSlotPosition(this);
        Vector3    startScale = transform.localScale;
        Quaternion startRot   = _rb.rotation;
        Quaternion targetRot  = Quaternion.Euler(_trayRotation);
        float      elapsed    = 0f;

        while (elapsed < _autoMoveDuration)
        {
            elapsed += Time.deltaTime;
            float t      = Mathf.Clamp01(elapsed / _autoMoveDuration);
            float smooth = t * t * (3f - 2f * t);

            _rb.MovePosition(Vector3.Lerp(startPos, destPos, smooth));
            _rb.MoveRotation(Quaternion.Slerp(startRot, targetRot, smooth));
            transform.localScale = Vector3.Lerp(startScale, _trayScale, smooth);

            yield return null;
        }

        _rb.MovePosition(destPos);
        _rb.MoveRotation(targetRot);
        transform.localScale = _trayScale;

        bool placed = tray.TryAutoPlaceObject(this);
        Debug.Log($"{LogPrefix} TryAutoPlaceObject returned {placed} for '{name}'.");

        if (!placed)
        {
            _rb.isKinematic = false;
            ReturnToPosition(originalPosition);
        }
        // On success, TrayController.Lock() snaps position, rotation, and scale to tray
        // values as a hard guarantee; nothing more to do here.

        _isAutoMoving = false;
    }

    /// <summary>
    /// Animates only position between tray slots. Scale and rotation are already at
    /// tray values for any object that is currently locked in the tray.
    /// </summary>
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

    /// <summary>
    /// Animates position, scale, and rotation back to the board values captured before
    /// the fly-in, then restores dynamic physics so the object can be interacted with again.
    /// </summary>
    private IEnumerator ReturnCoroutine(Vector3 targetPosition)
    {
        _rb.isKinematic = true;

        Vector3    startPos   = _rb.position;
        Vector3    startScale = transform.localScale;
        Quaternion startRot   = _rb.rotation;
        float      elapsed    = 0f;

        while (elapsed < _returnDuration)
        {
            elapsed += Time.deltaTime;
            float t      = Mathf.Clamp01(elapsed / _returnDuration);
            float smooth = t * t * (3f - 2f * t);

            _rb.MovePosition(Vector3.Lerp(startPos, targetPosition, smooth));
            _rb.MoveRotation(Quaternion.Slerp(startRot, _boardRotation, smooth));
            transform.localScale = Vector3.Lerp(startScale, _boardScale, smooth);

            yield return null;
        }

        _rb.MovePosition(targetPosition);
        _rb.MoveRotation(_boardRotation);
        transform.localScale = _boardScale;
        _rb.isKinematic      = false;
        StopMotion();

        Debug.Log($"{LogPrefix} ReturnCoroutine complete on '{name}' — now at {targetPosition}");
    }

    /// <summary>Zeroes out all velocity to prevent drifting on pick-up or release.</summary>
    private void StopMotion()
    {
        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }
}
