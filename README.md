# AeroStart

Windows-7-inspiriertes Startmenü für Windows 11, entwickelt mit WPF, C# und .NET 10.

> **Experimentelles Prerelease – keine stabile Freigabe.** Mattglas ist deaktiviert. Die experimentelle Umleitung der einzelnen Windows-Taste hat einen bekannten sporadischen Fehler im Release-Build. Für den regulären Aufruf Strg+Alt+Leertaste oder das Tray-Menü verwenden.

## Experimenteller Freeze

Der eingefrorene Anwendungsstand ist über den annotierten Tag `experimental-freeze-2026-10-24` erreichbar, Commit `71bc1687547db6b675f55fd93962706d61404eea`.

Das Prerelease verwendet diesen unveränderten Tag und bietet ausschließlich die von GitHub erzeugten Quellarchive an, keine fertigen Binärpakete oder Installer. Dokumentationskorrekturen auf dem gleichnamigen Branch können nach dem Freeze liegen; sie ändern weder den Tag noch dessen Quellarchiv. Diese README beschreibt den Funktionsumfang des eingefrorenen Anwendungscodes.

## Funktionen

### Programme und Suche

- Alphabetische Programmliste aus den Startmenü-Ordnern des aktuellen Benutzers und aller Benutzer (`.lnk` und `.url`).
- Suche nach Programmname und ursprünglichem Ordner. Exakte Namen werden bevorzugt; eine Namensheuristik ordnet Hilfs-, Setup- und Deinstallationseinträge nach Hauptprogrammen ein, ohne sie auszublenden.
- Lokale Anheftungen unter **Angeheftet**, darunter **Alle Programme**. Suche filtert beide Abschnitte.
- Rechtsklick: **An Start anheften / Von Start lösen**, **Als Administrator ausführen** für auflösbare EXE-Ziele sowie **Dateispeicherort öffnen**, soweit für den Eintrag verfügbar. UAC-Abbruch wird berücksichtigt.
- Programmicons werden über die Windows-Shell bedarfsgesteuert auf einem STA-Hintergrundthread geladen und für die Sitzung gecacht. Fehlende Icons erhalten ein Ersatzsymbol.

### Windows-Pins: ausschließlich lesender Import

Beim Menüöffnen werden Windows-Start-Pins zusätzlich unter **Angeheftet** eingelesen. Hierfür wird `Export-StartLayout` in einem separaten Prozess mit Timeout verwendet; temporäre Exportdateien werden entfernt. Desktopverknüpfungen und Paket-IDs werden nach Möglichkeit über vorhandene Windows-Shell-Funktionen aufgelöst.

Die Windows-Reihenfolge bleibt vor lokalen Anheftungen erhalten; doppelte Identitäten werden innerhalb dieses Abschnitts zusammengeführt. Der Cache wird bei Menüaufrufen frühestens nach 30 Sekunden erneuert, mit höchstens einem gleichzeitigen Export. Bei Fehlern bleibt der letzte erfolgreiche Sitzungsstand erhalten und ein Hinweis erscheint. Ohne erfolgreichen Import sind lokale Anheftungen weiterhin verfügbar.

**Windows-Pins können durch AeroStart nicht angeheftet oder gelöst werden.** Die lokalen Kontextaktionen verändern keine Windows-Pin-Datenbank.

### Schnellzugriffe und Systemwerkzeuge

Das Menü enthält Zugriffe auf Benutzerordner, Dokumente, Bilder, Explorer, Systemsteuerung, Windows-Einstellungen und den Ausführen-Dialog.

Das abschaltbare Untermenü **Systemwerkzeuge** enthält:

- Task-Manager (`taskmgr.exe`)
- Geräte-Manager, Datenträgerverwaltung und Computerverwaltung
- Dienste, Aufgabenplanung und Ereignisanzeige
- Leistungsüberwachung, Firewall und Netzwerkverbindungen
- Windows Terminal, sofern installiert

**Task-Manager-Schnellzugriff** blendet unabhängig davon ein kleines Symbol oben rechts im Startmenü ein. Sowohl dieses Symbol als auch der feste Untermenüeintrag starten `taskmgr.exe`. Das Abschalten des Schnellzugriffs entfernt den Untermenüeintrag nicht.

### Konto und Energie

Profilbild und Anzeigename öffnen ein Kontomenü mit Windows-Kontoeinstellungen, Kontoverwaltung und dem Link zum Microsoft-Konto. Anzeigename und Kontobild werden lokal ermittelt; fehlende Daten erhalten einen neutralen Ersatz. Eine E-Mail wird nur bei passend ermittelter Windows-Kontoidentität angezeigt. Microsoft-365-Abonnement und OneDrive-Kontingent werden nicht dargestellt; es gibt keine automatische Cloud-Anmeldung.

