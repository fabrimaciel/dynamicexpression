using System.Linq.Expressions;

namespace DynamicExpression.Dynamics
{
    internal class DynamicOrdering
    {
        public Expression Selector { get; set; }
        public bool Ascending { get; set; }
    }
}
