# Performance Optimization for 600+ Simultaneous Users

## Project Context
KRT-Com is designed as a **secondary radio-like communication layer** to complement Discord. While users remain in their respective Discord voice channels for primary talk, KRT-Com enables simultaneous communication across multiple groups and frequencies. To support 600+ users where different groups (e.g., 100 people per channel) are active at once, the following optimizations are required.

---

## 1. Audio Relay & Event Loop
*   **Problem**: Node.js is single-threaded. Forwarding a single audio packet to 100+ listeners in a synchronous loop stops the server from doing anything else for a few milliseconds.
*   **Impact**: If multiple people talk across different frequencies, the delays stack up, causing "robotic" audio, lag, and eventually disconnecting users as the server falls behind.
*   **Fixes**:
    *   **Implement Worker Threads**: Move the binary audio forwarding logic (`handleAudio`) to a dedicated Node.js Worker Thread.
    *   **WebSocket Backpressure**: Check `ws.bufferedAmount`. If a client's internet is too slow to receive audio, drop their packets instead of letting the server's memory fill up.
    *   **Optimize Forwarding Loop**: Use binary-optimized transmission and avoid expensive JSON operations during the audio path.

## 2. Scalability & Metadata
*   **Problem**: Every time a user joins/leaves a frequency or moves in Discord, the server broadcasts an update to everyone. With 600 users, this "management traffic" can become heavier than the audio itself.
*   **Impact**: CPU spikes and network congestion caused by thousands of small JSON status messages.
*   **Fixes**:
    *   **Debounce Listener Updates**: Batch join/leave notifications and send them every 1-2 seconds instead of instantly.
    *   **Session Lookup Optimization**: Use HashMaps for session management to ensure checking "is this user allowed to talk" takes the same amount of time regardless of whether there are 10 or 1000 users ($O(1)$ vs $O(N)$).
    *   **Async Database**: Ensure SQLite writes (e.g., logging transmissions) don't block the main process.

## 3. Infrastructure & OS Tuning
*   **Problem**: Default Linux settings often limit a single application to 1024 open files (connections).
*   **Impact**: When reaching ~600-800 connections (including DB handles and internal pipes), the server will refuse new logins.
*   **Fixes**:
    *   **File Descriptor Limits**: Increase `LimitNOFILE` to 65535 in the systemd service.
    *   **PPS (Packets Per Second) Tuning**: Adjust kernel networking buffers to handle the high volume of small UDP-like WebSocket packets.

## 4. Discord Integration
*   **Problem**: Discord sends "Voice State Updates" for every person moving channels. 600 people moving for a briefing creates a massive flood of events.
*   **Impact**: The bot may hit rate limits or crash the backend trying to process 600 database updates in one second.
*   **Fixes**:
    *   **Voice State Debouncing**: Queue Discord updates and process them in small batches.
    *   **Selective Syncing**: Only sync member data when absolutely necessary for authentication.
