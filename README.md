# Azure Monitoring Console // Matrix Operations Node

Willkommen im Grid, Chummer.

Die **Azure Monitoring Console** zieht Azure-Monitor-Alerts aus dem Konzernrauschen, zerlegt ihre Payloads und legt die verwertbaren Signale in einem gehärteten Operations-Node ab. Statt zwischen Portalen, Log-Queries und halbgaren Alarmmeldungen zu springen, bekommt die Crew eine gemeinsame Sicht auf aktive Störungen, historische Ereignisse, betroffene Systeme und kritische Lebenszyklen.

Unter der Chromhaube läuft eine **ASP.NET Core Blazor** WebApp auf **.NET 9**, gespeist von Azure Monitor und Logic Apps und abgesichert durch SQL Server oder Azure SQL. Für Decker mit Vorliebe für die Kommandozeile liegt zusätzlich ein CLI im Repository.

> **Straßenregel Nr. 1:** Vertraue keinem unvalidierten Payload.
>
> **Straßenregel Nr. 2:** Sorge für ein Backup, bevor du das Schema anfasst.
>
> **Straßenregel Nr. 3:** Mach niemals einen Deal mit einem Drachen.

## Was im Node verdrahtet ist

- Live-Inbox für aktive Alerts und Ereignishistorie mit Filtern, Kategorien und frei konfigurierbarer Severity-Darstellung.
- Zwei 48-Stunden-Timelines für ausgelöste Alerts und gleichzeitig aktive kritische Alerts.
- Normalisierte Speicherung in `ParsedAlerts` zusätzlich zur unveränderten Original-Payload in `Alerts`.
- Kritische Alert-Lebenszyklen mit automatischer Erkennung von Fired und Resolved.
- SQL-basiertes Logbuch für manuelle Einsatznotizen und automatische Systemmeldungen.
- Dreistufiger Infrastrukturgraph aus Parsed Alerts und korrigierten Inventardaten.
- Wartbares Computerinventar für Subscription, Resource Group, Computer, Site, Domain und Rolle.
- Datenbankgesteuerte Regeln für Kategorisierung, Unterdrückung, Kritikalität und automatische Rollenzuweisung.
- SQL-Authentifizierung mit gesalzenen PBKDF2-HMAC-SHA256-Passworthashes.
- Serverseitige Rollen und Berechtigungen für Reader, Operator und Admin.
- Datenbankgestützte Anwendungseinstellungen mit Validierung beim Start.
- Query-Endpunkte, kontrollierte Copilot-Prompt-Erzeugung, Logic-App-Workflows und CLI.

## Alert Inbox // Jack in

Die Inbox ist das AR-Overlay für den laufenden Einsatz. Sie zeigt aktive Zustände und historische Events, unterstützt Text-, Ziel-, Subscription-, Resource-Group- und Zeitfilter und gruppiert Signale anhand priorisierter Regeln aus der Datenbank.

Die erste Timeline zählt ausschließlich **Fired**-Ereignisse. Direkt darunter zeigt eine rote 48-Stunden-Linie, wie viele kritische Alert-Lebenszyklen zu jedem Zeitpunkt aktiv waren. Ein späteres Resolved-Event beendet den kritischen Zeitraum, ohne die vorherigen Spuren aus der Matrix zu löschen.

Query-Ergebnisse wie DCDiag oder Portprüfungen werden lesbar aufbereitet. Kommentare können direkt am Alert gespeichert werden. Jeder neue oder geänderte Inbox-Kommentar erzeugt zusätzlich einen Logbucheintrag mit:

- angemeldetem Benutzer,
- automatischem UTC-Zeitstempel,
- Alertname,
- Targetname,
- Kommentartext.

Unverändertes erneutes Speichern erzeugt keinen Doppelgänger im Logbuch. Ein leerer Kommentar wird ebenfalls nicht als Logbucheintrag materialisiert. Geisterdaten sind schon schlimm genug.

![Alert Inbox mit Filtern, Heatmap und kategorisierten Alerts](./Media/Inbox.png)

## Regelwerk // ICE gegen das Rauschen

Regeln liegen in `AlertRules` und werden nach aufsteigender `Priority` ausgewertet. Die erste passende Kategorisierungsregel gewinnt.

### Kategorisierungsbedingungen

- `RowCountGreaterThan`: Die Zahl der Query-Zeilen muss größer als der konfigurierte Schwellwert sein.
- `OnlyFailedItems`: Mindestens einer der kommagetrennt angegebenen Einträge muss fehlschlagen; kein Fehler darf außerhalb dieser Liste liegen.
- `NoFailedItems`: Im dargestellten Ergebnis darf kein fehlgeschlagener Eintrag vorhanden sein.

Bei `OnlyFailedItems` passt zum Beispiel `DFSREvent, KCCError` auf:

- nur `DFSREvent`,
- nur `KCCError`,
- `DFSREvent` und `KCCError` gemeinsam, unabhängig von Reihenfolge und Groß-/Kleinschreibung.

