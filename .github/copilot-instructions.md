# Copilot Instructions

## Projektrichtlinien
- Für AeroStart-Mattglas ist ein separates nicht-layered Hintergrundfenster ausdrücklich zulässig; ein fehlgeschlagener einzelner DWM-Prototyp darf nicht als grundsätzliche Unmöglichkeit des Umbaus bewertet werden. Produktintegration erst nach erfolgreichem visuellem Blur-Nachweis.
- Bei AeroStart sind Taskleisten-Overlay und insbesondere die bestehenden acht Pixel unangetastet zu lassen. Blur-Integration zunächst ausschließlich isoliert mit visuellem Nachweis prüfen; keine produktive Integration ohne anschließende Benutzerbeurteilung.
- Bei aktiviertem Mattglas muss die Öffnungs-/Schließtransition exakt die bestehende Richtung ohne Mattglas erhalten: Öffnen von unten nach oben an der Taskleiste, kein Aufziehen von einem Punkt in beide Richtungen. Taskleisten-Overlay und +8-Pixel-Offset unverändert lassen.
- AeroStart-Darstellungsmodi: Mattglas EIN verwendet echten Acrylic-Blur mit Hintergrund-Alpha immer 255; Deckkraftregler deaktiviert, gespeicherter Wert bleibt erhalten. Mattglas AUS verwendet unverändert Transparenz 30–100 % ohne Acrylic-Hintergrund. Rundungen erst blur-kompatibel nachweisen; keinen SetWindowRgn-Rückgriff oder visuellen Kompromiss ohne Rückfrage. Taskleisten-Overlay/+8-Pixel-Offset unverändert.
- Bei AeroStart-Positionsregressionen keine neuen Positions-Offsets einführen; Taskleisten-Overlay und manuellen +8-Pixel-Fix unverändert lassen. Abweichungen zuerst gegen Git-Checkpoint und letzten bestätigten Stand konkret berichten, vor einer Korrektur.

## Diagnoseanweisungen
- Für die aktuelle AeroStart-Windows-Tasten-Diagnose ausschließlich Release verwenden; Debug funktioniert laut Benutzer zuverlässig. Diagnose ohne Debugger-Attach oder Prozesspause über temporären Tray-Eintrag mit Diagnosefenster, maximal 30 relevante Win-Key-Ereignisse im Speicher, keine anderen Tasten oder dauerhafte Protokollierung. Hook-Verhalten und Position VisualOffsetX - 2 unverändert lassen.