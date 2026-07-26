FUNCTION_BLOCK MotorController IMPLEMENTS IRunnable
VAR_INPUT
    StartRequest : BOOL;
    StopRequest : BOOL;
END_VAR
VAR_OUTPUT
    IsRunning : BOOL;
END_VAR

METHOD PUBLIC Run
    IsRunning := TRUE;
END_METHOD

IF StopRequest THEN
    IsRunning := FALSE;
ELSIF StartRequest THEN
    IsRunning := TRUE;
END_IF;
END_FUNCTION_BLOCK
