TYPE MachineConfig :
STRUCT
    MaximumSpeed : REAL := 1500.0;
    AccelerationTime : TIME := T#3s;
    DecelerationTime : TIME := T#2s;
    EnableDiagnostics : BOOL := TRUE;
END_STRUCT
END_TYPE
