namespace Furikiri.Emit
{
    static class CodeObjectExtensions
    {
        public static Method ResolveMethod(this CodeObject obj)
        {
            return new Method(obj);
        }

        public static Property ResolveProperty(this CodeObject obj, Method getter, Method setter)
        {
            return new Property(obj){Getter = getter, Setter = setter};
        }
    }
}
