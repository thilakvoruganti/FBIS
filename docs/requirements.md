# FBIS Transaction Ingestion Requirements

## Overview
This project implements a .NET 10 console application that ingests retail transaction snapshots.

The application runs once per execution and processes a snapshot containing all transactions that occurred within the last 24 hours.

## Core Behavior

1. Load transaction snapshot from a JSON feed.
2. Insert new transactions into the database.
3. If an existing transaction appears with the same TransactionId:
   - Compare fields
   - Record any changes
4. If a previously stored transaction within the last 24 hours does not appear in the snapshot:
   - Mark it as revoked
5. Transactions older than 24 hours may be finalized and should not change afterward.

## Idempotency Requirement

Repeated runs with the same input must NOT:

- create duplicate transactions
- create duplicate audit records
- change unchanged records

## Data Model

Minimum fields:

TransactionId
CardNumber
LocationCode
ProductName
Amount
TransactionTime

Card numbers must NOT be stored fully. Only last4 or hash may be stored.

## Expected Database Tables

TransactionRecord
TransactionRevision
IngestionRun

## Non Functional Requirements

- .NET 10 console app
- EF Core with SQLite
- Configuration via appsettings.json
- Automated tests