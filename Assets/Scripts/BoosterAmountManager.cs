using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Manages the use count and UI label for a single booster button.
///
/// While <see cref="_currentAmount"/> is greater than 0, the label shows the
/// numeric count ("3", "2", "1"). When the count reaches 0 the label switches
/// to "+" to signal that the player can buy more.
///
/// Wire <see cref="TryConsumeBooster"/> to the booster button's OnClick event,
/// and <see cref="OpenShopFromPlusButton"/> to a separate "+" button if needed.
/// </summary>
public class BoosterAmountManager : MonoBehaviour
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string LogPrefix   = "[BoosterAmountManager]";
    private const string PlusLabel   = "+";

    // ── Inspector ────────────────────────────────────────────────────────────

    [Tooltip("Text label that displays the remaining booster count or '+' when empty.")]
    [SerializeField] private Text _amountText;

    [Tooltip("Window opened when the player taps the '+' label to purchase more boosters.")]
    [SerializeField] private GameObject _shopWindow;

    [Tooltip("Window shown when the player tries to use the booster but the count is 0.")]
    [SerializeField] private GameObject _boosterFinishedWindow;

    [Tooltip("Number of uses this booster starts with.")]
    [SerializeField] private int _startingAmount = 3;

    // ── Private state ────────────────────────────────────────────────────────

    private int _currentAmount;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Start()
    {
        _currentAmount = _startingAmount;
        UpdateAmountText();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>The number of booster uses currently remaining.</summary>
    public int CurrentAmount => _currentAmount;

    /// <summary>
    /// Attempts to consume one booster use.
    /// Decrements the count and refreshes the label when uses remain.
    /// Opens <see cref="_boosterFinishedWindow"/> and returns <c>false</c> when the count is 0.
    /// </summary>
    /// <returns><c>true</c> when a use was successfully consumed; <c>false</c> otherwise.</returns>
    public bool TryConsumeBooster()
    {
        if (_currentAmount > 0)
        {
            _currentAmount--;
            UpdateAmountText();
            Debug.Log($"{LogPrefix} Booster consumed. Remaining: {_currentAmount}.");
            return true;
        }

        Debug.Log($"{LogPrefix} No uses remaining — opening booster finished window.");
        OpenWindow(_boosterFinishedWindow);
        return false;
    }

    /// <summary>
    /// Opens the shop window when the booster count is 0 (the label shows "+").
    /// No-ops when uses are still available, since the button should behave as a
    /// normal booster button rather than a shop shortcut in that state.
    /// </summary>
    public void OpenShopFromPlusButton()
    {
        if (_currentAmount <= 0)
        {
            Debug.Log($"{LogPrefix} Amount is '+' — opening shop window.");
            OpenWindow(_shopWindow);
        }
        else
        {
            Debug.Log($"{LogPrefix} OpenShopFromPlusButton ignored — booster still has {_currentAmount} use(s) remaining.");
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates <see cref="_amountText"/> to show the numeric count when
    /// uses remain, or "+" when the count has reached 0.
    /// </summary>
    private void UpdateAmountText()
    {
        if (_amountText == null)
            return;

        _amountText.text = _currentAmount > 0 ? _currentAmount.ToString() : PlusLabel;
    }

    /// <summary>
    /// Activates <paramref name="window"/> if it is assigned.
    /// Logs a warning when the reference is missing so the caller's intent is clear.
    /// </summary>
    private void OpenWindow(GameObject window)
    {
        if (window == null)
        {
            Debug.LogWarning($"{LogPrefix} Window reference is not assigned.", this);
            return;
        }

        window.SetActive(true);
    }
}
