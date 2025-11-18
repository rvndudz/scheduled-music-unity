using System.Text;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class EventStatusDisplay : MonoBehaviour
{
    [SerializeField] private ScheduledPlaybackController playbackController;
    [SerializeField] private UtcTimeSyncService timeSyncService;
    [SerializeField] private TMP_Text statusLabel;
    [SerializeField] private float refreshIntervalSeconds = 1f;

    private ScheduledEventPayload currentEvent;
    private ScheduledTrack currentTrack;
    private float refreshTimer;

    private void Awake()
    {
        if (playbackController == null)
        {
            playbackController = FindObjectOfType<ScheduledPlaybackController>();
        }

        if (timeSyncService == null)
        {
            timeSyncService = FindObjectOfType<UtcTimeSyncService>();
        }
    }

    private void OnEnable()
    {
        if (playbackController != null)
        {
            playbackController.ActiveEventChanged += OnActiveEventChanged;
            playbackController.TrackChanged += OnTrackChanged;
            currentEvent = playbackController.CurrentActiveEvent;
            currentTrack = playbackController.CurrentTrack;
        }

        UpdateStatus();
    }

    private void OnDisable()
    {
        if (playbackController != null)
        {
            playbackController.ActiveEventChanged -= OnActiveEventChanged;
            playbackController.TrackChanged -= OnTrackChanged;
        }
    }

    private void Update()
    {
        refreshTimer += Time.unscaledDeltaTime;
        if (refreshTimer >= Mathf.Max(0.1f, refreshIntervalSeconds))
        {
            refreshTimer = 0f;
            UpdateStatus();
        }
    }

    private void OnActiveEventChanged(ScheduledEventPayload payload)
    {
        currentEvent = payload;
        if (payload == null)
        {
            currentTrack = null;
        }

        UpdateStatus();
    }

    private void OnTrackChanged(ScheduledTrack track)
    {
        currentTrack = track;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (statusLabel == null)
        {
            return;
        }

        var builder = new StringBuilder();
        var utcNow = timeSyncService != null ? timeSyncService.GetCurrentUtc() : System.DateTimeOffset.UtcNow;
        builder.AppendLine($"Current UTC: {utcNow:yyyy-MM-dd HH:mm:ss 'UTC'}");

        if (currentEvent == null)
        {
            builder.AppendLine("No active event.");
            statusLabel.text = builder.ToString();
            return;
        }

        builder.AppendLine($"Event: {currentEvent.event_name} ({currentEvent.event_id})");
        builder.AppendLine($"Artist: {currentEvent.artist_name}");
        builder.AppendLine($"Window: {currentEvent.start_time_utc} → {currentEvent.end_time_utc}");

        if (currentTrack != null)
        {
            builder.AppendLine($"Now Playing: {currentTrack.track_name} ({currentTrack.track_duration_seconds:F0}s)");
        }
        else
        {
            builder.AppendLine("Now Playing: --");
        }

        if (currentEvent.tracks != null && currentEvent.tracks.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Tracks:");
            for (int i = 0; i < currentEvent.tracks.Length; i++)
            {
                var track = currentEvent.tracks[i];
                builder.AppendLine($"{i + 1}. {track.track_name} ({track.track_duration_seconds:F0}s)");
            }
        }

        statusLabel.text = builder.ToString();
    }
}
