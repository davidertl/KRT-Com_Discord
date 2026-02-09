# Privacy Policy

This project is a **self-hosted open-source software**.  
Responsibility for operation, configuration, and legal compliance lies entirely with the **server operator**.

This document explains **which data may be processed**, **for what purpose**, and **how it can be controlled or deleted**.

---

## 1. Principles

This project follows a **privacy-by-design** and **data minimization** approach:

- Only data strictly required for technical operation is processed
- No hidden data collection
- No telemetry, analytics, or “phone-home” behavior, contact server only when status changes or user action occurs.
- All data remains **exclusively on the operator’s server**

---
# 1.1 Transparency

when installing and updating the software the user needs to read and agree the privacy policy can be fetched after entering ip adress and port in the companion app, so that the user can make an informed decision about using the software. if the privacy policy is updated, please also display a notification in the companion app and require the user to read and accept the updated privacy policy before connecting to the server again.
within this pricacy policy please include which data is processed and what for. also show the current status of the server when entering the ip and port, so the user can immediately see if the server is in debug mode or dsgvo compliance mode.
after reading and accepting the tos and privacy policy the user can choose to login with his disgard the login gets cancelled.

## 2. What data is processed?

### 2.1 User Identifiers
- external identifiers (e.g. Discord ID), **never stored in plain text**, only mapped or hashed
The discord_username is stored in the database for display purposes, but it is not used for authentication or any critical functionality. It can be changed by the user in the discord server so it does not replicate his real discord username. old usernames are not stored in the database, so there is no history of username changes, only the latest username is stored.
guild IDs are stored for frequency mapping and are used for authentication, but they are not linked to any user data and are not stored in plain text. they are only used to determine if a user is allowed to connect to the voice server and to which frequencies they have access to.
guildrules will be stored in the database, and are only processed for acl and give certain. only the latest rank is stored.
No real names, email addresses, or personal profile data are required or stored.

---

### 2.2 Authentication & Sessions
- Temporary authentication tokens
- Active sessions
- Expiration timestamps

These data are **time-limited** and automatically invalidated.

---

### 2.3 Logs
Depending on server configuration, the following technical logs may be created:
- Connection events
- Error messages
- System and debug events
- if debug mode is enabled this shows in the companion app with the set duration the files are saved now. 
- also if dsgvo compliance mode is enabled, the logs are automatically deleted after the set duration and dsgvo mode is displayed in the companion app.

**Logs never contain:**
- Audio data
- Voice content
- Persistent IP histories
- ptt speech events send the channel-id and the username of the user that triggered the event, but this information is not stored in the database and is only used to display the correct information in the companion app.
- when using emergency frequencies, the client sends extra information to other clients that are also listening to emergency frequencies. this information includes the username, the channel name and the frequencys the user has active and listening. this information is only stored in memory and is only visible for a set of new entries on each client recieving it. it is only used to display the correct information in the companion app when listening to an emergency call. this information is not stored in the database and is not logged except in debug mode.

Log retention duration is **fully configurable by the server operator** and can be disabled entirely.

---

### 2.4 Audio & Radio Communication
- Audio data is **never recorded or stored**
- Voice transmission is **live-only**
- Push-to-Talk events and radio states are **ephemeral**

There is **no technical capability** to reconstruct past conversations.

---

## 3. Data Retention

All data retention periods are **configurable by the server operator**, for example:
- No retention
- Short-term retention (e.g. 7 days)
- Extended retention (e.g. 30 / 90 days)

Short or disabled retention is strongly recommended.
in the companion app, the server status is displayed in the server tab, so that the user can see the current retention settings and the dsgvo compliance mode. if the dsgvo compliance mode is disabled please include a additional warning before logging in and disable the automatic login, so that the user is aware of the current settings and can make an informed decision about logging in. if the dsgvo compliance mode is enabled, please also display its state. 

---

## 4. Data Deletion

### 4.1 User Data Deletion
A **hard delete** can be executed for any User ID, removing:
- Logs
- Sessions
- Tokens
- Channel and radio mappings

Deletion is **irreversible** and should be used with caution. the administrator can set a ban for the user, so that the user can not log in again after his data has been deleted. this is recommended to prevent the user from creating a new session and generating new data after deletion. the ban should be stored in a separate table with only the user ID and the timestamp of the ban, so that it does not contain any personal data and is only used to prevent logins from deleted users.

---

### 4.2 Relation to Bans
A full deletion operation technically results in:
- Complete removal of all stored data
- Automatic addition of the User ID to a **ban list**

The ban list stores **minimal information only**:
- hashed DiscordUserID
- Timestamp
- Optional reason

Unbanning a user **does not restore deleted data**.

---

## 5. Debugging & Maintenance

- Debug functionality is **enabled on installation**, but will be toggled off before public release if not needed for ongoing maintenance.
- Debug logs are **not persistent**. they will be deleted after a set period even in debug mode and get deleted immediately when debug mode is turned off. this is to prevent the accumulation of sensitive data in debug logs and to ensure that debug mode does not lead to unintended data retention.


---

## 6. Data Sharing

No data is shared with third parties.

This project:
- gets data from discord bot api and from the client when logging in. these data gets compared for easy login.
- does not collect usage statistics, except for debug logs when debug mode is enabled.
- does not integrate cloud-based tracking services

---

## 7. Server Operator Responsibility

The server operator is responsible for:
- log retention configuration
- compliance with applicable local data protection laws
- secure infrastructure operation

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
- are documented
- are clearly stated in the changelog

---

## 10. Contact

Questions or concerns regarding privacy and data handling should be submitted via the project’s repository.
