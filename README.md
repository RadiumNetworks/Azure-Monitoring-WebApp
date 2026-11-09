# Azure Monitoring Console // Matrix Operations Node

Willkommen im Grid, Chummer.

Mehrere Tenants. Flackernde Warnlampen. Irgendwo im Datenstrom hinterlässt ein aktiver Decker seine Spuren.

Die **Azure Monitoring Console** bringt Azure-Monitor-Meldungen, Inventar und Log-Analytics-Auswertungen in einen gemeinsamen Node. **Multi-Tenant-Monitoring statt Portalslalom. Dashboards statt Blindflug hinter dem ICE.** Die Crew sieht, wo Systeme ausfallen, Signale zusammenlaufen und sich ein genauerer Blick lohnt.

> **Straßenregel Nr. 1:** Nicht jedes Signal ist ein Infrastrukturbrand.
>
> **Straßenregel Nr. 2:** Halte die Crew auf demselben Stand.
>
> **Straßenregel Nr. 3:** Mach niemals einen Deal mit einem Drachen.

## Was im Node verdrahtet ist

Unter der Chromhaube läuft eine **ASP.NET Core Blazor WebApp auf .NET 10** mit **SQL Server oder Azure SQL** als Datenspeicher. Azure Monitor und Logic Apps liefern die Meldungen; die Console verbindet sie mit Inventar, Regeln und Analyse.

- **Multi-Tenant-Monitoring // Ein Node, mehrere Konzernnetze:** Meldungen zentral verfolgen und Log-Analytics-Abfragen automatisch zum passenden Workspace routen.
- **Dashboards // Aktiven Deckern auf der Spur:** Persönliche Cockpits aus freigegebenen KQL-Abfragen bauen, Zeiträume vergleichen, automatisch aktualisieren und für die Crew veröffentlichen. Auffälligkeiten untersuchen statt im Lograuschen stochern.
- **Workspace- und Domänenwechsel // Den richtigen Sektor aufschalten:** Systembezogene Widgets nach Workspace, Domäne, Standort und Rolle filtern, ohne die gespeicherte Systemauswahl anzutasten.
- **Alert Inbox und Timelines // Das Frühwarnradar:** Aktive Meldungen, Ereignishistorie und zwei 48-Stunden-Verläufe mit Filtern, Severity-Farben und flexiblem Raster.
- **Graph und Inventar // Die Karte hinter dem ICE:** Systeme, Domänen, Standorte und Rollen in Topologie, Standortkarte und gepflegtem Inventar verbinden.
- **Regelwerk // Weniger Rauschen, mehr Signal:** Meldungen kategorisieren, bekannte Situationen unterdrücken, kritische Störungen hervorheben und eigene KQL-Prüfungen zeitgesteuert auswerten.
- **Query Library // Das Datenarsenal:** 38 AD-Vorlagen, versionierte Abfragefreigaben, verschachtelte Kategorien, Drag & Drop und ZIP-Export.
- **Visualisierungen // Mehr als Neon:** Tabellen, Linien, Balken, Waben, Gauges, Kennzahlen und Statusverläufe mit anpassbaren Farben und Beschriftungen.
- **RCA und Logbuch // Keine Spur geht verloren:** Gemeinsame Untersuchungsfälle, Zuständigkeit, Status, Kommentare und Einsatzhistorie; gespeicherte RCA-Prompts zur manuellen Analyse.
- **Copilot Handoff // Mensch am Deck:** Kontext für eine bewusste, manuelle Übergabe vorbereiten. Keine automatische Jagd, kein autonomer Decker-Detektor.
- **Zugriff und Betrieb // Wards am Eingang:** Reader-, Operator- und Admin-Rechte, zentrale Einstellungen, REST-Schnittstellen, CLI und optionales Azure-Monitor-Anwendungsmonitoring.

## Das Cockpit // Was die Crew bekommt

Unter **Overview** stehen **Inbox**, **Graph** und **Dashboards** bereit. **Analyze** führt zu Queries, Logbook, Root Cause Analysis und Analysis Workspace. Inventar und Regeln ergänzen die Arbeit; unter **Admin** liegen Benutzerverwaltung, Einstellungen und Abfrageverwaltung.

Im laufenden Betrieb beginnt der Blick meist in der **Inbox**: Welche Meldungen sind neu, welche Störungen noch aktiv und wo steigt der Druck? Der **Infrastrukturgraph** ergänzt die Zusammenhänge zwischen Systemen und Standorten. Das **Inventar** liefert die gepflegten Angaben zu den betroffenen Computern.

