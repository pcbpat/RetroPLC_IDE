FUNCTION_BLOCK MotorControlTests
VAR
    ControllerUnderTest : MotorController;
    TestPassed : BOOL;
END_VAR

ControllerUnderTest(StartRequest := TRUE, StopRequest := FALSE);
TestPassed := ControllerUnderTest.IsRunning;
END_FUNCTION_BLOCK
