# Azure Monitoring Console // Matrix Operations Node

Willkommen im Grid, Chummer.

Wenn überall Warnlampen blinken, braucht die Crew keine weitere Liste voller Alarmmeldungen. Sie braucht einen Überblick: **Was ist passiert? Welche Systeme sind betroffen? Was verdient jetzt Aufmerksamkeit?**

Die **Azure Monitoring Console** bündelt Meldungen aus Azure Monitor, macht Zusammenhänge sichtbar und bringt Analyse, Systemübersicht und Einsatznotizen in eine gemeinsame Leitstelle. Weniger Portalsprünge. Mehr Lagebewusstsein.

Dabei geht es nicht nur darum, Alarme einzusammeln. Die Console verbindet die erste Warnung mit den betroffenen Systemen, weiterführenden Auswertungen und den Beobachtungen der Crew. So bleibt der Weg von „Da stimmt etwas nicht“ bis zur gemeinsamen Untersuchung nachvollziehbar.

> **Straßenregel Nr. 1:** Nicht jedes Signal ist ein Infrastrukturbrand.
>
> **Straßenregel Nr. 2:** Halte die Crew auf demselben Stand.
>
> **Straßenregel Nr. 3:** Mach niemals einen Deal mit einem Drachen.

## Was im Node verdrahtet ist

Unter der Chromhaube läuft eine **ASP.NET Core Blazor WebApp auf .NET 10** mit **SQL Server oder Azure SQL** als Datenspeicher. Azure Monitor und Logic Apps liefern die Meldungen; die Console verbindet sie mit Inventar, Regeln und Analyse.

- **Alert Inbox:** Aktive Meldungen und Ereignishistorie mit Filtern, Kategorien, Severity-Farben und anpassbarem Rasterlayout.
- **Zeitverläufe:** Zwei 48-Stunden-Timelines für ausgelöste Meldungen und gleichzeitig aktive kritische Störungen.
- **Regelwerk:** Zentrale Kategorisierung, Unterdrückung bekannter Meldungen, Kritikalität und automatische Rollenzuordnung im Inventar.
- **Infrastrukturübersicht:** Konfigurierbarer dreistufiger Graph, Standortkarte und Computerinventar mit Domain, Standort und Systemrolle.
- **Logbuch:** Persistente Einsatznotizen, Alert-Kommentare und automatische Einträge zu Beginn und Ende kritischer Störungen.
- **Live Analysis:** Versionierte, freigegebene KQL-Abfragen mit kontrolliertem Zugriff auf Log-Analytics-Datenquellen.
- **AD-Bibliothek:** 38 externe JSON-Vorlagen mit Abfragen und voreingestellten Visualisierungen.
- **Abfragenverwaltung:** Verschachtelte Kategorien, Drag & Drop und ZIP-Export gespeicherter Abfragen samt Darstellung.
- **Visualisierungen:** Tabellen, Linien- und Balkencharts, Waben, Bar-Gauges, Stat-Kacheln und Statusverläufe mit anpassbaren Farben und Beschriftungen.
- **Dashboards:** Mehrere persönliche Analyseansichten mit gemeinsamer Zeitraumwahl, automatischer Aktualisierung und Veröffentlichung für die Crew.
- **Zugriffsschutz:** SQL-Benutzerkonten mit sicher gespeicherten Passwörtern und serverseitig geprüften Reader-, Operator- und Admin-Rechten.
- **Anbindungen:** Lesende REST-Schnittstellen, Kommandozeilenclient und kontrollierte Vorbereitung einer manuellen Copilot-Übergabe.
- **Betrieb:** Zentral verwaltete Anwendungseinstellungen und optionales Anwendungsmonitoring über Azure Monitor.

## Das Cockpit // Was die Crew bekommt

Im laufenden Betrieb beginnt der Blick meist in der **Inbox**: Welche Meldungen sind neu, welche Störungen noch aktiv und wo steigt der Druck? Der **Infrastrukturgraph** ergänzt die Zusammenhänge zwischen Systemen und Standorten. Das **Inventar** liefert die gepflegten Angaben zu den betroffenen Computern.

Für die Untersuchung geht es weiter in die **Analyse**. Dort stehen freigegebene Auswertungen und persönliche Dashboards bereit. Die Abfragenbibliothek hält die passenden Werkzeuge zusammen, ohne dass jeder seine Untersuchung wieder bei null beginnen muss.

**Regeln und Logbuch** halten der Crew den Rücken frei: Bekanntes Rauschen lässt sich einordnen, wichtige Ereignisse werden hervorgehoben und Erkenntnisse bleiben für die nächste Schicht erhalten.

