PROGRAM Main
VAR
    motor : MotorController;
    startButton AT %IX0.0 : BOOL;
    stopButton AT %IX0.1 : BOOL;
    motorEnable AT %QX0.0 : BOOL;
END_VAR

motor(
    StartRequest := startButton,
    StopRequest := stopButton
);

motorEnable := motor.IsRunning;
END_PROGRAM
