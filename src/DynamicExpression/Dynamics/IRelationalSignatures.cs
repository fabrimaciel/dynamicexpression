using System;

namespace DynamicExpression.Dynamics
{
    public interface IRelationalSignatures : IArithmeticSignatures
    {
        void F(string x, string y);
        void F(char x, char y);
        void F(DateTime x, DateTime y);
        void F(TimeSpan x, TimeSpan y);
        void F(char? x, char? y);
        void F(DateTime? x, DateTime? y);
        void F(TimeSpan? x, TimeSpan? y);
    }
}