*Alle Screenshots zeigen fiktive Demodaten. Die Analyseansichten veranschaulichen die Darstellung mit vorbereiteten Beispielen, nicht mit live abgefragten Azure-Daten.*

## Alert Inbox // Jack in

Die Inbox ist der erste Blick auf die Lage. Sie zeigt aktuelle Störungen ebenso wie vergangene Ereignisse. Filter nach Zeitraum, betroffenem System und Umgebung helfen dabei, aus vielen Meldungen genau den Ausschnitt herauszuholen, der für die Untersuchung zählt.

Kategorien und farbliche Hervorhebungen machen Prioritäten erkennbar. Zwei Zeitverläufe zeigen, wann Meldungen ausgelöst wurden und wie viele kritische Störungen gleichzeitig aktiv waren. Wird eine Störung später behoben, bleibt ihr vorheriger Verlauf nachvollziehbar. Eine grüne Lampe im Jetzt löscht schließlich nicht den Brand von vorhin.

Prüfergebnisse werden direkt an der Meldung lesbar aufbereitet. So lässt sich beispielsweise erkennen, welche Verbindungstests fehlgeschlagen sind, ohne dafür durch mehrere Ansichten springen zu müssen. Eigene Kommentare halten fest, was bereits geprüft wurde.

Auch das Cockpit selbst ist anpassbar: Filter, Zeitverläufe und Ergebnisbereiche lassen sich nach dem eigenen Arbeitsstil anordnen und in ihrer Größe verändern.

![Inbox mit kategorisierten Meldungen und aufbereiteten Ergebnissen](./Media/InboxDetails.png)

## Infrastruktur // Folge dem Datenpfad

Welche Systeme gehören zusammen? Wo häufen sich die Meldungen? Der Infrastrukturgraph macht aus einzelnen Alarmen ein verständliches Gesamtbild. Seine Ebenen lassen sich beispielsweise nach Umgebung, Standort und System ausrichten. Damit wird schneller sichtbar, ob nur ein einzelner Computer auffällt oder sich eine Störung durch einen ganzen Bereich zieht.

Die Standortkarte ergänzt die geografische Orientierung. Hinweise auf veraltete Statusmeldungen, auffällige Dienste und Wartungszustände helfen dabei, die Lage einzuordnen, statt jede Warnfarbe gleich zu interpretieren.

![Infrastrukturgraph mit Standorten und betroffenen Systemen](./Media/GraphTopology.png)

### Computer Inventory // Die Lagekarte sauber halten

Automatisch gemeldete Systemangaben sind nicht immer vollständig. Das Inventar ergänzt sie um gepflegte Informationen zu Standort, Domain und Aufgabe eines Computers. Die Crew kann Einträge suchen, filtern und korrigieren, statt dauerhaft mit unklaren oder veralteten Zuordnungen zu arbeiten.

Diese Angaben kommen auch dem Graphen und der Systemauswahl für Analysen zugute. Aus einem wenig aussagekräftigen Computernamen wird so ein einzuordnender Teil der Infrastruktur.

![Computerinventar mit Suche und Filtern](./Media/Inventory.png)

## Live Analysis // Hinter das ICE schauen

Eine Alarmmeldung ist oft erst der Anfang der Untersuchung. Mit freigegebenen Auswertungen lässt sich gezielt nachsehen, wie sich betroffene Systeme verhalten und ob weitere Auffälligkeiten hinzukommen. Die Auswahl nach Domain, Standort und Aufgabe grenzt den Blick auf die Systeme ein, die gerade relevant sind.

Dabei arbeitet die Crew mit vorbereiteten und freigegebenen Abfragen. Nicht jede Untersuchung erfordert einen selbst geschriebenen Analyseauftrag; wiederkehrende Fragestellungen können als gemeinsame Werkzeuge bereitstehen. Welche Datenquellen und Auswertungen verfügbar sind, richtet sich nach den eingerichteten Freigaben.

### Die passende Sicht // Nicht alles ist eine Tabellenzeile

Tabellen eignen sich für Einzelwerte und genaue Vergleiche. Waben bringen viele Zustände auf einen Blick zusammen, während Balkenanzeigen Auslastung und Größenverhältnisse verdeutlichen. Liniencharts zeigen Entwicklungen über die Zeit; Kennzahlen und Statusverläufe ergänzen die Übersicht.

Farben, Beschriftungen und Größen lassen sich passend zur jeweiligen Auswertung gestalten. Eine übersichtliche Darstellung soll beim Erkennen helfen, nicht mit möglichst viel Neon vom eigentlichen Problem ablenken.

![Analyse-Dashboard mit Tabelle, Waben, Balkenanzeigen und Linienchart; synthetische Demodaten](./Media/Analysis.png)

### Dashboards // Das persönliche Cockpit

