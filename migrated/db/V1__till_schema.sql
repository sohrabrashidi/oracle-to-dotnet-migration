-- PostgreSQL version of legacy/oracle/01_till_schema.sql
-- Notes on each change are in docs/case-study.md ("Schema conversion").

create table tills (
    till_id      bigint generated always as identity primary key,  -- was NUMBER + seq_tills
    branch_code  varchar(10)  not null,
    cashier      varchar(30)  not null,
    opened_at    timestamptz  not null default now(),               -- was DATE (no time zone)
    closed_at    timestamptz,
    status       varchar(10)  not null default 'OPEN'
                 check (status in ('OPEN', 'BALANCED', 'SHORT', 'OVER'))
);

-- Oracle enforced "one open till per cashier" in PL/SQL with a SELECT COUNT(*),
-- which two sessions could pass at the same time. A partial unique index closes that gap.
create unique index ux_tills_one_open_per_cashier on tills (cashier) where status = 'OPEN';

create table till_floats (
    till_id   bigint         not null references tills (till_id),
    currency  char(3)        not null,
    amount    numeric(15, 3) not null,
    primary key (till_id, currency)
);

create table till_movements (
    movement_id    bigint generated always as identity primary key,  -- was trigger + sequence
    till_id        bigint         not null references tills (till_id),
    currency       char(3)        not null,
    movement_type  varchar(3)     not null check (movement_type in ('IN', 'OUT')),
    amount         numeric(15, 3) not null check (amount > 0),
    reference      varchar(30),
    created_at     timestamptz    not null default now()
);

create index ix_till_movements_till_currency on till_movements (till_id, currency);

create table till_closings (
    till_id        bigint         not null references tills (till_id),
    currency       char(3)        not null,
    opening_float  numeric(15, 3) not null,
    expected       numeric(15, 3) not null,
    counted        numeric(15, 3) not null,
    variance       numeric(15, 3) not null,
    primary key (till_id, currency)
);
