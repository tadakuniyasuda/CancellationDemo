using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Demonstrates the difference between SWALLOWING an OperationCanceledException
/// and RE-THROWING it in a chain of UniTask steps.
///
/// Sequence: Step 1 (Connecting) -> Step 2 (Downloading) -> Step 3 (Finishing), 2s each.
/// </summary>
public class CancellationDemo : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button startBuggyButton;
    [SerializeField] private Button startCorrectButton;
    [SerializeField] private Button cancelButton;

    [Header("UI - Extra experiment: catch (Exception)")]
    [SerializeField] private Button startBuggyGeneralButton;
    [SerializeField] private Button startCorrectGeneralButton;

    [Header("Settings")]
    [SerializeField] private int stepDurationMs = 2000;

    [Tooltip("Simulates a real-world API that ignores the token (e.g. a 3rd-party SDK call). " +
             "Turn ON to see Step 3 actually run its full 2 seconds after Cancel in the buggy version.")]
    [SerializeField] private bool step3IgnoresToken = false;

    [Tooltip("Step 2 throws a fake network error after its delay. " +
             "Used to check that the correct catch (Exception) version still handles REAL errors.")]
    [SerializeField] private bool step2ThrowsNetworkError = false;

    private CancellationTokenSource _cts;
    private float _runStartTime;

    // ------------------------------------------------------------------
    // Unity lifecycle
    // ------------------------------------------------------------------

    private void Awake()
    {
        startBuggyButton.onClick.AddListener(() => StartSequence("BUGGY", BuggySequenceAsync));
        startCorrectButton.onClick.AddListener(() => StartSequence("CORRECT", CorrectSequenceAsync));
        cancelButton.onClick.AddListener(OnCancelPressed);

        startBuggyGeneralButton.onClick.AddListener(() => StartSequence("BUGGY-GENERAL", BuggyGeneralSequenceAsync));
        startCorrectGeneralButton.onClick.AddListener(() => StartSequence("CORRECT-GENERAL", CorrectGeneralSequenceAsync));
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ------------------------------------------------------------------
    // Buttons
    // ------------------------------------------------------------------

    private void StartSequence(string label, Func<CancellationToken, UniTask> sequence)
    {
        // Kill any previous run so two sequences never overlap.
        _cts?.Cancel();
        _cts?.Dispose();

        // Linked to the destroy token: leaving Play Mode / destroying the object also cancels.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
        _runStartTime = Time.realtimeSinceStartup;

        RunAsync(label, sequence, _cts.Token).Forget();
    }

    private void OnCancelPressed()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;

        Log(">>> CANCEL BUTTON PRESSED <<<");
        _cts.Cancel();
    }

    // ------------------------------------------------------------------
    // Top-level runner: the ONE place that decides "completed" vs "aborted"
    // ------------------------------------------------------------------

    private async UniTaskVoid RunAsync(string label, Func<CancellationToken, UniTask> sequence, CancellationToken ct)
    {
        Log($"===== {label} sequence START =====");
        try
        {
            await sequence(ct);
            // If we reach this line, the caller believes EVERYTHING succeeded.
            Log($"===== {label} sequence COMPLETED (reported as success) =====");
        }
        catch (OperationCanceledException)
        {
            LogWarning($"===== {label} sequence ABORTED =====");
        }
    }

    // ------------------------------------------------------------------
    // 1. BUGGY version: catches and swallows the cancellation
    // ------------------------------------------------------------------

    private async UniTask BuggySequenceAsync(CancellationToken ct)
    {
        await BuggyStepAsync(1, "Connecting", ct);
        await BuggyStepAsync(2, "Downloading", ct);
        await BuggyStepAsync(3, "Finishing", ct);
    }

    private async UniTask BuggyStepAsync(int step, string name, CancellationToken ct)
    {
        try
        {
            Log($"Step {step}: {name}... started");
            await SimulateWorkAsync(step, ct);
            Log($"Step {step}: {name} done");
        }
        catch (OperationCanceledException)
        {
            LogWarning($"Step {step}: cancelled -> SWALLOWED (method returns normally)");
            // BUG: no re-throw. To the caller, this step "succeeded",
            // so the caller happily moves on to the next step.
        }
    }

    // ------------------------------------------------------------------
    // 2. CORRECT version: catches, cleans up, then re-throws
    // ------------------------------------------------------------------

    private async UniTask CorrectSequenceAsync(CancellationToken ct)
    {
        await CorrectStepAsync(1, "Connecting", ct);
        await CorrectStepAsync(2, "Downloading", ct);
        await CorrectStepAsync(3, "Finishing", ct);
    }

    private async UniTask CorrectStepAsync(int step, string name, CancellationToken ct)
    {
        try
        {
            Log($"Step {step}: {name}... started");
            await SimulateWorkAsync(step, ct);
            Log($"Step {step}: {name} done");
        }
        catch (OperationCanceledException)
        {
            LogWarning($"Step {step}: cancelled -> cleaning up, then RE-THROWING");
            // Step-local cleanup would go here (close socket, delete temp file, etc.)

            // Only re-throw if it was OUR token that was cancelled.
            // The exception unwinds through CorrectSequenceAsync, skipping Steps 2 and 3,
            // and lands in RunAsync's catch block.
            if (ct.IsCancellationRequested) throw;
        }
    }

    // ------------------------------------------------------------------
    // 3. EXTRA - BUGGY general catch: catch (Exception) treats cancel as an error
    //    (the most common real-world form of this bug)
    // ------------------------------------------------------------------

    private async UniTask BuggyGeneralSequenceAsync(CancellationToken ct)
    {
        await BuggyGeneralStepAsync(1, "Connecting", ct);
        await BuggyGeneralStepAsync(2, "Downloading", ct);
        await BuggyGeneralStepAsync(3, "Finishing", ct);
    }

    private async UniTask BuggyGeneralStepAsync(int step, string name, CancellationToken ct)
    {
        try
        {
            Log($"Step {step}: {name}... started");
            await SimulateWorkAsync(step, ct);
            Log($"Step {step}: {name} done");
        }
        catch (Exception ex)
        {
            // BUG: cancellation is caught here too, reported as a failure,
            // and the chain continues.
            LogError($"Step {step}: FAILED with {ex.GetType().Name} -> handled as error, continuing");
        }
    }

    // ------------------------------------------------------------------
    // 4. EXTRA - CORRECT general catch: let cancellation through, handle real errors
    // ------------------------------------------------------------------

    private async UniTask CorrectGeneralSequenceAsync(CancellationToken ct)
    {
        await CorrectGeneralStepAsync(1, "Connecting", ct);
        await CorrectGeneralStepAsync(2, "Downloading", ct);
        await CorrectGeneralStepAsync(3, "Finishing", ct);
    }

    private async UniTask CorrectGeneralStepAsync(int step, string name, CancellationToken ct)
    {
        try
        {
            Log($"Step {step}: {name}... started");
            await SimulateWorkAsync(step, ct);
            Log($"Step {step}: {name} done");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                LogWarning($"Step {step}: cancelled -> RE-THROWING (not treated as an error)");
                throw;
            }

            // Only genuine failures reach here (e.g. network error -> fallback/retry).
            LogError($"Step {step}: FAILED with {ex.GetType().Name} -> handled as error, continuing");
        }
    }

    // ------------------------------------------------------------------
    // Simulated work
    // ------------------------------------------------------------------

    private async UniTask SimulateWorkAsync(int step, CancellationToken ct)
    {
        // Optionally simulate an API that doesn't accept/respect a token.
        var token = (step == 3 && step3IgnoresToken) ? CancellationToken.None : ct;
        await UniTask.Delay(stepDurationMs, cancellationToken: token);

        // Optionally simulate a genuine (non-cancellation) failure.
        if (step == 2 && step2ThrowsNetworkError)
            throw new InvalidOperationException("Simulated network error: connection reset");
    }

    // ------------------------------------------------------------------
    // Logging with elapsed time + frame number (this is your evidence)
    // ------------------------------------------------------------------

    private string Stamp(string msg)
        => $"[{Time.realtimeSinceStartup - _runStartTime:F2}s | f{Time.frameCount}] {msg}";

    private void Log(string msg) => Debug.Log(Stamp(msg));
    private void LogWarning(string msg) => Debug.LogWarning(Stamp(msg));
    private void LogError(string msg) => Debug.LogError(Stamp(msg));
}