namespace ChokaQ.Abstractions.Enums;

public enum CircuitFailureSeverity
{
    /// <summary>
    /// A temporary issue (e.g. timeout, network glitch). Contributes to failure count.
    /// </summary>
    Transient,

    /// <summary>
    /// A permanent/severe issue (e.g. database dropped, unauthorized). Immediately opens the circuit.
    /// </summary>
    Fatal
}
