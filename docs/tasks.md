# Implementation Tasks

## Phase 1 – Infrastructure

- Setup Generic Host
- Configure logging
- Configure EF Core DbContext
- Setup SQLite database

## Phase 2 – Domain Models

Create entities:

TransactionRecord
TransactionRevision
IngestionRun
TransactionStatus enum

## Phase 3 – Feed

Implement

ITransactionFeedClient
MockTransactionFeedClient

## Phase 4 – Ingestion Logic

Implement:

IngestionRunner
TransactionReconciler

Responsibilities:
- insert new transactions
- detect updates
- create revision records
- revoke missing transactions
- finalize transactions older than 24 hours

## Phase 5 – Tests

Write tests for:

insert
update detection
revocation
idempotency
finalization