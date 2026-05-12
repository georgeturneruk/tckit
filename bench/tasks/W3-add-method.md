# Task W3 — Add a new method to an existing FB

In the TwinCAT project at `${TCUNIT_PATH}`, add a new method to
`FB_TestSuite`.

Method name: `LogSkipped`

Signature:

    METHOD LogSkipped : BOOL
    VAR_INPUT
        sReason : STRING;
    END_VAR

Body: a single line that returns TRUE.

    LogSkipped := TRUE;

Do not modify any existing method, declaration, or file other
than what is necessary to add this method. Persist the change
to the project files on disk. Briefly state which tool you
used to make the change.
