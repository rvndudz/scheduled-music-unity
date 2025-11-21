using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class EventStatusDisplay : MonoBehaviour
{
    [SerializeField] private ScheduledPlaybackController playbackController;
    [SerializeField] private UtcTimeSyncService timeSyncService;
    [Header("Text Outputs")]
    [SerializeField] private TMP_Text eventNameLabel;
    [SerializeField] private TMP_Text artistNameLabel;
    [SerializeField] private TMP_Text currentTrackLabel;
    [SerializeField] private TMP_Text timeWindowLabel;
    [SerializeField] private TMP_Text tracksListLabel;
    [SerializeField] private float refreshIntervalSeconds = 1f;
    [Header("Cover Image")]
    [SerializeField] private Renderer coverRenderer;
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
        var utcNow = timeSyncService != null ? timeSyncService.GetCurrentUtc() : System.DateTimeOffset.UtcNow;

        if (currentEvent == null)
        {
            Debug.Log("EventStatusDisplay: No current event when updating status.");
            ApplyText(eventNameLabel, "No active event");
            ApplyText(artistNameLabel, string.Empty);
            ApplyText(currentTrackLabel, string.Empty);
            ApplyText(timeWindowLabel, $"Current UTC: {utcNow:yyyy-MM-dd HH:mm:ss 'UTC'}");
            ApplyText(tracksListLabel, string.Empty);
            return;
        }

        ApplyText(eventNameLabel, currentEvent.event_name);
        ApplyText(artistNameLabel, currentEvent.artist_name);

        var trackLabel = currentTrack != null
            ? $"{currentTrack.track_name} ({currentTrack.track_duration_seconds:F0}s)"
            : "Now Playing: --";
        ApplyText(currentTrackLabel, trackLabel);

        ApplyText(timeWindowLabel, $"{currentEvent.start_time_utc} -> {currentEvent.end_time_utc} (UTC now {utcNow:yyyy-MM-dd HH:mm:ss})");

        if (tracksListLabel != null)
        {
            if (currentEvent.tracks != null && currentEvent.tracks.Length > 0)
            {
                var builder = new StringBuilder();
                for (int i = 0; i < currentEvent.tracks.Length; i++)
                {
                    var track = currentEvent.tracks[i];
                    builder.AppendLine($"{i + 1}. {track.track_name} ({track.track_duration_seconds:F0}s)");
                }
                tracksListLabel.text = builder.ToString();
            }
            else
            {
                tracksListLabel.text = string.Empty;
            }
        }

        Debug.Log("EventStatusDisplay: Status labels updated.");
    }

    private void UpdateCoverImage()
    {
        if (coverRenderer == null)
        {
            Debug.LogWarning("EventStatusDisplay: Cover renderer is not assigned.");
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

        var resolvedUrl = CloudflareR2UrlBuilder.GetSignedOrPublicUrl(currentEvent.cover_image_url);
        if (coverCache.TryGetValue(resolvedUrl, out var cachedTexture))
        {
            Debug.Log("EventStatusDisplay: Using cached cover.");
            ApplyCoverTexture(cachedTexture);
            return;
        }

        Debug.Log($"EventStatusDisplay: Downloading cover from {resolvedUrl}");
        coverRoutine = StartCoroutine(DownloadCoverImage(resolvedUrl));
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
        if (coverRenderer == null)
        {
            return;
        }

        var appliedTexture = texture != null ? texture : fallbackCoverTexture;
        // material.mainTexture works across Standard/URP shaders; creates an instance per renderer.
        var materialInstance = coverRenderer.material;
        materialInstance.mainTexture = appliedTexture;
        coverRenderer.enabled = appliedTexture != null;
        Debug.Log($"EventStatusDisplay: ApplyCoverTexture called. Texture null: {texture == null}");
    }

    private static void ApplyText(TMP_Text label, string value)
    {
        if (label != null)
        {
            label.text = value;
        }
    }
}
