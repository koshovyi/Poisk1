namespace Poisk1.Core.Cpu;

/// <summary>
/// The CPU-core contract. The implementation is a self-contained 8088 interpreter
/// on top of IMemoryBus and IoBus.
/// </summary>
public interface ICpu
{
    CpuState State { get; }
    bool Halted { get; }

    /// <summary>Reset the CPU to its reset state (CS=F000, IP=FFF0).</summary>
    void Reset();

    /// <summary>Execute one instruction. Returns the (approximate) number of cycles spent.</summary>
    int Step();
}
