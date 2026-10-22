# Azure Monitoring Console // Matrix Operations Node

Willkommen im Grid, Chummer.

Die **Azure Monitoring Console** zieht Azure-Monitor-Alerts aus dem Konzernrauschen, zerlegt ihre Payloads und legt die verwertbaren Signale in einem gehärteten Operations-Node ab. Statt zwischen Portalen, Log-Queries und halbgaren Alarmmeldungen zu springen, bekommt die Crew eine gemeinsame Sicht auf aktive Störungen, historische Ereignisse, betroffene Systeme und kritische Lebenszyklen.

Unter der Chromhaube läuft eine **ASP.NET Core Blazor** WebApp auf **.NET 10**, gespeist von Azure Monitor und Logic Apps und abgesichert durch SQL Server oder Azure SQL. Für Decker mit Vorliebe für die Kommandozeile liegt zusätzlich ein CLI im Repository.

> **Straßenregel Nr. 1:** Vertraue keinem unvalidierten Payload.
>
> **Straßenregel Nr. 2:** Sorge für ein Backup, bevor du das Schema anfasst.
>
> **Straßenregel Nr. 3:** Mach niemals einen Deal mit einem Drachen.

## Was im Node verdrahtet ist

- Live-Inbox für aktive Alerts und Ereignishistorie mit Filtern, Kategorien und frei konfigurierbarer Severity-Darstellung.
- Persönliche Inbox-Rasterlayouts mit verschiebbaren und skalierbaren Bereichen.
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
- Live Log Analytics mit freigegebenen, versionierten Queries und kontrolliertem Workspace-Routing.
- 38 externe AD-Query-Vorlagen mit KQL und voreingestellter Visualisierung.
- Verschachtelte Query-Kategorien, Drag & Drop und ZIP-Export der gespeicherten Query-Konfiguration.
- Persönliche Analyse-Dashboards und administrativ veröffentlichte Dashboard-Snapshots.
- Tabellen, Charts, Stat-Kacheln und Statusverläufe mit konfigurierbaren Farben, Schriftgrößen und Umbrüchen.

### Bildmaterial // Trainingsgelände, kein Konzernleck

Die Screenshots unter [Media](./Media/) stammen aus einer isolierten lokalen Demo-Umgebung mit fiktiven Alerts, Systemen, Benutzern und `.example.test`-Domains. Es wurden keine Azure-Zugangsdaten verwendet und keine Live-Log-Analytics-Abfragen ausgeführt. Die Query-Freigaben in dieser Demo sind vorbereitete Dokumentationsdaten, kein Nachweis einer erfolgreichen Azure-Validierung.

Der Testhost verwendet ausschließlich Loopback und eine separate Demo-Datenbank. Offene Authentifizierung ist hier bewusst auf das lokale Trainingsgelände begrenzt. Für den produktiven Run bleiben Login, Rollen und Workspace-Freigaben Pflicht.

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

![Detailansicht der kategorisierten Demo-Alerts](./Media/InboxDetails.png)

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

![Regelverwaltung für Kategorisierung und Inventory-Rollen](./Media/Rules.png)

## Logbook // Das Gedächtnis der Crew

Das Logbuch unter `/logbook` ist die gemeinsame Chronik des Runs. Jeder angemeldete Benutzer kann dort Kommentare erfassen. Benutzer und UTC-Zeitstempel setzt der Server automatisch; beide Werte können im Formular nicht manipuliert werden.

Zusätzlich schreibt der Node selbst als Benutzer **System**, wenn:

- ein Alert durch eine kritische Regel als kritisch ausgelöst wird,
- ein kritischer Alert aufgelöst wird.

Automatische Einträge enthalten Alertname, Target, Severity und Alert-ID. Doppelt zugestellte Alert-Events werden dedupliziert und erzeugen keine zweite Systemmeldung. Logbucheinträge werden neueste zuerst angezeigt und bleiben als eigenständige Einsatzhistorie erhalten.

![Logbuch mit manuellen Demo-Notizen und automatischen Systemmeldungen](./Media/Logbook.png)

## Alert Graph // Folge dem Datenpfad

Der Graph verwandelt Alarmrauschen in eine konfigurierbare Topologie mit drei Ebenen. Jede Ebene kann unter anderem Subscription, Resource Group, Alertname, Target, Site, Domain oder Rolle verwenden.

Die Darstellung basiert auf `ParsedAlerts` und wird mit `ComputerInventory` angereichert. Korrigiert ein Operator Site, Domain, Rolle oder Resource Group, erscheint die Änderung im Graphen, ohne die historische Original-Payload umzuschreiben. Alte Alerts ohne Parsed-Record werden bei Bedarf nachgezogen.

