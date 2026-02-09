# Privacy Policy

This project is a **self-hosted open-source software**.  
Responsibility for operation, configuration, and legal compliance lies entirely with the **server operator**.

This document explains **which data may be processed**, **for what purpose**, and **how it can be controlled or deleted**.

---

## 1. Principles

This project follows a **privacy-by-design** and **data minimization** approach:

- Only data strictly required for technical operation is processed
- No hidden data collection
- No telemetry, analytics, or "phone-home" behavior — the client contacts the server only when status changes or user action occurs
- All data remains **exclusively on the operator's server**

---

## 1.1 Transparency

When installing and updating the software, the user needs to read and agree to the privacy policy. The privacy policy can be fetched after entering IP address and port in the companion app, so that the user can make an informed decision about using the software. If the privacy policy is updated, a notification is displayed in the companion app and the user must read and accept the updated privacy policy before connecting to the server again.

Within this privacy policy, all data that is processed and what for is documented. The current status of the server is displayed when entering the IP and port, so the user can immediately see if the server is in debug mode or DSGVO compliance mode.

After reading and accepting the terms and privacy policy, the user can choose to login with Discord OAuth2. The login is handled via Discord's authorization code flow — the user authorizes the application in their browser, and the server receives only the minimum required identity information.

---

## 2. What data is processed?

### 2.1 User Identifiers

- **Discord OAuth2**: When logging in, the server requests Discord `identify` and `guilds` scopes via OAuth2 authorization code flow. This provides the user's Discord ID, display name, and guild memberships. The Discord access token is **immediately revoked** after use — the server does not retain long-term access to the user's Discord account.
- **User ID Hashing**: Raw Discord User IDs (snowflake IDs) are **never stored** in the database. All user identifiers are hashed using HMAC-SHA256 before persistence. This means that even if the database were compromised, the stored hashes cannot be reversed to obtain the original Discord User IDs without access to the server's secret key.
- Discord display names (server nicknames only, changeable by user) are stored for display purposes but are not used for authentication. Old usernames are not stored — only the latest nickname is kept.
- Guild IDs are stored for frequency mapping and access control. They are used to determine if a user is allowed to connect to the voice server and to which frequencies they have access.
- Guild roles will be stored for ACL purposes in a future update. Only the latest rank/role is stored.
- No real names, email addresses, or personal profile data are required or stored.

---

### 2.2 Authentication & Sessions

- Temporary HMAC-SHA256 signed authentication tokens (24h expiry)
- Active voice sessions
- Expiration timestamps

These data are **time-limited** and automatically invalidated after 24 hours.

**Debug Login**: A manual login endpoint (POST /auth/login) exists for development purposes. This endpoint is **disabled by default** and only available when the server administrator explicitly enables debug mode. The companion app displays a warning when debug mode is active. In production, this endpoint returns HTTP 410 (Gone).

---

### 2.3 Logs

Depending on server configuration, the following technical logs may be created:
- Connection events
- Error messages
- System and debug events
- If debug mode is enabled, this is shown in the companion app along with the data retention duration
- If DSGVO compliance mode is enabled, logs are automatically deleted after the configured retention period and DSGVO mode status is displayed in the companion app

**Logs never contain:**
- Audio data
- Voice content
- Persistent IP histories
- PTT speech events send the channel-id and the username of the user that triggered the event, but this information is not stored in the database and is only used to display the correct information in the companion app
- When using emergency frequencies, the client sends extra information to other clients listening on emergency frequencies. This information includes the username, the channel name, and the frequencies the user has active. This information is only stored in memory and is only visible for a set number of entries on each receiving client. It is not stored in the database and is not logged except in debug mode.

Log retention duration is **fully configurable by the server operator** and can be disabled entirely.

---

### 2.4 Audio & Radio Communication

- Audio data is **never recorded or stored**
- Voice transmission is **live-only** (Opus codec, relayed through server)
- Push-to-Talk events and radio states are **ephemeral**

There is **no technical capability** to reconstruct past conversations.

---

### 2.5 Admin Token

- The admin token is used exclusively for server management endpoints
- The companion app does **not persist** the admin token to disk — it exists only in memory during runtime
- The admin token input field is only visible in the companion app when the server is in debug mode

---

## 3. Data Retention

All data retention periods are **configurable by the server operator**, for example:
- No retention
- Short-term retention (e.g. 2 days with DSGVO compliance mode)
- Debug mode retention (7 days)
- Extended retention (configurable)

Short or disabled retention is strongly recommended.

In the companion app, the server status is displayed in the server tab, so the user can see the current retention settings and DSGVO compliance mode. If DSGVO compliance mode is disabled, an additional warning is shown before logging in and automatic login is disabled, so the user is aware of the current settings and can make an informed decision.

---

## 4. Data Deletion

### 4.1 User Data Deletion

A **hard delete** can be executed for any User ID, removing:
- Logs
- Sessions
- Tokens
- Channel and radio mappings
- Policy acceptance records

Deletion is **irreversible** and should be used with caution. The administrator can set a ban for the user, so that the user cannot log in again after deletion. This prevents the user from creating a new session and generating new data. The ban is stored in a separate table with the hashed user ID, the raw user ID (for admin identification), and a timestamp.

---

### 4.2 Relation to Bans

A full deletion operation technically results in:
- Complete removal of all stored data
- Automatic addition of the User ID to a **ban list**

The ban list stores **minimal information only**:
- Hashed Discord User ID (HMAC-SHA256, irreversible)
- Raw Discord User ID (for administrator identification purposes)
- Timestamp
- Optional reason

Unbanning a user **does not restore deleted data**.

---

## 5. Debugging & Maintenance

- Debug functionality can be enabled by the server administrator via `service.sh` (menu items 22 and 50).
- When debug mode is active:
  - The companion app displays a visible warning to all users
  - The manual login endpoint (POST /auth/login) becomes available
  - DSGVO compliance mode is automatically disabled
  - Data retention extends to 7 days instead of 2 days
- Debug logs are **not persistent** — they are deleted after the configured period and are deleted immediately when debug mode is turned off.
- The server administrator is warned in the CLI when debug mode or debug tools are active.

---

## 6. Data Sharing

No data is shared with third parties.

This project:
- Communicates with Discord API only for: OAuth2 login (authorization code exchange, immediately revoked), bot guild member verification, and channel name synchronization
- Does not collect usage statistics, except for debug logs when debug mode is enabled
- Does not integrate cloud-based tracking services

---

## 7. Server Operator Responsibility

The server operator is responsible for:
- Log retention configuration
- Compliance with applicable local data protection laws
- Secure infrastructure operation
- TLS/HTTPS configuration for encrypted transport

This software provides **technical tools for privacy control** but does not enforce legal decisions.

---

## 8. Open Source Transparency

The complete source code is publicly available.  
All data-relevant functionality is inspectable and auditable.

If in doubt:
> **Code over promises.**

---

## 9. Changes

Any changes affecting data handling or privacy:
- Are documented
- Are clearly stated in the changelog
- Trigger a re-acceptance prompt in the companion app when the policy version is updated

---

## 10. Contact

Questions or concerns regarding privacy and data handling should be submitted via the project's repository.
