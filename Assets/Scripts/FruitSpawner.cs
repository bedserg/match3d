using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns a configurable number of matched fruit pairs above the play area.
///
/// Pair rule  : <see cref="_initialObjectPairCount"/> = N → 2N total fruits.
///              Each pair shares the same randomly chosen <see cref="FruitType"/>.
/// Physics    : Fruits are dropped from a randomised height above the board so
///              they fall under gravity and scatter naturally before gameplay begins.
///              The Y constraint is re-applied once each fruit settles so it stays
///              on the board plane for dragging.
/// </summary>
public class FruitSpawner : MonoBehaviour
{
    // ── Inner types ───────────────────────────────────────────────────────────

    [Serializable]
    public struct FruitTypeColor
    {
        [Tooltip("Which FruitType this color applies to.")]
        public FruitType type;

        [Tooltip("Color applied to a fruit of this type via MaterialPropertyBlock.")]
        public Color color;
    }

    // ── Constants ────────────────────────────────────────────────────────────

    private const string LogPrefix            = "[FruitSpawner]";
    private const int    MaxPlacementRetries  = 30;
    private const float  SettleVelocityThreshold = 0.08f; // m/s — considered at rest below this
    private const float  SettleMinWait        = 0.5f;     // s   — always wait at least this long
    private const float  SettleMaxWait        = 6f;       // s   — give up waiting after this long

    private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");

    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Prefab")]
    [Tooltip("Fruit prefab. Must have DraggableFruit and Rigidbody on its root.")]
    [SerializeField] private GameObject _fruitPrefab;

    [Header("Spawning")]
    [Tooltip("Number of matched pairs to spawn. Total objects = InitialObjectPairCount × 2.")]
    [SerializeField, Min(1)] private int _initialObjectPairCount = 5;

    [Tooltip("XZ extents of the spawn area, centred on this GameObject's position.")]
    [SerializeField] private Vector2 _spawnAreaSize = new Vector2(5f, 10f);

    [Tooltip("Base Y height above the board from which fruits are dropped.")]
    [SerializeField] private float _spawnHeight = 5f;

    [Tooltip("Additional random Y added per fruit so they land at slightly different times.")]
    [SerializeField, Min(0f)] private float _spawnHeightVariance = 2f;

    [Tooltip("Minimum XZ separation between spawn points to avoid heavy initial overlaps.")]
    [SerializeField, Min(0f)] private float _minSeparation = 0.9f;

    [Header("No-Spawn Zone")]
    [Tooltip("Transform whose XZ position defines the centre of the exclusion rectangle (assign the DropZone).")]
    [SerializeField] private Transform _noSpawnZoneCenter;

    [Tooltip("XZ extents of the exclusion rectangle. Add padding beyond the DropZone's own size so fruits never clip its edge.")]
    [SerializeField] private Vector2 _noSpawnZoneSize = new Vector2(3f, 3f);

    [Header("Fruit Colors")]
    [Tooltip("One entry per available FruitType. The spawner randomly picks from this list for each pair.")]
    [SerializeField] private FruitTypeColor[] _fruitTypeColors = DefaultColors();

    // ── Public properties ────────────────────────────────────────────────────

    /// <summary>Number of matched pairs that will be (or were) spawned.</summary>
    public int InitialObjectPairCount => _initialObjectPairCount;

    /// <summary>All fruit instances currently alive in the scene.</summary>
    public IReadOnlyList<DraggableFruit> LiveFruits => _liveFruits;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="DropZone"/> after it destroys a matched pair so the
    /// spawner can keep its live-fruit list accurate.
    /// </summary>
    public void OnFruitsDestroyed(DraggableFruit left, DraggableFruit right)
    {
        _liveFruits.Remove(left);
        _liveFruits.Remove(right);
        Debug.Log($"{LogPrefix} Pair removed — {_liveFruits.Count} fruit(s) remaining.");
    }

    // ── Private state ─────────────────────────────────────────────────────────

    private readonly List<Vector2>       _placedXZ    = new List<Vector2>();       // XZ only for separation checks
    private readonly List<DraggableFruit> _liveFruits = new List<DraggableFruit>(); // all fruits currently in the scene

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Start()
    {
        if (!ValidateConfig()) return;
        SpawnPairs();
    }

    // ── Spawning ─────────────────────────────────────────────────────────────

