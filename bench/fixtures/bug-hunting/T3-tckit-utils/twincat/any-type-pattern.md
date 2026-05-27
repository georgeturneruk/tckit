# The TwinCAT `ANY` descriptor

TwinCAT exposes a built-in generic input type, `ANY`, that lets a
method accept "a value of any type, including user-defined STRUCTs"
without per-type overloads. The compiler resolves `ANY` to a small
descriptor at the call site:

```
TYPE __SYSTEM.AnyType :
STRUCT
    nTypeClass : __SYSTEM.TYPE_CLASS;
    diSize     : DINT;                   // SIZEOF(actual argument)
    pValue     : POINTER TO BYTE;        // ADR(actual argument)
END_STRUCT
END_TYPE
```

A method declared as

```
METHOD Push : BOOL
VAR_INPUT
    item : ANY;
END_VAR
```

can be called as `rb.Push(myLreal)` or `rb.Push(myStruct)` and read
the argument's address with `item.pValue` and its byte width with
`item.diSize`. The caller writes no `ADR(...)` and no `SIZEOF(...)`.

## Reading and writing through `pValue`

`ANY` is technically a `VAR_INPUT` type - the FB only sees the
descriptor by value. But the descriptor *contains* the caller's
address. The pointer is live for the duration of the synchronous
call, so the method body may both read from and write to it:

```
// Push (write into our storage from the caller's variable)
MEMCPY(destAddr := ADR(_storage[_writeIdx * elementSize]),
       srcAddr  := item.pValue,
       n        := elementSize);

// Pop (write back into the caller's variable from our storage)
MEMCPY(destAddr := out.pValue,
       srcAddr  := ADR(_storage[_readIdx * elementSize]),
       n        := elementSize);
```

The "write back through a `VAR_INPUT ANY`" direction is unusual but
not a hack: `pValue` is a `POINTER TO BYTE`, and using a pointer for
its address-of meaning is exactly what pointers are for. The result
is that `rb.Pop(sample);` updates `sample` in the caller's frame as
if it had been declared `VAR_IN_OUT`, while keeping the API
pointer-free at the call site.

## The size-mismatch guard

Because the FB can no longer rely on the type system to keep
elements homogeneous, every `Push`/`Pop`/`Peek` should compare the
caller's `diSize` against the element size locked in by
`Configure`:

```
IF UDINT_TO_DINT(elementSize) <> item.diSize THEN
    Push := FALSE;
    RETURN;
END_IF
```

This catches `rb.Push(myInt)` against a buffer that was configured
for `ARRAY OF LREAL`: the operation refuses rather than copying the
wrong number of bytes.

## When to reach for `ANY`

Use `ANY` to hide pointer arithmetic at API boundaries where:

- the FB stores or shuffles raw bytes irrespective of type
  (queues, ring buffers, byte pools), and
- the call site would otherwise repeat the same
  `ADR(x), SIZEOF(x)` pair on every line.

Do **not** use `ANY` where the value actually matters - PID
tunings, control set-points, anything you'd want to compute with.
The descriptor erases the type, so arithmetic on the underlying
bytes is the caller's problem. `ANY` is a transport, not a value.
