# Naming conventions

## POU prefixes

| Prefix | Meaning |
|---|---|
| `FB_` | Function block |
| `PRG_` | Program |
| `GVL_` | Global variable list |
| `E_` | Enum |
| `ST_` | Struct |
| `I_` | Interface |

Apply the prefix to the type name, not to instances. So `FB_Pid`
is the FB type, `fbPid` is an instance, and
`pids : ARRAY[1..N] OF FB_Pid` is an array of instances.

## Methods, actions, properties

PascalCase, no prefix:

```
METHOD Cyclic : BOOL
METHOD Update : LREAL
PROPERTY Kp : LREAL
ACTION Reset
```

## Variables

camelCase, no type prefix (avoid Hungarian-style `bEnable`,
`nCount`, the type is already on the declaration line):

```
enableMotor : BOOL;
targetSpeed : LREAL;
nextState : E_State;
```

Match existing file style if it differs; a single file should be
self-consistent.

## Property backing fields

A property often needs a private field of the same type to store
the value. ST is case-insensitive, so the field cannot share the
property's name (`Kp` and `kp` are the same identifier). Prefix the
backing field with a single underscore and use the same name in
camelCase:

```
PROPERTY Kp : LREAL

// in the property's GET / SET:
Kp := _kp;          // GET
IF Kp >= 0.0 THEN   // SET
    _kp := Kp;
END_IF

// in the FB's VAR block:
VAR
    _kp : LREAL;
END_VAR
```

The leading underscore is the only deviation from the "no type
prefix" rule above. It exists specifically to break the
property/field name collision and signals "backing field, accessed
only through the property". Plain camelCase still applies to
everything else: loop counters, scratch variables, and FB-internal
state that is not exposed through a property (e.g. `lastMeasurement`,
`hasPrevious`).

## Acronyms

Treat acronyms as words: `FB_PidController`, not
`FB_PIDController`. `Tcp` not `TCP`. Keeps PascalCase readable when
acronyms stack.
