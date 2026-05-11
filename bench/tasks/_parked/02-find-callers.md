# Task C — Find call sites of FB_TestSuite.AssertEquals_INT

In the TwinCAT project at `${TCUNIT_PATH}`, find every place where the
`AssertEquals_INT` method on `FB_TestSuite` is called. The whole
project is in scope — both `TcUnit/` (the main project) and
`TcUnit-Verifier/` (the verifier project).

List each call site as `file:line` (or the closest equivalent you can
produce) with the calling POU and method when knowable.

Be exhaustive — don't stop early. Disambiguate from other
`AssertEquals_*` variants; we want specifically `AssertEquals_INT`,
not `AssertEquals_DINT` or `AssertEquals_UINT`.