Für die Untersuchung geht es weiter zu den **Dashboards** und **RCA-Fällen**. Freigegebene Auswertungen, persönliche Cockpits und gemeinsame Untersuchungen helfen dabei, aus einzelnen Signalen ein Lagebild zu machen. **Regeln und Logbuch** halten der Crew den Rücken frei: Bekanntes Rauschen lässt sich einordnen, wichtige Ereignisse werden hervorgehoben und Erkenntnisse bleiben für die nächste Schicht erhalten.

*Die Screenshots zeigen die lokale Anwendung mit fiktiven Demodaten. Das Dashboard ist ausdrücklich als synthetisches Beispiel gekennzeichnet und zeigt keine live abgefragten Azure-Daten.*

## Alert Inbox // Jack in

Die Inbox ist der erste Blick auf die Lage. Sie zeigt aktuelle Störungen ebenso wie vergangene Ereignisse. Filter nach Zeitraum, betroffenem System und Umgebung helfen dabei, aus vielen Meldungen genau den Ausschnitt herauszuholen, der für die Untersuchung zählt.

Kategorien und farbliche Hervorhebungen machen Prioritäten erkennbar. Zwei Zeitverläufe zeigen, wann Meldungen ausgelöst wurden und wie viele kritische Störungen gleichzeitig aktiv waren. Wird eine Störung später behoben, bleibt ihr vorheriger Verlauf nachvollziehbar. Eine grüne Lampe im Jetzt löscht schließlich nicht den Brand von vorhin.

Prüfergebnisse werden direkt an der Meldung lesbar aufbereitet. So lässt sich beispielsweise erkennen, welche Verbindungstests fehlgeschlagen sind, ohne dafür durch mehrere Ansichten springen zu müssen. Eigene Kommentare halten fest, was bereits geprüft wurde. Hinweise auf offene RCA-Fälle verbinden die aktuelle Meldung mit der gemeinsamen Untersuchung.

Auch das Cockpit selbst ist anpassbar: Filter, Zeitverläufe und Ergebnisbereiche lassen sich nach dem eigenen Arbeitsstil anordnen und in ihrer Größe verändern.

![Alert Inbox mit Zeitverläufen und kategorisierten Meldungen](./Media/Inbox.png)

## Infrastrukturgraph // Folge dem Datenpfad

Welche Systeme gehören zusammen? Wo häufen sich die Meldungen? Der Infrastrukturgraph macht aus einzelnen Alarmen ein verständliches Gesamtbild. Seine Ebenen lassen sich beispielsweise nach Umgebung, Standort und System ausrichten. Damit wird schneller sichtbar, ob nur ein einzelner Computer auffällt oder sich eine Störung durch einen ganzen Bereich zieht.

Die Standortkarte ergänzt die geografische Orientierung. Hinweise auf veraltete Statusmeldungen, auffällige Dienste und Wartungszustände helfen dabei, die Lage einzuordnen, statt jede Warnfarbe gleich zu interpretieren.

![Infrastrukturgraph mit Standorten und betroffenen Systemen](./Media/Graph.png)

### Computer Inventory // Die Lagekarte sauber halten

Automatisch gemeldete Systemangaben sind nicht immer vollständig. Das Inventar ergänzt sie um gepflegte Informationen zu Standort, Domäne und Aufgabe eines Computers. Die Crew kann Einträge suchen, filtern und korrigieren, statt dauerhaft mit unklaren oder veralteten Zuordnungen zu arbeiten.

Diese Angaben kommen auch dem Graphen und der Systemauswahl für Analysen zugute. Aus einem wenig aussagekräftigen Computernamen wird so ein einzuordnender Teil der Infrastruktur.

![Computerinventar mit Suche und Filtern](./Media/Inventory.png)

## Dashboards // Hinter das ICE schauen

Ein Alarm ist der Einstieg, nicht die ganze Story. Die Dashboards legen freigegebene KQL-Auswertungen nebeneinander: Systemzustand, AD-Signale und zeitliche Auffälligkeiten im selben Blickfeld. Hilfreich, wenn die Crew nach Spuren aktiver Decker sucht – was tatsächlich sichtbar wird, bestimmen Datenquellen und Abfragen, nicht die Neonfarbe.