    private void SpawnPairs()
    {
        List<FruitType> deck = BuildDeck();
        ShuffleDeck(deck);

        foreach (FruitType type in deck)
            SpawnFruit(type);

        Debug.Log($"{LogPrefix} Spawned {deck.Count} fruits ({_initialObjectPairCount} pairs).");
    }

    /// <summary>
    /// Builds a deck of 2 × <see cref="_initialObjectPairCount"/> types.
    /// Each pair's type is chosen independently at random from <see cref="_fruitTypeColors"/>.
    /// </summary>
    private List<FruitType> BuildDeck()
    {
        var deck = new List<FruitType>(_initialObjectPairCount * 2);

        for (int pair = 0; pair < _initialObjectPairCount; pair++)
        {
            int       index = UnityEngine.Random.Range(0, _fruitTypeColors.Length);
            FruitType type  = _fruitTypeColors[index].type;
            deck.Add(type); // first of the pair
            deck.Add(type); // matching second
        }

        return deck;
    }

    /// <summary>Fisher-Yates in-place shuffle.</summary>
    private static void ShuffleDeck(List<FruitType> deck)
    {
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    private void SpawnFruit(FruitType type)
    {
        Vector3 spawnPos = FindSpawnPosition();

        GameObject fruit = Instantiate(_fruitPrefab, spawnPos, Quaternion.identity);
        fruit.name = $"Fruit_{type}";

        ApplyType(fruit, type);
        ApplyColor(fruit, type);
        ConfigurePhysicsForFall(fruit);

        _placedXZ.Add(new Vector2(spawnPos.x, spawnPos.z));

        if (fruit.TryGetComponent(out DraggableFruit draggable))
            _liveFruits.Add(draggable);
    }

    // ── Positioning ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a random position above the board. XZ separation is checked against
    /// previously placed fruits; the no-spawn zone is also excluded.
    /// Y is randomised within [_spawnHeight, _spawnHeight + _spawnHeightVariance].
    /// </summary>
    private Vector3 FindSpawnPosition()
    {
        float halfX = _spawnAreaSize.x * 0.5f;
        float halfZ = _spawnAreaSize.y * 0.5f;
        Vector3 origin = transform.position;

        for (int attempt = 0; attempt < MaxPlacementRetries; attempt++)
        {
            float x = origin.x + UnityEngine.Random.Range(-halfX, halfX);
            float z = origin.z + UnityEngine.Random.Range(-halfZ, halfZ);

            if (IsXZFarEnough(x, z) && IsOutsideNoSpawnZone(x, z))
            {
                float y = origin.y + _spawnHeight + UnityEngine.Random.Range(0f, _spawnHeightVariance);
                return new Vector3(x, y, z);
            }
        }

        Debug.LogWarning($"{LogPrefix} Could not find a non-overlapping position after " +
                         $"{MaxPlacementRetries} retries — placing anyway.");

        float fx = origin.x + UnityEngine.Random.Range(-halfX, halfX);
        float fz = origin.z + UnityEngine.Random.Range(-halfZ, halfZ);
        float fy = origin.y + _spawnHeight + UnityEngine.Random.Range(0f, _spawnHeightVariance);
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

    /// <summary>
    /// Returns true when the XZ point falls outside the configured no-spawn rectangle.
    /// If no center Transform is assigned the check is skipped (always returns true).
    /// </summary>
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
    /// Enables gravity and removes the Y-freeze constraint so the fruit falls freely.
    /// Starts a coroutine that re-applies the constraint once the fruit has settled.
    /// </summary>
    private void ConfigurePhysicsForFall(GameObject fruit)
    {
        if (!fruit.TryGetComponent(out Rigidbody rb))
        {
            Debug.LogWarning($"{LogPrefix} '{fruit.name}' has no Rigidbody — physics fall skipped.");
            return;
        }

        rb.useGravity  = true;
        rb.isKinematic = false;
        // Allow free vertical movement while falling; keep rotations frozen.
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        StartCoroutine(SettleRoutine(rb, fruit.TryGetComponent(out DraggableFruit df) ? df : null));
    }

    /// <summary>
    /// Waits until the Rigidbody velocity drops below <see cref="SettleVelocityThreshold"/>
    /// (or <see cref="SettleMaxWait"/> elapses), then re-freezes Y and disables gravity
    /// so the fruit stays on the board plane ready for gameplay.
    /// </summary>
    private IEnumerator SettleRoutine(Rigidbody rb, DraggableFruit fruit)
    {
        // Minimum wait — fruit needs time to actually start moving.
        yield return new WaitForSeconds(SettleMinWait);

        float elapsed = SettleMinWait;
        while (elapsed < SettleMaxWait)
        {
            if (rb == null) yield break;

            if (rb.linearVelocity.sqrMagnitude <= SettleVelocityThreshold * SettleVelocityThreshold)
                break;

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (rb == null) yield break;

        // Lock the fruit to its resting Y, matching the gameplay constraint setup.
        rb.useGravity      = false;
        rb.linearVelocity  = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.constraints     = RigidbodyConstraints.FreezePositionY
                           | RigidbodyConstraints.FreezeRotationX
                           | RigidbodyConstraints.FreezeRotationZ;

        // Notify DraggableFruit that the fruit has finished falling.
        fruit?.OnSettled();
    }

    // ── Component wiring ─────────────────────────────────────────────────────

    /// <summary>Sets the <see cref="FruitType"/> on the <see cref="DraggableFruit"/> component.</summary>
    private static void ApplyType(GameObject fruit, FruitType type)
    {
        if (fruit.TryGetComponent(out DraggableFruit draggable))
        {
            draggable.SetFruitType(type);
            return;
        }

        Debug.LogWarning($"{LogPrefix} '{fruit.name}' has no DraggableFruit — type not set.");
    }

    /// <summary>
    /// Applies the configured color via <see cref="MaterialPropertyBlock"/> so the
    /// shared material is never mutated (GPU instancing stays intact).
    /// Searches the entire child hierarchy for a <see cref="MeshRenderer"/> because
    /// the renderer lives on the child Visual GameObject, not the prefab root.
    /// </summary>
    private void ApplyColor(GameObject fruit, FruitType type)
    {
        MeshRenderer meshRenderer = fruit.GetComponentInChildren<MeshRenderer>();
        if (meshRenderer == null)
        {
            Debug.LogWarning($"{LogPrefix} '{fruit.name}' has no MeshRenderer in hierarchy — color skipped.");
            return;
        }

        Color color = GetColorForType(type);
        var   block = new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);
        block.SetColor(BaseColorID, color);
        meshRenderer.SetPropertyBlock(block);
    }

    private Color GetColorForType(FruitType type)
    {
        foreach (FruitTypeColor entry in _fruitTypeColors)
        {
            if (entry.type == type)
                return entry.color;
        }

        Debug.LogWarning($"{LogPrefix} No color configured for '{type}'. Using white.");
        return Color.white;
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    private static FruitTypeColor[] DefaultColors() => new[]
    {
        new FruitTypeColor { type = FruitType.Tomato,    color = new Color(0.90f, 0.18f, 0.18f) },
        new FruitTypeColor { type = FruitType.Frog,      color = new Color(0.22f, 0.75f, 0.25f) },
        new FruitTypeColor { type = FruitType.Watermelon, color = new Color(0.10f, 0.55f, 0.20f) },
        new FruitTypeColor { type = FruitType.Ladybug,   color = new Color(0.85f, 0.10f, 0.45f) },
    };

    // ── Validation ───────────────────────────────────────────────────────────

    private bool ValidateConfig()
    {
        if (_fruitPrefab == null)
        {
            Debug.LogError($"{LogPrefix} Fruit prefab is not assigned.", this);
            return false;
        }

        if (_fruitTypeColors == null || _fruitTypeColors.Length == 0)
        {
            Debug.LogError($"{LogPrefix} No fruit type colors configured.", this);
            return false;
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
        Vector3 volumeSize   = new Vector3(_spawnAreaSize.x, _spawnHeightVariance, _spawnAreaSize.y);
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
            Vector3 bottom = new Vector3(origin.x + c.x, origin.y + _spawnHeight, origin.z + c.z);
            Vector3 top    = new Vector3(origin.x + c.x, origin.y + _spawnHeight + _spawnHeightVariance, origin.z + c.z);
            Gizmos.DrawLine(bottom, top);
        }

        // No-spawn zone exclusion rect
        if (_noSpawnZoneCenter != null)
        {
            Vector3 nsc = _noSpawnZoneCenter.position;
            Vector3 noSpawnCenter = new Vector3(nsc.x, origin.y + _spawnHeight + _spawnHeightVariance * 0.5f, nsc.z);
            Vector3 noSpawnSize   = new Vector3(_noSpawnZoneSize.x, _spawnHeightVariance, _noSpawnZoneSize.y);

            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.18f);
            Gizmos.DrawCube(noSpawnCenter, noSpawnSize);

            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(noSpawnCenter, noSpawnSize);
        }
    }
#endif
}
