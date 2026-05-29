using UnityEngine;
using UnityEngine.UI;

[System.Serializable]
public class ObjectiveSlotUI
{
    [Tooltip("The whole objective UI box. Example: ObjectiveSlot_1.")]
    public GameObject root;

    [Tooltip("The Image component inside the slot. Example: levelObjectiveIcon / imgIcon.")]
    public Image iconImage;

    [Tooltip("The Text component showing remaining amount. Example: txtAmount.")]
    public Text amountText;
}

[System.Serializable]
public class ObjectiveRequirement
{
    public ObjectType targetObjectType;

    [Tooltip("Sprite shown in the objective UI slot.")]
    public Sprite targetIcon;

    [Tooltip("Must usually be 3, 6, 9, 12... because your game works with match-3.")]
    public int requiredCount = 3;
}

[System.Serializable]
public class LevelRequirement
{
    public int levelNumber;

    [Tooltip("Add 1, 2, or 3 objectives for this level.")]
    public ObjectiveRequirement[] objectives;
}

public class LevelObjectiveManager : MonoBehaviour
{
    private const string CurrentLevelKey = "CurrentLevel";

    [Header("Level")]
    [Tooltip("Fallback level number used only when no saved level exists in PlayerPrefs.")]
    [SerializeField] private int _currentLevel = 1;

    [Tooltip("One entry per level. Each level can have 1, 2, or 3 objectives.")]
    [SerializeField] private LevelRequirement[] _levelRequirements;

    [Header("Objective UI Slots")]
    [Tooltip("Your 3 UI objective boxes. Each one has root, icon image, and amount text.")]
    [SerializeField] private ObjectiveSlotUI[] _objectiveSlots;

    [Header("References")]
    [Tooltip("Reference to the UIManager. Auto-resolved in Start if left empty.")]
    [SerializeField] private UIManager _uiManager;

    private ObjectiveRequirement[] _activeObjectives;
    private int[] _currentCounts;
    private bool _isComplete;

    private void Start()
    {
        if (_uiManager == null)
            _uiManager = FindFirstObjectByType<UIManager>();

        _currentLevel = PlayerPrefs.GetInt(CurrentLevelKey, _currentLevel);

        LoadRequirementForLevel(_currentLevel);
    }

    public int CurrentLevel => _currentLevel;

    public ObjectType? CurrentObjectiveType
    {
        get
        {
            if (_activeObjectives == null || _activeObjectives.Length == 0)
                return null;

            return _activeObjectives[0].targetObjectType;
        }
    }

    public bool TryGetCurrentNeededObjectType(out ObjectType neededType)
    {
        neededType = default;

        if (_activeObjectives == null || _currentCounts == null)
        {
            Debug.Log("[LevelObjectiveManager] TryGetCurrentNeededObjectType — no active objectives.");
            return false;
        }

        if (_isComplete)
        {
            Debug.Log("[LevelObjectiveManager] TryGetCurrentNeededObjectType — all objectives complete.");
            return false;
        }

        for (int i = 0; i < _activeObjectives.Length; i++)
        {
            ObjectiveRequirement objective = _activeObjectives[i];
            if (objective == null) continue;

            if (_currentCounts[i] < objective.requiredCount)
            {
                neededType = objective.targetObjectType;
                return true;
            }
        }

        return false;
    }

    public void AdvanceToNextLevel()
    {
        _currentLevel++;
        PlayerPrefs.SetInt(CurrentLevelKey, _currentLevel);
        PlayerPrefs.Save();

        Debug.Log($"[LevelObjectiveManager] Advanced to level {_currentLevel} — saved to PlayerPrefs.");
    }

    public void SetLevel(int levelNumber)
    {
        _currentLevel = levelNumber;
        PlayerPrefs.SetInt(CurrentLevelKey, _currentLevel);
        PlayerPrefs.Save();

        LoadRequirementForLevel(_currentLevel);
    }

    public bool RegisterPlacedObject(ObjectType objectType)
    {
        if (_isComplete)
            return false;

        if (_activeObjectives == null || _currentCounts == null)
            return false;

        for (int i = 0; i < _activeObjectives.Length; i++)
        {
            ObjectiveRequirement objective = _activeObjectives[i];
            if (objective == null) continue;

            if (objective.targetObjectType != objectType)
                continue;

            if (_currentCounts[i] >= objective.requiredCount)
                return false;

            _currentCounts[i]++;

            int remaining = objective.requiredCount - _currentCounts[i];
            UpdateObjectiveSlotAmount(i, remaining);

            Debug.Log($"[LevelObjectiveManager] {objective.targetObjectType} progress: " +
                      $"{_currentCounts[i]}/{objective.requiredCount}, remaining: {remaining}");

            CheckAllObjectivesComplete();

            return true;
        }

        return false;
    }

    public void UnregisterPlacedObject(ObjectType objectType)
    {
        if (_isComplete)
            return;

        if (_activeObjectives == null || _currentCounts == null)
            return;

        for (int i = 0; i < _activeObjectives.Length; i++)
        {
            ObjectiveRequirement objective = _activeObjectives[i];
            if (objective == null) continue;

            if (objective.targetObjectType != objectType)
                continue;

            if (_currentCounts[i] <= 0)
                return;

            _currentCounts[i]--;

            int remaining = objective.requiredCount - _currentCounts[i];
            UpdateObjectiveSlotAmount(i, remaining);

            Debug.Log($"[LevelObjectiveManager] {objective.targetObjectType} count unregistered: " +
                      $"{_currentCounts[i]}/{objective.requiredCount}, remaining: {remaining}");

            return;
        }
    }

