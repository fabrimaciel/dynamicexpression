using System.Collections.Generic;

namespace DynamicExpression.Test
{
    public class DictionaryAccessor<TKey, TValue>
    {
        private readonly IDictionary<TKey, TValue> inner;

        public DictionaryAccessor(IDictionary<TKey, TValue> inner)
        {
            this.inner = inner;
        }

        public TValue this[TKey key] => this.inner[key];
    }
}
