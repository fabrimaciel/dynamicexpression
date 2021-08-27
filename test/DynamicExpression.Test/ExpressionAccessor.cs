using System.Collections.Generic;

namespace DynamicExpression.Test
{
    public class ExpressionAccessor<TValue>
    {
        public ExpressionAccessor(IDictionary<string, TValue> variables)
        {
            this.Variables = new VariableAccessor<TValue>(variables);
        }

        public VariableAccessor<TValue> Variables { get; }
    }
}
