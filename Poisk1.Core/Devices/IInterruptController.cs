namespace Poisk1.Core.Devices;

/// <summary>Source of hardware interrupts for the CPU.</summary>
public interface IInterruptController
{
    /// <summary>
    /// If there is an interrupt ready to be serviced, moves it to "in-service"
    /// and returns the vector number; otherwise -1.
    /// </summary>
    int Poll();
}
