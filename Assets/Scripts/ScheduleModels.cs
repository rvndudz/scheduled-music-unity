using System;

[Serializable]
public class ScheduledEventPayload
{
    public string event_id;
    public string event_name;
    public string artist_name;
    public string start_time_utc;
    public string end_time_utc;
    public ScheduledTrack[] tracks;
}

[Serializable]
public class ScheduledTrack
{
    public string track__id;
    public string track_id;
    public string track_name;
    public string track_url;
    public float track_duration_seconds;
}

[Serializable]
public class ScheduledEventListWrapper
{
    public ScheduledEventPayload[] events;
}