Kommt zusätzlich ein nicht erlaubter Fehler vor, schlägt die Regel nicht an. Bekannte gesunde oder isolierte Ergebnisse können so in **Suppressed alerts** landen, während echte Mehrfachstörungen sichtbar bleiben.

Eine Kategorisierungsregel kann außerdem als **Critical** markiert werden. Die eingebaute System-Outage-Regel nutzt das bereits. Kritikalität und Auflösungszeit werden über alle Events mit derselben Alert-ID synchronisiert.

Inventory-Role-Regeln laufen separat. Die mitgelieferten Regeln erkennen DCDiag- und Replication-Signale und weisen dem Ziel die Rolle `domaincontrollers` zu.

## Logbook // Das Gedächtnis der Crew

Das Logbuch unter `/logbook` ist die gemeinsame Chronik des Runs. Jeder angemeldete Benutzer kann dort Kommentare erfassen. Benutzer und UTC-Zeitstempel setzt der Server automatisch; beide Werte können im Formular nicht manipuliert werden.

Zusätzlich schreibt der Node selbst als Benutzer **System**, wenn:

- ein Alert durch eine kritische Regel als kritisch ausgelöst wird,
- ein kritischer Alert aufgelöst wird.

Automatische Einträge enthalten Alertname, Target, Severity und Alert-ID. Doppelt zugestellte Alert-Events werden dedupliziert und erzeugen keine zweite Systemmeldung. Logbucheinträge werden neueste zuerst angezeigt und bleiben als eigenständige Einsatzhistorie erhalten.

## Alert Graph // Folge dem Datenpfad

Der Graph verwandelt Alarmrauschen in eine konfigurierbare Topologie mit drei Ebenen. Jede Ebene kann unter anderem Subscription, Resource Group, Alertname, Target, Site, Domain oder Rolle verwenden.

Die Darstellung basiert auf `ParsedAlerts` und wird mit `ComputerInventory` angereichert. Korrigiert ein Operator Site, Domain, Rolle oder Resource Group, erscheint die Änderung im Graphen, ohne die historische Original-Payload umzuschreiben. Alte Alerts ohne Parsed-Record werden bei Bedarf nachgezogen.

Der sichtbare Zeitraum verwendet dieselbe konfigurierbare `AlertHistory`-Einstellung wie die Inbox. Standardmäßig zeigt der Node die letzten sieben Tage statt jeden Schatten, der jemals durch die Leitungen gekrochen ist.

![Topologiegraph mit konfigurierbaren Hierarchieebenen](./Media/Graph.png)

## Computer Inventory // Lokale Wahrheit

Telemetry ist nicht immer gelogen, aber häufig unvollständig. Das Inventar hält deshalb die wartbare Wahrheit über entdeckte Systeme:

- Subscription
- Resource Group
- Computer
- Site
- Domain
- Rolle

Neue Alerts entdecken Systeme und ergänzen fehlende Werte. Bereits manuell korrigierte Angaben werden nicht blind überschrieben. Das Inventar kann aus der vorhandenen Alert-Historie vorgefüllt und nach allen wichtigen Feldern gefiltert werden.

![Computerinventar mit Metadaten und Filtern](./Media/Inventory.png)

## Authentication // Wards vor dem Host

Der Datensatz `Authentication` in `dbo.Settings` bestimmt den Zugangsmodus:

- `sql`: sicherer Standard mit Konten aus `AuthenticationUsers`.
- `open`: anonymer Zugriff, nur für kontrollierte Initialisierung oder isolierte Entwicklung.

Passwörter landen niemals als Klartext in der Datenbank. Sie werden als versionierte, gesalzene PBKDF2-HMAC-SHA256-Hashes mit hohem Arbeitsfaktor gespeichert. Das Login-Cookie schützt Weboberfläche und interaktive Endpunkte. Die Alert-Ingestion besitzt davon unabhängig ihre eigene Bearer-Token-Policy für Logic Apps und Managed Identities.

### Zugriffsrollen

| Rolle | Matrixzugriff |
| --- | --- |
| **Reader** | Inbox, Graph, Queries, Logbook und Copilot Prompt |
| **Operator** | Reader-Rechte plus Regeln und Computerinventar |
| **Admin** | Operator-Rechte plus Benutzer- und Anwendungseinstellungen |

Die Grenzen werden serverseitig geprüft und nicht nur im Menü versteckt. Der letzte Benutzer und der letzte Admin sind gegen versehentliche Löschung oder Herabstufung geschützt. Sich selbst aus dem eigenen Host auszusperren ist kein guter Karriere-Move.

![Benutzerverwaltung mit Reader-, Operator- und Admin-Rollen](./Media/User.png)

## Application Settings // Host konfigurieren

Startkritische Einstellungen liegen in `dbo.Settings`. Admins verwalten dort unter anderem:

- Authentifizierungsmodus,
- Aufbewahrungs- und Anzeigezeitraum der Alerts,
- Ebenen des Alert-Graphen,
- Farben und Schriftstile der Severity-Werte.

