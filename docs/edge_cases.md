# FBIS Transaction Ingestion – Edge Cases

This document describes important edge cases that must be handled correctly by the ingestion logic.

The ingestion job processes a snapshot of transactions that occurred within the last 24 hours. Because upstream systems may send events late or out of order, the system must reconcile changes carefully.

## 1. Out-of-Order Transactions

Transactions in the snapshot may not be ordered by time.

Example:

Snapshot:
T-1003 (10:00)
T-1001 (08:00)
T-1002 (09:00)

Expected behavior:
The system should not rely on ordering when processing records.

Solution:
All transactions should be processed independently using TransactionId as the primary identifier.

---

## 2. Late Arriving Transactions

A transaction that occurred earlier may arrive in a later snapshot.

Example:

Run 1 snapshot:
T-1001
T-1002

Run 2 snapshot:
T-1001
T-1002
T-1003 (timestamp from 2 hours ago)

Expected behavior:
T-1003 should be inserted normally even though its timestamp is earlier than previously processed transactions.

---

## 3. Transaction Update

A transaction with the same TransactionId may appear with updated values.

Example:

Run 1:
T-1001 amount = 19.99

Run 2:
T-1001 amount = 21.99

Expected behavior:
- Detect the change
- Update the stored record
- Record the change in the audit or revision table

---

## 4. No Change Scenario

A transaction appears again with identical values.

Example:

Run 1:
T-1001 amount = 19.99

Run 2:
T-1001 amount = 19.99

Expected behavior:
- No database update
- No revision record
- Ensures idempotent behavior

---

## 5. Revoked Transactions

A previously stored transaction within the 24 hour window may disappear from the snapshot.

Example:

Run 1 snapshot:
T-1001
T-1002

Run 2 snapshot:
T-1001

Expected behavior:
T-1002 should be marked as revoked because it is no longer present in the latest snapshot.

---

## 6. Revoked Transaction Appearing Again

A revoked transaction may appear again in a later snapshot.

Example:

Run 1:
T-1002

Run 2:
T-1002 missing → revoked

Run 3:
T-1002 appears again

Expected behavior:
The transaction should be restored to active status and recorded as an update.

---

## 7. Duplicate Records in Snapshot

The snapshot may contain duplicate entries for the same TransactionId.

Example:

Snapshot:
T-1001
T-1001

Expected behavior:
Only one record should be processed. Duplicate entries should not create multiple updates.

---

## 8. Finalized Transactions

Transactions older than 24 hours may be finalized.

Example:

TransactionTime = 30 hours ago

Expected behavior:
The transaction should be marked finalized and must not be modified in future runs.

---

## 9. Idempotent Reprocessing

Running the job multiple times with the same snapshot should not create:

- duplicate transactions
- duplicate revisions
- unnecessary updates

Example:

Run 1:
Insert T-1001

Run 2:
Same snapshot

Expected behavior:
No new records should be created.

---

## 10. Partial Failure During Run

If an error occurs during ingestion, partial updates should not remain in the database.

Expected behavior:
The entire ingestion run should execute within a single database transaction.
If an error occurs, the transaction should roll back.

---

## Summary

Handling these edge cases ensures the ingestion process remains:

- reliable
- idempotent
- resilient to late and unordered events