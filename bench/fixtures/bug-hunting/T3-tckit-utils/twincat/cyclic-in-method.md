# Cyclic logic in a method, not the FB body

A function block has two places to put runtime behaviour: methods
(and actions, properties) declared underneath it, and the FB's own
implicit body — the ST that runs when you instantiate the FB and
call `fbInst()` like a function.

**Only methods, actions, and properties form the interface surface.**
The FB body sits *outside* any `INTERFACE` contract. If `FB_Foo`
declares `IMPLEMENTS I_Foo` and the cyclic behaviour lives in the FB
body, a consumer holding `I_Foo` cannot reach that behaviour: there
is no syntax for `iFoo()` to call the body.

## The rule

Put cyclic behaviour in an explicit method:

```
METHOD Cyclic : BOOL
VAR_INPUT
    fInput : LREAL;
END_VAR
// cyclic logic here
```

Leave the FB body empty, or use it only as a one-liner that
forwards to the cyclic method:

```
// FB_Foo body (the cyclic implementation block)
THIS^.Cyclic(fInput := fInput);
```

The forwarding variant is the convenience for callers who instantiate
`FB_Foo` directly and call it like a function; the method itself is
what consumers of `I_Foo` will call.

## Why this matters

You can write a working controller entirely in the FB body and the
tests against `fbController()` will pass. The moment another part
of the codebase tries to iterate `ARRAY[1..N] OF I_Controller` and
call `arr[i].Cyclic()`, your FB's logic never runs — silently. The
controller is part of the array, but it cannot be driven.

The rule is conservative: even if no interface is on the table
today, write cyclic logic as a method so an interface can be added
later without restructuring the FB.

→ Pairs with [polymorphism-arrays.md](polymorphism-arrays.md).
