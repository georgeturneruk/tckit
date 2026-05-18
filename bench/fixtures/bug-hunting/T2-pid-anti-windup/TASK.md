# T2 - PID controller with anti-windup (TDD)

Implement a PID controller in `T2Pid_Plc/` such that the TcUnit suite
under `PidTests/` passes.

## Public API the tests rely on

`FB_Pid` must implement `I_Pid`. The interface declares two methods;
the tests also use these properties directly on the concrete FB
instance.

Methods (must satisfy `I_Pid`):

```
METHOD Update : LREAL
VAR_INPUT
    fSetpoint    : LREAL;
    fMeasurement : LREAL;
    fDeltaT      : LREAL;
END_VAR

METHOD Reset
```

Properties (FB-only; not on `I_Pid`):

| Property      | Type   | Accessors | Notes                                          |
| ------------- | ------ | --------- | ---------------------------------------------- |
| `Kp`          | LREAL  | GET + SET | Setter must reject negative values.            |
| `Ki`          | LREAL  | GET + SET | Setter must reject negative values.            |
| `Kd`          | LREAL  | GET + SET | Setter must reject negative values.            |
| `OutputMin`   | LREAL  | GET + SET |                                                |
| `OutputMax`   | LREAL  | GET + SET |                                                |
| `Mode`        | INT    | GET + SET | `0` = direct action, `1` = reverse action.     |
| `IntegralTerm`| LREAL  | GET only  | Exposes the internal integral state.           |
| `IsSaturated` | BOOL   | GET only  | TRUE when the output is currently clamped.     |

## Behaviour the tests assert

- **Proportional only.** With `Ki = Kd = 0`, `output = Kp * (setpoint - measurement)`.
- **Output clamps** to `OutputMax` (positive saturation) and `OutputMin`
  (negative saturation).
- **Integral accumulates** correctly when `Ki > 0` and the controller
  is not saturated.
- **Anti-windup.** When the output is saturated, the integral term
  must not continue to grow. A simple "stop integrating while
  saturated" (conditional integration) is enough.
- **Derivative on measurement.** The derivative term must be computed
  from the (negative) rate of change of the measurement, not from
  the rate of change of the error. A step in setpoint must not
  produce a derivative spike.
- **Reverse mode** flips the sign of the controller output.
- **Reset()** clears the integrator (and any other latched state).
- **Setter validation.** Setting `Kp`, `Ki`, or `Kd` to a negative
  value must not change the property's value.
- **Polymorphism.** Calling `Update(...)` through an `I_Pid` reference
  must produce the same output as calling it on the concrete
  `FB_Pid` instance. This implies the cyclic logic must live in a
  method, not in the FB body. The FB body is not part of any
  `INTERFACE` contract.

## Rules

- Do not change anything under `PidTests/`. Test files are
  read-only for grading.
- Follow the conventions in `CLAUDE.md` and the `twincat/` topic
  files. In particular: cyclic logic lives in a method, not the FB
  body.
- Vanilla and TcKit arms receive this prompt verbatim. No diagnosis
  hints.
