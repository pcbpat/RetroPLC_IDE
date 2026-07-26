PROGRAM Blink
VAR
    timer : TON;
    runLamp AT %QX0.1 : BOOL;
END_VAR

timer(IN := NOT timer.Q, PT := T#500ms);

IF timer.Q THEN
    runLamp := NOT runLamp;
END_IF;
END_PROGRAM
