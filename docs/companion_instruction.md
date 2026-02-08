
# Anforderungen und eingesetzte Installer-Technologie

Dieses Dokument beschreibt die Anforderungen und die empfohlene Installer-Technologie für die **das-KRT Companion App** unter Windows 11.

Der resultierende Installer soll den gängigen Windows-Konventionen entsprechen (Einträge im Startmenü, sauberer Uninstall, kein manuelles ZIP-Entpacken) und gleichzeitig die projektspezifischen Anforderungen erfüllen.

---

## Zielplattform

- **Betriebssystem:** Windows 11 (64 Bit)
- **Architektur:** x64
- **Runtime:** .NET 8 (Windows Desktop Runtime)
- **App-Typ:** WPF Desktop Application

---

## Grundlegende Anforderungen an den Installer

### Installationsformat
- Klassischer **Windows-Installer (.exe)**
- Kein portables ZIP
- Integrierter Uninstaller
- Geeignet für Endnutzer ohne technische Vorkenntnisse

### Installationsverzeichnis
- Installationspfad wird im Setup abgefragt
- Standardpfad:
  
  ```
  %ProgramFiles%\das-KRT_com
  ```

- Technische Umsetzung über eine Auto-Konstante, sodass:
  - bei vorhandenen Rechten nach *Program Files* installiert wird
  - ansonsten automatisch ein benutzerbezogener Programmpfad verwendet wird

### Administratorrechte
- **Keine Administratorrechte erforderlich**
- Keine UAC-Abfrage erzwingen
- Installation muss vollständig im User-Kontext funktionieren

Begründung:
- Keine Systemdienste
- Keine Treiber
- Kein Kernel-Zugriff
- Discord Ducking / Mute funktioniert ohne erhöhte Rechte

---

## Autostart

### Verhalten
- Autostart ist **optional**
- Standardmäßig **deaktiviert**
- Auswahl erfolgt im Installer per Checkbox

### Technische Umsetzung
- Autostart über Benutzer-Startup-Ordner:
  
  ```
  %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
  ```

- Keine Registry-Run-Keys
- Nur für den aktuell installierenden Benutzer

---

## Desktop- und Startmenüintegration

### Startmenü
- Immer vorhanden
- Eintrag:
  - **das-KRT.com Companion App**
- Optionaler zusätzlicher Eintrag:
  - „Deinstallieren …“

### Desktop-Verknüpfung
- Optional auswählbar
- Standard: **deaktiviert**
- Erstellung nur bei expliziter Auswahl

---

## .NET Runtime Strategie

### Bevorzugter Ansatz
- **Framework-dependent Build**
- Erwartet installierte:
  - .NET 8 Windows Desktop Runtime (x64)

### Gründe
- Deutlich kleinere Installergröße
- Saubere Trennung zwischen App und Runtime
- Geeignet für Alpha- und Testphase

### Hinweise
- Automatische Runtime-Installation würde Adminrechte erfordern
- Für aktuelle Testphase ist eine dokumentierte Voraussetzung akzeptabel

---

## Empfohlene Installer-Technologie

### Inno Setup (Version 6.x)

**Begründung:**

- Aktiv gepflegt
- Sehr stabil
- Weit verbreitet
- Ideal für klassische Desktop-Installer
- Sehr gute Unterstützung für:
  - Non-Admin-Installationen
  - optionale Tasks (Autostart, Desktop-Icon)
  - sauberen Uninstall
- Kein MSI-Zwang

---

## Wesentliche Installer-Eigenschaften

- Windows-11-konform
- Saubere Registrierung in „Apps & Features“
- Vollständige Entfernung bei Deinstallation
- Kein Zurücklassen von Autostart-Einträgen oder Verknüpfungen
- Keine systemweiten Änderungen

---

## Ergebnis

Der Installer:

- entspricht gängigen Windows-Konventionen
- benötigt keine Administratorrechte
- bietet optionale Komfortfunktionen (Autostart, Desktop-Icon)
- ist für Endnutzer verständlich und robust
- eignet sich ideal für Alpha- und Testphasen

---

**Status:** Spezifikation abgeschlossen  
**Nächster Schritt:** Umsetzung & Test auf Windows 11