Ein gemeinsamer Filterbereich mit Workspace, Domäne, Standort und Rolle grenzt die Systemauswahl und systembezogene Widgets ein. Bereits ausgewählte Systeme bleiben gespeichert und werden für die aktuelle Anzeige nur eingegrenzt. Eigene Anzeigenamen halten auch geteilte Workspaces übersichtlich. Workspaceweite Auswertungen liefern dagegen bewusst den breiteren Blick auf die beteiligten Workspaces.

### Die passende Sicht // Nicht alles ist eine Tabellenzeile

Tabellen für Details, Waben für Zustände, Linien für Zeitspuren, Balken und Gauges für Größenverhältnisse. Kennzahlen und Statusverläufe ergänzen das Lagebild. Farben und Beschriftungen dienen dem Signal, nicht der Lichtshow.

Liniencharts halten den Verlauf im Vordergrund; ihre Legende ist bei Bedarf per Maus oder Tastatur erreichbar. Waben passen sich der verfügbaren Widgetfläche an und lassen sich für einen genaueren Blick vergrößern. Wo es auf einzelne Werte ankommt, ergänzt die Tabellenansicht die grafische Darstellung.

![Dashboard mit unterschiedlichen Visualisierungen und synthetischen Beispieldaten](./Media/Analysis.png)

### Das persönliche Cockpit // Umbauen, ohne die Crew zu überraschen

Widgets aus der Bibliothek ziehen, verschieben und skalieren. Mehrere Cockpits speichern, Zeiträume und Mittelungsintervalle gemeinsam setzen, automatisch aktualisieren und Layoutänderungen rückgängig machen. Im kompakten Modus steht die Auswertung im Mittelpunkt; **Edit** öffnet die Werkzeuge zum Umbau.

Beim Aktualisieren werden die Widgets nacheinander geladen. Bisherige Inhalte bleiben bis zur jeweiligen Aktualisierung sichtbar; ein Fehler in einer Abfrage stoppt nicht das ganze Cockpit. Fehlende Daten und Abfragefehler werden kenntlich gemacht, statt eine falsche Entwarnung vorzutäuschen.

Admins veröffentlichen ihre Cockpits für die Crew. Private Umbauten bleiben privat, bis eine neue Version freigegeben wird. Veröffentlichte Dashboards lassen sich ansehen und als Ausgangspunkt für eigene Cockpits verwenden. Kein Überraschungsumbau mitten im Run.

## Root Cause Analysis // Keine Spur geht verloren

Kritische Meldungen werden zu gemeinsamen Untersuchungsfällen zusammengeführt. Die Übersicht zeigt offene und abgeschlossene Fälle mit Status und Zuständigkeit. So erkennt die Crew, welche Störung noch niemand übernommen hat und wo bereits an einer Lösung gearbeitet wird.

In der Detailansicht stehen zugeordnete Alerts samt Verlauf neben Analyse, Zuständigkeit und Kommentaren. Administratoren können auch eigene Fälle anlegen und weitere Alerts zuordnen. Änderungen werden mit Benutzer und Zeitpunkt dokumentiert; Erkenntnisse verschwinden nicht beim nächsten Schichtwechsel.

Ein erster Kontext-Prompt und ein späterer Folge-Snapshot unterstützen die manuelle Untersuchung. Sie werden nicht automatisch ausgeführt; die fachliche Analyse bleibt bei der Crew. Auch nach Entwarnung bleibt der Fall zur Nachbereitung erhalten und wird nicht stillschweigend geschlossen.

![Root Cause Analysis mit Untersuchungsfällen und Falldetails](./Media/RootCauseAnalysis.png)

## Query Library // Ordnung im Datenarsenal

Die Abfragenbibliothek hält wiederverwendbare Auswertungen bereit. Kategorien und Unterkategorien sorgen dafür, dass auch ein größerer Werkzeugkasten übersichtlich bleibt. Statt eine lange Liste zu durchsuchen, kann die Crew beispielsweise direkt zu Replikation, Leistung oder Sicherheit wechseln.

Die mitgelieferte **Active-Directory-Bibliothek mit 38 Vorlagen** bietet einen Ausgangspunkt für typische Untersuchungen. Sie deckt unter anderem Systemzustand, Replikation, Leistung und Sicherheit ab und enthält auch Übersichten zu wichtigen Verzeichnisrollen und Gruppenrichtlinien.

![Abfragenbibliothek mit aufklappbaren Themenbereichen](./Media/QueryLibrary.png)

