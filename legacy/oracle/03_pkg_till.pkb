CREATE OR REPLACE PACKAGE BODY pkg_till AS

    PROCEDURE assert_open (p_till_id IN NUMBER) IS
        v_status tills.status%TYPE;
    BEGIN
        SELECT status INTO v_status
          FROM tills
         WHERE till_id = p_till_id
           FOR UPDATE;

        IF v_status <> 'OPEN' THEN
            RAISE_APPLICATION_ERROR(e_till_not_open, 'Till ' || p_till_id || ' is ' || v_status);
        END IF;
    EXCEPTION
        WHEN NO_DATA_FOUND THEN
            RAISE_APPLICATION_ERROR(e_till_not_open, 'Till ' || p_till_id || ' does not exist');
    END assert_open;

    FUNCTION open_till (
        p_branch_code IN VARCHAR2,
        p_cashier     IN VARCHAR2,
        p_floats      IN t_count
    ) RETURN NUMBER IS
        v_till_id  NUMBER;
        v_existing NUMBER;
        v_ccy      VARCHAR2(3);
    BEGIN
        SELECT COUNT(*) INTO v_existing
          FROM tills
         WHERE cashier = UPPER(p_cashier)
           AND status = 'OPEN';

        IF v_existing > 0 THEN
            RAISE_APPLICATION_ERROR(e_till_already_open, 'Cashier ' || p_cashier || ' already has an open till');
        END IF;

        v_till_id := seq_tills.NEXTVAL;

        INSERT INTO tills (till_id, branch_code, cashier)
        VALUES (v_till_id, UPPER(p_branch_code), UPPER(p_cashier));

        v_ccy := p_floats.FIRST;
        WHILE v_ccy IS NOT NULL LOOP
            INSERT INTO till_floats (till_id, currency, amount)
            VALUES (v_till_id, v_ccy, NVL(p_floats(v_ccy), 0));
            v_ccy := p_floats.NEXT(v_ccy);
        END LOOP;

        RETURN v_till_id;
    END open_till;

    FUNCTION cash_on_hand (
        p_till_id  IN NUMBER,
        p_currency IN VARCHAR2
    ) RETURN NUMBER IS
        v_float NUMBER := 0;
        v_net   NUMBER := 0;
    BEGIN
        BEGIN
            SELECT amount INTO v_float
              FROM till_floats
             WHERE till_id = p_till_id
               AND currency = p_currency;
        EXCEPTION
            WHEN NO_DATA_FOUND THEN v_float := 0;
        END;

        SELECT NVL(SUM(DECODE(movement_type, 'IN', amount, 'OUT', -amount)), 0)
          INTO v_net
          FROM till_movements
         WHERE till_id = p_till_id
           AND currency = p_currency;

        RETURN v_float + v_net;
    END cash_on_hand;

    PROCEDURE record_movement (
        p_till_id   IN NUMBER,
        p_currency  IN VARCHAR2,
        p_type      IN VARCHAR2,
        p_amount    IN NUMBER,
        p_reference IN VARCHAR2 DEFAULT NULL
    ) IS
    BEGIN
        assert_open(p_till_id);

        IF p_type = 'OUT' AND cash_on_hand(p_till_id, UPPER(p_currency)) < p_amount THEN
            RAISE_APPLICATION_ERROR(e_insufficient_cash,
                'Not enough ' || UPPER(p_currency) || ' in till ' || p_till_id);
        END IF;

        INSERT INTO till_movements (till_id, currency, movement_type, amount, reference)
        VALUES (p_till_id, UPPER(p_currency), p_type, ROUND(p_amount, 3), p_reference);
    END record_movement;

    FUNCTION close_till (
        p_till_id IN NUMBER,
        p_counted IN t_count
    ) RETURN VARCHAR2 IS
        v_status   VARCHAR2(10) := 'BALANCED';
        v_expected NUMBER;
        v_counted  NUMBER;
        v_variance NUMBER;
        v_float    NUMBER;
    BEGIN
        assert_open(p_till_id);

        -- Every currency the till touched must be counted, plus anything the cashier declares.
        FOR r IN (
            SELECT currency FROM till_floats WHERE till_id = p_till_id
            UNION
            SELECT currency FROM till_movements WHERE till_id = p_till_id
        ) LOOP
            IF NOT p_counted.EXISTS(r.currency) THEN
                RAISE_APPLICATION_ERROR(e_bad_count, 'Missing count for ' || r.currency);
            END IF;
        END LOOP;

        DECLARE
            v_ccy VARCHAR2(3) := p_counted.FIRST;
        BEGIN
            WHILE v_ccy IS NOT NULL LOOP
                v_counted := p_counted(v_ccy);
                IF v_counted IS NULL OR v_counted < 0 THEN
                    RAISE_APPLICATION_ERROR(e_bad_count, 'Invalid count for ' || v_ccy);
                END IF;

                v_expected := cash_on_hand(p_till_id, v_ccy);
                v_variance := ROUND(v_counted - v_expected, 3);

                BEGIN
                    SELECT amount INTO v_float FROM till_floats WHERE till_id = p_till_id AND currency = v_ccy;
                EXCEPTION
                    WHEN NO_DATA_FOUND THEN v_float := 0;
                END;

                INSERT INTO till_closings (till_id, currency, opening_float, expected, counted, variance)
                VALUES (p_till_id, v_ccy, v_float, v_expected, v_counted, v_variance);

                -- SHORT wins over OVER: a shortage anywhere needs investigating first.
                IF v_variance < -c_tolerance THEN
                    v_status := 'SHORT';
                ELSIF v_variance > c_tolerance AND v_status = 'BALANCED' THEN
                    v_status := 'OVER';
                END IF;

                v_ccy := p_counted.NEXT(v_ccy);
            END LOOP;
        END;

        UPDATE tills
           SET status = v_status,
               closed_at = SYSDATE
         WHERE till_id = p_till_id;

        RETURN v_status;
    END close_till;

END pkg_till;
/
