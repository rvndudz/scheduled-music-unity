using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
[RequireComponent(typeof(ScheduleJsonLoader))]
[RequireComponent(typeof(UtcTimeSyncService))]
public class ScheduledPlaybackController : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private ScheduleJsonLoader scheduleLoader;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private UtcTimeSyncService timeSyncService;

    public event Action<ScheduledEventPayload> ActiveEventChanged;
    public event Action<ScheduledTrack> TrackChanged;

    public ScheduledEventPayload CurrentActiveEvent { get; private set; }
    public ScheduledTrack CurrentTrack { get; private set; }

    private Coroutine playbackRoutine;

    private void Awake()
    {
        if (scheduleLoader == null)
        {
            scheduleLoader = GetComponent<ScheduleJsonLoader>();
        }

        if (timeSyncService == null)
        {
            timeSyncService = GetComponent<UtcTimeSyncService>();
        }

        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        audioSource.playOnAwake = false;
        audioSource.loop = false;
    }

    private void OnEnable()
    {
        if (playbackRoutine == null)
        {
            playbackRoutine = StartCoroutine(InitializeSchedule());
        }
    }

    private void OnDisable()
    {
        if (playbackRoutine != null)
        {
            StopCoroutine(playbackRoutine);
            playbackRoutine = null;
        }

        if (audioSource != null)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }
    }

    private IEnumerator InitializeSchedule()
    {
        if (scheduleLoader == null)
        {
            scheduleLoader = GetComponent<ScheduleJsonLoader>();
        }

        if (scheduleLoader == null)
        {
            Debug.LogError("ScheduledPlaybackController: missing ScheduleJsonLoader component.");
            yield break;
        }

        ScheduledEventPayload[] events = null;
        string scheduleError = null;

        yield return scheduleLoader.LoadSchedule(payloads => events = payloads, error => scheduleError = error);

        if (events == null || events.Length == 0)
        {
            Debug.LogError(scheduleError ?? "ScheduledPlaybackController: Unable to load schedule.");
            yield break;
        }

        if (timeSyncService == null)
        {
            timeSyncService = GetComponent<UtcTimeSyncService>();
        }

        if (timeSyncService == null)
        {
            Debug.LogError("ScheduledPlaybackController: missing UtcTimeSyncService component.");
            yield break;
        }

        yield return timeSyncService.EnsureInitialized();

        while (true)
        {
            var currentTime = timeSyncService.GetCurrentUtc();

            if (!TrySelectRelevantEvent(events, currentTime, out var schedule, out var eventStart, out var eventEnd, out var eventAlreadyStarted))
            {
                Debug.Log("ScheduledPlaybackController: No active or upcoming events to play. Stopping playback.");
                yield break;
            }

            if (schedule.tracks == null || schedule.tracks.Length == 0)
            {
                Debug.LogError($"Event \"{schedule.event_name}\" does not include any tracks.");
                yield break;
            }

            var eventChanged = CurrentActiveEvent != schedule;
            CurrentActiveEvent = schedule;
            ActiveEventChanged?.Invoke(schedule);
            if (eventChanged)
            {
                NotifyTrackChanged(null);
            }

            if (!eventAlreadyStarted)
            {
                var waitSeconds = Mathf.Max(0f, (float)(eventStart - currentTime).TotalSeconds);
                if (waitSeconds > 0f)
                {
                    Debug.Log($"Event \"{schedule.event_name}\" has not started yet. Waiting {waitSeconds:F0} seconds.");
                    yield return new WaitForSecondsRealtime(waitSeconds);
                }

                continue;
            }

            var elapsedSeconds = Mathf.Max(0f, (float)(currentTime - eventStart).TotalSeconds);
            yield return PlayFromElapsed(schedule, elapsedSeconds, eventEnd);
        }
    }

    private IEnumerator PlayFromElapsed(ScheduledEventPayload schedule, float elapsedSeconds, DateTimeOffset eventEnd)
    {
        if (!TryFindTrackAtTime(schedule, elapsedSeconds, out var startingTrackIndex, out var offsetInTrack))
        {
            Debug.LogWarning("Unable to find a track for the current elapsed time. Event might be complete.");
            yield break;
        }

        for (int i = startingTrackIndex; i < schedule.tracks.Length; i++)
        {
            var track = schedule.tracks[i];
            AudioClip clip = null;
            var resolvedTrackUrl = CloudflareR2UrlBuilder.GetSignedOrPublicUrl(track.track_url);

            using (var clipRequest = UnityWebRequestMultimedia.GetAudioClip(resolvedTrackUrl, GuessAudioType(resolvedTrackUrl)))
            {
                Debug.Log($"ScheduledPlaybackController: Downloading track \"{track.track_name}\" from {resolvedTrackUrl}...");
                yield return clipRequest.SendWebRequest();

                if (clipRequest.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Failed to download track {track.track_name} ({resolvedTrackUrl}): {clipRequest.error}");
                    yield break;
                }

                clip = DownloadHandlerAudioClip.GetContent(clipRequest);
            }

            if (clip == null)
            {
                Debug.LogError($"Unable to decode audio clip for track {track.track_name}");
                yield break;
            }

            audioSource.clip = clip;
            var playbackOffset = i == startingTrackIndex
                ? Mathf.Clamp(offsetInTrack, 0f, Mathf.Max(0f, clip.length - 0.01f))
                : 0f;

            // If offset exceeds clip length (because metadata was shorter), skip to next track.
            if (playbackOffset >= clip.length - 0.01f)
            {
                continue;
            }

            audioSource.time = playbackOffset;
            audioSource.Play();

            NotifyTrackChanged(track);

            var secondsUntilEventEnds = (float)(eventEnd - timeSyncService.GetCurrentUtc()).TotalSeconds;
            if (secondsUntilEventEnds <= 0f)
            {
                audioSource.Stop();
                NotifyTrackChanged(null);
                yield break;
            }

            var clipTimeRemaining = clip.length - playbackOffset;
            var waitSeconds = Mathf.Min(clipTimeRemaining, secondsUntilEventEnds);
            if (waitSeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(waitSeconds);
            }

            if (timeSyncService.GetCurrentUtc() >= eventEnd)
            {
                audioSource.Stop();
                NotifyTrackChanged(null);
                yield break;
            }
        }

        NotifyTrackChanged(null);
        Debug.Log("Finished scheduled playback for this event.");
    }

    private bool TrySelectRelevantEvent(ScheduledEventPayload[] events, DateTimeOffset currentTime, out ScheduledEventPayload selectedEvent, out DateTimeOffset eventStart, out DateTimeOffset eventEnd, out bool hasStarted)
    {
        selectedEvent = null;
        eventStart = default;
        eventEnd = default;
        hasStarted = false;

        ScheduledEventPayload upcomingEvent = null;
        DateTimeOffset upcomingStart = DateTimeOffset.MaxValue;
        DateTimeOffset upcomingEnd = default;

        foreach (var evt in events)
        {
            if (!TryParseUtcTimestamp(evt.start_time_utc, out var start))
            {
                Debug.LogWarning($"ScheduledPlaybackController: Invalid start time for event {evt.event_id} ({evt.start_time_utc}).");
                continue;
            }

            if (!TryParseUtcTimestamp(evt.end_time_utc, out var end))
            {
                Debug.LogWarning($"ScheduledPlaybackController: Invalid end time for event {evt.event_id} ({evt.end_time_utc}).");
                continue;
            }

            if (end <= start)
            {
                Debug.LogWarning($"ScheduledPlaybackController: Event {evt.event_id} has end time before start time.");
                continue;
            }

            var adjustedEnd = AdjustEventEndWithTracks(evt, start, end);

            if (currentTime >= start && currentTime < adjustedEnd)
            {
                selectedEvent = evt;
                eventStart = start;
                eventEnd = adjustedEnd;
                hasStarted = true;
                return true;
            }

            if (currentTime < start && start < upcomingStart)
            {
                upcomingEvent = evt;
                upcomingStart = start;
                upcomingEnd = adjustedEnd;
            }
        }

        if (upcomingEvent != null)
        {
            selectedEvent = upcomingEvent;
            eventStart = upcomingStart;
            eventEnd = upcomingEnd;
            hasStarted = false;
            return true;
        }

        return false;
    }

    private bool TryFindTrackAtTime(ScheduledEventPayload schedule, float elapsedSeconds, out int trackIndex, out float offsetInTrack)
    {
        var remaining = Mathf.Max(0f, elapsedSeconds);

        for (int i = 0; i < schedule.tracks.Length; i++)
        {
            var track = schedule.tracks[i];
            var duration = Mathf.Max(0f, track.track_duration_seconds);

            if (duration <= 0f)
            {
                Debug.LogError($"Track {track.track_name} is missing duration metadata (track_duration_seconds).");
                continue;
            }

            if (remaining < duration)
            {
                trackIndex = i;
                offsetInTrack = remaining;
                return true;
            }

            remaining -= duration;
        }

        trackIndex = -1;
        offsetInTrack = 0f;
        return false;
    }

    private static bool TryParseUtcTimestamp(string raw, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private static AudioType GuessAudioType(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return AudioType.UNKNOWN;
        }

        string extension;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            extension = Path.GetExtension(uri.AbsolutePath);
        }
        else
        {
            extension = Path.GetExtension(url);
        }

        extension = extension?.ToLowerInvariant();
        return extension switch
        {
            ".wav" => AudioType.WAV,
            ".mp3" => AudioType.MPEG,
            ".ogg" => AudioType.OGGVORBIS,
            ".aiff" => AudioType.AIFF,
            ".aif" => AudioType.AIFF,
            _ => AudioType.UNKNOWN
        };
    }

    private static DateTimeOffset AdjustEventEndWithTracks(ScheduledEventPayload schedule, DateTimeOffset start, DateTimeOffset scheduledEnd)
    {
        var trackDurationSeconds = CalculateTotalTrackDuration(schedule);
        if (trackDurationSeconds <= 0f)
        {
            return scheduledEnd;
        }

        var trackBasedEnd = start.AddSeconds(trackDurationSeconds);
        return trackBasedEnd < scheduledEnd ? trackBasedEnd : scheduledEnd;
    }

    private static float CalculateTotalTrackDuration(ScheduledEventPayload schedule)
    {
        if (schedule?.tracks == null || schedule.tracks.Length == 0)
        {
            return 0f;
        }

        float total = 0f;
        for (int i = 0; i < schedule.tracks.Length; i++)
        {
            total += Mathf.Max(0f, schedule.tracks[i].track_duration_seconds);
        }
        return total;
    }

    private void NotifyTrackChanged(ScheduledTrack track)
    {
        if (CurrentTrack == track)
        {
            return;
        }

        CurrentTrack = track;
        TrackChanged?.Invoke(track);
    }
}