### Query Registry // Den Werkzeugkasten pflegen

Admins verwalten die Abfragen und ihre Darstellung an einer zentralen Stelle. Neue oder geänderte Auswertungen werden geprüft, bevor sie für die Crew freigegeben werden. Ein unfertiger Arbeitsstand ersetzt damit nicht versehentlich das Werkzeug, das andere gerade im Einsatz haben.

Kategorien lassen sich anlegen, umbenennen und neu ordnen. Abfragen können per Ziehen oder über eine Auswahl zugeordnet werden. Die Gliederung erscheint auch in der Bibliothek; zum bloßen Aufräumen müssen die Auswertungen nicht neu veröffentlicht werden. Sammelzuordnungen erleichtern es, vorhandene Abfragen für weitere Workspaces vorzubereiten.

Eigene Anpassungen bleiben beim erneuten Installieren der mitgelieferten Bibliothek erhalten. Gespeicherte Abfragen lassen sich zusammen mit ihren Ansichten exportieren, um sie weiterzuverwenden. Das nimmt den Werkzeugkasten mit, ersetzt aber keine vollständige Sicherung der Anwendung.

![Verwaltung der Abfragen und Kategorien](./Media/QueryRegistry.png)

## Regelwerk // ICE gegen das Rauschen

Nicht jede Meldung braucht dieselbe Aufmerksamkeit. Regeln helfen dabei, bekannte Situationen einzuordnen, unauffällige Ergebnisse zurückzunehmen und kritische Störungen hervorzuheben. So bleiben wichtige Signale sichtbar, auch wenn viele ähnliche Meldungen eintreffen.

Beispielsweise kann ein einzelner bekannter Prüffehler anders behandelt werden als mehrere gleichzeitig fehlgeschlagene Prüfungen. Regeln können außerdem dabei helfen, die Aufgabe eines erkannten Systems im Inventar zuzuordnen. Die Bewertung bleibt zentral pflegbar, statt sich auf persönliche Gewohnheiten einzelner Operatoren zu verlassen.

Geplante KQL-Alert-Regeln ergänzen eingehende Meldungen um eigene regelmäßige Prüfungen. Neben systembezogenen Prüfungen lassen sich ganze Workspaces unabhängig vom Computerinventar überwachen. Vorlagen für Anmeldungen und Kerberos, Verzeichnis- und Accountänderungen sowie DSRM-Passwort-Resets erleichtern den Einstieg. Ein gemessener Grenzwertverstoß kann eine Störung auslösen, eine gemessene Erholung sie wieder auflösen. Fehler und fehlende Daten bleiben dabei von einer echten Entwarnung unterscheidbar.

## Logbook // Das Gedächtnis der Crew

Das Logbuch hält Beobachtungen und wichtige Systemereignisse fest. Manuelle Notizen werden mit Benutzer und Zeitpunkt versehen; auch neue oder geänderte Kommentare an Meldungen finden hier ihren Platz.

Zusätzlich dokumentiert die Console, wenn kritische Störungen beginnen oder behoben werden. So weiß auch die nächste Schicht, was passiert ist und was bereits untersucht wurde. Keine Einsatzgeschichte, die nur im Kopf eines einzelnen Deckers existiert.

![Gemeinsames Logbuch mit Notizen und Systemmeldungen](./Media/Logbook.png)

## Copilot Handoff // Mensch in der Schleife

Für die manuelle Übergabe an Copilot lassen sich ausgewählte Meldungen mit ihrem Zusammenhang zu einer Analysevorlage zusammenstellen. Dazu gehören nicht nur die einzelnen Alarme, sondern auch passende Informationen aus Systemhistorie, Inventar, Graph und Regelbewertung.

Zeitraum und Auswahl bestimmen, welcher Ausschnitt der Lage vorbereitet wird. Im Analysis Workspace kann die Crew den Kontext prüfen und entscheidet bewusst, was sie weitergibt. Das spart das wiederholte Zusammensuchen derselben Informationen, ohne die fachliche Bewertung aus der Hand zu geben.

Ein Agent ist ein Werkzeug, kein Johnson, dem man blind vertraut.

![Vorbereitung einer Analyseübergabe mit ausgewählten Meldungen](./Media/Prompt.png)

## Zugriff und Verwaltung // Wards vor dem Host

Nicht jedes Crewmitglied braucht dieselben Werkzeuge. Die Rollen teilen den Zugriff nach Aufgabe auf:

