namespace Furikiri.Emit
{
    public class Property
    {
        public CodeObject Parent
        {
            get => Object.Parent;
            set => Object.Parent = value;
        }

        public CodeObject Object { get; set; }

        public string Name
        {
            get => Object.Name;
            set => Object.Name = value;
        }

        public Method Getter { get; set; }
        public Method Setter { get; set; }

        public Property(CodeObject obj)
        {
            Object = obj;
        }
    }
}