# Task W1 — Patch one line of an existing method

In the TwinCAT project at `${TCUNIT_PATH}`, modify the
`FB_TestSuite.AssertEquals_INT` method.

The method has a leading line comment that reads exactly:

    // Asserts that two INTs are equal. If they are not, an assertion error is created.

Change that single line to read exactly:

    // Asserts that two INT values are equal; raises an assertion error if they differ.

Do not modify anything else. Do not rewrite the method body. Do not
touch any other method, POU, or file. Make the smallest possible
change that performs this one-line edit and persists it through to
the project files on disk. Briefly state which tool you used to
make the change.
