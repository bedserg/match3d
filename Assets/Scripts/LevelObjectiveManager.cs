using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Defines a single level's objective: which object type to collect and how many.
/// Populate the <see cref="LevelObjectiveManager._levelRequirements"/> array in the Inspector.
/// </summary>
[System.Serializable]
public class LevelRequirement
{
    public int        levelNumber;
    public ObjectType targetObjectType;
    public int        requiredCount;
}

/// <summary>
/// Manages one objective per level. Loads the correct <see cref="LevelRequirement"/>
/// for the active level from PlayerPrefs, shows the matching icon, counts objects as
/// they are placed into the tray, and triggers the win screen via <see cref="UIManager"/>
/// when done. Call <see cref="RegisterPlacedObject"/> from <see cref="TrayController"/>
/// each time an object is successfully locked into a tray slot.
/// </summary>
public class LevelObjectiveManager : MonoBehaviour
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string CurrentLevelKey = "CurrentLevel";

    // ── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("Fallback level number used only when no saved level exists in PlayerPrefs.")]
    [SerializeField] private int _currentLevel = 1;

    [Tooltip("One entry per level. Each entry maps a level number to an object type and required count.")]
    [SerializeField] private LevelRequirement[] _levelRequirements;

    [Tooltip("Legacy UI Text label showing how many more objects are still needed.")]
    [SerializeField] private Text _countText;

    [Tooltip("Icon GameObject for the Strawberry (PastelStrawberry) objective.")]
    [SerializeField] private GameObject _strawberryIcon;

    [Tooltip("Icon GameObject for the Ice Coffee (PastelIcedCoffee) objective.")]
    [SerializeField] private GameObject _iceCoffeeIcon;

    [Tooltip("Reference to the UIManager. Auto-resolved in Start if left empty.")]
    [SerializeField] private UIManager _uiManager;

    // ── Private state ────────────────────────────────────────────────────────

    private LevelRequirement _activeRequirement;
    private int              _currentCount;
    private bool             _isComplete;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Start()
    {
        if (_uiManager == null)
            _uiManager = FindFirstObjectByType<UIManager>();

        _currentLevel = PlayerPrefs.GetInt(CurrentLevelKey, _currentLevel);
        LoadRequirementForLevel(_currentLevel);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>The level number currently active.</summary>
    public int CurrentLevel => _currentLevel;

    /// <summary>
    /// The <see cref="ObjectType"/> the player must collect for the current level,
    /// or <c>null</c> when no requirement has been loaded yet.
    /// </summary>
    public ObjectType? CurrentObjectiveType =>
        _activeRequirement != null ? _activeRequirement.targetObjectType : (ObjectType?)null;

    /// <summary>
    /// Returns the <see cref="ObjectType"/> that still needs progress toward the current
    /// level objective. Use this to drive boosters that need to know which type to collect.
    /// </summary>
    /// <param name="neededType">
    /// The target <see cref="ObjectType"/> when the method returns <c>true</c>;
    /// undefined when the method returns <c>false</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> when an objective is loaded and not yet complete;
    /// <c>false</c> when there is no active objective or progress is already complete.
    /// </returns>
    public bool TryGetCurrentNeededObjectType(out ObjectType neededType)
    {
        neededType = default;

        if (_activeRequirement == null)
        {
            Debug.Log("[LevelObjectiveManager] TryGetCurrentNeededObjectType — no active requirement.");
            return false;
        }

        if (_isComplete)
        {
            Debug.Log("[LevelObjectiveManager] TryGetCurrentNeededObjectType — objective already complete.");
            return false;
        }

        neededType = _activeRequirement.targetObjectType;
        return true;
    }

    /// <summary>
    /// Increments the saved level number and persists it so the next scene load
    /// starts the correct objective. Called by <see cref="UIManager"/> after a win.
    /// </summary>
    public void AdvanceToNextLevel()
    {
        _currentLevel++;
        PlayerPrefs.SetInt(CurrentLevelKey, _currentLevel);
        PlayerPrefs.Save();
        Debug.Log($"[LevelObjectiveManager] Advanced to level {_currentLevel} — saved to PlayerPrefs.");
    }

    /// <summary>
    /// Switches to a specific level's objective at runtime and resets all progress.
    /// </summary>
    public void SetLevel(int levelNumber)
    {
        _currentLevel = levelNumber;
        LoadRequirementForLevel(_currentLevel);
    }

    /// <summary>
    /// Called by <see cref="TrayController"/> each time an object is successfully locked
    /// into a tray slot. Adds 1 progress when <paramref name="objectType"/> matches the
    /// active objective, updates the remaining count label, and triggers the win screen
    /// when the required count is reached.
    /// </summary>
    /// <param name="objectType">The type of the object that just entered the tray.</param>
    public bool RegisterPlacedObject(ObjectType objectType)
    {
        if (_isComplete)
            return false;

        if (_activeRequirement == null)
            return false;

        if (objectType != _activeRequirement.targetObjectType)
            return false;

        int countBefore = _currentCount;
        _currentCount++;
        _currentCount = Mathf.Min(_currentCount, _activeRequirement.requiredCount);

        if (_currentCount == countBefore)
            return false;

        int remaining = _activeRequirement.requiredCount - _currentCount;

        UpdateCountText(remaining);

        Debug.Log($"[LevelObjectiveManager] {_activeRequirement.targetObjectType} progress: " +
                  $"{_currentCount}/{_activeRequirement.requiredCount}, remaining: {remaining}");

        if (remaining == 0)
        {
            _isComplete = true;
            Debug.Log("[LevelObjectiveManager] Objective complete.");
            _uiManager?.OnAllObjectsMatched();
        }

        return true;
    }

    /// <summary>
    /// Reverses a single <see cref="RegisterPlacedObject"/> call when <paramref name="objectType"/>
    /// matches the active objective. Called by <see cref="TrayController"/> when booster 1 returns
    /// an object from the tray back to the gameplay area.
    /// <para>No-ops when:
    /// <list type="bullet">
    ///   <item>The objective is already complete — the win state is never reversed.</item>
    ///   <item>There is no active requirement.</item>
    ///   <item><paramref name="objectType"/> does not match the active objective type.</item>
    ///   <item><see cref="_currentCount"/> is already 0.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="objectType">The type of the object being returned to the board.</param>
    public void UnregisterPlacedObject(ObjectType objectType)
    {
        if (_isComplete)
            return;

        if (_activeRequirement == null)
            return;

        if (objectType != _activeRequirement.targetObjectType)
            return;

        if (_currentCount <= 0)
            return;

        _currentCount--;
        _currentCount = Mathf.Max(_currentCount, 0);

        int remaining = _activeRequirement.requiredCount - _currentCount;
        UpdateCountText(remaining);

        Debug.Log($"[LevelObjectiveManager] {_activeRequirement.targetObjectType} count unregistered: " +
                  $"{_currentCount}/{_activeRequirement.requiredCount}, remaining: {remaining}");
    }

    // ── Debug helpers ─────────────────────────────────────────────────────────

    /// <summary>Resets the saved level back to 1. Available via right-click on the component.</summary>
    [ContextMenu("Reset Saved Level")]
    private void ResetSavedLevel()
    {
        PlayerPrefs.DeleteKey(CurrentLevelKey);
        PlayerPrefs.Save();
        Debug.Log("[LevelObjectiveManager] Saved level reset to default.");
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the <see cref="LevelRequirement"/> for <paramref name="levelNumber"/>,
    /// resets progress, and refreshes the UI to reflect the new objective.
    /// </summary>
    private void LoadRequirementForLevel(int levelNumber)
    {
        _activeRequirement = null;

        if (_levelRequirements != null)
        {
            foreach (LevelRequirement req in _levelRequirements)
            {
                if (req.levelNumber == levelNumber)
                {
                    _activeRequirement = req;
                    break;
                }
            }
        }

        if (_activeRequirement == null)
        {
            Debug.LogWarning($"[LevelObjectiveManager] No requirement found for level {levelNumber}.");
            return;
        }

        _currentCount = 0;
        _isComplete   = false;

        UpdateCountText(_activeRequirement.requiredCount);

        // Hide both icons, then show only the one matching the active objective.
        if (_strawberryIcon != null) _strawberryIcon.SetActive(false);
        if (_iceCoffeeIcon  != null) _iceCoffeeIcon.SetActive(false);

        if (_activeRequirement.targetObjectType == ObjectType.PastelStrawberry)
        {
            if (_strawberryIcon != null) _strawberryIcon.SetActive(true);
        }
        else if (_activeRequirement.targetObjectType == ObjectType.PastelIcedCoffee)
        {
            if (_iceCoffeeIcon != null) _iceCoffeeIcon.SetActive(true);
        }

        Debug.Log($"[LevelObjectiveManager] Loaded Level {levelNumber} objective: " +
                  $"{_activeRequirement.targetObjectType} x {_activeRequirement.requiredCount}");
    }

    private void UpdateCountText(int value)
    {
        if (_countText != null)
            _countText.text = value.ToString();
    }
}
