# FBIS – Transaction Ingestion Console Application

## 1. Project Overview
FBIS is a .NET 10 console application that ingests retail transaction snapshots, reconciles them with a SQLite database, and maintains a complete audit trail of every change. Each run processes a JSON snapshot covering the last 24 hours, applies inserts/updates/revocations/finalization rules, and records per-run metrics so downstream systems can trust the resulting dataset.

## 2. Architecture
The solution follows the layered architecture called out in `docs/architecture.md`:

- **Application/** – Cross-cutting services and options, most notably `TransactionIngestionService`, which orchestrates ingestion, logging, metrics, and EF Core interactions.
- **Domain/** – Core entities (`TransactionRecord`, `TransactionRevision`, `IngestionRun`) plus the `TransactionStatus` enum. These files capture the business model and validation rules.
- **Infrastructure/** – Persistence concerns. `Infrastructure/Persistence/FbisDbContext.cs` defines EF Core mappings and migrations. Database initialization is triggered automatically from `Program.cs`.
- **Mock/** – Snapshot JSON files (e.g., `mock-transactions-run1.json`) used for local testing and demos.
- **FBIS.Tests/** – xUnit-based automated tests that re-use the production EF Core stack by spinning up in-memory SQLite databases.

`Program.cs` bootstraps the Generic Host, resolves configuration from `appsettings.json`, wires dependency injection, and executes a single ingestion run. A deterministic database path (`FBIS.App/fbis.db`) ensures consistent development/runtime behavior.

For fuller context, `docs/architecture.md` walks through the system design and ingestion workflow, while `docs/edge_cases.md` catalogues the reconciliation edge cases and resilience strategies the service handles.

## 3. Data Model
- **TransactionRecord** – Represents the latest known state of a transaction. Fields include `TransactionId` (unique), `CardLast4`, `LocationCode`, `ProductName`, `Amount` (`decimal(18,2)`), `TransactionTime`, `Status`, `CreatedAt`, and `UpdatedAt`. Each record holds a navigation property to its `TransactionRevision`s.
- **TransactionRevision** – Audit entries capturing the list of changed fields and serialized previous values. A `TransactionRevision` belongs to exactly one `TransactionRecord`; cascade delete is enabled so revisions disappear with their parent.
- **IngestionRun** – Stores run-level metrics (`TotalProcessed`, `Inserted`, `Updated`, `Revoked`) and timestamps (`StartedAt`, `CompletedAt`).
- **TransactionStatus enum** – Possible values are `Active`, `Revoked`, and `Finalized`.

Schema rules enforced via EF Core:
- Unique index on `TransactionRecord.TransactionId` to guarantee idempotent upserts.
- `Amount` uses `HasPrecision(18, 2)`.
- `CardLast4` is limited to 4 characters.
- `TransactionRevision` has a required FK to `TransactionRecord` with cascade delete.
- Timestamps (`CreatedAt`, `UpdatedAt`, `ChangedAt`) are stored for every record/revision.

## 4. Ingestion Workflow
`TransactionIngestionService.RunAsync` executes the following pipeline:
1. **Load snapshot JSON** from the path specified in `TransactionFeedOptions.MockFilePath`.
2. **Normalize input** – Trim IDs/strings, extract last4 digits, coerce timestamps to UTC, and skip invalid rows.
3. **Deduplicate entries** – Use a dictionary keyed by `TransactionId` to discard duplicates within the snapshot.
4. **Single EF Core transaction** – All work occurs inside `Database.BeginTransactionAsync` so partial failures roll back.
5. **Upserts** – New transactions result in inserted `TransactionRecord`s; existing ones are compared field-by-field.
6. **Update detection** – Differences in `LocationCode`, `ProductName`, `Amount`, or `TransactionTime` trigger updates and revision creation.
7. **Revision generation** – `TransactionRevision` captures changed fields and previous values for auditing.
8. **Revocation** – Records in the last 24h window missing from the snapshot are marked `Revoked` with a reason.
9. **Finalization** – Records older than 24h transition to `Finalized`, preventing future modifications.
10. **Run metrics** – An `IngestionRun` entity logs totals for processed/inserted/updated/revoked.
11. **Logging** – Structured logs report duplicate counts, snapshot location, DB initialization events, and run summaries.

## 5. Update Detection Strategy
For every existing `TransactionRecord`, the service compares the snapshot values for:
- `LocationCode`
- `ProductName`
- `Amount`
- `TransactionTime`

When a difference is detected, the previous value is captured in a dictionary (later serialized into `TransactionRevision.PreviousValues`) and the field name is added to `ChangedFields`. The entity’s `UpdatedAt` is refreshed and a revision is saved, ensuring a permanent audit trail.

## 6. Revocation Logic
After processing the snapshot, the service finds `TransactionRecord`s within the last 24 hours that did not appear in the snapshot. These records move from `Active` to `Revoked`, `UpdatedAt` advances, and a revision is recorded with `ChangedFields = ["Status"]` and `Reason = "RevokedMissingFromSnapshot"`. This guarantees downstream systems understand why a record disappeared.

## 7. Idempotency Strategy
The ingestion pipeline enforces idempotency through multiple safeguards:
- **Snapshot deduplication** – Duplicate entries for the same `TransactionId` are collapsed.
- **Upsert short-circuiting** – If no field changes, no update or revision occurs.
- **Stateful transitions** – Revocation/finalization only execute when the status actually changes.
- **Transactional execution** – The entire run executes within a single EF Core transaction, so partial work never persists.

## 8. Finalization Logic
Records whose `TransactionTime` is older than 24 hours transition to `TransactionStatus.Finalized`. Finalization updates `UpdatedAt`, adds a revision (`Reason = "FinalizedAfter24Hours"`), and future ingestion runs skip further modifications. Finalized records effectively become immutable snapshots of history.

## 9. Automated Testing
`FBIS.Tests` uses xUnit and in-memory SQLite to exercise the real EF Core stack. The test suite covers:

**Core behavior**
- **Insert** – Validates new transactions insert correctly and card numbers are reduced to last4 digits.
- **Update detection** – Confirms field changes update the record, advance `UpdatedAt`, and generate `TransactionRevision`s with accurate payloads.
- **Revocation** – Ensures missing transactions within the window become `Revoked` and revisions capture the reason.
- **Idempotent reprocessing** – Running the same snapshot twice does not add records or revisions.
- **Finalization logic** – Transactions older than the lookback window finalize and emit revisions.
- **Finalized immutability** – Once finalized, a record stays immutable even if a new snapshot tries to mutate it.

**Advanced edge cases**
- **Duplicate storm** – Multiple identical entries in a snapshot never create duplicate database rows.
- **Out-of-order snapshot** – Processing does not rely on snapshot ordering.
- **Revoked → Reappears** – Revoked transactions can re-activate when they reappear.
- **Late arrival** – Older timestamps still insert if they fall within the ingestion window.
- **Snapshot with finalized transaction** – Finalized records remain untouched when appearing again.
- **Idempotency stress** – Repeated runs of the same snapshot cause no DB churn.
- **Crash rollback** – Simulated exceptions verify EF Core transactions roll back properly.
- **Data integrity** – Card numbers are always stored as last4.

All tests run against SQLite in-memory connections so EF Core behaviors (relationships, constraints, cascade delete) align with production.

## 10. Configuration
`appsettings.json` defines:
- `ConnectionStrings:Default` – Base connection string; the runtime augments it to point at `FBIS.App/fbis.db`.
- `TransactionFeed` – Mode and `MockFilePath`, controlling which JSON file is ingested.
- `Job` – Lookback hours and finalization toggle (currently defaulted to 24hrs / enabled).
- `Logging` – Standard .NET logging levels.

## 11. Build & Run Instructions
```bash
dotnet restore
dotnet build
dotnet run --project FBIS.App
```

Run the automated tests:
```bash
dotnet test
```
SQLite database files live at `FBIS.App/fbis.db`. Delete the file before re-running ingestion if you need a clean slate.

## 12. Design Decisions
- **EF Core code-first** – Simplifies schema evolution via migrations and keeps the domain model in C#.
- **Transactional ingestion** – Guarantees atomicity; either the entire snapshot applies or nothing does.
- **Audit trail** – `TransactionRevision` preserves field-level history for compliance and troubleshooting.
- **SQLite** – Lightweight, cross-platform database that requires zero external setup.
- **Snapshot normalization** – Handling deduplication, timestamp coercion, and card masking centrally makes the pipeline resilient to upstream quirks.

## 13. Assumptions
- Each snapshot contains transactions from roughly the last 24 hours.
- `TransactionId` uniquely identifies a transaction regardless of ordering.
- Snapshot timestamps are in UTC or can be safely converted to UTC.
- Card numbers may contain spaces/dashes; only numeric digits matter for extracting last4.

## 14. Time Estimate
- **Estimated time:** 6 hours
- **Actual time spent:** ~7 hours

Most of the additional time was spent designing automated tests and validating ingestion edge cases such as duplicate snapshots, out-of-order transactions, revoked transaction recovery, and idempotent reprocessing.

## 15. Tradeoffs and Future Improvements
While the current implementation focuses on correctness, auditability, and idempotent ingestion, several improvements could further enhance the system in a production environment.

- **Pluggable transaction feeds**  
	The ingestion service currently reads from a mock JSON feed. In a production setting this could be replaced with an API-backed implementation of a feed client interface, enabling ingestion from external transaction gateways.

- **Configurable ingestion window**  
	The system currently assumes a 24 hour reconciliation window. Making this configurable via application settings would allow the pipeline to support different operational requirements.

- **Background scheduling integration**  
	The console application is designed to run under an external scheduler. Integrating with a scheduling framework or containerized job runner would allow easier deployment in cloud environments.

- **Observability enhancements**  
	Adding metrics export (Prometheus/OpenTelemetry) and structured event tracing would provide deeper insight into ingestion performance and anomaly detection.

- **Performance optimization for large snapshots**  
	For very large transaction feeds, batching updates and using bulk insert strategies could further reduce database round trips.

These enhancements were intentionally left out to keep the exercise focused on correctness, reconciliation logic, and automated testing.
