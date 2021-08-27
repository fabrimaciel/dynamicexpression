using System;

namespace DynamicExpression.Dynamics
{
    internal class DynamicProperty
    {
        public DynamicProperty(string name, Type type)
        {
            this.Name = name ?? throw new ArgumentNullException(nameof(name));
            this.Type = type ?? throw new ArgumentNullException(nameof(type));
        }

        public string Name { get; }

        public Type Type { get; }
    }
}
