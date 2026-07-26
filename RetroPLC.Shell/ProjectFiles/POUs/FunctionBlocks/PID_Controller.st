FUNCTION_BLOCK PID_Controller
VAR_INPUT
    Setpoint : REAL;
    ProcessValue : REAL;
    Kp : REAL := 1.0;
END_VAR
VAR_OUTPUT
    Output : REAL;
END_VAR
VAR
    error : REAL;
END_VAR

error := Setpoint - ProcessValue;
Output := Kp * error;
END_FUNCTION_BLOCK