Sperren, Abmelden, Herunterfahren, Neustarten und Energiesparen rufen echte Windows-Aktionen auf. Abmelden und Energieaktionen haben eine Sicherheitsabfrage; Sperren wirkt unmittelbar. Verfügbarkeit und Berechtigungen einzelner Systemwerkzeuge bestimmt Windows.

## Einstellungen und Darstellung

Alle Einstellungsseiten übernehmen Benutzeränderungen sofort. Die Anwendung speichert ihre Einstellungen atomar unter `%LocalAppData%\AeroStart\settings.json`. Laden und unveränderte Auswahl erzeugen keinen Schreibvorgang. Bei Speicherfehlern erscheint ein Hinweis und die Oberfläche kehrt zum bestätigten Zustand zurück. Autostart wird getrennt direkt in der Registry verwaltet.

| Seite | Einstellungen |
| --- | --- |
| Allgemein | AeroStart- oder Windows-Startmenü; Mit Windows starten |
| Integration | Stabiler Hotkey oder experimentelle Windows-Taste; Startbutton-Overlays; Monitor für Tastaturaufrufe |
| Darstellung | Windows-Designhinweis; Deckkraft; deaktivierter Mattglas-Schalter; Startmenü-Animation |
| Funktionen | Systemwerkzeuge anzeigen; Task-Manager-Schnellzugriff |

Binäre Schalter verwenden einen gemeinsamen Windows-11-inspirierten WPF-Toggle-Stil mit Beschriftung links, Schalter rechts und Tastaturbedienung. Auswahlgruppen bleiben RadioButtons. Einen Speichern-Button gibt es nicht mehr.

- Hell-/Dunkelmodus, Hochkontrast und gegebenenfalls Akzentfarbe folgen Windows.
- Die Deckkraft lässt sich von 30 bis 100 Prozent in Fünferschritten einstellen. Texte und Icons bleiben deckend; Hochkontrast verwendet deckende Systemfarben.
- Das Menü hat eine feste Größe von **650 × 610 WPF-Einheiten**. Die abschaltbare Öffnungs-/Schließanimation verändert nicht die Fenstermaße. Windows-Einstellungen für Oberflächenanimationen und Hochkontrast werden berücksichtigt.
- **Mattglas ist zur Laufzeit gesperrt**, auch bei früher gespeichertem EIN. Der sichtbare, ausgegraute Schalter trägt den Hinweis „Experimentell – derzeit nicht verfügbar“. Es wird kein WinUI-Mattglas-Hintergrundfenster erzeugt. Transparenz ist kein Blur. Experimenteller Mattglas-Code und isolierte Prototypen bleiben im Repository erhalten; daraus folgt keine aktiv verfügbare Produktfunktion.

### Mit Windows starten

Der Schalter liest beim Öffnen der Einstellungen den tatsächlichen Registry-Zustand. Ohne vorhandenen Eintrag ist Autostart aus. Einschalten erstellt oder aktualisiert ausschließlich:

- Schlüssel: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- Wertname: `AeroStart`, Typ `REG_SZ`
- Inhalt: `"vollständiger EXE-Pfad" --background`

Wiederholtes Einschalten verwendet denselben Wert, Ausschalten entfernt nur diesen. Es werden keine Administratorrechte, geplanten Aufgaben oder zusätzlichen Dienste benötigt. Fehler werden angezeigt; ein fehlgeschlagener Schreibvorgang wird nicht als Erfolg übernommen. Nach Verschieben der Anwendung den Eintrag durch erneutes Einschalten aktualisieren. Die Registry-Funktion ist kein Installer: Die verwendete EXE und ihr Ausgabeordner müssen am eingetragenen Ort bestehen bleiben.

## Starten, Tray und Bedienung

| Aufruf | Verhalten |
| --- | --- |
| `AeroStart.exe` | Einstellungen öffnen |
| `AeroStart.exe --background` | Im Infobereich starten, ohne Menü-/Einstellungsfenster |
| `AeroStart.exe --menu` | Ausgewähltes Startmenü öffnen |
| `AeroStart.exe --diagnose-taskbar` | Vorhandene Taskleisten-Diagnose auslösen |

Das Tray-Icon bleibt nach dem Schließen der Einstellungen aktiv und wird bei einem Explorer-Neustart erneut angemeldet. Doppelklick öffnet die Einstellungen. Rechtsklick bietet Menüöffnung, Einstellungen, Aktivieren/Deaktivieren und **Beenden**; im Release-Build zusätzlich die temporäre Windows-Tasten-Diagnose.

