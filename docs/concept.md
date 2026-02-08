# Funkkonzept

## Grundprinzip
- Jeder User kann mehreren Funkfrequenzen zugeordnet sein
- Pro Frequenz kann ein eigener Push-to-Talk-Hotkey festgelegt werden (optional)
- Hotkeys können auch wieder gelöscht werden
- Empfang erfolgt passiv (Mithören mehrerer Frequenzen möglich)
- Senden erfolgt in der Regel gezielt auf eine Frequenz
- Send-All Funktion auf ausgewählte Frequenzen mit ausgewähltem Hotkey Broadcasten
- Ton bei beginn und Ende des Funkspruchs
- Companion App (.NET) auf dem PC läuft lokal und greift Audio und Hotkeys ab und sendet diese an den Server

## Funklogik
- Nur ein aktiver Sender je Frequenz (wenn möglich)
- Keine Rückkopplung in Discord Voice Channels
- MuteDiscord während PTT

## Nutzerverhalten
- User bleibt im Gruppen-Voice-Channel
- Funk ist davon logisch getrennt
- Hotkeys bestimmen die aktive Funkfrequenz
- Rechteverwaltung pro Frequenz? (late late Beta content)