Persönliche Dashboards bringen mehrere Ansichten auf einer Fläche zusammen. Abfragen lassen sich aus der Bibliothek hinzufügen und ihre Bereiche anschließend verschieben oder vergrößern. Für unterschiedliche Aufgaben können getrennte Dashboards entstehen, etwa für den täglichen Zustandscheck und eine laufende Störungsanalyse.

Systemauswahl, Zeiträume und Aktualisierung gehören zur jeweiligen Übersicht. Gemeinsame Zeitvorgaben erleichtern den Vergleich mehrerer Ansichten; bei Bedarf bleiben einzelne Auswertungen unabhängig eingestellt. Änderungen am Aufbau lassen sich rückgängig machen.

Admins können eigene Dashboards für die Crew veröffentlichen. Die gemeinsame Ansicht bleibt vom privaten Arbeitsstand getrennt: Weitere Umbauten werden erst mit einer erneuten Veröffentlichung übernommen. So bekommt nicht die ganze Schicht jede spontane Verschiebung im Cockpit mit.

## Query Library // Ordnung im Datenarsenal

Die Abfragenbibliothek hält wiederverwendbare Auswertungen bereit. Kategorien und Unterkategorien sorgen dafür, dass auch ein größerer Werkzeugkasten übersichtlich bleibt. Statt eine lange Liste zu durchsuchen, kann die Crew beispielsweise direkt zu Replikation, Leistung oder Sicherheit wechseln.

Die mitgelieferte **Active-Directory-Bibliothek mit 38 Vorlagen** bietet einen Ausgangspunkt für typische Untersuchungen. Sie deckt unter anderem Systemzustand, Replikation, Leistung und Sicherheit ab und enthält auch Übersichten zu wichtigen Verzeichnisrollen und Gruppenrichtlinien.

![Abfragenbibliothek mit aufklappbaren Themenbereichen](./Media/QueryLibrary.png)

### Query Registry // Den Werkzeugkasten pflegen

Admins verwalten die Abfragen und ihre Darstellung an einer zentralen Stelle. Neue oder geänderte Auswertungen werden geprüft, bevor sie für die Crew freigegeben werden. Ein unfertiger Arbeitsstand ersetzt damit nicht versehentlich das Werkzeug, das andere gerade im Einsatz haben.

Kategorien lassen sich anlegen, umbenennen und neu ordnen. Abfragen können per Ziehen oder über eine Auswahl zugeordnet werden. Die Gliederung erscheint auch in der Bibliothek; zum bloßen Aufräumen müssen die Auswertungen nicht neu veröffentlicht werden.

Eigene Anpassungen bleiben beim erneuten Installieren der mitgelieferten Bibliothek erhalten. Gespeicherte Abfragen lassen sich zusammen mit ihren Ansichten exportieren, um sie weiterzuverwenden. Das nimmt den Werkzeugkasten mit, ersetzt aber keine vollständige Sicherung der Anwendung.

![Verwaltung der Abfragen und Kategorien](./Media/QueryRegistry.png)

## Regelwerk // ICE gegen das Rauschen

Nicht jede Meldung braucht dieselbe Aufmerksamkeit. Regeln helfen dabei, bekannte Situationen einzuordnen, unauffällige Ergebnisse zurückzunehmen und kritische Störungen hervorzuheben. So bleiben wichtige Signale sichtbar, auch wenn viele ähnliche Meldungen eintreffen.

Beispielsweise kann ein einzelner bekannter Prüffehler anders behandelt werden als mehrere gleichzeitig fehlgeschlagene Prüfungen. Regeln können außerdem dabei helfen, die Aufgabe eines erkannten Systems im Inventar zuzuordnen. Die Bewertung bleibt zentral pflegbar, statt sich auf persönliche Gewohnheiten einzelner Operatoren zu verlassen.

## Logbook // Das Gedächtnis der Crew

Das Logbuch hält Beobachtungen und wichtige Systemereignisse fest. Manuelle Notizen werden mit Benutzer und Zeitpunkt versehen; auch neue oder geänderte Kommentare an Meldungen finden hier ihren Platz.

Zusätzlich dokumentiert die Console, wenn kritische Störungen beginnen oder behoben werden. So weiß auch die nächste Schicht, was passiert ist und was bereits untersucht wurde. Keine Einsatzgeschichte, die nur im Kopf eines einzelnen Deckers existiert.

![Gemeinsames Logbuch mit Notizen und Systemmeldungen](./Media/Logbook.png)

## Copilot Handoff // Mensch in der Schleife

Für die manuelle Übergabe an Copilot lassen sich ausgewählte Meldungen mit ihrem Zusammenhang zu einer Analysevorlage zusammenstellen. Dazu gehören nicht nur die einzelnen Alarme, sondern auch passende Informationen aus Systemhistorie, Inventar, Graph und Regelbewertung.

