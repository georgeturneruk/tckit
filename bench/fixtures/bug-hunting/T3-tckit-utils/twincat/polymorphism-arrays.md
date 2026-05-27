# Polymorphism: arrays of FB instances via interfaces

Use **interfaces** to group and loop over collections of function
blocks. Pointers are unsafe (no type system enforcement, no
automatic null check); `REFERENCE TO FB_X` cannot live in an array
the way an interface reference can.

## Pattern

1. Define an interface:

   ```
   INTERFACE I_Sensor
   METHOD Cyclic : BOOL
   PROPERTY Value : LREAL  // get-only
   ```

2. Implement it on the target FBs:

   ```
   FUNCTION_BLOCK FB_Laser IMPLEMENTS I_Sensor
   FUNCTION_BLOCK FB_Encoder IMPLEMENTS I_Sensor
   ```

3. Create an array of the interface type:

   ```
   VAR
       arrSensors : ARRAY[1..N] OF I_Sensor;
       fbLaser    : FB_Laser;
       fbEncoder  : FB_Encoder;
   END_VAR
   ```

4. Populate the array with concrete FB instances:

   ```
   arrSensors[1] := fbLaser;
   arrSensors[2] := fbEncoder;
   ```

5. Loop:

   ```
   FOR i := 1 TO N DO
       IF arrSensors[i] <> 0 THEN
           arrSensors[i].Cyclic();
       END_IF
   END_FOR
   ```

## Safety rule

Always null-check before calling through an interface reference. An
unpopulated array slot will page-fault the PLC. The check is cheap;
the page-fault is not.

```
IF arrSensors[i] <> 0 THEN
    arrSensors[i].Cyclic();
END_IF
```

## Why not pointers or REFERENCE TO

- `POINTER TO FB_X` works but bypasses the type system. The compiler
  cannot catch a mistyped pointer cast; a wrong dereference is a
  page fault, not a build error.
- `REFERENCE TO FB_X` is type-checked but cannot be arrayed
  natively in IEC61131-3. Only interface references behave like
  values when assigned into arrays.

→ Pairs with [cyclic-in-method.md](cyclic-in-method.md): if cyclic
logic lives in the FB body rather than a method, calling
`arrSensors[i].Cyclic()` reaches a method that does not exist —
the whole pattern silently breaks.
