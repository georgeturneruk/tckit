// :Description: Example function block illustrating the TcKit RST line-comment style,
//               variable type-prefix convention, and bError propagation pattern.
//               Use as a starting point for new FBs.
FUNCTION_BLOCK FB_Example
VAR_INPUT
    bEnable    : BOOL;          // Rising edge starts the operation
    nSetpoint  : INT;           // Target value
END_VAR
VAR_OUTPUT
    bDone      : BOOL;          // TRUE for one cycle when complete
    bError     : BOOL;          // Latched until Reset
    nErrorId   : UDINT;         // ADS or library-specific error code
END_VAR
VAR
    eState     : E_ExampleState := E_ExampleState.Idle;
    fbInner    : FB_SomeOperation;
END_VAR

// :Description: Cyclic body. Drives the inner FB and propagates errors.
fbInner(bExecute := bEnable, nValue := nSetpoint);

IF fbInner.bError THEN
    eState := E_ExampleState.Error;
    bError := TRUE;
    nErrorId := fbInner.nErrorId;
ELSIF fbInner.bDone THEN
    eState := E_ExampleState.Done;
    bDone := TRUE;
END_IF

// :Description: Clear the error latch so the FB can be re-armed.
METHOD Reset : BOOL
    bError := FALSE;
    nErrorId := 0;
    eState := E_ExampleState.Idle;
    Reset := TRUE;
END_METHOD
