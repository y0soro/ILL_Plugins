using Iced.Intel;

namespace ILL_SliderUnlocker.NativeCode;

// wrap struct Instruction to reduce reallocation cost and made possible to carry extra context
public class Instr(Instruction instr)
{
    public readonly Instruction Inner = instr;

    public ulong IP => Inner.IP;
    public ulong NextIP => Inner.NextIP;

    public FlowControl FlowControl => Inner.FlowControl;
    public Mnemonic Mnemonic => Inner.Mnemonic;
    public OpKind Op0Kind => Inner.Op0Kind;
    public OpKind Op1Kind => Inner.Op1Kind;
    public Register Op0Register => Inner.Op0Register;
    public Register Op1Register => Inner.Op1Register;

    public ulong NearBranchTarget => Inner.NearBranchTarget;
}
