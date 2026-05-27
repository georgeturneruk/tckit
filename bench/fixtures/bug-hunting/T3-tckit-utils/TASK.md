# T3 - TcKit utilities (TDD)

Implement three small TwinCAT utilities in `T3TckitUtils_Plc/` so that
the 28 TcUnit tests under `TckitUtilsTests/` pass. The library is
organised into one folder per utility on both the library and tests
PLCs.

## What the fixture ships

Library (`T3TckitUtils_Plc`):

- `POUs/PID/`
  - `I_Pid` interface (Update, Reset) - fully declared.
  - `FB_Pid` - empty FB stub.
- `POUs/RingBuffer/`
  - `FB_RingBuffer` - empty FB stub.
- `POUs/Strings/`
  - `FB_StringBuilder` - empty FB stub.
  - `F_Trim`, `F_StartsWith`, `F_EndsWith`, `F_Contains` - empty FUNCTION
    stubs (headers only).

Tests project (`TckitUtilsTests`):

- Three TcUnit suites under `POUs/PID/`, `POUs/RingBuffer/`,
  `POUs/Strings/`, plus `MAIN` driving all three.
- `DUTs/RingBuffer/ST_Sample` - `{ t : LREAL; v : LREAL; }`, a
  tests-internal fixture used only by the user-defined-type ring-
  buffer test. Not part of the library's public surface.

## Public API the tests rely on

### FB_Pid  (`POUs/PID/`)

`FB_Pid` must implement `I_Pid`. The interface declares two methods;
the tests also use these properties directly on the concrete FB.

Methods (must satisfy `I_Pid`):

```
METHOD Update : LREAL
VAR_INPUT
    setpoint    : LREAL;
    measurement : LREAL;
    deltaT      : LREAL;
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

Behaviour the tests assert: proportional-only, output clamping to
`OutputMin`/`OutputMax`, integral accumulation, anti-windup (stop
integrating while saturated), derivative-on-measurement (a step in
`setpoint` must not spike the output), reverse-mode sign flip,
`Reset()` clears latched state, negative-tuning setter rejection,
`IsSaturated` reflects the clamp state, and `Update` reachable
through an `I_Pid` reference. The polymorphism check implies the
cyclic logic lives in a method, not in the FB body. See
`twincat/cyclic-in-method.md`.

### FB_RingBuffer  (`POUs/RingBuffer/`)

A FIFO ring buffer over caller-supplied storage; the element type is
opaque to the FB. Every method takes a TwinCAT `ANY`, so the call
site never types `ADR(...)` or `SIZEOF(...)`. See
`twincat/any-type-pattern.md` for the descriptor mechanics and the
size-mismatch guard convention.

```
METHOD Configure : BOOL
VAR_INPUT
    storage  : ANY;
    capacity : UDINT;
END_VAR
// Returns FALSE if storage.pValue is NULL, capacity = 0,
// storage.diSize < capacity, or storage.diSize MOD capacity <> 0.
// Otherwise records elementSize := storage.diSize / capacity and
// resets the buffer.

METHOD Push : BOOL
VAR_INPUT
    item : ANY;
END_VAR
// Returns FALSE when full OR item.diSize <> elementSize.

METHOD Pop : BOOL
VAR_INPUT
    out : ANY;
END_VAR
// FIFO. Returns FALSE when empty OR out.diSize <> elementSize.
// On success, copies the front element into out.pValue and advances.

METHOD Peek : BOOL
VAR_INPUT
    out : ANY;
END_VAR
// Same as Pop but non-destructive.

METHOD Clear
```

Properties (all GET-only):

| Property   | Type   | Notes                                       |
| ---------- | ------ | ------------------------------------------- |
| `Count`    | UDINT  | Number of elements currently in the buffer. |
| `Capacity` | UDINT  | Maximum element count (set by `Configure`). |
| `IsEmpty`  | BOOL   | `Count = 0`.                                 |
| `IsFull`   | BOOL   | `Count = Capacity`.                          |

Call-site shape the tests use:

```
VAR
    storage : ARRAY[1..16] OF LREAL;
    rb      : FB_RingBuffer;
    sample  : LREAL;
END_VAR

rb.Configure(storage, 16);
rb.Push(3.14);
rb.Pop(sample);     // sample is now 3.14
```

The same call shape applies to `ARRAY OF INT` and `ARRAY OF ST_Sample`.
Use `MEMCPY` for the byte copies.

### FB_StringBuilder  (`POUs/Strings/`)

Append-only string accumulator with a fixed 4095-character backing
buffer. Reads back via `CopyTo`, never via a STRING-by-value property
(a 4 KB stack copy through a return value would be a footgun).

```
METHOD Append : BOOL
VAR_INPUT
    s : STRING;
END_VAR
// Returns FALSE if appending would push Length above 4095. On a
// refused Append, Length is unchanged.

METHOD AppendLine : BOOL
VAR_INPUT
    s : STRING;
END_VAR
// Append followed by '$N'. Returns FALSE on overflow.

METHOD Clear
// Zeros Length and the backing buffer.

METHOD CopyTo : UDINT
VAR_INPUT
    pDest    : POINTER TO BYTE;
    destSize : UDINT;
END_VAR
// Copies the accumulated string into the caller's buffer, NUL-
// terminating if room permits, truncating otherwise. Returns the
// number of payload bytes written (excluding the NUL).
```

Properties (all GET-only):

| Property   | Type   | Notes                                |
| ---------- | ------ | ------------------------------------ |
| `Length`   | UDINT  | Current payload length, in bytes.    |
| `Capacity` | UDINT  | Returns the literal constant 4095.   |
| `IsFull`   | BOOL   | `Length = Capacity`.                 |

### Standalone string functions  (`POUs/Strings/`)

```
FUNCTION F_Trim       : STRING  (s : STRING)
FUNCTION F_StartsWith : BOOL    (s, prefix : STRING)
FUNCTION F_EndsWith   : BOOL    (s, suffix : STRING)
FUNCTION F_Contains   : BOOL    (s, needle : STRING)
```

- `F_Trim` strips leading and trailing `$20`, `$09`, `$0D`, `$0A`
  bytes. The empty string trims to the empty string.
- `F_StartsWith` returns TRUE for an empty prefix, FALSE when the
  prefix is longer than `s`.
- `F_EndsWith` follows the same edge-case rules.
- `F_Contains` returns TRUE for an empty needle.

## Rules

- Do not change anything under `TckitUtilsTests/`. Test files are
  read-only for grading.
- Follow the conventions in `CLAUDE.md` and the `twincat/` topic
  files. In particular: cyclic logic for `FB_Pid` lives in a method,
  not in the FB body; the ring buffer hides pointer arithmetic
  behind `ANY` descriptors.
- Vanilla and TcKit arms receive this prompt verbatim. No diagnosis
  hints.
