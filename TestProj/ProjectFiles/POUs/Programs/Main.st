PROGRAM Main
    VAR
        // mapping of opta digital outputs to IEC address
        ledState_s : eLED_STATE;
        relay1 AT %QX0.0 : BOOL;
        relay2 AT %QX0.1 : BOOL;
        relay3 AT %QX0.2 : BOOL;
        relay4 AT %QX0.3 : BOOL;
        led1 AT %QX0.4 : BOOL;
        led2 AT %QX0.5 : BOOL;
        led3 AT %QX0.6 : BOOL;
        led4 AT %QX0.7 : BOOL;
    END_VAR

    CASE ledState_s OF
        eLED_STATE.LED_ON :
	        led1 := TRUE;
	        ledState_s := eLED_STATE.LED_OFF;

        eLED_STATE.LED_OFF :
	        led1 := FALSE;  
	        ledState_s := eLED_STATE.LED_ON;

    END_CASE;

    led2 := NOT led1;

END_PROGRAM