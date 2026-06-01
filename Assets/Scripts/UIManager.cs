using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Manages the countdown timer UI and end-game panels.
/// Counts down from a configurable start time, flashes red during the
/// final warning period, and stops cleanly at zero.
/// Shows <see cref="levelUpWindow"/> when all objects are matched before time runs out,
/// or <see cref="timeIsUpWindow"/> when the countdown reaches zero with objects remaining.
/// </summary>
public class UIManager : MonoBehaviour
{
    // ── Inspector-exposed fields ─────────────────────────────────────────────

    [Header("Timer UI")]
    [Tooltip("Drag your timer Text UI object here.")]
    public Text timerText;

    [Header("End-Game Windows")]
    [Tooltip("Panel shown when the player matches all objects before time runs out.")]
    [SerializeField] private GameObject levelUpWindow;

    [Tooltip("Text label inside the Level Up window that displays the completed level, e.g. 'Level 1'.")]
    [SerializeField] private Text levelText;

    [Tooltip("Panel shown when time runs out and objects are still remaining.")]
    [SerializeField] private GameObject timeIsUpWindow;

    [Tooltip("Panel shown when the 7-slot tray fills up without a match-3 removal.")]
    [SerializeField] private GameObject failWindow;

    [Header("Level Progression")]
    [Tooltip("Reference to the LevelObjectiveManager. Auto-resolved in Start if left empty.")]
    [SerializeField] private LevelObjectiveManager _levelObjectiveManager;

    [Header("Timer Settings")]
    [Tooltip("Starting minutes for the countdown.")]
    public int startMinutes = 3;

    [Tooltip("Starting seconds for the countdown.")]
    public int startSeconds = 30;

    [Tooltip("Remaining seconds at which the warning flash begins.")]
    public int warningThresholdSeconds = 30;

    [Header("Flash Settings")]
    [Tooltip("How long one full red-to-white fade cycle takes, in seconds.")]
    public float flashCycleDuration = 1f;

    [Header("Booster Settings")]
    [Tooltip("Default duration in seconds the timer is frozen by the timer-freeze booster.")]
    [SerializeField] private float _defaultTimerPauseDuration = 5f;

    // ── Private state ────────────────────────────────────────────────────────

    /// Total remaining time in seconds.
    private float remainingTime;

    /// True while the timer is actively counting down.
    private bool isRunning;

    /// Set to true when the player wins before time is up, to skip the time-is-up panel.
    private bool isGameOver;

    /// True while the timer is frozen by the timer-freeze booster.
    private bool _isTimerPausedByBooster;

    /// Cached reference to the active booster-pause coroutine so it can be restarted or cancelled.
    private Coroutine _timerPauseCoroutine;

    /// Cached reference to the countdown coroutine so it can be stopped explicitly during a booster pause.
    private Coroutine _countdownCoroutine;

    // ── Public state ─────────────────────────────────────────────────────────

    /// <summary>
    /// True once any end-game condition has been triggered (time-up, win, or tray-full fail).
    /// Queried by <see cref="DraggableObject"/> to block all further tap and fly-in input
    /// even if <see cref="DraggableObject.IsInputBlocked"/> was not yet set on that instance.
    /// </summary>
    public bool IsGameOver => isGameOver;

    /// Cached reference to the flash coroutine so it can be stopped cleanly.
    private Coroutine flashCoroutine;

    // ── Colors ───────────────────────────────────────────────────────────────

    private static readonly Color ColorWhite = Color.white;
    private static readonly Color ColorRed   = Color.red;

    // ────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        Application.targetFrameRate = 60;
        remainingTime = startMinutes * 60f + startSeconds;

        if (_levelObjectiveManager == null)
            _levelObjectiveManager = FindFirstObjectByType<LevelObjectiveManager>();

        // Hide end-game windows at start.
        SetWindowActive(levelUpWindow,  false);
        SetWindowActive(timeIsUpWindow, false);
        SetWindowActive(failWindow,     false);

        isRunning  = true;
        isGameOver = false;