| Rolle | Aufgabe |
| --- | --- |
| **Reader** | Lage verfolgen, Ereignisse nachlesen und Beobachtungen festhalten. |
| **Operator** | Zusätzlich Systeme und Regeln pflegen sowie freigegebene Analysen nutzen. |
| **Admin** | Zusätzlich Benutzer, Einstellungen und Abfragen verwalten, Dashboards veröffentlichen und RCA-Fälle bearbeiten. |

Die **Benutzerverwaltung** macht Rollen und Zugänge zentral pflegbar. Unter **Settings** werden Darstellung, Anzeigezeiträume und weitere Anwendungseinstellungen verwaltet. Die Crew bekommt Orientierung, ohne dass jeder an allen Schaltern drehen muss.

Die Zugriffsgrenzen gelten nicht nur für sichtbare Menüpunkte, sondern auch für die dahinterliegenden Funktionen. Für den produktiven Einsatz gehören persönliche Zugänge und bewusst vergebene Rechte zum Grundschutz des Nodes.

## Zusammenspiel // Vom Sensor zur Untersuchung

Azure Monitor liefert die Meldungen. Die Console ordnet sie ein, verbindet sie mit den bekannten Systemen und stellt sie in Inbox, Verlauf und Graph dar. Von dort kann die Crew eine weiterführende Analyse beginnen und ihre Erkenntnisse in RCA-Fällen und im Logbuch festhalten.

Für ergänzende Werkzeuge stehen auch lesende Zugriffsmöglichkeiten auf aktuelle Meldungen und die Ereignishistorie bereit. CLI und Web-Agent ergänzen die Weboberfläche für bestehende Betriebsabläufe. Die Console kann damit Teil des Werkzeugkastens sein, statt die Crew in eine weitere isolierte Insel zu schicken.

Ein typischer Run: Mehrere Meldungen treffen an einem Standort ein. In der Inbox fällt die Häufung auf, der Graph zeigt die betroffenen Systeme, und ein Dashboard hilft beim Vergleich ihrer Zustände. Im RCA-Fall werden Zuständigkeit, Untersuchung und nächste Schritte festgehalten. Die Ergebnisse bleiben für die nächste Schicht dokumentiert.

## Weitere Einblicke // Rundgang durch den Node

<details>
<summary>Weitere Screenshots aufklappen</summary>

### Meldungsdetails und Abfrageverwaltung

![Inbox mit aufbereiteten Prüfergebnissen](./Media/InboxDetails.png)

![Detailansicht der Abfragenverwaltung](./Media/QueryEditor.png)

### Regeln, Benutzer und Einstellungen

![Regelverwaltung](./Media/Rules.png)

![Benutzerverwaltung](./Media/User.png)

![Zentrale Anwendungseinstellungen](./Media/Settings.png)

### Anbindung weiterer Werkzeuge

![Beispiele für den lesenden Zugriff auf Meldungen](./Media/Queries.png)

</details>

## Vor dem Run // Einrichtung und Betrieb

Diese README gibt den Überblick. Konfigurationsbeispiele, Datenbank-Upgrades und Betriebsdetails gehören in die weiterführende Dokumentation:

- [Technische Dokumentation und lokale Entwicklung](./MonitoringApp/README.md)
- [Projekt- und Testpläne](./doc/README.md)
- [Authentifizierung einrichten](./MonitoringApp/doc/AUTHENTICATION-SETUP.md)
- [Lokale Demodaten laden](./MonitoringApp/DemoData/README.md)
- [CLI](./AlertConsoleCli/README.md) und [Web-Agent](./AlertWebAgent/README.md)
- [Externe Komponenten und Lizenzen](./doc/THIRD-PARTY-DEPENDENCIES.md)

Für den echten Einsatz gilt: Zugriffe bewusst vergeben, vertrauliche Daten schützen und Backups prüfen. Das Trainingsgelände ist nicht das Produktionsnetz.

## Schlusswort // Bleib wachsam

Die Console hilft der Crew, zwischen Matrixrauschen und echtem Infrastrukturbrand zu unterscheiden. Halte die Lagekarte sauber, das Logbuch ehrlich und den Blick auf das Wesentliche.

Und wenn ein freundlicher Auftraggeber mit goldenen Augen darum bittet, die Einsatzhistorie „nur ganz kurz“ verschwinden zu lassen: Verbindung trennen, Credstick einpacken und laufen.

**Vorsicht vor Drachen.**
