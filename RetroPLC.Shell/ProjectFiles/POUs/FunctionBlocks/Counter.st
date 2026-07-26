FUNCTION_BLOCK Counter
VAR_INPUT
    Enable : BOOL;
    Reset : BOOL;
END_VAR
VAR_OUTPUT
    Value : DINT;
END_VAR

IF Reset THEN
    Value := 0;
ELSIF Enable THEN
    Value := Value + 1;
END_IF;
END_FUNCTION_BLOCK
