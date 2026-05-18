using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns a configurable number of matched object pairs above the play area.
///
/// Pair rule  : <see cref="_initialObjectPairCount"/> = N → 2N total objects.
///              Each pair is built from the same randomly chosen prefab entry in
///              <see cref="_objectPrefabs"/>. ObjectType is read directly from the
///              prefab's <see cref="DraggableObject"/> component (or the optional
///              override on each <see cref="ObjectPrefabEntry"/>).
/// Positioning: Objects are placed on a shuffled grid derived from the spawn area
///              so they are evenly distributed and never start on top of each other.
///              Random jitter per cell keeps it looking natural.
/// Batching   : Objects are released in small waves (<see cref="_batchSize"/>) with
///              a short delay between waves so they don't collide mid-air en masse.
/// Physics    : Objects fall under gravity and tumble until velocity settles, then
///              Y position and XZ rotation are frozen for top-down dragging.
/// Extensible : Add a new entry to <see cref="_objectPrefabs"/> in the Inspector and
///              it will automatically be included in random pair generation.
/// </summary>
public class ObjectSpawner : MonoBehaviour
{
    // ── Inner types ───────────────────────────────────────────────────────────

    [Serializable]
    public struct ObjectPrefabEntry
    {
        [Tooltip("Prefab to spawn. Must have DraggableObject and Rigidbody on its root.")]
        public GameObject prefab;

        [Tooltip("ObjectType override. Leave as ObjectType.Object1 and enable 'Read From Prefab' " +
                 "to have the spawner resolve the type automatically from the prefab's ObjectIdentifier.")]
        public bool readTypeFromPrefab;

        [Tooltip("Used when Read From Prefab is false.")]
        public ObjectType overrideType;
    }

    // ── Constants ────────────────────────────────────────────────────────────

    private const string LogPrefix               = "[ObjectSpawner]";
    private const int    MaxPlacementRetries      = 30;
    private const float  SettleVelocityThreshold  = 0.18f;
    private const float  SettleMinWait            = 0.2f;
    private const float  SettleMaxWait            = 3f;

    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Prefabs")]
    [Tooltip("One entry per object type. The spawner randomly picks from this list for each pair. " +
             "Add more entries here to include new types in generation.")]
    [SerializeField] private ObjectPrefabEntry[] _objectPrefabs;

    [Header("Spawning")]
    [Tooltip("Number of matched pairs to spawn. Total objects = InitialObjectPairCount × 2.")]
    [SerializeField, Min(1)] private int _initialObjectPairCount = 5;

    [Tooltip("XZ extents of the spawn area, centred on this GameObject's position. " +
             "Should fit inside the play area walls with inset padding.")]
    [SerializeField] private Vector2 _spawnAreaSize = new Vector2(5f, 10f);

    [Tooltip("Base Y height above the board from which objects are dropped. " +
             "Keep this low (1–3) so objects don't fall far and build momentum.")]
    [SerializeField] private float _spawnHeight = 2f;

    [Tooltip("Additional random Y offset per object so they land at slightly different times. " +
             "Keep this small (0.5–1) to avoid a long chaotic cascade.")]
    [SerializeField, Min(0f)] private float _spawnHeightVariance = 0.8f;

    [Tooltip("Minimum XZ separation between spawn points to avoid heavy initial overlaps. " +
             "Should be at least the diameter of the largest object in the prefab set.")]
    [SerializeField, Min(0f)] private float _minSeparation = 1.2f;

    [Header("Grid Placement")]
    [Tooltip("When enabled, spawn positions are distributed on a jittered grid instead of fully " +
             "random positions. This gives even coverage and prevents clustering. Recommended.")]
    [SerializeField] private bool _useGridPlacement = true;

    [Tooltip("Maximum XZ jitter (metres) applied to each grid cell centre to keep positions " +
             "looking natural. Should be less than half the cell size.")]
    [SerializeField, Min(0f)] private float _gridJitter = 0.25f;

