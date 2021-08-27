using System.Linq.Expressions;
using System.Reflection;

namespace DynamicExpression.Dynamics
{
    internal class MethodData
    {
        public MethodBase MethodBase;
        public ParameterInfo[] Parameters;
        public Expression[] Args;
    }
}
