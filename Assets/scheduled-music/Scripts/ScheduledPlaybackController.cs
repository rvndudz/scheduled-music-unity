using System;
using System.Collections;
using System.Collections.Generic;
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

    [Header("Default Event Fallback")]
    [SerializeField] private bool enableDefaultEventFallback = true;
    [SerializeField] private TextAsset defaultEventJson;
    [SerializeField] private float defaultEventCheckIntervalSeconds = 60f;
    [SerializeField] private string defaultEventId;

    public event Action<ScheduledEventPayload> ActiveEventChanged;
    public event Action<ScheduledTrack> TrackChanged;

    public ScheduledEventPayload CurrentActiveEvent { get; private set; }
    public ScheduledTrack CurrentTrack { get; private set; }
    public bool IsDefaultEventActive { get; private set; }

    private readonly Dictionary<string, AudioClip> clipCache = new();

    private ScheduledEventPayload defaultEventPayload;
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

        TryLoadDefaultEvent(events);

        yield return timeSyncService.EnsureInitialized();

        while (true)
        {
            var currentTime = timeSyncService.GetCurrentUtc();

            if (TryFindActiveEvent(events, currentTime, out var schedule, out var eventStart, out var eventEnd))
            {
                SetActiveEvent(schedule, isDefault: false);

                if (schedule.tracks == null || schedule.tracks.Length == 0)
                {
                    Debug.LogError($"Event \"{schedule.event_name}\" does not include any tracks.");
                    yield break;
                }

                var elapsedSeconds = Mathf.Max(0f, (float)(currentTime - eventStart).TotalSeconds);
                yield return PlayFromElapsed(schedule, elapsedSeconds, eventEnd);
                continue;
            }

            if (enableDefaultEventFallback && defaultEventPayload != null)
            {
                yield return PlayDefaultUntilScheduledEventStarts(events);
                continue;
            }

            if (TryFindNextUpcomingEvent(events, currentTime, out var nextEvent, out var nextStart, out _))
            {
                var waitSeconds = Mathf.Max(0f, (float)(nextStart - currentTime).TotalSeconds);
                if (waitSeconds > 0f)
                {
                    Debug.Log($"ScheduledPlaybackController: No active event. Waiting {waitSeconds:F0} seconds for next event \"{nextEvent.event_name}\".");
                    yield return new WaitForSecondsRealtime(waitSeconds);
                }

                continue;
            }

            Debug.Log("ScheduledPlaybackController: No active or upcoming events to play. Stopping playback.");
            SetActiveEvent(null, isDefault: false);
            yield break;
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

            yield return GetOrDownloadClip(resolvedTrackUrl, GuessAudioType(resolvedTrackUrl), c => clip = c);

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

    private bool TryFindActiveEvent(ScheduledEventPayload[] events, DateTimeOffset currentTime, out ScheduledEventPayload activeEvent, out DateTimeOffset start, out DateTimeOffset adjustedEnd)
    {
        activeEvent = null;
        start = default;
        adjustedEnd = default;

        foreach (var evt in events)
        {
            if (!TryParseUtcTimestamp(evt.start_time_utc, out var parsedStart))
            {
                Debug.LogWarning($"ScheduledPlaybackController: Invalid start time for event {evt.event_id} ({evt.start_time_utc}).");
                continue;
            }

            if (!TryParseUtcTimestamp(evt.end_time_utc, out var parsedEnd))
            {
                Debug.LogWarning($"ScheduledPlaybackController: Invalid end time for event {evt.event_id} ({evt.end_time_utc}).");
                continue;
            }

            if (parsedEnd <= parsedStart)
            {
                Debug.LogWarning($"ScheduledPlaybackController: Event {evt.event_id} has end time before start time.");
                continue;
            }

            var endWithTracks = AdjustEventEndWithTracks(evt, parsedStart, parsedEnd);
            if (currentTime >= parsedStart && currentTime < endWithTracks)
            {
                activeEvent = evt;
                start = parsedStart;
                adjustedEnd = endWithTracks;
                return true;
            }
        }

        return false;
    }

    private bool TryFindNextUpcomingEvent(ScheduledEventPayload[] events, DateTimeOffset currentTime, out ScheduledEventPayload nextEvent, out DateTimeOffset start, out DateTimeOffset adjustedEnd)
    {
        nextEvent = null;
        start = default;
        adjustedEnd = default;
        var earliestStart = DateTimeOffset.MaxValue;

        foreach (var evt in events)
        {
            if (!TryParseUtcTimestamp(evt.start_time_utc, out var parsedStart) ||
                !TryParseUtcTimestamp(evt.end_time_utc, out var parsedEnd))
            {
                continue;
            }

            if (parsedEnd <= parsedStart || parsedStart <= currentTime)
            {
                continue;
            }

            if (parsedStart < earliestStart)
            {
                earliestStart = parsedStart;
                start = parsedStart;
                adjustedEnd = AdjustEventEndWithTracks(evt, parsedStart, parsedEnd);
                nextEvent = evt;
            }
        }

        return nextEvent != null;
    }

    private IEnumerator PlayDefaultUntilScheduledEventStarts(ScheduledEventPayload[] scheduledEvents)
    {
        if (defaultEventPayload == null)
        {
            yield break;
        }

        if (defaultEventPayload.tracks == null || defaultEventPayload.tracks.Length == 0)
        {
            Debug.LogWarning("ScheduledPlaybackController: Default event is missing tracks, cannot play fallback audio.");
            yield break;
        }

        SetActiveEvent(defaultEventPayload, isDefault: true);

        while (true)
        {
            if (HasScheduledEventStarted(scheduledEvents))
            {
                StopDefaultPlayback();
                yield break;
            }

            for (int i = 0; i < defaultEventPayload.tracks.Length; i++)
            {
                var track = defaultEventPayload.tracks[i];
                var resolvedTrackUrl = CloudflareR2UrlBuilder.GetSignedOrPublicUrl(track.track_url);

                AudioClip clip = null;
                yield return GetOrDownloadClip(resolvedTrackUrl, GuessAudioType(resolvedTrackUrl), c => clip = c);

                if (clip == null)
                {
                    Debug.LogError($"ScheduledPlaybackController: Unable to download default track \"{track.track_name}\"");
                    StopDefaultPlayback();
                    yield break;
                }

                audioSource.clip = clip;
                audioSource.time = 0f;
                audioSource.loop = false;
                audioSource.Play();

                NotifyTrackChanged(track);

                var remaining = clip.length;
                var checkInterval = Mathf.Max(1f, defaultEventCheckIntervalSeconds);
                while (remaining > 0f)
                {
                    var wait = Mathf.Min(checkInterval, remaining);

                    if (TryFindNextUpcomingEvent(scheduledEvents, timeSyncService.GetCurrentUtc(), out _, out var upcomingStart, out _))
                    {
                        var untilNextEvent = Mathf.Max(0f, (float)(upcomingStart - timeSyncService.GetCurrentUtc()).TotalSeconds);
                        if (untilNextEvent > 0f)
                        {
                            wait = Mathf.Min(wait, untilNextEvent);
                        }
                    }

                    yield return new WaitForSecondsRealtime(wait);
                    remaining -= wait;

                    if (HasScheduledEventStarted(scheduledEvents))
                    {
                        StopDefaultPlayback();
                        yield break;
                    }
                }
            }
        }
    }

    private void StopDefaultPlayback()
    {
        audioSource.Stop();
        SetActiveEvent(null, isDefault: false);
    }

    private ScheduledEventPayload FindDefaultEventFromSchedule(ScheduledEventPayload[] events)
    {
        if (events == null || events.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(defaultEventId))
        {
            foreach (var evt in events)
            {
                if (string.Equals(evt.event_id, defaultEventId, StringComparison.OrdinalIgnoreCase))
                {
                    return evt;
                }
            }
        }

        foreach (var evt in events)
        {
            if (!string.IsNullOrWhiteSpace(evt.event_name) &&
                string.Equals(evt.event_name.Trim(), "default", StringComparison.OrdinalIgnoreCase))
            {
                return evt;
            }
        }

        return null;
    }

    private bool HasScheduledEventStarted(ScheduledEventPayload[] scheduledEvents)
    {
        var now = timeSyncService.GetCurrentUtc();
        return TryFindActiveEvent(scheduledEvents, now, out _, out _, out _);
    }

    private void SetActiveEvent(ScheduledEventPayload payload, bool isDefault)
    {
        var changed = CurrentActiveEvent != payload || IsDefaultEventActive != isDefault;
        CurrentActiveEvent = payload;
        IsDefaultEventActive = isDefault;

        if (changed)
        {
            ActiveEventChanged?.Invoke(payload);
            NotifyTrackChanged(null);
        }
    }

    private void TryLoadDefaultEvent(ScheduledEventPayload[] loadedEvents)
    {
        if (!enableDefaultEventFallback)
        {
            defaultEventPayload = null;
            return;
        }

        if (defaultEventPayload != null)
        {
            return;
        }

        if (defaultEventJson != null)
        {
            string parseError = null;
            if (scheduleLoader != null && scheduleLoader.TryParseEvents(defaultEventJson.text, out var parsedEvents, out parseError))
            {
                defaultEventPayload = parsedEvents != null && parsedEvents.Length > 0 ? parsedEvents[0] : null;
            }
            else
            {
                Debug.LogWarning($"ScheduledPlaybackController: Unable to parse default event JSON. {parseError ?? "No parser available."}");
            }
        }

        if (defaultEventPayload == null)
        {
            defaultEventPayload = FindDefaultEventFromSchedule(loadedEvents);
            if (defaultEventPayload != null)
            {
                Debug.Log("ScheduledPlaybackController: Using default event from loaded schedule.");
            }
        }

        if (defaultEventPayload == null)
        {
            Debug.LogWarning("ScheduledPlaybackController: Default event fallback is enabled but no default event data was found.");
        }
    }

    private IEnumerator GetOrDownloadClip(string url, AudioType audioType, Action<AudioClip> onReady)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            onReady?.Invoke(null);
            yield break;
        }

        if (clipCache.TryGetValue(url, out var cachedClip) && cachedClip != null)
        {
            onReady?.Invoke(cachedClip);
            yield break;
        }

        using (var clipRequest = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
        {
            Debug.Log($"ScheduledPlaybackController: Downloading track from {url}...");
            yield return clipRequest.SendWebRequest();

            if (clipRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"ScheduledPlaybackController: Failed to download track ({url}): {clipRequest.error}");
                onReady?.Invoke(null);
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(clipRequest);
            if (clip != null)
            {
                clipCache[url] = clip;
            }

            onReady?.Invoke(clip);
        }
    }
}
