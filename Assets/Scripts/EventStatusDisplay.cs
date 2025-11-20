using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class EventStatusDisplay : MonoBehaviour
{
    [SerializeField] private ScheduledPlaybackController playbackController;
    [SerializeField] private UtcTimeSyncService timeSyncService;
    [SerializeField] private TMP_Text statusLabel;
    [SerializeField] private float refreshIntervalSeconds = 1f;
    [Header("Cover Image")]
    [SerializeField] private RawImage coverImage;
    [SerializeField] private Texture2D fallbackCoverTexture;

    private ScheduledEventPayload currentEvent;
    private ScheduledTrack currentTrack;
    private float refreshTimer;
    private readonly Dictionary<string, Texture2D> coverCache = new();
    private Coroutine coverRoutine;

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
        Debug.Log("EventStatusDisplay: OnEnable");
        if (playbackController != null)
        {
            playbackController.ActiveEventChanged += OnActiveEventChanged;
            playbackController.TrackChanged += OnTrackChanged;
            currentEvent = playbackController.CurrentActiveEvent;
            currentTrack = playbackController.CurrentTrack;
            UpdateCoverImage();
        }

        UpdateStatus();
    }

    private void OnDisable()
    {
        Debug.Log("EventStatusDisplay: OnDisable");
        if (playbackController != null)
        {
            playbackController.ActiveEventChanged -= OnActiveEventChanged;
            playbackController.TrackChanged -= OnTrackChanged;
        }

        if (coverRoutine != null)
        {
            StopCoroutine(coverRoutine);
            coverRoutine = null;
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
        Debug.Log($"EventStatusDisplay: Active event changed to {(payload != null ? payload.event_id : "null")}");
        currentEvent = payload;
        if (payload == null)
        {
            currentTrack = null;
        }

        UpdateStatus();
        UpdateCoverImage();
    }

    private void OnTrackChanged(ScheduledTrack track)
    {
        Debug.Log($"EventStatusDisplay: Track changed to {(track != null ? track.track_name : "null")}");
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
            Debug.Log("EventStatusDisplay: No current event when updating status.");
            builder.AppendLine("No active event.");
            statusLabel.text = builder.ToString();
            return;
        }

        builder.AppendLine($"Event: {currentEvent.event_name} ({currentEvent.event_id})");
        builder.AppendLine($"Artist: {currentEvent.artist_name}");
        builder.AppendLine($"Window: {currentEvent.start_time_utc} -> {currentEvent.end_time_utc}");

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
        Debug.Log("EventStatusDisplay: Status label updated.");
    }

    private void UpdateCoverImage()
    {
        if (coverImage == null)
        {
            Debug.LogWarning("EventStatusDisplay: CoverImage is not assigned.");
            return;
        }

        if (coverRoutine != null)
        {
            StopCoroutine(coverRoutine);
            coverRoutine = null;
        }

        if (currentEvent == null || string.IsNullOrWhiteSpace(currentEvent.cover_image_url))
        {
            Debug.Log("EventStatusDisplay: No cover url; applying fallback.");
            ApplyCoverTexture(fallbackCoverTexture);
            return;
        }

        if (coverCache.TryGetValue(currentEvent.cover_image_url, out var cachedTexture))
        {
            Debug.Log("EventStatusDisplay: Using cached cover.");
            ApplyCoverTexture(cachedTexture);
            return;
        }

        Debug.Log($"EventStatusDisplay: Downloading cover from {currentEvent.cover_image_url}");
        coverRoutine = StartCoroutine(DownloadCoverImage(currentEvent.cover_image_url));
    }

    private IEnumerator DownloadCoverImage(string url)
    {
        using (var request = UnityWebRequestTexture.GetTexture(url))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"EventStatusDisplay: Failed to download cover image from {url}: {request.error}");
                ApplyCoverTexture(fallbackCoverTexture);
                coverRoutine = null;
                yield break;
            }

            var texture = DownloadHandlerTexture.GetContent(request);
            if (texture != null)
            {
                coverCache[url] = texture;
            }

            ApplyCoverTexture(texture != null ? texture : fallbackCoverTexture);
            Debug.Log($"EventStatusDisplay: Cover downloaded and applied from {url}. Size: {(texture != null ? texture.width : 0)}x{(texture != null ? texture.height : 0)}");
        }

        coverRoutine = null;
    }

    private void ApplyCoverTexture(Texture texture)
    {
        if (coverImage == null)
        {
            return;
        }

        coverImage.texture = texture;
        coverImage.color = texture == null ? Color.clear : Color.white;
        Debug.Log($"EventStatusDisplay: ApplyCoverTexture called. Texture null: {texture == null}");
    }
}
