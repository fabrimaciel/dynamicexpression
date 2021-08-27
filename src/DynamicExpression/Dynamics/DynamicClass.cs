using System.Reflection;
using System.Text;

namespace DynamicExpression.Dynamics
{
    internal abstract class DynamicClass
    {
        public override string ToString()
        {
            var props = this.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            var builder = new StringBuilder();
            builder.Append("{");
            for (int i = 0; i < props.Length; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(props[i].Name);
                builder.Append("=");
                builder.Append(props[i].GetValue(this, null));
            }
            builder.Append("}");
            return builder.ToString();
        }
    }
}
