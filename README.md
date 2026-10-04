# oracle-to-dotnet-migration

A worked example of migrating business logic from an **Oracle PL/SQL package** to a **C# (.NET 10) service on PostgreSQL**, with parity tests that prove the new code behaves like the old one.

The example is a branch cash-till module from a currency exchange back office: cashiers open a till with opening cash (the "float") per currency, record cash in and out during the day, and count the cash at closing. The system then decides whether the till is BALANCED, SHORT or OVER.

The full write-up of the approach is in **[docs/case-study.md](docs/case-study.md)**: schema conversion, behaviour changes, Oracle vs PostgreSQL pitfalls and the cutover plan.

## What's in here

```
legacy/oracle/
  01_till_schema.sql        original Oracle tables, sequences, trigger
  02_pkg_till.pks           package spec
  03_pkg_till.pkb           package body: the business rules being migrated

migrated/
  db/V1__till_schema.sql    PostgreSQL schema
  src/TillManagement.Core   TillService: one-to-one port of pkg_till
  src/TillManagement.Postgres  Npgsql repository, explicit transactions, row locks
  tests/TillManagement.Tests
    scenarios/*.json        parity scenarios (expected results = legacy behaviour)
    ParityScenarioTests     runs every scenario against in-memory AND PostgreSQL
    BehaviourChangeTests    the three intentional differences from Oracle

docs/case-study.md
```

## Highlights

- **Same contract as the old package.** Each PL/SQL procedure maps to one C# method, and the `ORA-20001..20004` error codes are preserved, so the existing screens can switch over without changing their error handling.
- **Parity tests driven by scenario files.** Adding a test case means adding a JSON file, not writing C#, so a business analyst can write them too.
- **Same tests against a real database.** CI starts PostgreSQL, applies the migrated schema and runs every scenario through the Npgsql repository.
- **A race condition fixed at the database level.** "One open till per cashier" was a `SELECT COUNT(*)` check in PL/SQL. It's now also a partial unique index.
- **Bugs fixed on purpose, not by accident.** Three legacy quirks (rounding order, `''` vs `NULL`, case-sensitive currency keys) are documented and each one has its own test.

## Running the tests

```bash
cd migrated
dotnet test TillManagement.slnx
```

To include the PostgreSQL run locally:

```bash
createdb till && psql till -f db/V1__till_schema.sql
export TILL_PG="Host=localhost;Database=till;Username=postgres;Password=postgres"
dotnet test TillManagement.slnx
```

## Example scenario

```json
{
  "description": "One currency over, another short: the till is SHORT.",
  "steps": [
    { "action": "open", "till": "t1", "branch": "hawally", "cashier": "omar", "amounts": { "KWD": 300, "EUR": 1000 } },
    { "action": "close", "till": "t1", "amounts": { "EUR": 1005, "KWD": 298.000 },
      "expect": { "status": "SHORT", "variances": { "EUR": 5, "KWD": -2 } } }
  ]
}
```

## Note

All code here was written for this repository as a demonstration. Company names, branches and figures are made up.

## License

MIT