Der sichtbare Zeitraum verwendet dieselbe konfigurierbare `AlertHistory`-Einstellung wie die Inbox. Standardmäßig zeigt der Node die letzten sieben Tage statt jeden Schatten, der jemals durch die Leitungen gekrochen ist.

Die Standortkarte ergänzt die Hierarchie um geografische Orientierung. Im Topologiegraphen helfen Legenden für aktuelle oder veraltete Heartbeats, Servicezustände und Wartung beim Einordnen der Ziele.

![Graph-Steuerung mit Standortkarte](./Media/Graph.png)

![Topologiegraph mit konfigurierbaren Hierarchieebenen und fiktiven Systemen](./Media/GraphTopology.png)

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
| **Operator** | Reader-Rechte plus Regeln, Computerinventar und Live Analysis mit freigegebenen Queries |
| **Admin** | Operator-Rechte plus Benutzer, Anwendungseinstellungen, Query Registry und Veröffentlichung eigener Dashboards |

Die Grenzen werden serverseitig geprüft und nicht nur im Menü versteckt. Der letzte Benutzer und der letzte Admin sind gegen versehentliche Löschung oder Herabstufung geschützt. Sich selbst aus dem eigenen Host auszusperren ist kein guter Karriere-Move.

![Benutzerverwaltung mit Reader-, Operator- und Admin-Rollen](./Media/User.png)

## Application Settings // Host konfigurieren

Startkritische Einstellungen liegen in `dbo.Settings`. Admins verwalten dort unter anderem:

- Authentifizierungsmodus,
- Aufbewahrungs- und Anzeigezeitraum der Alerts,
- Ebenen des Alert-Graphen,
- Farben und Schriftstile der Severity-Werte.

JSON-Konfigurationen werden vor dem Speichern validiert. Fehlen erforderliche Werte oder ist das Format beschädigt, startet die Anwendung lieber laut als heimlich mit einer unsicheren Ersatzkonfiguration. Änderungen an Startparametern werden nach einem Neustart aktiv.

Die Cross-Tenant-Log-Analytics-Anbindung besitzt zusätzlich eine eigene Konfiguration und Validierung. Ein sichtbarer Query-Katalog ist noch kein offenes Tor in jeden Workspace.

![Datenbankgestützte Anwendungseinstellungen](./Media/Settings.png)

## Query Interface // Direkter Datenjack

Die schreibgeschützte Query-API liefert aktive Alerts und Ereignishistorie für Integrationen, Agenten und Automatisierung. Die Queries-Seite zeigt sofort verwendbare URLs und aktuelle JSON-Antworten. Ideal, um die Leitung zu prüfen, bevor ein weiterer Host angeschlossen wird.

![Query-Beispiele mit aktuellen JSON-Antworten](./Media/Queries.png)

## Live Analysis // Freigegebener Blick hinter das ICE

Unter `/analysis` arbeiten Operatoren mit **veröffentlichten Query-Versionen**, nicht mit beliebigem KQL aus dem Browser. Systeme werden nach Domain, Site und Rolle ausgewählt; der Server routet die Ausführung zu den dafür konfigurierten Workspaces. Rollen, Workspace-Freigaben und Ausführungslimits bleiben an der Leitung.

Standardmäßig gilt `SelectedSystems`: Die Query ist auf die gewählten Computer ausgelegt. Für Daten ohne `Computer`-Spalte, etwa die GPO-/SYSVOL-Statistik, kann ein Admin ausdrücklich `Workspace` freigeben. Die Systemauswahl bestimmt dabei weiterhin das Routing; die Query darf innerhalb dieser Workspaces jedoch auch andere Systeme und Domains erfassen. Ergebnisse verschiedener Workspaces bleiben getrennt. Eine Dummy-Erwähnung von `_SelectedSystems` ist kein Sicherheitskonzept.

Vier Blickwinkel, ein Cockpit: Tabelle mit Statusfarben, LDAP-Waben, Bar-Gauges und Linienchart. Die folgende Dokumentationsvorschau rendert die echten Dashboard- und Visualisierungskomponenten mit synthetischen Ergebnissen, nicht mit ausgeführten Azure-Queries. Keine Konzerntelemetrie im Schaufenster.

![Analyse-Dashboard mit Tabelle, Waben, Bar-Gauges und Linienchart; echte UI-Komponenten mit synthetischen Demodaten](./Media/Analysis.png)

