using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class EventManagementPanel : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ScheduleJsonLoader scheduleLoader;
    [SerializeField] private UtcTimeSyncService timeSyncService;
    [SerializeField] private TMP_Text eventsListLabel;
    [SerializeField] private TMP_InputField eventJsonInput;
    [SerializeField] private TMP_InputField selectionIndexInput;
    [SerializeField] private TMP_Text statusLabel;
    [SerializeField] private AudioSource previewAudioSource;

    private readonly List<ScheduledEventPayload> workingEvents = new();

    private void Awake()
    {
        if (scheduleLoader == null)
        {
            scheduleLoader = FindObjectOfType<ScheduleJsonLoader>();
        }

        if (timeSyncService == null)
        {
            timeSyncService = FindObjectOfType<UtcTimeSyncService>();
        }
    }

    private void OnEnable()
    {
        if (scheduleLoader != null)
        {
            scheduleLoader.ScheduleLoaded += OnScheduleLoaded;
            if (scheduleLoader.CurrentEvents != null && scheduleLoader.CurrentEvents.Length > 0)
            {
                SyncWorkingList(scheduleLoader.CurrentEvents);
            }
            else
            {
                StartCoroutine(scheduleLoader.LoadSchedule(events => SyncWorkingList(events), error => LogStatus(error)));
            }
        }
    }

    private void OnDisable()
    {
        if (scheduleLoader != null)
        {
            scheduleLoader.ScheduleLoaded -= OnScheduleLoaded;
        }
    }

    public void RefreshDisplay()
    {
        if (eventsListLabel == null)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Events Count: {workingEvents.Count}");

        for (int i = 0; i < workingEvents.Count; i++)
        {
            var evt = workingEvents[i];
            builder.AppendLine($"[{i}] {evt.event_name} ({evt.artist_name})");
            builder.AppendLine($"    {evt.start_time_utc} → {evt.end_time_utc}");
            builder.AppendLine($"    Tracks: {(evt.tracks != null ? evt.tracks.Length : 0)}");
        }

        eventsListLabel.text = builder.ToString();
    }

    public void LoadSelectedEventIntoEditor()
    {
        if (eventJsonInput == null)
        {
            return;
        }

        var idx = GetSelectedIndex();
        if (!IsValidIndex(idx))
        {
            LogStatus("Invalid event index.");
            return;
        }

        eventJsonInput.text = JsonUtility.ToJson(workingEvents[idx], true);
    }

    public void ApplyEventEdits()
    {
        if (eventJsonInput == null)
        {
            return;
        }

        var idx = GetSelectedIndex();
        if (!IsValidIndex(idx))
        {
            LogStatus("Invalid event index.");
            return;
        }

        try
        {
            var updated = JsonUtility.FromJson<ScheduledEventPayload>(eventJsonInput.text);
            if (updated == null)
            {
                LogStatus("Unable to parse event JSON.");
                return;
            }

            workingEvents[idx] = updated;
            PushUpdatesToLoader();
            RefreshDisplay();
            LogStatus($"Updated event at index {idx}.");
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to parse event JSON: {ex.Message}");
        }
    }

    public void DeleteCurrentEvent()
    {
        var idx = GetSelectedIndex();
        if (!IsValidIndex(idx))
        {
            LogStatus("Invalid event index.");
            return;
        }

        workingEvents.RemoveAt(idx);
        PushUpdatesToLoader();
        RefreshDisplay();
        LogStatus($"Deleted event at index {idx}.");
    }

    public void DeleteAllEvents()
    {
        workingEvents.Clear();
        PushUpdatesToLoader();
        RefreshDisplay();
        LogStatus("Deleted all events.");
    }

    public void DeleteExpiredEvents()
    {
        if (timeSyncService == null)
        {
            LogStatus("Time sync service missing.");
            return;
        }

        var now = timeSyncService.GetCurrentUtc();
        var removed = workingEvents.RemoveAll(evt =>
        {
            if (!DateTimeOffset.TryParse(evt.end_time_utc, out var end))
            {
                return false;
            }
            return end < now;
        });

        PushUpdatesToLoader();
        RefreshDisplay();
        LogStatus($"Removed {removed} expired events.");
    }

    public void PlayPreviewTrack(int trackIndex)
    {
        var idx = GetSelectedIndex();
        if (!IsValidIndex(idx))
        {
            LogStatus("Invalid event index.");
            return;
        }

        var evt = workingEvents[idx];
        if (evt.tracks == null || trackIndex < 0 || trackIndex >= evt.tracks.Length)
        {
            LogStatus("Invalid track index.");
            return;
        }

        var track = evt.tracks[trackIndex];
        StartCoroutine(DownloadAndPlayPreview(CloudflareR2UrlBuilder.GetSignedOrPublicUrl(track.track_url)));
    }

    public void SaveEventsToJsonFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = "events.json";
        }

        var path = System.IO.Path.Combine(Application.persistentDataPath, relativePath);
        var json = scheduleLoader != null ? scheduleLoader.SerializeEvents(workingEvents.ToArray(), true) : "[]";
        System.IO.File.WriteAllText(path, json);
        LogStatus($"Saved events to {path}");
    }

    private IEnumerator DownloadAndPlayPreview(string url)
    {
        if (previewAudioSource == null)
        {
            LogStatus("Preview AudioSource is not assigned.");
            yield break;
        }

        using (var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.UNKNOWN))
        {
            LogStatus($"Downloading preview from {url}...");
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                LogStatus($"Preview download failed: {request.error}");
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(request);
            previewAudioSource.clip = clip;
            previewAudioSource.Play();
            LogStatus("Playing preview.");
        }
    }

    private void OnScheduleLoaded(ScheduledEventPayload[] events)
    {
        SyncWorkingList(events);
    }

    private void SyncWorkingList(ScheduledEventPayload[] events)
    {
        workingEvents.Clear();
        if (events != null)
        {
            workingEvents.AddRange(events);
        }
        RefreshDisplay();
    }

    private void PushUpdatesToLoader()
    {
        if (scheduleLoader != null)
        {
            scheduleLoader.OverwriteSchedule(workingEvents.ToArray());
        }
    }

    private int GetSelectedIndex()
    {
        if (selectionIndexInput == null || string.IsNullOrWhiteSpace(selectionIndexInput.text))
        {
            return 0;
        }

        return int.TryParse(selectionIndexInput.text, out var idx) ? idx : -1;
    }

    private bool IsValidIndex(int idx) => idx >= 0 && idx < workingEvents.Count;

    private void LogStatus(string message)
    {
        if (statusLabel != null)
        {
            statusLabel.text = message;
        }
        else
        {
            Debug.Log(message);
        }
    }
}