Zeitraum und Auswahl bestimmen, welcher Ausschnitt der Lage vorbereitet wird. Die Crew kann den Inhalt vor der Übergabe prüfen und entscheidet bewusst, was sie weitergibt. Das spart das wiederholte Zusammensuchen derselben Informationen, ohne die fachliche Bewertung aus der Hand zu geben.

Ein Agent ist ein Werkzeug, kein Johnson, dem man blind vertraut.

![Vorbereitung einer Analyseübergabe mit ausgewählten Meldungen](./Media/Prompt.png)

## Zugriff und Verwaltung // Wards vor dem Host

Nicht jedes Crewmitglied braucht dieselben Werkzeuge. Die Rollen teilen den Zugriff nach Aufgabe auf:

| Rolle | Aufgabe |
| --- | --- |
| **Reader** | Die Lage verfolgen, Ereignisse nachlesen und Beobachtungen festhalten. |
| **Operator** | Zusätzlich Systeme und Regeln pflegen sowie freigegebene Analysen nutzen. |
| **Admin** | Zusätzlich Benutzer, Einstellungen und Abfragen verwalten sowie eigene Dashboards veröffentlichen. |

Darstellung, Anzeigezeiträume und weitere Einstellungen werden zentral verwaltet. Die Crew bekommt Orientierung, ohne dass jeder an allen Schaltern drehen muss.

Die Zugriffsgrenzen gelten nicht nur für sichtbare Menüpunkte, sondern auch für die dahinterliegenden Funktionen. Für den produktiven Einsatz gehören persönliche Zugänge und bewusst vergebene Rechte zum Grundschutz des Nodes.

## Zusammenspiel // Vom Sensor zur Untersuchung

Azure Monitor liefert die Meldungen. Die Console ordnet sie ein, verbindet sie mit den bekannten Systemen und stellt sie in Inbox, Verlauf und Graph dar. Von dort kann die Crew eine weiterführende Analyse beginnen und ihre Erkenntnisse im Logbuch festhalten.

Für ergänzende Werkzeuge stehen auch lesende Zugriffsmöglichkeiten auf aktuelle Meldungen und die Ereignishistorie bereit. Die Console kann damit Teil eines bestehenden Betriebsablaufs sein, statt die Crew in eine weitere isolierte Insel zu schicken.

Ein typischer Run: Mehrere Meldungen treffen an einem Standort ein. In der Inbox fällt die Häufung auf, der Graph zeigt die betroffenen Systeme, und eine vorbereitete Analyse hilft beim Vergleich ihrer Zustände. Die Untersuchung und ihre Ergebnisse bleiben für die nächste Schicht dokumentiert.

## Weitere Einblicke // Rundgang durch den Node

<details>
<summary>Weitere Screenshots aufklappen</summary>

### Inbox und Standortkarte

![Gesamtansicht der Inbox mit Zeitverläufen und Filtern](./Media/Inbox.png)

![Standortkarte und Graph-Steuerung](./Media/Graph.png)

### Analyseverwaltung

![Dashboard-Verwaltung und Zeitraumwahl](./Media/AnalysisControls.png)

![Detailansicht der Abfragenverwaltung](./Media/QueryEditor.png)

### Regeln, Benutzer und Einstellungen

![Regelverwaltung](./Media/Rules.png)

![Benutzerverwaltung](./Media/User.png)

![Zentrale Anwendungseinstellungen](./Media/Settings.png)

### Anbindung weiterer Werkzeuge

![Beispiele für den lesenden Zugriff auf Meldungen](./Media/Queries.png)

</details>

## Vor dem Run // Weiterführende Informationen

Diese Seite gibt den Überblick. Einrichtung, Betrieb und technische Details stehen in der [technischen Dokumentation](./MonitoringApp/README.md). Eine Übersicht der verwendeten externen Komponenten bietet das [Abhängigkeitsverzeichnis](./THIRD-PARTY-DEPENDENCIES.md).

Für den echten Einsatz gilt: Zugriffe bewusst vergeben, vertrauliche Daten schützen und Backups prüfen. Das Trainingsgelände ist nicht das Produktionsnetz.

## Schlusswort // Bleib wachsam

Die Console hilft der Crew, zwischen Matrixrauschen und echtem Infrastrukturbrand zu unterscheiden. Halte die Lagekarte sauber, das Logbuch ehrlich und den Blick auf das Wesentliche.

Und wenn ein freundlicher Auftraggeber mit goldenen Augen darum bittet, die Einsatzhistorie „nur ganz kurz“ verschwinden zu lassen: Verbindung trennen, Credstick einpacken und laufen.

**Vorsicht vor Drachen.**
