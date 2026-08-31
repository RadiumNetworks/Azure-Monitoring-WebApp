## Azure Monitoring Console // Grid Operations Node

Welcome to the grid, chummer. This console pulls Azure Monitor alerts out of the corporate data stream and lands them in one hardened operations node. When a system goes dark, the crew can trace the signal, identify the affected target or site, cut through background noise, and spot a real outage before the run goes sideways.

This is an ASP.NET Core Blazor application backed by SQL Server or Azure SQL. Azure Monitor and Logic Apps feed the node through its REST ingress, while operators work the live alert stream from the browser. The repository also carries a CLI for deckers who prefer a direct line to the data.

### What is wired into the node

- A live alert inbox with active/history views, filtering, severity styling, comments, result presentation, and a target heatmap.
- A parsed-alert store that keeps normalized alert details and links every signal to the computer inventory when possible.
- A three-layer graph fed by parsed alerts and operator-maintained inventory data instead of trusting incomplete telemetry.
- A computer inventory for correcting subscription, resource group, site, domain, role, and computer metadata.
- Database-driven rules for inbox categorization and automatic inventory-role assignment.
- Built-in SQL authentication with salted PBKDF2-HMAC-SHA256 password hashes.
- Server-side role-based access for Readers, Operators, and Admins.
- Database-backed application settings with validation before startup.
- Query endpoints, Copilot prompt preparation, Logic App enrichment workflows, and a command-line client.

Watch the heatmap, follow the data trail, and keep the infrastructure online. In the sprawl, visibility is survival.

## Alert inbox // Live traffic

The inbox is where the incoming noise gets sorted. It shows the current active state as well as full event history, supports free-text and time filters, and groups matching signals with database-managed alert rules. Operators can inspect query results, add comments, and mark an alert as resolved without deleting the original evidence.

Categorization rules can suppress known static or flag a wider target outage. Severity colors and font styles are also database-managed, so the visual threat level can be tuned without rebuilding the application.

![Alert inbox with filters, heatmap, and categorized alerts](./Media/Inbox.png)

## Alert graph // Trace the signal

The graph turns the alert stream into a configurable three-layer topology. Layers can use subscription, resource group, alert name, target, site, domain, or role.

The graph reads normalized records from `ParsedAlerts` and enriches them through `ComputerInventory`. That means a corrected site, domain, role, or resource group is reflected in the graph even when the original alert arrived with missing or bad metadata. Older alerts that predate parsed storage are backfilled automatically when the graph loads, so no ghosts disappear just because the schema evolved.

![Alert topology graph with configurable hierarchy layers](./Media/Graph.png)

## Computer inventory // Your local truth

Telemetry lies, or at least turns up half dressed. The inventory gives the crew a maintained source of truth for every discovered system:

- Subscription
- Resource group
- Computer
- Site
- Domain
- Role

Incoming alerts discover new systems and fill missing metadata, but they do not overwrite values already corrected by an operator. The inventory can also be prefilled from recent alert history. Filters for subscription, resource group, site, domain, and role keep the console usable when the system count gets heavy.

Inventory-role rules run separately from inbox categorization. The built-in rules assign `domaincontrollers` when a target produces a DCDiag or replication alert. More role rules can be maintained from the Rules page without hard-wiring every new pattern into the code.

![Computer inventory with metadata and dedicated filters](./Media/Inventory.png)

## Authentication // Keep the door warded

Application authentication is controlled by the `Authentication` row in `dbo.Settings`:

- `sql` is the secure default and requires a login stored in `AuthenticationUsers`.
- `open` allows anonymous access and is intended only for controlled setups or initial configuration.

Passwords never land in the database as clear text. Each password is stored as a versioned, salted PBKDF2-HMAC-SHA256 hash with a high work factor. The login cookie protects the Blazor interface and application endpoints, while alert ingestion has its own independent bearer-token policy for Logic Apps and managed identities.

### Crew access levels

| Role | Grid access |
| --- | --- |
| **Reader** | Standard operational views such as Inbox, Graph, Queries, and Copilot Prompt |
| **Operator** | Reader access plus alert Rules and Computer Inventory maintenance |
| **Admin** | Operator access plus Authentication Users and Application Settings |

These are server-side authorization boundaries, not just hidden navigation links. Direct page requests are checked too. The final user and final Admin are protected from deletion or demotion, which helps prevent the crew from locking itself outside the host.

![Authentication user administration with Reader, Operator, and Admin roles](./Media/User.png)

## Application settings // Tune the host

Startup-critical configuration lives in `dbo.Settings`. Admins can maintain authentication mode, alert-history options, graph layers, and severity presentation from the Settings page. JSON values are validated before they are written, and startup fails loudly if required settings are missing or malformed instead of quietly falling back to an unsafe configuration.

Saved startup settings take effect after an application restart.

![Database-backed application settings](./Media/Settings.png)

## Query interface // Jack in directly

The read-only query API supports active-alert and event-history requests for integrations, agents, and automation. The Queries page shows ready-to-use request URLs and their current JSON responses, making it a quick diagnostics deck before another system is connected.

![Query examples with active-alert and event-history responses](./Media/Queries.png)

## Copilot handoff // Human stays in the loop

The Copilot Prompt page assembles a controlled incident snapshot from selected alerts. The operator chooses the scope, time range, alerts, and detail fields before copying the prompt. Nothing is sent automatically; the handoff stays visible and human-controlled.

![Copilot prompt preparation and alert selection](./Media/Prompt.png)

## Data flow // From sensor to street map

1. Azure Monitor fires an alert.
2. A Logic App can enrich the Common Alert Schema payload with query results.
3. `POST /api/alerts` validates and deduplicates the event.
4. The original event is stored in `Alerts` and its normalized twin in `ParsedAlerts`.
5. The target is linked to `ComputerInventory`; missing inventory values can be discovered or assigned by role rules.
6. The Inbox works the current alert lifecycle, while the Graph uses parsed history plus corrected inventory metadata.

Deleting an original alert also removes its parsed twin. Deleting an inventory entry keeps parsed history intact and clears only the inventory link, so the evidence survives even when the local map is rebuilt.

## Deployment notes // Before the run

The main web project is under `MonitoringApp`, with EF Core migrations in `MonitoringApp/Migrations` and the idempotent database bootstrap script in `MonitoringApp/Database/CreateMonitoringDatabase.template.sql`.

Apply the EF migrations before sending production traffic, create at least one Admin account before enabling SQL authentication, and keep the alert-ingestion identity separate from interactive users. No one wants to discover the access plan was written in pencil after the doors lock.
