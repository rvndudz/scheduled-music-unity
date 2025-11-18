# Scheduled Music

Unity project for creating scheduled playlists that power in-game radios, kiosks, or virtual venues without needing a dedicated streaming server. The system runs locally in your scene and chooses the right track based on the configured schedule so every player hears the same programming.

## Features
- Configure playlists with time slots (daily or weekly) for live-feeling programming
- Trigger playback for radios, kiosks, or other in-world speakers
- No external streaming service required; audio assets stay inside the project
- Optional web dashboard (see [`scheduled-music-nextjs`](https://github.com/rvndudz/scheduled-music-nextjs)) to manage playlists via Cloudflare R2 storage

## Getting Started
1. Open the project in Unity (2021.3 LTS or later recommended).
2. Import your audio clips into `Assets/Audio`.
3. Use the scheduling inspector (under `Assets/Scripts`) to define playlists and slots.
4. Drop a playback controller into your scene to start the scheduled station.

## Use Cases
- **In-game radio**: Simulate always-on stations players can tune into.
- **Kiosks**: Rotate announcements or music at venues and hubs.
- **Virtual events**: Create coordinated audio programs for concerts or exhibitions.

## Contributing
Feel free to open issues or submit pull requests for bug fixes and new features related to scheduling, playback devices, or editor tooling.
