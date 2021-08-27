using System;

namespace DynamicExpression.Dynamics
{
    public sealed class ParseException : Exception
    {
        public ParseException(string message, int position)
            : base(message)
        {
            this.Position = position;
        }

        public int Position { get; }

        public override string ToString()
        {
            return string.Format(Res.ParseExceptionFormat, this.Message, this.Position);
        }
    }
}
