namespace Furikiri.Emit
{
    public interface IRegisterData
    {
        Instruction Instruction { get; }
        string Comment { get; }
    }

    class JumpData : IRegisterData
    {
        public OpCode Type => Instruction.OpCode;
        public Instruction Instruction { get; set; }
        public Instruction Goto { get; set; }
        public string Comment => $"goto {Goto}";

        public JumpData(Instruction from, Instruction to)
        {
            Instruction = from;
            Goto = to;
        }
    }

    class OperandData : IRegisterData
    {
        public IRegister Register { get; set; }
        public ITjsVariant Variant { get; set; }
        public Instruction Instruction { get; }
        public string Comment => $"{Register} = {Variant?.DebugString.Flatten() ?? ("(void)")}";

        public OperandData(Instruction ins, IRegister register, ITjsVariant v)
        {
            Instruction = ins;
            Register = register;
            Variant = v;
        }
    }
}
