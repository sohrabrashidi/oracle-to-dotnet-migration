-- Legacy Oracle schema (simplified). This is what the migration starts from.
-- Branch cashiers open a till in the morning, every cash movement is recorded
-- per currency, and at closing the counted cash is compared with the system.

CREATE TABLE tills (
    till_id        NUMBER(10)      NOT NULL,
    branch_code    VARCHAR2(10)    NOT NULL,
    cashier        VARCHAR2(30)    NOT NULL,
    opened_at      DATE            DEFAULT SYSDATE NOT NULL,
    closed_at      DATE,
    status         VARCHAR2(10)    DEFAULT 'OPEN' NOT NULL,
    CONSTRAINT pk_tills PRIMARY KEY (till_id),
    CONSTRAINT ck_tills_status CHECK (status IN ('OPEN', 'BALANCED', 'SHORT', 'OVER'))
);

CREATE SEQUENCE seq_tills START WITH 1 INCREMENT BY 1 NOCACHE;

CREATE TABLE till_movements (
    movement_id    NUMBER(12)      NOT NULL,
    till_id        NUMBER(10)      NOT NULL,
    currency       CHAR(3)         NOT NULL,
    movement_type  VARCHAR2(3)     NOT NULL,   -- 'IN' cash received, 'OUT' cash paid
    amount         NUMBER(15,3)    NOT NULL,
    reference      VARCHAR2(30),
    created_at     DATE            DEFAULT SYSDATE NOT NULL,
    CONSTRAINT pk_till_movements PRIMARY KEY (movement_id),
    CONSTRAINT fk_movements_till FOREIGN KEY (till_id) REFERENCES tills (till_id),
    CONSTRAINT ck_movements_type CHECK (movement_type IN ('IN', 'OUT')),
    CONSTRAINT ck_movements_amount CHECK (amount > 0)
);

CREATE SEQUENCE seq_till_movements START WITH 1 INCREMENT BY 1 CACHE 50;

CREATE OR REPLACE TRIGGER trg_till_movements_bi
BEFORE INSERT ON till_movements
FOR EACH ROW
BEGIN
    IF :NEW.movement_id IS NULL THEN
        :NEW.movement_id := seq_till_movements.NEXTVAL;
    END IF;
END;
/

CREATE TABLE till_closings (
    till_id        NUMBER(10)      NOT NULL,
    currency       CHAR(3)         NOT NULL,
    opening_float  NUMBER(15,3)    NOT NULL,
    expected       NUMBER(15,3)    NOT NULL,
    counted        NUMBER(15,3)    NOT NULL,
    variance       NUMBER(15,3)    NOT NULL,
    CONSTRAINT pk_till_closings PRIMARY KEY (till_id, currency),
    CONSTRAINT fk_closings_till FOREIGN KEY (till_id) REFERENCES tills (till_id)
);

CREATE TABLE till_floats (
    till_id        NUMBER(10)      NOT NULL,
    currency       CHAR(3)         NOT NULL,
    amount         NUMBER(15,3)    NOT NULL,
    CONSTRAINT pk_till_floats PRIMARY KEY (till_id, currency),
    CONSTRAINT fk_floats_till FOREIGN KEY (till_id) REFERENCES tills (till_id)
);
