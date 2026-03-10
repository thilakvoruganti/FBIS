# FBIS Architecture

The system follows a layered architecture.

## Layers

Program
↓
IngestionRunner
↓
TransactionReconciler
↓
Repository / DbContext
↓
SQLite Database

## Responsibilities

Program
- bootstraps Generic Host
- configures dependency injection
- starts ingestion run

IngestionRunner
- orchestrates the ingestion process
- manages database transaction

TransactionReconciler
- compares incoming snapshot with stored data
- performs inserts, updates, revocations

MockTransactionFeedClient
- loads snapshot from JSON file

Repository
- persists entities using EF Core