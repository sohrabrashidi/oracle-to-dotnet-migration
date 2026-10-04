# Case study: moving branch till logic from Oracle PL/SQL to .NET and PostgreSQL

This write-up describes how I approach migrating business logic out of Oracle stored procedures. The worked example is a branch cash-till module from a currency exchange back office.

The code in this repository is a cut-down version written from scratch for this repo. It is not production code from an employer, but the problems and decisions below are the ones that come up in real migrations of this kind.

## 1. Starting point

Like many systems built in the 2000s and 2010s, the legacy application keeps most of its business rules in PL/SQL packages. The APEX screens are thin: they call `pkg_till.open_till`, `record_movement` and `close_till`, and show whatever `ORA-200xx` error comes back.

That design worked for years, but it causes the usual problems:

- business rules can only be changed by people who know PL/SQL and the APEX app;
- there are no automated tests, so every change is tested by hand at a branch;
- integrating with anything new (a mobile app, a partner API) means calling the database directly;
- Oracle licensing cost grows with every new server.

The goal was to move the rules into a C# service backed by PostgreSQL **without changing anything branch staff would notice**, except for a short list of bugs the business agreed to fix.

## 2. Approach

### Strangler, one package at a time

I don't rewrite the whole system in one go. Each PL/SQL package becomes a C# service with the same operations. The old screens keep working until the new UI or API is ready to replace them. The till module is a good first candidate because it's self-contained: four tables, one package, clear inputs and outputs.

### Keep the contract, then improve it

The first version of the C# service deliberately mirrors the package:

| PL/SQL | C# |
|---|---|
| `pkg_till.open_till` | `TillService.OpenTillAsync` |
| `pkg_till.record_movement` | `TillService.RecordMovementAsync` |
| `pkg_till.cash_on_hand` | `TillService.CashOnHandAsync` |
| `pkg_till.close_till` | `TillService.CloseTillAsync` |
| `RAISE_APPLICATION_ERROR(-20001..-20004)` | `TillException` with `TillErrorCode` 20001..20004 |
| `c_tolerance` | `TillService.Tolerance` |

Keeping the same error numbers looks old-fashioned, but it meant the existing APEX screens could switch to the new service through a thin adapter and show identical messages. Renaming things is cheap once the old code is gone. Doing it during the migration doubles the risk.

### Parity tests before cutover

The key safety net is a set of **scenario files** (`migrated/tests/TillManagement.Tests/scenarios/*.json`). Each one is a short script of operations plus the expected outcome: final status, per-currency variance, or the error code.

In a real project the expected values are captured by running the same scripts against the Oracle package. The C# port then has to produce the same results. The test suite runs every scenario twice:

1. against an in-memory repository (fast, runs anywhere);
2. against a real PostgreSQL database with the migrated schema (in CI).

If the C# port ever gives a different answer than Oracle, the test fails. The only allowed differences are the ones listed in section 4, and each of those has its own test.

## 3. Schema conversion

| Oracle | PostgreSQL | Why |
|---|---|---|
| `NUMBER(10)` + `SEQUENCE` | `bigint generated always as identity` | No sequence/trigger pair to keep in sync |
| `BEFORE INSERT` trigger setting the id | removed | Identity column does it |
| `NUMBER(15,3)` | `numeric(15,3)` | Same exact decimal semantics. Never `float` for money |
| `VARCHAR2(n)` | `varchar(n)` | |
| `CHAR(3)` | `char(3)` | Currency codes are always 3 letters |
| `DATE` + `SYSDATE` | `timestamptz` + `now()` | Oracle `DATE` has a time part but no time zone. Branches in different zones made reports ambiguous |
| `DECODE(type, 'IN', a, 'OUT', -a)` | `CASE type WHEN 'IN' THEN a WHEN 'OUT' THEN -a END` | |
| `NVL(x, 0)` | `COALESCE(x, 0)` | |
| `SELECT ... FOR UPDATE` | `SELECT ... FOR UPDATE` | Same row lock, so concurrent movements on one till are still serialised |

One change goes beyond translation. The legacy rule *"a cashier can only have one open till"* was enforced in PL/SQL with `SELECT COUNT(*)` followed by `INSERT`. Two sessions could both pass the check. In PostgreSQL it's now also a **partial unique index**:

```sql
create unique index ux_tills_one_open_per_cashier on tills (cashier) where status = 'OPEN';
```

The C# code still checks first (for a friendly message), and turns a unique violation into the same `20001` error if it loses a race.

## 4. Behaviour changes (agreed with the business)

A migration always uncovers bugs in the old system. Fixing them silently breaks parity tests and makes reconciliation during the parallel run confusing. So each one is listed, agreed, and covered by a test in `BehaviourChangeTests.cs`.

**#1 Round before validating a payout.**
The package checked `cash_on_hand < p_amount` with the unrounded amount, then stored `ROUND(p_amount, 3)`. Paying out `10.0004` from a till holding exactly `10.000` was refused, even though only `10.000` would have been recorded. The port rounds first.

**#2 Empty reference becomes NULL explicitly.**
Oracle treats `''` as `NULL`. PostgreSQL does not. Reports filtering `WHERE reference IS NULL` would have silently changed meaning, so the service converts empty or whitespace references to `null` before saving.

**#3 Currency codes in the closing count are case-insensitive.**
`p_counted('kwd')` did not match movements stored as `'KWD'`, so closing failed with "Missing count". This is the kind of bug branch staff hit every week. The port normalises keys to upper case.

## 5. Things to watch in Oracle → PostgreSQL ports

From experience, these are the details that cause trouble later if nobody checks them early:

- **Empty string vs NULL**: see #2. Search the PL/SQL for `IS NULL` checks on text columns.
- **Implicit transactions**: a PL/SQL call is atomic unless it commits inside. In C# the transaction has to be explicit. Here every service method opens one session, and nothing is visible until `CommitAsync`. `Failed_call_leaves_no_partial_data` tests this.
- **`DATE` arithmetic**: `SYSDATE - 1` is "24 hours ago" in Oracle. In PostgreSQL use `now() - interval '1 day'`.
- **Rounding**: Oracle `ROUND` rounds half away from zero. .NET's `Math.Round` defaults to banker's rounding, so always pass `MidpointRounding.AwayFromZero` explicitly.
- **Associative array order**: `t_count.FIRST/NEXT` iterates in key order. The C# port sorts keys the same way, so closing lines are written in the same order and reports match.
- **Error handling**: `WHEN NO_DATA_FOUND` blocks are often business logic in disguise (e.g. "no float means zero"). Each one needs an explicit equivalent, here `coalesce(..., 0)`.

## 6. Cutover plan

1. Deploy the new schema and service next to Oracle. No traffic yet.
2. **Shadow mode:** for two weeks, every till operation in Oracle is replayed against the new service, and closing results are compared nightly. Any mismatch is either a parity bug or one of the agreed changes.
3. Pilot one branch on the new service, with Oracle kept up to date by a sync job so it's possible to roll back the same day.
4. Roll out branch by branch. Oracle tables become read-only for this module.
5. After a full month-end close on the new system, remove the PL/SQL package.

## 7. Result

- Business rules are in C#, covered by unit and parity tests that run in CI on every change.
- A known race condition (two open tills per cashier) is closed at the database level.
- Three long-standing bugs were fixed in a controlled, documented way, not as a side effect.
- The module no longer depends on Oracle, so it can be reused by the API and mobile app.
