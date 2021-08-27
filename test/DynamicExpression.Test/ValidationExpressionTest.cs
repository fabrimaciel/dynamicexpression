using System;
using System.Collections.Generic;
using Xunit;

namespace DynamicExpression.Test
{
    public class ValidationExpressionTest
    {
        private IDictionary<string, double> CreateVariables() =>
            new Dictionary<string, double>(StringComparer.InvariantCultureIgnoreCase)
            {
                { "value_2.3", 2.3 },
                { "value_10", 10 }
            };

        [Theory]
        [InlineData("Variables[\"value_10\"] == 1", false)]
        [InlineData("(Variables[\"value_10\"] + Variables[\"value_2.3\"]) > 10", true)]
        public void GivenExpressionWhenHasVariablesRepositoryThenCheckExpectedResult(string expression, bool expected)
        {
            var variables = this.CreateVariables();

            var dynamicExpression = DynamicExpression.ParseLambda<ExpressionAccessor<double>, bool>(expression);
            var operation = dynamicExpression.Compile();

            var expressionAccessor = new ExpressionAccessor<double>(variables);
            var result = (bool)operation.Invoke(expressionAccessor);

            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Variables[\"xpto\"] = 0 && Variables[\"value_10\"] > 1")]
        public void GivenExpressionWithInvalidVariableNamesWhenHasVariablesRepositoryThenCheckHasErrors(string expression)
        {
            var variables = this.CreateVariables();
            var dynamicExpression = DynamicExpression.ParseLambda<ExpressionAccessor<double>, bool>(expression);
            var errors = new List<string>();
            var visitor = new ExpressionAccessorVisitor<double>(dynamicExpression, variables.Keys, errors);
            visitor.Visit();

            Assert.NotEmpty(errors);
        }
    }
}