JSON-Konfigurationen werden vor dem Speichern validiert. Fehlen erforderliche Werte oder ist das Format beschädigt, startet die Anwendung lieber laut als heimlich mit einer unsicheren Ersatzkonfiguration. Änderungen an Startparametern werden nach einem Neustart aktiv.

![Datenbankgestützte Anwendungseinstellungen](./Media/Settings.png)

## Query Interface // Direkter Datenjack

Die schreibgeschützte Query-API liefert aktive Alerts und Ereignishistorie für Integrationen, Agenten und Automatisierung. Die Queries-Seite zeigt sofort verwendbare URLs und aktuelle JSON-Antworten. Ideal, um die Leitung zu prüfen, bevor ein weiterer Host angeschlossen wird.

![Query-Beispiele mit aktuellen JSON-Antworten](./Media/Queries.png)

## Copilot Handoff // Mensch in der Schleife

Die Copilot-Prompt-Seite baut aus ausgewählten Alerts einen kontrollierten Incident-Snapshot. Der Operator bestimmt Zeitraum, Signale und Detailtiefe. Nichts wird automatisch an einen externen Dienst übertragen; der fertige Prompt bleibt sichtbar und wird bewusst kopiert.

Auch in der Matrix gilt: Ein Agent ist ein Werkzeug, kein Johnson, dem man blind vertraut.

![Vorbereitung eines Copilot-Prompts mit Alert-Auswahl](./Media/Prompt.png)

## Datenfluss // Vom Sensor zum Straßenplan

1. Azure Monitor löst einen Alert aus.
2. Eine Logic App kann die Common-Alert-Schema-Payload um Query-Ergebnisse anreichern.
3. `POST /api/alerts` authentifiziert, validiert und dedupliziert das Event.
4. Die Originalmeldung landet in `Alerts`; die normalisierte Projektion wird in `ParsedAlerts` gespeichert.
5. Das Ziel wird mit `ComputerInventory` verbunden und gegebenenfalls durch Rollenregeln ergänzt.
6. Kritische Regeln bestimmen Kritikalität und Lebenszyklus.
7. Fired- und Resolved-Events kritischer Alerts erzeugen transaktional Systemeinträge im Logbuch.
8. Inbox, Timelines und Graph lesen dieselbe persistierte Lage aus unterschiedlichen Blickwinkeln.

Wird ein Original-Alert gelöscht, verschwindet auch sein Parsed-Record. Wird ein Inventareintrag entfernt, bleibt die Alert-Historie bestehen und nur die Inventarverknüpfung wird gelöst.

## Repository // Ausrüstung für den Run

| Pfad | Funktion |
| --- | --- |
| `MonitoringApp/` | Blazor WebApp, REST API, EF Core und UI |
| `MonitoringApp.Tests/` | Unit- und JSON-getriebene Tests |
| `MonitoringApp/AlertDefinitions/` | Definitionen für aufbereitete Query-Ergebnisse |
| `MonitoringApp/LogicApps/` | Logic-App-Vorlagen und Dokumentation |
| `MonitoringApp/Database/` | Idempotentes SQL-Bootstrap-Skript |
| `MonitoringApp/Migrations/` | EF-Core-Migrationen |
| `AlertConsoleCli/` | Kommandozeilenclient für aktive Alerts |
| `AlertWebAgent/` | Hintergrundagent und Teams-Benachrichtigung |
| `PlaywrightDemo/` | Browserbasierte End-to-End-Tests |
| `Shared/` | Gemeinsam verwendete URL-Auflösung |

## Deployment // Vor dem Einsatz

1. Verbindung zu SQL Server oder Azure SQL konfigurieren.
2. EF-Core-Migrationen anwenden oder `MonitoringApp/Database/CreateMonitoringDatabase.template.sql` gegen die Zieldatenbank ausführen.
3. Mindestens einen Admin anlegen, bevor SQL-Authentifizierung aktiviert wird.
4. Alert-Ingestion-Identität und interaktive Benutzer strikt trennen.
5. Logic-App-Berechtigungen und Managed Identities nach dem Prinzip der minimalen Rechte vergeben.
6. Tests ausführen, bevor Produktionsverkehr auf den neuen Build zeigt.
7. Backups prüfen. Nicht nur behaupten, dass es welche gibt.

Produktionsdaten sind keine Trainingsattrappen. Geheimnisse gehören nicht ins Repository, offene Authentifizierung nicht ins öffentliche Netz und spontane Schemaoperationen nicht in die Hauptverkehrszeit.

## Schlusswort // Bleib wachsam

Die Console soll der Crew helfen, schneller zwischen Matrixrauschen und echtem Infrastrukturbrand zu unterscheiden. Halte Regeln nachvollziehbar, das Inventar sauber, die Migrationen aktuell und das Logbuch ehrlich.

Und wenn ein freundlicher Auftraggeber mit goldenen Augen darum bittet, die Audit-Historie „nur ganz kurz“ abzuschalten: Verbindung trennen, Credstick einpacken und laufen.

**Vorsicht vor Drachen.**