    [Header("No-Spawn Zone")]
    [Tooltip("Transform whose XZ position defines the centre of the exclusion rectangle (assign the DropZone).")]
    [SerializeField] private Transform _noSpawnZoneCenter;

    [Tooltip("XZ extents of the exclusion rectangle. Add padding beyond the DropZone's own size.")]
    [SerializeField] private Vector2 _noSpawnZoneSize = new Vector2(3f, 3f);

    [Header("Tumble")]
    [Tooltip("Maximum angular speed (degrees/s) applied to each axis on spawn. " +
             "Keep low (30–60) to avoid violent collisions between falling objects.")]
    [SerializeField] private Vector3 _maxTumbleSpeed = new Vector3(45f, 45f, 45f);

    [Tooltip("Linear damping applied while falling. 0 = full gravity speed. " +
             "2–3 bleeds horizontal momentum quickly without noticeably slowing the drop.")]
    [SerializeField, Min(0f)] private float _fallLinearDamping = 2.5f;

    [Tooltip("Angular damping applied while falling. Higher values kill the tumble faster " +
             "so the object settles its rotation sooner. Default Unity value is 0.05.")]
    [SerializeField, Min(0f)] private float _fallAngularDamping = 4f;

    [Header("Drop Zone Blocker")]
    [Tooltip("Assign the DropZoneSpawnBlocker GameObject here. It will be enabled before " +
             "spawning and disabled automatically once all objects have settled.")]
    [SerializeField] private GameObject _dropZoneBlocker;

    // ── Public properties ────────────────────────────────────────────────────

    /// <summary>Number of matched pairs that will be (or were) spawned.</summary>
    public int InitialObjectPairCount => _initialObjectPairCount;

    /// <summary>All object instances currently alive in the scene.</summary>
    public IReadOnlyList<DraggableObject> LiveObjects => _liveObjects;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="DropZone"/> after it destroys a matched pair so the
    /// spawner can keep its live-object list accurate.
    /// </summary>
    public void OnObjectsDestroyed(DraggableObject left, DraggableObject right)
    {
        _liveObjects.Remove(left);
        _liveObjects.Remove(right);
        Debug.Log($"{LogPrefix} Pair removed — {_liveObjects.Count} object(s) remaining.");
    }

    // ── Private state ─────────────────────────────────────────────────────────

    private readonly List<Vector2>         _placedXZ    = new List<Vector2>();
    private readonly List<DraggableObject> _liveObjects = new List<DraggableObject>();
    private int                            _pendingSettleCount;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Start()
    {
        if (!ValidateConfig()) return;
        SpawnPairs();
    }

    // ── Spawning ─────────────────────────────────────────────────────────────

    private void SpawnPairs()
    {
        List<int>     deck      = BuildDeck();
        List<Vector3> positions = BuildSpawnPositions(deck.Count);
        ShuffleDeck(deck);

        // Enable the blocker before any object is instantiated so it is solid
        // for the entire fall/settle phase.
        if (_dropZoneBlocker != null)
            _dropZoneBlocker.SetActive(true);

        _pendingSettleCount = deck.Count;

        int total = deck.Count;
        for (int i = 0; i < total; i++)
            SpawnObject(deck[i], positions[i]);

        Debug.Log($"{LogPrefix} Spawned {total} objects ({_initialObjectPairCount} pairs).");
    }

    /// <summary>
    /// Builds a deck of 2 × <see cref="_initialObjectPairCount"/> prefab entry indices.
    /// Each pair's entry is chosen independently at random from <see cref="_objectPrefabs"/>.
    /// </summary>
    private List<int> BuildDeck()
    {
        var deck = new List<int>(_initialObjectPairCount * 2);

        for (int pair = 0; pair < _initialObjectPairCount; pair++)
        {
            int index = UnityEngine.Random.Range(0, _objectPrefabs.Length);
            deck.Add(index);
            deck.Add(index);
        }

        return deck;
    }