    private void LoadRequirementForLevel(int levelNumber)
    {
        LevelRequirement levelRequirement = FindLevelRequirement(levelNumber);

        if (levelRequirement == null)
        {
            Debug.LogWarning($"[LevelObjectiveManager] No requirement found for level {levelNumber}.");
            HideAllObjectiveSlots();
            return;
        }

        if (levelRequirement.objectives == null || levelRequirement.objectives.Length == 0)
        {
            Debug.LogWarning($"[LevelObjectiveManager] Level {levelNumber} has no objectives.");
            HideAllObjectiveSlots();
            return;
        }

        int slotCount = _objectiveSlots != null ? _objectiveSlots.Length : 0;
        int objectiveCount = Mathf.Min(levelRequirement.objectives.Length, slotCount);

        _activeObjectives = new ObjectiveRequirement[objectiveCount];
        _currentCounts = new int[objectiveCount];
        _isComplete = false;

        HideAllObjectiveSlots();

        for (int i = 0; i < objectiveCount; i++)
        {
            ObjectiveRequirement objective = levelRequirement.objectives[i];
            _activeObjectives[i] = objective;
            _currentCounts[i] = 0;

            ValidateObjective(levelNumber, objective);
            ShowObjectiveInSlot(i, objective);
        }

        Debug.Log($"[LevelObjectiveManager] Loaded Level {levelNumber} with {objectiveCount} objective(s).");
    }

    private LevelRequirement FindLevelRequirement(int levelNumber)
    {
        if (_levelRequirements == null)
            return null;

        foreach (LevelRequirement requirement in _levelRequirements)
        {
            if (requirement != null && requirement.levelNumber == levelNumber)
                return requirement;
        }

        return null;
    }

    private void ShowObjectiveInSlot(int slotIndex, ObjectiveRequirement objective)
    {
        if (_objectiveSlots == null || slotIndex < 0 || slotIndex >= _objectiveSlots.Length)
            return;

        ObjectiveSlotUI slot = _objectiveSlots[slotIndex];
        if (slot == null)
            return;

        if (slot.root != null)
            slot.root.SetActive(true);

        if (slot.iconImage != null)
            slot.iconImage.sprite = objective.targetIcon;

        if (slot.amountText != null)
            slot.amountText.text = objective.requiredCount.ToString();
    }

    private void UpdateObjectiveSlotAmount(int slotIndex, int remaining)
    {
        if (_objectiveSlots == null || slotIndex < 0 || slotIndex >= _objectiveSlots.Length)
            return;

        ObjectiveSlotUI slot = _objectiveSlots[slotIndex];
        if (slot == null || slot.amountText == null)
            return;

        slot.amountText.text = remaining.ToString();
    }

    private void HideAllObjectiveSlots()
    {
        if (_objectiveSlots == null)
            return;

        foreach (ObjectiveSlotUI slot in _objectiveSlots)
        {
            if (slot != null && slot.root != null)
                slot.root.SetActive(false);
        }
    }

    private void CheckAllObjectivesComplete()
    {
        if (_activeObjectives == null || _currentCounts == null)
            return;

        for (int i = 0; i < _activeObjectives.Length; i++)
        {
            ObjectiveRequirement objective = _activeObjectives[i];
            if (objective == null)
                return;

            if (_currentCounts[i] < objective.requiredCount)
                return;
        }

        _isComplete = true;
        Debug.Log("[LevelObjectiveManager] All objectives complete.");
        _uiManager?.OnAllObjectsMatched();
    }

    private void ValidateObjective(int levelNumber, ObjectiveRequirement objective)
    {
        if (objective == null)
            return;

        if (objective.requiredCount <= 0)
        {
            Debug.LogWarning($"[LevelObjectiveManager] Level {levelNumber} has objective with 0 or negative required count.");
            return;
        }

        if (objective.requiredCount % 3 != 0)
        {
            Debug.LogWarning($"[LevelObjectiveManager] Level {levelNumber} objective {objective.targetObjectType} " +
                             $"has required count {objective.requiredCount}. It should usually be 3, 6, 9, 12...");
        }

        int sceneCount = CountObjectsInScene(objective.targetObjectType);
        if (sceneCount < objective.requiredCount)
        {
            Debug.LogWarning($"[LevelObjectiveManager] Level {levelNumber} wants {objective.requiredCount}x " +
                             $"{objective.targetObjectType}, but scene has only {sceneCount}.");
        }
    }

    private int CountObjectsInScene(ObjectType objectType)
    {
        DraggableObject[] sceneObjects = FindObjectsByType<DraggableObject>(FindObjectsSortMode.None);

        int count = 0;

        foreach (DraggableObject obj in sceneObjects)
        {
            if (obj != null && obj.ObjectType == objectType)
                count++;
        }

        return count;
    }

    [ContextMenu("Reset Saved Level")]
    private void ResetSavedLevel()
    {
        PlayerPrefs.DeleteKey(CurrentLevelKey);
        PlayerPrefs.Save();

        Debug.Log("[LevelObjectiveManager] Saved level reset to default.");
    }
}