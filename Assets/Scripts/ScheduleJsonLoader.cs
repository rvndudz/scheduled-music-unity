using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class ScheduleJsonLoader : MonoBehaviour
{
    [Header("Schedule Source")]
    [SerializeField] private bool useMockSchedule = true;
    [SerializeField] private TextAsset mockScheduleJson;
    [SerializeField] private string scheduleUrl = "https://your-backend/scheduled-event.json";
    [SerializeField] private bool cacheLastResponse = true;

    public event Action<ScheduledEventPayload[]> ScheduleLoaded;
    public event Action<string> ScheduleLoadFailed;

    private ScheduledEventPayload[] currentEvents;

    public bool UseMockSchedule
    {
        get => useMockSchedule;
        set => useMockSchedule = value;
    }

    public string ScheduleUrl
    {
        get => scheduleUrl;
        set => scheduleUrl = value;
    }

    public TextAsset MockScheduleJson
    {
        get => mockScheduleJson;
        set => mockScheduleJson = value;
    }

    public ScheduledEventPayload[] CurrentEvents => currentEvents;

    public IEnumerator LoadSchedule(Action<ScheduledEventPayload[]> onLoaded, Action<string> onError)
    {
        if (cacheLastResponse && currentEvents != null && currentEvents.Length > 0)
        {
            onLoaded?.Invoke(currentEvents);
            yield break;
        }

        if (useMockSchedule)
        {
            if (mockScheduleJson == null)
            {
                HandleError("ScheduleJsonLoader: useMockSchedule is enabled but no mock JSON asset is assigned.", onError);
                yield break;
            }

            if (TryParseEvents(mockScheduleJson.text, out var payloads, out var parseError))
            {
                HandleSuccess(payloads, onLoaded);
            }
            else
            {
                HandleError(parseError, onError);
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(scheduleUrl))
        {
            HandleError("ScheduleJsonLoader: scheduleUrl is empty.", onError);
            yield break;
        }

        using (var request = UnityWebRequest.Get(scheduleUrl))
        {
            Debug.Log($"ScheduleJsonLoader: Downloading schedule from {scheduleUrl}...");
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                HandleError($"ScheduleJsonLoader: Failed to download schedule from {scheduleUrl}: {request.error}", onError);
                yield break;
            }

            if (TryParseEvents(request.downloadHandler.text, out var payloads, out var parseError))
            {
                HandleSuccess(payloads, onLoaded);
            }
            else
            {
                HandleError(parseError, onError);
            }
        }
    }

    public bool TryGetActiveEvent(DateTimeOffset timestamp, out ScheduledEventPayload activeEvent)
    {
        activeEvent = null;
        if (currentEvents == null || currentEvents.Length == 0)
        {
            return false;
        }

        ScheduledEventPayload upcomingEvent = null;
        DateTimeOffset upcomingStart = DateTimeOffset.MaxValue;

        foreach (var evt in currentEvents)
        {
            if (!DateTimeOffset.TryParse(evt.start_time_utc, out var start) ||
                !DateTimeOffset.TryParse(evt.end_time_utc, out var end))
            {
                continue;
            }

            if (timestamp >= start && timestamp < end)
            {
                activeEvent = evt;
                return true;
            }

            if (timestamp < start && start < upcomingStart)
            {
                upcomingEvent = evt;
                upcomingStart = start;
            }
        }

        if (upcomingEvent != null)
        {
            activeEvent = upcomingEvent;
            return true;
        }

        return false;
    }

    public bool TryParseEvents(string json, out ScheduledEventPayload[] events, out string error)
    {
        events = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "ScheduleJsonLoader: Received empty schedule payload.";
            return false;
        }

        var trimmed = json.Trim();

        try
        {
            if (trimmed.StartsWith("["))
            {
                var wrapped = $"{{\"events\":{trimmed}}}";
                var wrapper = JsonUtility.FromJson<ScheduledEventListWrapper>(wrapped);
                events = wrapper?.events;
            }
            else
            {
                var single = JsonUtility.FromJson<ScheduledEventPayload>(trimmed);
                events = single != null ? new[] { single } : null;
            }
        }
        catch (Exception ex)
        {
            error = $"ScheduleJsonLoader: Unable to parse schedule JSON: {ex.Message}";
            return false;
        }

        if (events == null || events.Length == 0)
        {
            error = "ScheduleJsonLoader: No events found in schedule payload.";
            return false;
        }

        return true;
    }

    public string SerializeEvents(ScheduledEventPayload[] events, bool prettyPrint = true)
    {
        var wrapper = new ScheduledEventListWrapper { events = events };
        var raw = JsonUtility.ToJson(wrapper, prettyPrint);
        if (string.IsNullOrEmpty(raw))
        {
            return "[]";
        }

        const string marker = "\"events\":";
        var index = raw.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var start = index + marker.Length;
            var arrayPortion = raw.Substring(start);
            if (arrayPortion.EndsWith("}"))
            {
                arrayPortion = arrayPortion.Substring(0, arrayPortion.Length - 1);
            }
            return arrayPortion.Trim();
        }

        return raw;
    }

    public void OverwriteSchedule(ScheduledEventPayload[] events, bool broadcast = true)
    {
        currentEvents = events;
        if (broadcast)
        {
            ScheduleLoaded?.Invoke(currentEvents);
        }
    }

    private void HandleSuccess(ScheduledEventPayload[] payloads, Action<ScheduledEventPayload[]> callback)
    {
        currentEvents = payloads;
        callback?.Invoke(payloads);
        ScheduleLoaded?.Invoke(currentEvents);
    }

    private void HandleError(string message, Action<string> callback)
    {
        callback?.Invoke(message);
        ScheduleLoadFailed?.Invoke(message);
    }
}
