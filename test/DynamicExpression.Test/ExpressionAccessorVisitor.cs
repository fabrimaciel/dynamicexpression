using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicExpression.Test
{
    public class ExpressionAccessorVisitor<TValue> : ExpressionVisitor
    {
        private readonly System.Linq.Expressions.Expression expression;
        private readonly IEnumerable<string> variableNames;
        private readonly IList<string> errors;

        public ExpressionAccessorVisitor(
            System.Linq.Expressions.Expression expression,
            IEnumerable<string> variableNames,
            IList<string> errors)
        {
            this.expression = expression;
            this.variableNames = variableNames;
            this.errors = errors;
        }

        public void Visit() =>
            this.Visit(this.expression);

        protected override System.Linq.Expressions.Expression VisitMethodCall(System.Linq.Expressions.MethodCallExpression m)
        {
            var result = base.VisitMethodCall(m);

            if (m.Object.Type == typeof(VariableAccessor<TValue>) &&
                m.Method.Name == "get_Item")
            {
                var constantExpression = m.Arguments[0] as System.Linq.Expressions.ConstantExpression;
                if (constantExpression != null)
                {
                    var name = constantExpression.Value.ToString();
                    if (!this.variableNames.Contains(name, StringComparer.InvariantCultureIgnoreCase))
                    {
                        var errorMessage = $"Variables {name} not found";

                        if (!this.errors.Contains(errorMessage))
                        {
                            this.errors.Add(errorMessage);
                        }
                    }
                }
            }
                
            return result;
        }
    }
}