### Dashboards // Das persönliche Cockpit

- Queries aus der Library ins Raster ziehen oder per Tastatur hinzufügen, Widgets verschieben und skalieren.
- Mehrere persönliche Dashboards anlegen, umbenennen und getrennt speichern.
- Layout, Systemauswahl, Widget-Vorgaben und Auto-Refresh pro Dashboard behalten; Bearbeitungen mit Undo/Redo zurücknehmen.
- **Dashboard time range** und **Dashboard averaging interval** für den nächsten Lauf gemeinsam überschreiben oder mit **Use widget settings** die einzelnen Vorgaben behalten.
- Überschrittene Query-Maximalzeiträume als Widget-Fehler sehen statt stillschweigend gekürzte Daten zu erhalten.
- Als Admin eigene Dashboards für die Crew veröffentlichen, aktualisieren oder zurückziehen.

Ein veröffentlichtes Dashboard ist ein schreibgeschützter Snapshot. Spätere private Layoutänderungen gehen erst mit dem nächsten Publish an die Crew. Die Queries darin nutzen weiterhin ihre aktuelle veröffentlichte Query-Version und alle Berechtigungsprüfungen. Dashboard-Publish ist **kein** Query-Publish.

![Analyse-Steuerung mit Dashboard-Verwaltung und Ausführungsparametern; Live Log Analytics ist im Demo-Host deaktiviert](./Media/AnalysisControls.png)

### Query Library // Ordnung im Datenarsenal

Die Library gruppiert freigegebene Queries in aufklappbaren Kategorien und Unterkategorien. Die Crew sieht nur Queries, für die ihre Rolle ausreicht; leere oder vollständig ausgeblendete Zweige verschwinden aus dieser Ansicht. Persönlich ausgeblendete Queries verändern nicht den Katalog der anderen Benutzer.

**Refresh library** übernimmt neue Freigaben und die aktuelle Kategoriestruktur, ohne die ganze Seite neu zu laden. Die Höhe des Library-Bereichs lässt sich anpassen, damit auch ein größerer Werkzeugkasten übersichtlich bleibt.

![Query Library mit verschachtelten AD-Kategorien und vergrößertem Bereich](./Media/QueryLibrary.png)

## Query Registry // Der Waffenschrank der Decker

Admins erstellen, testen, validieren und veröffentlichen Queries unter `/admin/analysis-queries`. KQL, Umfang, Mindestrolle, Zeitraumvorgaben und Visualisierung gehören zur veröffentlichten Query-Version. Ein geänderter Draft schiebt der Crew keine ungeprüfte neue Abfrage unter.

### Kategorien // Schubladen statt Kabelsalat

1. Unter **Manage categories** eine Kategorie anlegen und bei Bedarf eine Elternkategorie wählen.
2. Mit **Add subcategory** tiefer gliedern, etwa `Active Directory/Replication/Errors`.
3. Query-Karten auf eine Kategorieüberschrift ziehen oder die Kategorie-Auswahl an der Karte verwenden.
4. Mit **Rename / move** ganze Zweige einschließlich Unterkategorien und Query-Zuordnungen umbenennen oder verschieben.

Nur leere Kategorien ohne Unterkategorien lassen sich löschen. Der gesamte Kategoriepfad darf höchstens 128 Zeichen lang sein.

**Wichtig:** Kategorien sind aktuelle Organisations-Metadaten. Verschieben und Umbenennen wirken ohne erneute Validierung oder Veröffentlichung; eine bereits offene Library übernimmt sie per **Refresh library**. KQL, Visualisierung, Rolle und Versionsstand werden dadurch nicht verändert. Eine Schublade ersetzt keine Zugriffsberechtigung.

![Query Registry mit Kategorieverwaltung und verschachteltem AD-Baum](./Media/QueryRegistry.png)

### AD Library // 38 Werkzeuge, kein Blindflug

**Install AD query library** liest die einzelnen JSON-Vorlagen aus [AnalysisQueries/ActiveDirectory](./MonitoringApp/AnalysisQueries/ActiveDirectory/) ein. Jede Vorlage enthält Query, Metadaten, Zeitraumvorgaben und Standardansicht. Mit dabei sind unter anderem Health-, Replication-, Performance-, Security-, FSMO- und GPO-/SYSVOL-Auswertungen.

Erneutes Installieren legt **nur fehlende Query-Keys** an. Bereits installierte Queries und eigene Anpassungen werden nicht überschrieben. Geänderte Vorlagendateien ersetzen deshalb auch keine vorhandenen Drafts oder veröffentlichten Versionen. Neue Queries müssen vor dem regulären Einsatz für die vorgesehenen Workspaces getestet, validiert und veröffentlicht werden.