    /// <summary>Fisher-Yates in-place shuffle.</summary>
    private static void ShuffleDeck(List<int> deck)
    {
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    /// <summary>
    /// Pre-computes <paramref name="count"/> spawn positions before any object is
    /// instantiated. Uses a jittered grid when <see cref="_useGridPlacement"/> is
    /// enabled, otherwise falls back to the random rejection-sampling approach.
    /// </summary>
    private List<Vector3> BuildSpawnPositions(int count)
    {
        return _useGridPlacement
            ? BuildGridPositions(count)
            : BuildRandomPositions(count);
    }

    /// <summary>
    /// Distributes <paramref name="count"/> points across a regular grid fitted to
    /// <see cref="_spawnAreaSize"/>, then applies small random jitter to each cell.
    /// Cells that fall inside the no-spawn zone are skipped and re-sampled randomly.
    /// </summary>
    private List<Vector3> BuildGridPositions(int count)
    {
        var positions = new List<Vector3>(count);
        Vector3 origin = transform.position;
        float halfX = _spawnAreaSize.x * 0.5f;
        float halfZ = _spawnAreaSize.y * 0.5f;

        // Determine grid dimensions: cols × rows ≥ count, as square as possible.
        int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
        int rows = Mathf.CeilToInt((float)count / cols);

        float cellW = _spawnAreaSize.x / cols;
        float cellH = _spawnAreaSize.y / rows;

        // Build a list of all valid cell centres.
        var cells = new List<Vector2>(cols * rows);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                float cx = origin.x - halfX + cellW * (c + 0.5f);
                float cz = origin.z - halfZ + cellH * (r + 0.5f);

                if (!IsOutsideNoSpawnZone(cx, cz)) continue;
                cells.Add(new Vector2(cx, cz));
            }
        }

        // Shuffle cells so object pairing doesn't mirror the grid order.
        for (int i = cells.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (cells[i], cells[j]) = (cells[j], cells[i]);
        }

        for (int i = 0; i < count; i++)
        {
            float x, z;
            if (i < cells.Count)
            {
                float jx = UnityEngine.Random.Range(-_gridJitter, _gridJitter);
                float jz = UnityEngine.Random.Range(-_gridJitter, _gridJitter);
                x = Mathf.Clamp(cells[i].x + jx, origin.x - halfX, origin.x + halfX);
                z = Mathf.Clamp(cells[i].y + jz, origin.z - halfZ, origin.z + halfZ);
            }
            else
            {
                // More objects than grid cells (e.g. exclusion zone swallowed many) — fall back.
                x = origin.x + UnityEngine.Random.Range(-halfX, halfX);
                z = origin.z + UnityEngine.Random.Range(-halfZ, halfZ);
            }

            float y = origin.y + _spawnHeight + UnityEngine.Random.Range(0f, _spawnHeightVariance);
            positions.Add(new Vector3(x, y, z));
        }

