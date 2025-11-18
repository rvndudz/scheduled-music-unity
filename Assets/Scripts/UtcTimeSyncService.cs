using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class UtcTimeSyncService : MonoBehaviour
{
    [Header("Time Source")]
    [SerializeField] private string timeServiceUrl = DefaultTimeService;
    [SerializeField] private float resyncIntervalSeconds = 60f;

    [Header("Mocking")]
    [SerializeField] private bool useMockUtcTime = false;
    [SerializeField] private string mockUtcTimeIso = "2025-03-01T18:05:00+00:00";

    private const string DefaultTimeService = "https://aisenseapi.com/services/v1/datetime";

    private bool isInitialized;
    private DateTimeOffset baseUtc;
    private float baseRealtime;
    private Coroutine lifecycleRoutine;

    public bool IsReady => isInitialized;
    public bool IsMockTime => useMockUtcTime;

    public event Action<DateTimeOffset> TimeUpdated;

    private void OnEnable()
    {
        if (lifecycleRoutine == null)
        {
            lifecycleRoutine = StartCoroutine(InitializeAndSyncLoop());
        }
    }

    private void OnDisable()
    {
        if (lifecycleRoutine != null)
        {
            StopCoroutine(lifecycleRoutine);
            lifecycleRoutine = null;
        }
    }

    public IEnumerator EnsureInitialized()
    {
        if (lifecycleRoutine == null)
        {
            lifecycleRoutine = StartCoroutine(InitializeAndSyncLoop());
        }

        while (!isInitialized)
        {
            yield return null;
        }
    }

    public DateTimeOffset GetCurrentUtc()
    {
        if (!isInitialized)
        {
            return DateTimeOffset.UtcNow;
        }

        var elapsedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - baseRealtime);
        return baseUtc.AddSeconds(elapsedSeconds);
    }

    public void OverrideCurrentTime(DateTimeOffset timestamp)
    {
        useMockUtcTime = true;
        SetBaseTime(timestamp);
        isInitialized = true;
    }

    private IEnumerator InitializeAndSyncLoop()
    {
        yield return InitializeTime();

        if (useMockUtcTime)
        {
            yield break;
        }

        while (resyncIntervalSeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(resyncIntervalSeconds);
            yield return RefreshFromService();
        }
    }

    private IEnumerator InitializeTime()
    {
        if (isInitialized)
        {
            yield break;
        }

        if (useMockUtcTime)
        {
            if (TryParseTimestamp(mockUtcTimeIso, out var mockTime))
            {
                SetBaseTime(mockTime);
                isInitialized = true;
                yield break;
            }

            Debug.LogWarning($"UtcTimeSyncService: Mock UTC time \"{mockUtcTimeIso}\" is invalid. Falling back to remote service.");
        }

        yield return RefreshFromService(true);
    }

    private IEnumerator RefreshFromService(bool isInitialFetch = false)
    {
        if (string.IsNullOrWhiteSpace(timeServiceUrl))
        {
            Debug.LogWarning("UtcTimeSyncService: timeServiceUrl is empty. Falling back to local UTC time.");
            SetBaseTime(DateTimeOffset.UtcNow);
            isInitialized = true;
            yield break;
        }

        using (var request = UnityWebRequest.Get(timeServiceUrl))
        {
            Debug.Log($"UtcTimeSyncService: Fetching UTC time from {timeServiceUrl}...");
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var payload = JsonUtility.FromJson<UtcTimeResponse>(request.downloadHandler.text);
                    if (payload != null && TryParseTimestamp(payload.datetime, out var parsed))
                    {
                        SetBaseTime(parsed);
                        isInitialized = true;
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UtcTimeSyncService: Unable to parse response: {ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"UtcTimeSyncService: Failed to fetch UTC time: {request.error}");
            }
        }

        if (!isInitialized && isInitialFetch)
        {
            Debug.LogWarning("UtcTimeSyncService: Falling back to local UTC time for initialization.");
            SetBaseTime(DateTimeOffset.UtcNow);
            isInitialized = true;
        }
        else if (isInitialized)
        {
            SetBaseTime(DateTimeOffset.UtcNow);
        }
    }

    private static bool TryParseTimestamp(string raw, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private void SetBaseTime(DateTimeOffset timestamp)
    {
        baseUtc = timestamp;
        baseRealtime = Time.realtimeSinceStartup;
        TimeUpdated?.Invoke(timestamp);
    }

    [Serializable]
    private class UtcTimeResponse
    {
        public string datetime;
    }
}
