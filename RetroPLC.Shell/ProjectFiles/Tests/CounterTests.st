FUNCTION_BLOCK CounterTests
VAR
    CounterUnderTest : Counter;
    TestPassed : BOOL;
END_VAR

CounterUnderTest(Enable := TRUE, Reset := FALSE);
TestPassed := CounterUnderTest.Value = 1;
END_FUNCTION_BLOCK