        return positions;
    }

    /// <summary>
    /// Fallback random placement using rejection-sampling (original algorithm).
    /// </summary>
    private List<Vector3> BuildRandomPositions(int count)
    {
        var positions = new List<Vector3>(count);
        for (int i = 0; i < count; i++)
            positions.Add(FindSpawnPosition());
        return positions;
    }

    private void SpawnObject(int entryIndex, Vector3 spawnPos)
    {
        ObjectPrefabEntry entry = _objectPrefabs[entryIndex];

        GameObject obj = Instantiate(entry.prefab, spawnPos, RandomRotation());

        // Resolve ObjectType — prefer the prefab's own ObjectIdentifier.
        ObjectType resolvedType = ResolveObjectType(entry, obj);
        obj.name = $"Object_{resolvedType}";

        // Wire FruitType on the DraggableFruit so the DropZone type-matching still works.
        // We map ObjectType → FruitType by index (both enums are ordered Object1-6 / Tomato...).
        // Alternatively, the prefab can have _fruitType pre-set in the Inspector and we skip SetFruitType.
        if (obj.TryGetComponent(out DraggableObject draggable))
        {
            // The prefab already has ObjectType set in the Inspector — preserve it.
            // MarkUnsettled() gates drag until the fall settle coroutine completes.
            draggable.MarkUnsettled();
            _liveObjects.Add(draggable);
        }
        else
        {
            Debug.LogWarning($"{LogPrefix} '{obj.name}' has no DraggableObject — dragging will not work.");
        }

        ConfigurePhysicsForFall(obj);
        _placedXZ.Add(new Vector2(spawnPos.x, spawnPos.z));
    }

    /// <summary>
    /// Resolves the <see cref="ObjectType"/> for a spawned object.
    /// If <see cref="ObjectPrefabEntry.readTypeFromPrefab"/> is true (or the override
    /// is the default zero value), attempts to read it from the prefab's
    /// <see cref="ObjectIdentifier"/> component; falls back to the override field.
    /// </summary>
    private static ObjectType ResolveObjectType(ObjectPrefabEntry entry, GameObject obj)
    {
        if (entry.readTypeFromPrefab)
        {
            if (obj.TryGetComponent(out ObjectIdentifier id))
                return id.objectType;

            Debug.LogWarning($"{LogPrefix} readTypeFromPrefab is true but '{obj.name}' has no " +
                             "ObjectIdentifier — using overrideType instead.");
        }

        return entry.overrideType;
    }

    // ── Positioning ──────────────────────────────────────────────────────────

    /// <summary>
    /// Rejection-sampling fallback used when <see cref="_useGridPlacement"/> is false.
    /// Tries <see cref="MaxPlacementRetries"/> times to find a position that satisfies
    /// both min-separation and no-spawn-zone constraints.
    /// </summary>
    private Vector3 FindSpawnPosition()
    {
        float halfX    = _spawnAreaSize.x * 0.5f;
        float halfZ    = _spawnAreaSize.y * 0.5f;
        Vector3 origin = transform.position;

        for (int attempt = 0; attempt < MaxPlacementRetries; attempt++)
        {
            float x = origin.x + UnityEngine.Random.Range(-halfX, halfX);
            float z = origin.z + UnityEngine.Random.Range(-halfZ, halfZ);

            if (IsXZFarEnough(x, z) && IsOutsideNoSpawnZone(x, z))
            {
                _placedXZ.Add(new Vector2(x, z));
                float y = origin.y + _spawnHeight + UnityEngine.Random.Range(0f, _spawnHeightVariance);
                return new Vector3(x, y, z);
            }
        }

        Debug.LogWarning($"{LogPrefix} Could not find a non-overlapping position after " +
                         $"{MaxPlacementRetries} retries — placing anyway.");

        float fx = origin.x + UnityEngine.Random.Range(-halfX, halfX);
        float fz = origin.z + UnityEngine.Random.Range(-halfZ, halfZ);
        float fy = origin.y + _spawnHeight + UnityEngine.Random.Range(0f, _spawnHeightVariance);
        _placedXZ.Add(new Vector2(fx, fz));
        return new Vector3(fx, fy, fz);
    }

    private bool IsXZFarEnough(float x, float z)
    {
        float minSqr = _minSeparation * _minSeparation;
        foreach (Vector2 placed in _placedXZ)
        {
            float dx = x - placed.x;
            float dz = z - placed.y;
            if (dx * dx + dz * dz < minSqr)
                return false;
        }
        return true;
    }

    private bool IsOutsideNoSpawnZone(float x, float z)
    {
        if (_noSpawnZoneCenter == null) return true;

        Vector3 c    = _noSpawnZoneCenter.position;
        float halfX  = _noSpawnZoneSize.x * 0.5f;
        float halfZ  = _noSpawnZoneSize.y * 0.5f;

        bool insideX = x >= c.x - halfX && x <= c.x + halfX;
        bool insideZ = z >= c.z - halfZ && z <= c.z + halfZ;

        return !(insideX && insideZ);
    }

    // ── Physics ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables gravity with no rotation constraints so the object falls and tumbles freely.
    /// A random angular velocity is applied immediately to seed the tumble.
    /// Starts a coroutine that locks Y and freezes rotation once the object has settled.
    /// </summary>
    private void ConfigurePhysicsForFall(GameObject obj)
    {
        if (!obj.TryGetComponent(out Rigidbody rb))
        {
            Debug.LogWarning($"{LogPrefix} '{obj.name}' has no Rigidbody — physics fall skipped.");
            return;
        }

        rb.useGravity         = true;
        rb.isKinematic        = false;
        rb.linearDamping      = _fallLinearDamping;
        rb.angularDamping     = _fallAngularDamping;
        // No constraints during the fall — let the object tumble freely on all axes.
        rb.constraints        = RigidbodyConstraints.None;
        rb.angularVelocity    = RandomAngularVelocity();

        DraggableObject draggable = obj.TryGetComponent(out DraggableObject df) ? df : null;
        StartCoroutine(SettleRoutine(rb, draggable));
    }

    /// <summary>Returns a uniformly random rotation across all three axes.</summary>
    private static Quaternion RandomRotation() => UnityEngine.Random.rotation;

    /// <summary>
    /// Returns a random angular velocity vector where each component is independently
    /// sampled from [-max, +max] on its respective axis, converted to radians/s.
    /// </summary>
    private Vector3 RandomAngularVelocity()
    {
        return new Vector3(
            UnityEngine.Random.Range(-_maxTumbleSpeed.x, _maxTumbleSpeed.x),
            UnityEngine.Random.Range(-_maxTumbleSpeed.y, _maxTumbleSpeed.y),
            UnityEngine.Random.Range(-_maxTumbleSpeed.z, _maxTumbleSpeed.z)
        ) * Mathf.Deg2Rad;
    }

    /// <summary>
    /// Waits until both linear and angular velocity drop below <see cref="SettleVelocityThreshold"/>
    /// (or <see cref="SettleMaxWait"/> elapses), then freezes Y position and XZ rotation
    /// and disables gravity so the object is ready for top-down dragging gameplay.
    /// </summary>
    private IEnumerator SettleRoutine(Rigidbody rb, DraggableObject obj)
    {
        yield return new WaitForSeconds(SettleMinWait);

        float thresholdSqr = SettleVelocityThreshold * SettleVelocityThreshold;
        float elapsed      = SettleMinWait;

        while (elapsed < SettleMaxWait)
        {
            if (rb == null) yield break;

            bool linearSettled  = rb.linearVelocity.sqrMagnitude  <= thresholdSqr;
            bool angularSettled = rb.angularVelocity.sqrMagnitude <= thresholdSqr;

            if (linearSettled && angularSettled)
                break;

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (rb == null) yield break;

        rb.useGravity      = false;
        rb.linearVelocity  = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.constraints     = RigidbodyConstraints.FreezePositionY
                           | RigidbodyConstraints.FreezeRotationX
                           | RigidbodyConstraints.FreezeRotationZ;

        obj?.OnSettled();

        // Decrement the settle counter; when the last object settles, remove the blocker.
        _pendingSettleCount--;
        if (_pendingSettleCount <= 0 && _dropZoneBlocker != null)
        {
            _dropZoneBlocker.SetActive(false);
            Debug.Log($"{LogPrefix} All objects settled — DropZoneSpawnBlocker disabled.");
        }
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private bool ValidateConfig()
    {
        if (_objectPrefabs == null || _objectPrefabs.Length == 0)
        {
            Debug.LogError($"{LogPrefix} No object prefabs configured. " +
                           "Add at least one entry to the Object Prefabs list.", this);
            return false;
        }

        for (int i = 0; i < _objectPrefabs.Length; i++)
        {
            if (_objectPrefabs[i].prefab == null)
            {
                Debug.LogError($"{LogPrefix} Entry [{i}] has a null prefab reference.", this);
                return false;
            }
        }

        return true;
    }

    // ── Editor gizmos ────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Vector3 origin = transform.position;
        float   halfX  = _spawnAreaSize.x * 0.5f;
        float   halfZ  = _spawnAreaSize.y * 0.5f;

        Vector3 bottomCenter = new Vector3(origin.x, origin.y + _spawnHeight, origin.z);
        Vector3 topCenter    = new Vector3(origin.x, origin.y + _spawnHeight + _spawnHeightVariance, origin.z);
        Vector3 slabSize     = new Vector3(_spawnAreaSize.x, 0.05f, _spawnAreaSize.y);

        // Spawn volume fill
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.12f);
        Vector3 volumeCenter = new Vector3(origin.x, origin.y + _spawnHeight + _spawnHeightVariance * 0.5f, origin.z);
        Vector3 volumeSize   = new Vector3(_spawnAreaSize.x, Mathf.Max(_spawnHeightVariance, 0.1f), _spawnAreaSize.y);
        Gizmos.DrawCube(volumeCenter, volumeSize);

        // Bottom and top slab wires
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.DrawWireCube(bottomCenter, slabSize);
        Gizmos.DrawWireCube(topCenter, slabSize);

        // Vertical corner lines
        Vector3[] corners =
        {
            new Vector3(-halfX, 0f, -halfZ), new Vector3( halfX, 0f, -halfZ),
            new Vector3(-halfX, 0f,  halfZ), new Vector3( halfX, 0f,  halfZ),
        };
        foreach (Vector3 c in corners)
        {
            Vector3 bottom = new Vector3(origin.x + c.x, origin.y + _spawnHeight,                        origin.z + c.z);
            Vector3 top    = new Vector3(origin.x + c.x, origin.y + _spawnHeight + _spawnHeightVariance, origin.z + c.z);
            Gizmos.DrawLine(bottom, top);
        }

        // Grid lines (shown when grid placement is active)
        if (_useGridPlacement)
        {
            int totalObjects = _initialObjectPairCount * 2;
            int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(totalObjects)));
            int rows = Mathf.Max(1, Mathf.CeilToInt((float)totalObjects / cols));
            float cellW = _spawnAreaSize.x / cols;
            float cellH = _spawnAreaSize.y / rows;
            float gridY = origin.y + _spawnHeight;

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
            for (int c = 0; c <= cols; c++)
            {
                float wx = origin.x - halfX + cellW * c;
                Gizmos.DrawLine(new Vector3(wx, gridY, origin.z - halfZ),
                                new Vector3(wx, gridY, origin.z + halfZ));
            }
            for (int r = 0; r <= rows; r++)
            {
                float wz = origin.z - halfZ + cellH * r;
                Gizmos.DrawLine(new Vector3(origin.x - halfX, gridY, wz),
                                new Vector3(origin.x + halfX, gridY, wz));
            }
        }

        // No-spawn zone exclusion rect
        if (_noSpawnZoneCenter != null)
        {
            Vector3 nsc = _noSpawnZoneCenter.position;
            Vector3 noSpawnCenter = new Vector3(nsc.x, origin.y + _spawnHeight + _spawnHeightVariance * 0.5f, nsc.z);
            Vector3 noSpawnSize   = new Vector3(_noSpawnZoneSize.x, Mathf.Max(_spawnHeightVariance, 0.1f), _noSpawnZoneSize.y);

            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.18f);
            Gizmos.DrawCube(noSpawnCenter, noSpawnSize);

            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(noSpawnCenter, noSpawnSize);
        }
    }
#endif
}