**Beenden** entfernt die von AeroStart verwalteten Hooks, Hotkeys und Overlays. Pro Benutzer und Sitzung wird eine Instanz verwendet; weitere Starts leiten entsprechende Aufrufe an sie weiter. Bei deaktiviertem AeroStart oder ausgewähltem Windows-Menü verwendet die Menüaufruflogik das native Windows-Startmenü.

### Tastatur im Menü

- Beim Öffnen erhält das Suchfeld den Fokus.
- Pfeil hoch/runter wählt Treffer, Enter startet die Auswahl; Doppelklick startet ebenfalls.
- Escape leert zuerst die Suche und schließt bei leerer Suche das Menü. Geöffnete Flyouts haben eigene Schließ-/Fokusbehandlung.
- Tab ermöglicht die Navigation zwischen Bedienelementen; Toggles sind mit der Leertaste bedienbar.

### Windows-Taste: bekanntes experimentelles Problem

**Stabil:** Strg+Alt+Leertaste ruft das Menü beim Loslassen der Leertaste auf. Die einzelne Windows-Taste bleibt unverändert. Ein anderweitig belegter Hotkey oder Hookfehler wird in den Einstellungen angezeigt; das Tray bleibt als Alternative verfügbar.

**Experimentell:** Die alleinige linke oder rechte Windows-Taste soll AeroStart beim Loslassen umschalten, sofern AeroStart aktiv und als Menü ausgewählt ist. Die Gestenlogik reicht Win-Kombinationen wie Win+E, Win+R und Win+D weiter; dies ist keine Garantie für jede Shell-, Remapper- oder Sitzungskonfiguration.

**Bekannter Fehler im Release-Build:** Insbesondere nach Explorer-Neustart kann die einzelne Windows-Taste sporadisch das native Startmenü, AeroStart oder beide auslösen. Der Fehler ist nicht behoben. Erfolgreiches `SendInput` beweist nicht die erfolgreiche Unterdrückung der Shell-Aktion. Erhöhte Anwendungen/UIPI, fremde Hooks, Remotedesktop und ein von Windows nach Timeout entfernter Hook sind weitere Grenzen des Verfahrens. Es werden keine Administratorrechte oder dauerhaften Tastenzuordnungen angefordert. Für zuverlässigere Bedienung den stabilen Hotkey oder das Tray verwenden.

## Taskleiste, mehrere Monitore und Grenzen

Optionale Startbutton-Overlays werden auf erkannten primären und sekundären Taskleisten verwaltet. AeroStart verwendet eigene Fenster über eindeutig erkannten Windows-Startbuttons; native Buttons und Explorer werden nicht gepatcht oder ersetzt. Nicht eindeutig erkannte beziehungsweise ausgeblendete Buttons werden ausgelassen. Deaktivieren der Integration entfernt die Overlays. Der bestätigte experimentelle X-Abgleich bleibt `entry.Target.VisualOffsetX - 2`.

Implementiert sind:

- Monitorwahl für Tastaturaufrufe: Mauszeiger, Hauptmonitor oder aktives Fenster.
- Verankerung am angeklickten Startbutton; erneuter Klick bei offenem Menü auf demselben Monitor schließt, auf einem anderen Monitor öffnet dort.
- Physische Bildschirmkoordinaten einschließlich negativer Positionen, PerMonitorV2 und DPI-Umrechnung.
- Begrenzung auf die Arbeitsfläche und Berücksichtigung erkannter Taskleistenkanten; Erneuerung nach Explorer-, Anzeige- und Taskleistenereignissen.

**Keine pauschale Multi-Monitor-Kompatibilitätszusage:** Gemischte DPI, Monitorwechsel/-anschluss, Auto-Hide, Vollbild und besondere Shell-Konfigurationen müssen in der jeweiligen Windows-Sitzung geprüft werden. Die vollständige visuelle Abnahme aller Kombinationen steht aus. Auf Arbeitsflächen kleiner als das feste Menü ist vollständige Sichtbarkeit nicht gewährleistet. Windows-Shell-Geometrie kann nach Explorer-Neustart variieren. Automatisierte Geometrietests ersetzen keine reale Klick-, Vollbild- oder DPI-Sichtprüfung.

## Diagnose und Datenschutzgrenzen