### Visualization Options // Lesbare Signale statt Neonbrei

Die Ansichten `Table`, `LineChart`, `BarChart`, `Honeycomb`, `BarGauge`, `Stat` und `StatusHistory` verwenden eine gemeinsame Darstellungskonfiguration. **Visualization options (JSON)** zeigt sie eingerückt und mit Zeilenumbrüchen an.

Konfigurierbar sind unter anderem Spaltenreihenfolge, Sichtbarkeit, Breite, Anzeigenamen, Einheiten, Nachkommastellen, Text-/Hintergrundfarben, Schwellwerte und Wertzuordnungen einschließlich fehlender Werte.

Für eine FSMO-Tabelle kann eine Spalte beispielsweise so aussehen; im Editor gehört dieser Eintrag in das `columns`-Array:

```json
{
  "name": "PDCEmulator",
  "displayName": "PDC\nEmulator",
  "width": 180,
  "color": "blue",
  "cellDisplay": "ColorText",
  "breakAtFirstDot": true
}
```

`\n` erzwingt den Umbruch im Spaltentitel. `breakAtFirstDot` ersetzt nur den ersten Punkt des angezeigten Zellwerts: Aus `SERVER.domain.example` werden zwei Zeilen mit `SERVER` und `domain.example`. Originalwert und KQL bleiben unverändert; `wrapText` steuert weiterhin das normale Umbrechen der Datenzellen.

Für Stat-Kacheln lassen sich Überschrift und Zahl getrennt von 8 bis 72 Pixeln einstellen, zum Beispiel mit `"statTitleFontSize": 14` und `"statValueFontSize": 28` im Visualisierungsobjekt. Neue Darstellungsvorgaben erreichen Operatoren erst mit der veröffentlichten Query-Version.

![FSMO-Query-Editor mit eingerückter JSON-Darstellung und KQL](./Media/QueryEditor.png)

### Export // Den Werkzeugkasten mitnehmen

**Export queries (ZIP)** lädt alle **gespeicherten Draft-Stände** herunter, auch deaktivierte oder noch nicht veröffentlichte Queries. Pro Query entsteht eine eingerückte UTF-8-JSON-Datei im Format der AD-Vorlagen: inklusive KQL, aktuellem Kategoriepfad, Scope, Mindestrolle, Zeitraum-/Intervallvorgaben und Visualisierung.

Ungespeicherte Editoränderungen sind nicht enthalten. Der Export ist **kein Datenbankbackup**: Workspace-Zuordnungen, IDs, Enabled-/Validierungsstatus, Veröffentlichungen, Versionshistorie und leere Kategorien bleiben draußen. Beim Wiederverwenden gelten die normalen Vorlagenprüfungen und weiterhin die Regel „vorhandene Keys nicht überschreiben“.

Schema, vollständige JSON-Beispiele und Betriebsdetails stehen in der [technischen Dokumentation](./MonitoringApp/README.md).

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

Live Analysis ist ein zusätzlicher, kontrollierter Pfad: ausgewählte Inventarsysteme → Workspace-Routing → freigegebene Query-Version → Log Analytics → konfigurierte Visualisierung. Sie schreibt keine neuen Sensor-Alerts in die Inbox.

Wird ein Original-Alert gelöscht, verschwindet auch sein Parsed-Record. Wird ein Inventareintrag entfernt, bleibt die Alert-Historie bestehen und nur die Inventarverknüpfung wird gelöst.

## Repository // Ausrüstung für den Run

