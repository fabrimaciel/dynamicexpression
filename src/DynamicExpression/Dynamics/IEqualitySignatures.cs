namespace DynamicExpression.Dynamics
{
    public interface IEqualitySignatures : IRelationalSignatures
    {
        void F(bool x, bool y);
        void F(bool? x, bool? y);
    }
}