- Die temporäre **Windows-Tasten-Diagnose** im Release-Tray zeigt maximal 30 abgeschlossene Einzel-Win-Einträge im Speicher: Zeiten, Gestenentscheidung, `SendInput`-Ergebnis und Menücallback. Sie speichert keine Datei und protokolliert keine anderen Tastenwerte oder Win-Kombinationen. Das nicht modale Fenster kann nach der Fehlerreproduktion aktualisiert und sein Text kopiert werden. Ein fehlender Eintrag allein beweist keinen Hook-Timeout.
- Die separate **Taskleisten-Diagnose** kann unter `%LocalAppData%\AeroStart\Diagnostics\taskbar.jsonl` protokollieren, auch bei automatischen Fehler-/Positionsdiagnosen. Sie enthält unter anderem Fenster-/Prozessidentitäten, Geometrie und UIA-Befunde. Vor dem Teilen prüfen. Sie ist nicht mit dem flüchtigen Windows-Tastenpuffer gleichzusetzen.
- Der Windows-Pin-Import liest Shell-Daten und verwendet vorübergehende Exportdateien. AeroStart ändert keine Windows-Pin-Datenbank.

## Aus Quellcode bauen

Voraussetzungen:

- Windows 11 x64; das Projekt zielt auf `net10.0-windows10.0.22621.0`.
- .NET-10-SDK und eine passende Visual-Studio-Installation mit .NET-Desktopentwicklung; der Freeze wurde mit Visual Studio Community 2026 gebaut.
- NuGet-Wiederherstellung für Windows App SDK `1.7.250909003` und das referenzierte WinUI-Hostprojekt. Diese Abhängigkeiten bleiben trotz deaktiviertem Mattglas enthalten.

`AeroStart.slnx` öffnen und Debug oder Release für x64 bauen. Für Kommandozeilen-Builds **Visual-Studio-MSBuild** verwenden; ein reiner `dotnet build` kann an den benötigten PRI-/WinUI-Buildaufgaben scheitern.

In einer Visual-Studio-Developer-PowerShell aus dem Repositoryordner:

```powershell
MSBuild.exe .\AeroStart.csproj /restore /t:Build /p:Configuration=Release /v:minimal
.\bin\Release\net10.0-windows10.0.22621.0\win-x64\AeroStart.exe
```

Für Debug `Configuration=Debug` und den entsprechenden Ausgabeordner verwenden. Nicht nur die EXE isoliert kopieren: Der Build benötigt seine erzeugten Abhängigkeiten und Ressourcen. Dieses Prerelease stellt keinen getesteten Installer oder portablen Binärdownload bereit.

## Tests und Prüfung

Die ausführbaren FeatureChecks liegen im Repository. Sie prüfen unter anderem alte Einstellungen, sofortige Persistenz, Fehler-Rücksetzung, Autostart in einer HKCU-Testsandbox, Task-Manager-Katalog/Visibility/Geometrie, Programmkontextaktionen, Windows-Pin-Verarbeitung und gesperrtes Mattglas. Die Tests starten keine Programme, keinen Explorer, keine UAC-Abfrage und installieren keine Tastatur-Hooks; Testfenster können kurz sichtbar werden.

```powershell
MSBuild.exe .\Tests\AeroStart.FeatureChecks\AeroStart.FeatureChecks.csproj /restore /t:Build /p:Configuration=Release /v:minimal
.\Tests\AeroStart.FeatureChecks\bin\Release\net10.0-windows10.0.22621.0\win-x64\AeroStart.FeatureChecks.exe
```

Dieselbe Prüfung ist für Debug möglich. Der zusätzliche Parameter `--autostart-live <vollständiger AeroStart.exe-Pfad>` testet ausdrücklich den echten HKCU-Run-Wert: Der vorherige eigene Wert wird gesichert und anschließend wiederhergestellt; fremde Werte bleiben unangetastet. Ohne diesen Parameter verwendet der Autostarttest eine isolierte Testsandbox. `--windows-pins` führt einen lesenden Live-Export der Windows-Pins aus.

Für den Freeze wurden Debug-/Release-Builds und FeatureChecks erfolgreich ausgeführt; ergänzende Taskleisten-DiagnosticChecks liefen in einer separaten lokalen Prüflösung und sind nicht Bestandteil der Quellarchive. Diese Ergebnisse belegen weder die Behebung des bekannten Windows-Tastenfehlers noch eine vollständige visuelle Freigabe aller Windows-/Monitor-Konfigurationen.

Vor produktiver Nutzung insbesondere Hell-/Dunkelmodus, Tastaturnavigation, Autostartpfad, Task-Manager-Aufrufe, mehrere Monitore, DPI-Wechsel und Explorer-Neustart in der eigenen Umgebung prüfen. Die isolierten Mattglas-Prototypen im Tests-Verzeichnis sind Entwicklungsartefakte, keine freigeschalteten Anwendungsfunktionen.
