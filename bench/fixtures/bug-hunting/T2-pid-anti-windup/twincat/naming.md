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
`nCount` — the type is already on the declaration line):

```
enableMotor : BOOL;
targetSpeed : LREAL;
nextState : E_State;
```

Match existing file style if it differs; a single file should be
self-consistent.

## Acronyms

Treat acronyms as words: `FB_PidController`, not
`FB_PIDController`. `Tcp` not `TCP`. Keeps PascalCase readable when
acronyms stack.