        // Kick off the per-second tick.
        _countdownCoroutine = StartCoroutine(CountdownCoroutine());
    }

    // ────────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="LevelObjectiveManager"/> when the objective is completed.
    /// Displays the completed level number, advances the saved level via
    /// <see cref="LevelObjectiveManager.AdvanceToNextLevel"/>, stops the timer,
    /// and shows the Level Up window.
    /// </summary>
    public void OnAllObjectsMatched()
    {
        if (isGameOver) return;

        isGameOver = true;
        isRunning  = false;

        StopBoosterPause();
        StopFlash();
        UpdateTimerDisplay();

        if (levelText != null && _levelObjectiveManager != null)
            levelText.text = "Level " + _levelObjectiveManager.CurrentLevel;

        _levelObjectiveManager?.AdvanceToNextLevel();

        SetWindowActive(levelUpWindow, true);

        Debug.Log("[UIManager] All objects matched — showing LevelUpWindow.");
    }

    /// <summary>
    /// Called when the countdown reaches zero. Stops gameplay, blocks all
    /// object input and tray placement, and shows the Time Is Up window.
    /// </summary>
    public void OnTimeIsUp()
    {
        if (isGameOver) return;

        isGameOver = true;
        isRunning  = false;

        StopBoosterPause();
        StopFlash();
        UpdateTimerDisplay();

        // Block clicks on every DraggableObject still on the board.
        DraggableObject[] all = FindObjectsByType<DraggableObject>(FindObjectsSortMode.None);
        foreach (DraggableObject obj in all)
            obj.IsInputBlocked = true;

        // Block the tray from accepting new objects.
        TrayController tray = FindFirstObjectByType<TrayController>();
        if (tray != null)
            tray.SetFailed();

        SetWindowActive(timeIsUpWindow, true);

        Debug.Log("[UIManager] Time is up — showing TimeIsUpWindow.");
    }

    /// <summary>
    /// Called by <see cref="TrayController"/> when the tray fills up without a
    /// match-3 removal. Stops the timer, blocks all object input, and shows
    /// the Fail window.
    /// </summary>
    public void OnTrayFull()
    {
        if (isGameOver) return;

        isGameOver = true;
        isRunning  = false;

        StopBoosterPause();
        StopFlash();
        UpdateTimerDisplay();

        // Block clicks on every DraggableObject still on the board.
        DraggableObject[] all = FindObjectsByType<DraggableObject>(FindObjectsSortMode.None);
        foreach (DraggableObject obj in all)
            obj.IsInputBlocked = true;

        SetWindowActive(failWindow, true);

        Debug.Log("[UIManager] Tray is full — showing FailWindow.");
    }

    /// <summary>
    /// Stops the countdown immediately without triggering any end-game window.
    /// Useful for pausing or resetting the game state externally.
    /// </summary>
    public void StopTimer()
    {
        isRunning = false;

        if (_countdownCoroutine != null)
        {
            StopCoroutine(_countdownCoroutine);
            _countdownCoroutine = null;
        }

        StopFlash();
    }


    /// <summary>
    /// Freezes the countdown timer for <paramref name="duration"/> seconds.
    /// If a booster pause is already active, it is restarted from the beginning.
    /// Does nothing if the game is already over.
    /// The time-is-up window is suppressed for the duration of the pause.
    /// </summary>
    /// <param name="duration">Seconds to freeze the timer. Pass <see cref="_defaultTimerPauseDuration"/> for the default.</param>
    public void PauseTimerForSeconds(float duration)
    {
        if (isGameOver) return;

        // Restart an already-active booster pause from scratch.
        if (_timerPauseCoroutine != null)
        {
            StopCoroutine(_timerPauseCoroutine);
            _timerPauseCoroutine = null;
        }

        // Stop the countdown coroutine explicitly so no tick fires and no end-game
        // path is triggered while the timer is frozen.
        if (_countdownCoroutine != null)
        {
            StopCoroutine(_countdownCoroutine);
            _countdownCoroutine = null;
        }

        isRunning = false;
        _isTimerPausedByBooster = true;

        _timerPauseCoroutine = StartCoroutine(TimerPauseCoroutine(duration));

        Debug.Log($"[UIManager] Timer frozen for {duration}s.");
    }

    /// <summary>
    /// Reloads the active scene, resetting all gameplay state —
    /// ObjectSpawner re-spawns objects and the timer restarts from scratch.
    /// Hooked to the Continue button in both LevelUpWindow and TimeIsUpWindow.
    /// </summary>
    public void RestartGame()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Countdown logic
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ticks the timer down by one second each interval.
    /// Triggers the warning flash when the threshold is crossed,
    /// and stops at zero — showing the Time Is Up window when the game is not
    /// already won.
    /// </summary>
    private IEnumerator CountdownCoroutine()
    {
        while (isRunning)
        {
            // Update the display before waiting, so "3:30" is shown immediately.
            UpdateTimerDisplay();

            // Check whether we just crossed into the warning zone.
            if (remainingTime <= warningThresholdSeconds && flashCoroutine == null)
                flashCoroutine = StartCoroutine(FlashCoroutine());

            // Stop if time is up.
            if (remainingTime <= 0f)
            {
                isRunning = false;
                StopFlash();
                break;
            }

            // Wait exactly one second before the next tick.
            yield return new WaitForSeconds(1f);

            remainingTime -= 1f;

            // Clamp so we never display negative time.
            if (remainingTime < 0f)
                remainingTime = 0f;
        }

        // Final display pass to make sure "0:00" is shown.
        UpdateTimerDisplay();

        // Only trigger the time-up sequence if the game is not already over.
        if (!isGameOver)
            OnTimeIsUp();
    }

    /// <summary>
    /// Formats remaining time as M:SS and writes it to the Text component.
    /// </summary>
    private void UpdateTimerDisplay()
    {
        if (timerText == null) return;

        int totalSeconds = Mathf.CeilToInt(remainingTime);

        // CeilToInt can push a "0" reading to 1 on the last tick;
        // clamp to avoid displaying "0:01" when truly at zero.
        if (totalSeconds < 0) totalSeconds = 0;

        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;

        // Format: M:SS  (e.g. "3:30", "0:09")
        timerText.text = $"{minutes}:{seconds:00}";
    }

    // ────────────────────────────────────────────────────────────────────────
    // Flash / warning animation
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Continuously fades the timer color from red back to white in a loop.
    /// The text snaps to red at the start of each cycle, then smoothly
    /// transitions back to white over <see cref="flashCycleDuration"/> seconds.
    /// </summary>
    private IEnumerator FlashCoroutine()
    {
        while (true)
        {
            float elapsed = 0f;

            // Each cycle: start at red, lerp smoothly back to white.
            while (elapsed < flashCycleDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / flashCycleDuration);

              //  if (timerText != null)
                 //   timerText.color = Color.Lerp(ColorRed, ColorWhite, t);

                yield return null;
            }

            // Ensure we land exactly on white before the next cycle snaps to red.
          //  if (timerText != null)
              //  timerText.color = ColorRed; // snap to red for the next cycle start
        }
    }

    /// <summary>
    /// Stops the flash coroutine and resets the timer text to white.
    /// </summary>
    private void StopFlash()
    {
        if (flashCoroutine != null)
        {
            StopCoroutine(flashCoroutine);
            flashCoroutine = null;
        }

       // if (timerText != null)
          //  timerText.color = ColorWhite;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Booster logic
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for <paramref name="duration"/> seconds, then resumes the countdown
    /// — but only if no end-game condition has been reached in the meantime.
    /// </summary>
    private IEnumerator TimerPauseCoroutine(float duration)
    {
        yield return new WaitForSeconds(duration);

        _isTimerPausedByBooster = false;
        _timerPauseCoroutine = null;

        if (!isGameOver)
        {
            isRunning = true;
            _countdownCoroutine = StartCoroutine(CountdownCoroutine());
            Debug.Log("[UIManager] Timer resumed after freeze.");
        }
    }

    /// <summary>
    /// Cancels any active booster-pause coroutine and resets the booster state.
    /// </summary>
    private void StopBoosterPause()
    {
        if (_timerPauseCoroutine != null)
        {
            StopCoroutine(_timerPauseCoroutine);
            _timerPauseCoroutine = null;
        }

        _isTimerPausedByBooster = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void SetWindowActive(GameObject window, bool active)
    {
        if (window != null)
            window.SetActive(active);
    }
}
