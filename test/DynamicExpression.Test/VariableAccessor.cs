using System.Collections.Generic;

namespace DynamicExpression.Test
{
    public class VariableAccessor<TValue> : DictionaryAccessor<string, TValue>
    {
        public VariableAccessor(IDictionary<string, TValue> inner) : base(inner)
        {
        }
    }
}