| Pfad | Funktion |
| --- | --- |
| [MonitoringApp/](./MonitoringApp/) | Blazor WebApp, REST API, EF Core und UI |
| [MonitoringApp.Tests/](./MonitoringApp.Tests/) | Unit- und JSON-getriebene Tests |
| [MonitoringApp/AlertDefinitions/](./MonitoringApp/AlertDefinitions/) | Definitionen für aufbereitete Query-Ergebnisse |
| [MonitoringApp/AnalysisQueries/ActiveDirectory/](./MonitoringApp/AnalysisQueries/ActiveDirectory/) | Externe AD-Query-Vorlagen samt Visualisierung |
| [MonitoringApp/LogicApps/](./MonitoringApp/LogicApps/) | Logic-App-Vorlagen und Dokumentation |
| [MonitoringApp/Database/](./MonitoringApp/Database/) | Idempotentes SQL-Bootstrap-Skript |
| [MonitoringApp/Migrations/](./MonitoringApp/Migrations/) | EF-Core-Migrationen |
| [MonitoringApp/DemoData/](./MonitoringApp/DemoData/) | Fiktive Alert-Payloads und Importhilfen |
| [Media/](./Media/) | Screenshots des lokalen Demo-Hosts |
| [AlertConsoleCli/](./AlertConsoleCli/) | Kommandozeilenclient für aktive Alerts |
| [AlertWebAgent/](./AlertWebAgent/) | Hintergrundagent und Teams-Benachrichtigung |
| [PlaywrightDemo/](./PlaywrightDemo/) | Browserbasierte End-to-End-Tests |
| [Shared/](./Shared/) | Gemeinsam verwendete URL-Auflösung |
| [THIRD-PARTY-DEPENDENCIES.md](./THIRD-PARTY-DEPENDENCIES.md) | Inventar externer Komponenten, getrennt nach Produktion, Deployment, Tests und Hilfsprojekten |

## Deployment // Vor dem Einsatz

1. Verbindung zu SQL Server oder Azure SQL konfigurieren.
2. EF-Core-Migrationen anwenden oder [CreateMonitoringDatabase.template.sql](./MonitoringApp/Database/CreateMonitoringDatabase.template.sql) gegen die Zieldatenbank ausführen. Bestehende Datenbanken benötigen auch die aktuellen Schemaänderungen für Query-Visualisierung, Scope und Kategorien.
3. Mindestens einen Admin anlegen, bevor SQL-Authentifizierung aktiviert wird.
4. Alert-Ingestion-Identität und interaktive Benutzer strikt trennen.
5. Logic-App-Berechtigungen und Managed Identities nach dem Prinzip der minimalen Rechte vergeben.
6. Tests ausführen, bevor Produktionsverkehr auf den neuen Build zeigt.
7. Backups prüfen. Nicht nur behaupten, dass es welche gibt.

Für Live Analysis zusätzlich die Cross-Tenant-Verbindung konfigurieren und prüfen, die benötigten Workspaces zuordnen und die Queries dort validieren. Installierte JSON-Vorlagen allein sind noch keine ausführbare Freigabe.

Die Anleitung für lokale Entwicklung, SQL-Provisionierung und App Service steht in [MonitoringApp/README.md](./MonitoringApp/README.md). Demo-Importe nur gegen die beabsichtigte Testumgebung richten; Reset- und SQL-Hilfsskripte vor dem Einsatz auf ihre Zieldatenbank prüfen.

### Performance Monitoring mit Azure Monitor

Die WebApp aktiviert Azure Monitor OpenTelemetry, sobald die App-Service-Einstellung
`APPLICATIONINSIGHTS_CONNECTION_STRING` den Connection String einer Application-Insights-Ressource enthält.
Ohne diese Einstellung bleibt der Export deaktiviert; lokale Starts schlagen deshalb nicht wegen fehlender Telemetrie fehl.

Erfasst werden:

- eingehende ASP.NET-Core-Anfragen und ausgehende `HttpClient`-Abhängigkeiten,
- SQL-Aufrufe über `Microsoft.Data.SqlClient`, einschließlich Dauer und Fehlerstatus,
- Blazor-Navigationen, Event-Handler, Render-Dauer und Render-Größe,
- aktive, verbundene und beendete Blazor-Server-Circuits,
- zentrale Alert- und Logbook-Operationen als benannte `MonitoringApp`-Spans.

Die Daten können in Application Insights unter **Application Map**, **Performance**, **Failures**,
**Transaction search**, **Dependencies** und **Metrics** ausgewertet werden. Connection Strings und
andere Telemetrie-Zugangsdaten dürfen nicht in `appsettings.json` oder das Repository übernommen werden.

Produktionsdaten sind keine Trainingsattrappen. Geheimnisse gehören nicht ins Repository, offene Authentifizierung nicht ins öffentliche Netz und spontane Schemaoperationen nicht in die Hauptverkehrszeit.

## Schlusswort // Bleib wachsam

Die Console soll der Crew helfen, schneller zwischen Matrixrauschen und echtem Infrastrukturbrand zu unterscheiden. Halte Regeln nachvollziehbar, das Inventar sauber, die Migrationen aktuell und das Logbuch ehrlich.

Und wenn ein freundlicher Auftraggeber mit goldenen Augen darum bittet, die Audit-Historie „nur ganz kurz“ abzuschalten: Verbindung trennen, Credstick einpacken und laufen.

**Vorsicht vor Drachen.**
