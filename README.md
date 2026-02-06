# KRT-Com – Funkkommunikation für Discord

## Projektziel
KRT-Com ist eine Funk-ähnliche Kommunikationslösung für Discord, angelehnt an TeamSpeak-Funkplugins.
User sollen **parallel** mit mehreren Gruppen kommunizieren können, ohne den Voice-Channel zu wechseln.

Der Fokus liegt auf:
- Push-to-Talk pro Funkfrequenz
- Parallelem Mithören mehrerer Frequenzen
- Half-Duplex-Kommunikation
- Realistischer Funklogik statt klassischem Voice-Chat

## Kernanforderungen
- Funktioniert **innerhalb eines Discord Servers**
- Kommunikation ohne Voice-Channel-Wechsel
- Mehrere Push-to-Talk-Hotkeys (Frequenzen 1–5)
- Senden & Empfangen pro Frequenz
- Half-Duplex (kein gleichzeitiges Sprechen)
- Windows-Client
- Optional externer Server (Debian)

## Technische Grundidee
- Discord Bot übernimmt Routing & Status
- Audio darf **nicht** direkt in den Discord Voice Channel gesendet werden
- Funkkommunikation wird logisch vom Discord-Voice getrennt
- Discord-Status kann Reichweite / Erreichbarkeit beeinflussen

## Optional / Zukunft
- Subgruppen innerhalb einer Frequenz (Hash-basiert)
- Status-Sync mit externen Systemen (z. B. Mumble)
- Externer Audio-Mixer / Relay-Server
- Erweiterte ACLs für Funkfrequenzen

## Einschränkungen
- Kein Voll-Duplex
- Keine klassische Teilnehmerliste notwendig
- UI zweitrangig, Funktionalität priorisiert
