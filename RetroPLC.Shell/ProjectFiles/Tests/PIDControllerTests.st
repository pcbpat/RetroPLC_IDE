FUNCTION_BLOCK PIDControllerTests
VAR
    ControllerUnderTest : PID_Controller;
    TestPassed : BOOL;
END_VAR

ControllerUnderTest(Setpoint := 50.0, ProcessValue := 45.0);
TestPassed := ControllerUnderTest.Output > 0.0;
END_FUNCTION_BLOCK
