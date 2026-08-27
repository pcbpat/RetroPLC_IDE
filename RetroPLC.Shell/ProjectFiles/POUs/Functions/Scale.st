FUNCTION Scale : REAL
VAR_INPUT
    RawValue : INT;
    RawMinimum : INT;
    RawMaximum : INT;
    EngineeringMinimum : REAL;
    EngineeringMaximum : REAL;
END_VAR

Scale := EngineeringMinimum
    + INT_TO_REAL(RawValue - RawMinimum)
    * (EngineeringMaximum - EngineeringMinimum)
    / INT_TO_REAL(RawMaximum - RawMinimum);
END_FUNCTION
