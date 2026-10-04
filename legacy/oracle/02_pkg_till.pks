CREATE OR REPLACE PACKAGE pkg_till AS
    -- Error codes raised to the APEX front end.
    e_till_already_open  CONSTANT PLS_INTEGER := -20001;
    e_till_not_open      CONSTANT PLS_INTEGER := -20002;
    e_insufficient_cash  CONSTANT PLS_INTEGER := -20003;
    e_bad_count          CONSTANT PLS_INTEGER := -20004;

    -- Variance within this amount (in each currency) is still "BALANCED".
    c_tolerance          CONSTANT NUMBER := 0.5;

    TYPE t_count IS TABLE OF NUMBER INDEX BY VARCHAR2(3);

    FUNCTION open_till (
        p_branch_code IN VARCHAR2,
        p_cashier     IN VARCHAR2,
        p_floats      IN t_count
    ) RETURN NUMBER;

    PROCEDURE record_movement (
        p_till_id   IN NUMBER,
        p_currency  IN VARCHAR2,
        p_type      IN VARCHAR2,
        p_amount    IN NUMBER,
        p_reference IN VARCHAR2 DEFAULT NULL
    );

    FUNCTION cash_on_hand (
        p_till_id  IN NUMBER,
        p_currency IN VARCHAR2
    ) RETURN NUMBER;

    FUNCTION close_till (
        p_till_id IN NUMBER,
        p_counted IN t_count
    ) RETURN VARCHAR2;
END pkg_till;
/
