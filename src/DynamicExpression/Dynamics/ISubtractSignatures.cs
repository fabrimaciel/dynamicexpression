using System;

namespace DynamicExpression.Dynamics
{
    public interface ISubtractSignatures : IAddSignatures
    {
        void F(DateTime x, DateTime y);
        void F(DateTime? x, DateTime? y);
    }
}
