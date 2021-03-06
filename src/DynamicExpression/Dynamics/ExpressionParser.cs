using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace DynamicExpression.Dynamics
{
    internal class ExpressionParser
    {
        private static readonly Type[] predefinedTypes = {
            typeof(object),
            typeof(bool),
            typeof(char),
            typeof(string),
            typeof(sbyte),
            typeof(byte),
            typeof(short),
            typeof(ushort),
            typeof(int),
            typeof(uint),
            typeof(long),
            typeof(ulong),
            typeof(float),
            typeof(double),
            typeof(decimal),
            typeof(DateTime),
            typeof(TimeSpan),
            typeof(Guid),
            typeof(Math),
            typeof(Convert)
        };

        private static readonly Expression trueLiteral = Expression.Constant(true);
        private static readonly Expression falseLiteral = Expression.Constant(false);
        private static readonly Expression nullLiteral = Expression.Constant(null);

        private static readonly string keywordIt = "it";
        private static readonly string keywordIif = "iif";
        private static readonly string keywordNew = "new";

        private static Dictionary<string, object> keywords;

        private Dictionary<string, object> symbols;
        private IDictionary<string, object> externals;
        private Dictionary<Expression, string> literals;
        private ParameterExpression it;
        private string text;
        private int textPos;
        private int textLen;
        private char ch;
        private Token token;

        public ExpressionParser(ParameterExpression[] parameters, string expression, object[] values)
        {
            if (expression == null)
            {
                throw new ArgumentNullException("expression");
            }

            if (keywords == null)
            {
                keywords = CreateKeywords();
            }

            this.symbols = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            this.literals = new Dictionary<Expression, string>();
            if (parameters != null)
            {
                this.ProcessParameters(parameters);
            }

            if (values != null)
            {
                this.ProcessValues(values);
            }

            this.text = expression;
            this.textLen = text.Length;
            this.SetTextPos(0);
            this.NextToken();
        }

        private void ProcessParameters(ParameterExpression[] parameters)
        {
            foreach (var pe in parameters)
            {
                if (!string.IsNullOrEmpty(pe.Name))
                {
                    this.AddSymbol(pe.Name, pe);
                }
            }

            if (parameters.Length == 1 && string.IsNullOrEmpty(parameters[0].Name))
            {
                it = parameters[0];
            }
        }

        private void ProcessValues(object[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                object value = values[i];
                if (i == values.Length - 1 && value is IDictionary<string, object>)
                {
                    this.externals = (IDictionary<string, object>)value;
                }
                else
                {
                    this.AddSymbol("@" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
                }
            }
        }

        private void AddSymbol(string name, object value)
        {
            if (this.symbols.ContainsKey(name))
            {
                throw ParseError(Res.DuplicateIdentifier, name);
            }

            this.symbols.Add(name, value);
        }

        public Expression Parse(Type resultType)
        {
            var expressionPosition = this.token.Position;
            var expression = this.ParseExpression();
            if (resultType != null)
            {
                if ((expression = this.PromoteExpression(expression, resultType, true)) == null)
                {
                    throw ParseError(expressionPosition, Res.ExpressionTypeMismatch, GetTypeName(resultType));
                }
            }

            this.ValidateToken(TokenId.End, Res.SyntaxError);
            return expression;
        }

        public IEnumerable<DynamicOrdering> ParseOrdering()
        {
            var orderings = new List<DynamicOrdering>();
            while (true)
            {
                var expression = this.ParseExpression();
                bool ascending = true;
                if (this.TokenIdentifierIs("asc") || this.TokenIdentifierIs("ascending"))
                {
                    NextToken();
                }
                else if (this.TokenIdentifierIs("desc") || this.TokenIdentifierIs("descending"))
                {
                    this.NextToken();
                    ascending = false;
                }
                orderings.Add(new DynamicOrdering
                {
                    Selector = expression,
                    Ascending = ascending,
                });

                if (this.token.Id != TokenId.Comma)
                {
                    break;
                }

                this.NextToken();
            }

            this.ValidateToken(TokenId.End, Res.SyntaxError);
            return orderings;
        }

        // ?: operator
        private Expression ParseExpression()
        {
            var errorPosition = this.token.Position;
            var expression = this.ParseLogicalOr();
            if (this.token.Id == TokenId.Question)
            {
                this.NextToken();
                var expr1 = this.ParseExpression();
                this.ValidateToken(TokenId.Colon, Res.ColonExpected);
                this.NextToken();
                var expression2 = this.ParseExpression();
                expression = this.GenerateConditional(expression, expr1, expression2, errorPosition);
            }
            return expression;
        }

        // ||, or operator
        private Expression ParseLogicalOr()
        {
            var left = this.ParseLogicalAnd();
            while (this.token.Id == TokenId.DoubleBar || this.TokenIdentifierIs("or"))
            {
                var op = this.token;
                this.NextToken();
                var right = this.ParseLogicalAnd();
                this.CheckAndPromoteOperands(typeof(ILogicalSignatures), op.Text, ref left, ref right, op.Position);
                left = Expression.OrElse(left, right);
            }
            return left;
        }

        // &&, and operator
        Expression ParseLogicalAnd()
        {
            var left = this.ParseComparison();
            while (this.token.Id == TokenId.DoubleAmphersand || this.TokenIdentifierIs("and"))
            {
                var op = this.token;
                this.NextToken();
                var right = this.ParseComparison();
                this.CheckAndPromoteOperands(typeof(ILogicalSignatures), op.Text, ref left, ref right, op.Position);
                left = Expression.AndAlso(left, right);
            }
            return left;
        }

        // =, ==, !=, <>, >, >=, <, <= operators
        Expression ParseComparison()
        {
            var left = this.ParseAdditive();
            while (this.token.Id == TokenId.Equal ||
                this.token.Id == TokenId.DoubleEqual ||
                this.token.Id == TokenId.ExclamationEqual ||
                this.token.Id == TokenId.LessGreater ||
                this.token.Id == TokenId.GreaterThan ||
                this.token.Id == TokenId.GreaterThanEqual ||
                this.token.Id == TokenId.LessThan ||
                this.token.Id == TokenId.LessThanEqual)
            {
                var op = this.token;
                this.NextToken();
                var right = this.ParseAdditive();
                bool isEquality = 
                    op.Id == TokenId.Equal ||
                    op.Id == TokenId.DoubleEqual ||
                    op.Id == TokenId.ExclamationEqual ||
                    op.Id == TokenId.LessGreater;

                if (isEquality && !left.Type.IsValueType && !right.Type.IsValueType)
                {
                    if (left.Type != right.Type)
                    {
                        if (left.Type.IsAssignableFrom(right.Type))
                        {
                            right = Expression.Convert(right, left.Type);
                        }
                        else if (right.Type.IsAssignableFrom(left.Type))
                        {
                            left = Expression.Convert(left, right.Type);
                        }
                        else
                        {
                            throw this.IncompatibleOperandsError(op.Text, left, right, op.Position);
                        }
                    }
                }
                else if (IsEnumType(left.Type) || IsEnumType(right.Type))
                {
                    if (left.Type != right.Type)
                    {
                        Expression e;
                        if ((e = this.PromoteExpression(right, left.Type, true)) != null)
                        {
                            right = e;
                        }
                        else if ((e = this.PromoteExpression(left, right.Type, true)) != null)
                        {
                            left = e;
                        }
                        else
                        {
                            throw this.IncompatibleOperandsError(op.Text, left, right, op.Position);
                        }
                    }
                }
                else
                {
                    this.CheckAndPromoteOperands(
                        isEquality ? typeof(IEqualitySignatures) : typeof(IRelationalSignatures),
                        op.Text,
                        ref left,
                        ref right,
                        op.Position);
                }

                switch (op.Id)
                {
                    case TokenId.Equal:
                    case TokenId.DoubleEqual:
                        left = this.GenerateEqual(left, right);
                        break;
                    case TokenId.ExclamationEqual:
                    case TokenId.LessGreater:
                        left = this.GenerateNotEqual(left, right);
                        break;
                    case TokenId.GreaterThan:
                        left = this.GenerateGreaterThan(left, right);
                        break;
                    case TokenId.GreaterThanEqual:
                        left = this.GenerateGreaterThanEqual(left, right);
                        break;
                    case TokenId.LessThan:
                        left = this.GenerateLessThan(left, right);
                        break;
                    case TokenId.LessThanEqual:
                        left = this.GenerateLessThanEqual(left, right);
                        break;
                }
            }
            return left;
        }

        // +, -, & operators
        private Expression ParseAdditive()
        {
            var left = this.ParseMultiplicative();
            while (
                this.token.Id == TokenId.Plus ||
                this.token.Id == TokenId.Minus ||
                this.token.Id == TokenId.Amphersand)
            {
                Token op = this.token;
                this.NextToken();
                var right = this.ParseMultiplicative();
                switch (op.Id)
                {
                    case TokenId.Plus:
                        if (left.Type == typeof(string) || right.Type == typeof(string))
                        {
                            goto case TokenId.Amphersand;
                        }

                        this.CheckAndPromoteOperands(typeof(IAddSignatures), op.Text, ref left, ref right, op.Position);
                        left = this.GenerateAdd(left, right);
                        break;

                    case TokenId.Minus:
                        this.CheckAndPromoteOperands(typeof(ISubtractSignatures), op.Text, ref left, ref right, op.Position);
                        left = this.GenerateSubtract(left, right);
                        break;

                    case TokenId.Amphersand:
                        left = this.GenerateStringConcat(left, right);
                        break;
                }
            }
            return left;
        }

        // *, /, %, mod operators
        private Expression ParseMultiplicative()
        {
            var left = this.ParseUnary();
            while (
                this.token.Id == TokenId.Asterisk ||
                this.token.Id == TokenId.Slash ||
                this.token.Id == TokenId.Percent ||
                this.TokenIdentifierIs("mod"))
            {
                var op = this.token;
                this.NextToken();
                var right = this.ParseUnary();
                this.CheckAndPromoteOperands(typeof(IArithmeticSignatures), op.Text, ref left, ref right, op.Position);
                switch (op.Id)
                {
                    case TokenId.Asterisk:
                        left = Expression.Multiply(left, right);
                        break;
                    case TokenId.Slash:
                        left = Expression.Divide(left, right);
                        break;
                    case TokenId.Percent:
                    case TokenId.Identifier:
                        left = Expression.Modulo(left, right);
                        break;
                }
            }
            return left;
        }

        // -, !, not unary operators
        private Expression ParseUnary()
        {
            if (this.token.Id == TokenId.Minus ||
                this.token.Id == TokenId.Exclamation ||
                this.TokenIdentifierIs("not"))
            {
                var op = this.token;
                this.NextToken();
                if (op.Id == TokenId.Minus && 
                    (this.token.Id == TokenId.IntegerLiteral || this.token.Id == TokenId.RealLiteral))
                {
                    this.token.Text = "-" + this.token.Text;
                    this.token.Position = op.Position;
                    return this.ParsePrimary();
                }
                var expr = this.ParseUnary();
                if (op.Id == TokenId.Minus)
                {
                    this.CheckAndPromoteOperand(typeof(INegationSignatures), op.Text, ref expr, op.Position);
                    expr = Expression.Negate(expr);
                }
                else
                {
                    this.CheckAndPromoteOperand(typeof(INotSignatures), op.Text, ref expr, op.Position);
                    expr = Expression.Not(expr);
                }
                return expr;
            }

            return this.ParsePrimary();
        }

        private Expression ParsePrimary()
        {
            var expr = this.ParsePrimaryStart();
            while (true)
            {
                if (this.token.Id == TokenId.Dot)
                {
                    this.NextToken();
                    expr = this.ParseMemberAccess(null, expr);
                }
                else if (this.token.Id == TokenId.OpenBracket)
                {
                    expr = this.ParseElementAccess(expr);
                }
                else
                {
                    break;
                }
            }
            return expr;
        }

        private Expression ParsePrimaryStart()
        {
            switch (token.Id)
            {
                case TokenId.Identifier:
                    return this.ParseIdentifier();
                case TokenId.StringLiteral:
                    return this.ParseStringLiteral();
                case TokenId.IntegerLiteral:
                    return this.ParseIntegerLiteral();
                case TokenId.RealLiteral:
                    return this.ParseRealLiteral();
                case TokenId.OpenParen:
                    return this.ParseParenExpression();
                default:
                    throw this.ParseError(Res.ExpressionExpected);
            }
        }

        private Expression ParseStringLiteral()
        {
            this.ValidateToken(TokenId.StringLiteral);
            var quote = this.token.Text[0];
            var s = this.token.Text.Substring(1, this.token.Text.Length - 2);
            int start = 0;
            while (true)
            {
                int i = s.IndexOf(quote, start);
                if (i < 0)
                {
                    break;
                }

                s = s.Remove(i, 1);
                start = i + 1;
            }
            if (quote == '\'')
            {
                if (s.Length != 1)
                {
                    throw this.ParseError(Res.InvalidCharacterLiteral);
                }

                this.NextToken();
                return this.CreateLiteral(s[0], s);
            }
            this.NextToken();
            return this.CreateLiteral(s, s);
        }

        private Expression ParseIntegerLiteral()
        {
            this.ValidateToken(TokenId.IntegerLiteral);
            var text = this.token.Text;
            if (text[0] != '-')
            {
                ulong value;
                if (!ulong.TryParse(text, out value))
                {
                    throw this.ParseError(Res.InvalidIntegerLiteral, text);
                }

                this.NextToken();
                if (value <= (ulong)int.MaxValue)
                {
                    return this.CreateLiteral((int)value, text);
                }

                if (value <= (ulong)uint.MaxValue)
                {
                    return this.CreateLiteral((uint)value, text);
                }

                if (value <= (ulong)long.MaxValue)
                {
                    return this.CreateLiteral((long)value, text);
                }

                return this.CreateLiteral(value, text);
            }
            else
            {
                long value;
                if (!long.TryParse(text, out value))
                {
                    throw this.ParseError(Res.InvalidIntegerLiteral, text);
                }

                this.NextToken();
                if (value >= int.MinValue && value <= int.MaxValue)
                {
                    return this.CreateLiteral((int)value, text);
                }

                return this.CreateLiteral(value, text);
            }
        }

        private Expression ParseRealLiteral()
        {
            this.ValidateToken(TokenId.RealLiteral);
            var text = this.token.Text;
            object value = null;
            char last = text[text.Length - 1];
            if (last == 'F' || last == 'f')
            {
                float f;
                if (float.TryParse(
                    text.Substring(0, text.Length - 1),
                    System.Globalization.NumberStyles.AllowDecimalPoint |
                    System.Globalization.NumberStyles.AllowExponent |
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out f))
                {
                    value = f;
                }
            }
            else
            {
                double d;
                if (double.TryParse(
                    text,
                    System.Globalization.NumberStyles.AllowDecimalPoint |
                    System.Globalization.NumberStyles.AllowExponent |
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out d))
                {
                    value = d;
                }
            }

            if (value == null)
            {
                throw this.ParseError(Res.InvalidRealLiteral, text);
            }

            this.NextToken();
            return this.CreateLiteral(value, text);
        }

        private Expression CreateLiteral(object value, string text)
        {
            var expr = Expression.Constant(value);
            this.literals.Add(expr, text);
            return expr;
        }

        private Expression ParseParenExpression()
        {
            this.ValidateToken(TokenId.OpenParen, Res.OpenParenExpected);
            this.NextToken();
            var e = this.ParseExpression();
            this.ValidateToken(TokenId.CloseParen, Res.CloseParenOrOperatorExpected);
            this.NextToken();
            return e;
        }

        private Expression ParseIdentifier()
        {
            this.ValidateToken(TokenId.Identifier);
            object value;
            if (keywords.TryGetValue(this.token.Text, out value))
            {
                if (value is Type)
                {
                    return this.ParseTypeAccess((Type)value);
                }

                if (value == (object)keywordIt)
                {
                    return this.ParseIt();
                }

                if (value == (object)keywordIif)
                {
                    return this.ParseIif();
                }

                if (value == (object)keywordNew)
                {
                    return this.ParseNew();
                }

                this.NextToken();
                return (Expression)value;
            }

            if (this.symbols.TryGetValue(this.token.Text, out value) ||
                this.externals != null && this.externals.TryGetValue(this.token.Text, out value))
            {
                var expr = value as Expression;
                if (expr == null)
                {
                    expr = Expression.Constant(value);
                }
                else
                {
                    LambdaExpression lambda = expr as LambdaExpression;
                    if (lambda != null)
                    {
                        return this.ParseLambdaInvocation(lambda);
                    }
                }
                this.NextToken();
                return expr;
            }

            if (it != null)
            {
                return this.ParseMemberAccess(null, it);
            }

            throw this.ParseError(Res.UnknownIdentifier, this.token.Text);
        }

        private Expression ParseIt()
        {
            if (this.it == null)
            {
                throw ParseError(Res.NoItInScope);
            }

            this.NextToken();
            return it;
        }

        private Expression ParseIif()
        {
            int errorPos = token.Position;
            this.NextToken();
            var args = this.ParseArgumentList();
            if (args.Length != 3)
            {
                throw this.ParseError(errorPos, Res.IifRequiresThreeArgs);
            }
            return this.GenerateConditional(args[0], args[1], args[2], errorPos);
        }

        private Expression GenerateConditional(Expression test, Expression expr1, Expression expr2, int errorPos)
        {
            if (test.Type != typeof(bool))
            {
                throw this.ParseError(errorPos, Res.FirstExprMustBeBool);
            }

            if (expr1.Type != expr2.Type)
            {
                var expr1as2 = expr2 != nullLiteral ? this.PromoteExpression(expr1, expr2.Type, true) : null;
                var expr2as1 = expr1 != nullLiteral ? this.PromoteExpression(expr2, expr1.Type, true) : null;

                if (expr1as2 != null && expr2as1 == null)
                {
                    expr1 = expr1as2;
                }
                else if (expr2as1 != null && expr1as2 == null)
                {
                    expr2 = expr2as1;
                }
                else
                {
                    string type1 = expr1 != nullLiteral ? expr1.Type.Name : "null";
                    string type2 = expr2 != nullLiteral ? expr2.Type.Name : "null";
                    if (expr1as2 != null && expr2as1 != null)
                    {
                        throw this.ParseError(errorPos, Res.BothTypesConvertToOther, type1, type2);
                    }

                    throw this.ParseError(errorPos, Res.NeitherTypeConvertsToOther, type1, type2);
                }
            }

            return Expression.Condition(test, expr1, expr2);
        }

        private Expression ParseNew()
        {
            this.NextToken();
            this.ValidateToken(TokenId.OpenParen, Res.OpenParenExpected);
            this.NextToken();
            var properties = new List<DynamicProperty>();
            var expressions = new List<Expression>();
            while (true)
            {
                int exprPos = this.token.Position;
                Expression expr = this.ParseExpression();
                string propName;
                if (this.TokenIdentifierIs("as"))
                {
                    this.NextToken();
                    propName = this.GetIdentifier();
                    this.NextToken();
                }
                else
                {
                    MemberExpression me = expr as MemberExpression;
                    if (me == null)
                    {
                        throw this.ParseError(exprPos, Res.MissingAsClause);
                    }

                    propName = me.Member.Name;
                }
                expressions.Add(expr);
                properties.Add(new DynamicProperty(propName, expr.Type));
                if (this.token.Id != TokenId.Comma)
                {
                    break;
                }

                this.NextToken();
            }
            this.ValidateToken(TokenId.CloseParen, Res.CloseParenOrCommaExpected);
            this.NextToken();
            var type = DynamicExpression.CreateClass(properties);
            var bindings = new MemberBinding[properties.Count];
            for (int i = 0; i < bindings.Length; i++)
            {
                bindings[i] = Expression.Bind(type.GetProperty(properties[i].Name), expressions[i]);
            }

            return Expression.MemberInit(Expression.New(type), bindings);
        }

        private Expression ParseLambdaInvocation(LambdaExpression lambda)
        {
            int errorPos = this.token.Position;
            this.NextToken();
            Expression[] args = this.ParseArgumentList();
            MethodBase method;
            if (this.FindMethod(lambda.Type, "Invoke", false, args, out method) != 1)
            {
                throw this.ParseError(errorPos, Res.ArgsIncompatibleWithLambda);
            }

            return Expression.Invoke(lambda, args);
        }

        private Expression ParseTypeAccess(Type type)
        {
            int errorPos = this.token.Position;
            this.NextToken();
            if (this.token.Id == TokenId.Question)
            {
                if (!type.IsValueType || IsNullableType(type))
                {
                    throw this.ParseError(errorPos, Res.TypeHasNoNullableForm, GetTypeName(type));
                }

                type = typeof(Nullable<>).MakeGenericType(type);
                this.NextToken();
            }
            if (this.token.Id == TokenId.OpenParen)
            {
                var args = this.ParseArgumentList();
                MethodBase method;
                switch (this.FindBestMethod(type.GetConstructors(), args, out method))
                {
                    case 0:
                        if (args.Length == 1)
                        {
                            return this.GenerateConversion(args[0], type, errorPos);
                        }

                        throw this.ParseError(errorPos, Res.NoMatchingConstructor, GetTypeName(type));
                    case 1:
                        return Expression.New((ConstructorInfo)method, args);
                    default:
                        throw this.ParseError(errorPos, Res.AmbiguousConstructorInvocation, GetTypeName(type));
                }
            }
            this.ValidateToken(TokenId.Dot, Res.DotOrOpenParenExpected);
            this.NextToken();
            return this.ParseMemberAccess(type, null);
        }

        private Expression GenerateConversion(Expression expr, Type type, int errorPos)
        {
            Type exprType = expr.Type;
            if (exprType == type)
            {
                return expr;
            }

            if (exprType.IsValueType && type.IsValueType)
            {
                if ((IsNullableType(exprType) || IsNullableType(type)) &&
                    GetNonNullableType(exprType) == GetNonNullableType(type))
                {
                    return Expression.Convert(expr, type);
                }

                if ((IsNumericType(exprType) || IsEnumType(exprType)) &&
                    (IsNumericType(type)) || IsEnumType(type))
                {
                    return Expression.ConvertChecked(expr, type);
                }
            }

            if (exprType.IsAssignableFrom(type) || type.IsAssignableFrom(exprType) ||
                exprType.IsInterface || type.IsInterface)
            {
                return Expression.Convert(expr, type);
            }

            throw this.ParseError(
                errorPos,
                Res.CannotConvertValue,
                GetTypeName(exprType),
                GetTypeName(type));
        }

        private Expression ParseMemberAccess(Type type, Expression instance)
        {
            if (instance != null)
            {
                type = instance.Type;
            }

            var errorPos = this.token.Position;
            var id = this.GetIdentifier();
            this.NextToken();
            if (this.token.Id == TokenId.OpenParen)
            {
                if (instance != null && type != typeof(string))
                {
                    var enumerableType = FindGenericType(typeof(IEnumerable<>), type);
                    if (enumerableType != null)
                    {
                        var elementType = enumerableType.GetGenericArguments()[0];
                        return ParseAggregate(instance, elementType, id, errorPos);
                    }
                }

                var args = this.ParseArgumentList();
                MethodBase mb;
                switch (this.FindMethod(type, id, instance == null, args, out mb))
                {
                    case 0:
                        throw this.ParseError(
                            errorPos,
                            Res.NoApplicableMethod,
                            id,
                            GetTypeName(type));
                    case 1:
                        MethodInfo method = (MethodInfo)mb;
                        /*if (!IsPredefinedType(method.DeclaringType))
                            throw ParseError(errorPos, Res.MethodsAreInaccessible, GetTypeName(method.DeclaringType));*/
                        if (method.ReturnType == typeof(void))
                        {
                            throw ParseError(
                                errorPos,
                                Res.MethodIsVoid,
                                id,
                                GetTypeName(method.DeclaringType));
                        }

                        return Expression.Call(instance, (MethodInfo)method, args);
                    default:
                        throw this.ParseError(
                            errorPos,
                            Res.AmbiguousMethodInvocation,
                            id,
                            GetTypeName(type));
                }
            }
            else
            {
                var member = this.FindPropertyOrField(type, id, instance == null);
                if (member == null)
                {
                    throw this.ParseError(
                        errorPos,
                        Res.UnknownPropertyOrField,
                        id,
                        GetTypeName(type));
                }

                return member is PropertyInfo ?
                    Expression.Property(instance, (PropertyInfo)member) :
                    Expression.Field(instance, (FieldInfo)member);
            }
        }

        private static Type FindGenericType(Type generic, Type type)
        {
            while (type != null && type != typeof(object))
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == generic) return type;
                if (generic.IsInterface)
                {
                    foreach (Type intfType in type.GetInterfaces())
                    {
                        Type found = FindGenericType(generic, intfType);
                        if (found != null)
                        {
                            return found;
                        }
                    }
                }
                type = type.BaseType;
            }
            return null;
        }

        private Expression ParseAggregate(Expression instance, Type elementType, string methodName, int errorPos)
        {
            var outerIt = this.it;
            var innerIt = Expression.Parameter(elementType, "");
            it = innerIt;
            var args = this.ParseArgumentList();
            it = outerIt;
            MethodBase signature;
            if (this.FindMethod(typeof(IEnumerableSignatures), methodName, false, args, out signature) != 1)
            {
                throw this.ParseError(errorPos, Res.NoApplicableAggregate, methodName);
            }

            Type[] typeArgs;
            if (signature.Name == "Min" || signature.Name == "Max")
            {
                typeArgs = new Type[] { elementType, args[0].Type };
            }
            else
            {
                typeArgs = new Type[] { elementType };
            }
            if (args.Length == 0)
            {
                args = new Expression[] { instance };
            }
            else
            {
                args = new Expression[] { instance, Expression.Lambda(args[0], innerIt) };
            }

            return Expression.Call(typeof(System.Linq.Enumerable), signature.Name, typeArgs, args);
        }

        private Expression[] ParseArgumentList()
        {
            this.ValidateToken(TokenId.OpenParen, Res.OpenParenExpected);
            this.NextToken();
            var args = this.token.Id != TokenId.CloseParen ? ParseArguments() : new Expression[0];
            this.ValidateToken(TokenId.CloseParen, Res.CloseParenOrCommaExpected);
            this.NextToken();
            return args;
        }

        private Expression[] ParseArguments()
        {
            var argList = new List<Expression>();
            while (true)
            {
                argList.Add(this.ParseExpression());
                if (this.token.Id != TokenId.Comma)
                {
                    break;
                }

                this.NextToken();
            }
            return argList.ToArray();
        }

        private Expression ParseElementAccess(Expression expr)
        {
            int errorPos = this.token.Position;
            this.ValidateToken(TokenId.OpenBracket, Res.OpenParenExpected);
            this.NextToken();
            var args = this.ParseArguments();
            this.ValidateToken(TokenId.CloseBracket, Res.CloseBracketOrCommaExpected);
            this.NextToken();
            if (expr.Type.IsArray)
            {
                if (expr.Type.GetArrayRank() != 1 || args.Length != 1)
                {
                    throw this.ParseError(errorPos, Res.CannotIndexMultiDimArray);
                }
                var index = this.PromoteExpression(args[0], typeof(int), true);
                if (index == null)
                {
                    throw this.ParseError(errorPos, Res.InvalidIndex);
                }

                return Expression.ArrayIndex(expr, index);
            }
            else
            {
                MethodBase mb;
                switch (this.FindIndexer(expr.Type, args, out mb))
                {
                    case 0:
                        throw this.ParseError(
                            errorPos,
                            Res.NoApplicableIndexer,
                            GetTypeName(expr.Type));
                    case 1:
                        return Expression.Call(expr, (MethodInfo)mb, args);
                    default:
                        throw this.ParseError(
                            errorPos,
                            Res.AmbiguousIndexerInvocation,
                            GetTypeName(expr.Type));
                }
            }
        }

        private static bool IsPredefinedType(Type type)
        {
            foreach (Type t in predefinedTypes)
            {
                if (t == type)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsNullableType(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
        }

        private static Type GetNonNullableType(Type type)
        {
            return IsNullableType(type) ? type.GetGenericArguments()[0] : type;
        }

        private static string GetTypeName(Type type)
        {
            Type baseType = GetNonNullableType(type);
            string s = baseType.Name;
            if (type != baseType) s += '?';
            return s;
        }

        private static bool IsNumericType(Type type)
        {
            return GetNumericTypeKind(type) != 0;
        }

        private static bool IsSignedIntegralType(Type type)
        {
            return GetNumericTypeKind(type) == 2;
        }

        private static bool IsUnsignedIntegralType(Type type)
        {
            return GetNumericTypeKind(type) == 3;
        }

        private static int GetNumericTypeKind(Type type)
        {
            type = GetNonNullableType(type);
            if (type.IsEnum)
            {
                return 0;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Char:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return 1;
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                    return 2;
                case TypeCode.Byte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    return 3;
                default:
                    return 0;
            }
        }

        private static bool IsEnumType(Type type)
        {
            return GetNonNullableType(type).IsEnum;
        }

        private void CheckAndPromoteOperand(Type signatures, string opName, ref Expression expr, int errorPos)
        {
            var args = new Expression[] { expr };
            MethodBase method;
            if (this.FindMethod(signatures, "F", false, args, out method) != 1)
            {
                throw this.ParseError(
                    errorPos,
                    Res.IncompatibleOperand,
                    opName,
                    GetTypeName(args[0].Type));
            }

            expr = args[0];
        }

        private void CheckAndPromoteOperands(Type signatures, string opName, ref Expression left, ref Expression right, int errorPos)
        {
            var args = new Expression[] { left, right };
            MethodBase method;
            if (this.FindMethod(signatures, "F", false, args, out method) != 1)
            {
                throw this.IncompatibleOperandsError(opName, left, right, errorPos);
            }

            left = args[0];
            right = args[1];
        }

        private Exception IncompatibleOperandsError(string opName, Expression left, Expression right, int pos)
        {
            return this.ParseError(
                pos,
                Res.IncompatibleOperands,
                opName,
                GetTypeName(left.Type),
                GetTypeName(right.Type));
        }

        private MemberInfo FindPropertyOrField(Type type, string memberName, bool staticAccess)
        {
            var flags = BindingFlags.Public | BindingFlags.DeclaredOnly | (staticAccess ? BindingFlags.Static : BindingFlags.Instance);
            foreach (Type t in SelfAndBaseTypes(type))
            {
                var members = t.FindMembers(
                    MemberTypes.Property | MemberTypes.Field,
                    flags,
                    Type.FilterNameIgnoreCase,
                    memberName);

                if (members.Length != 0)
                {
                    return members[0];
                }
            }
            return null;
        }

        private int FindMethod(Type type, string methodName, bool staticAccess, Expression[] args, out MethodBase method)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.DeclaredOnly |
                (staticAccess ? BindingFlags.Static : BindingFlags.Instance);
            foreach (Type t in SelfAndBaseTypes(type))
            {
                var members = t.FindMembers(
                    MemberTypes.Method,
                    flags,
                    Type.FilterNameIgnoreCase,
                    methodName);

                int count = this.FindBestMethod(members.Cast<MethodBase>(), args, out method);
                if (count != 0)
                {
                    return count;
                }
            }

            method = null;
            return 0;
        }

        private int FindIndexer(Type type, Expression[] args, out MethodBase method)
        {
            foreach (var t in SelfAndBaseTypes(type))
            {
                var members = t.GetDefaultMembers();
                if (members.Length != 0)
                {
                    IEnumerable<MethodBase> methods = members.
                        OfType<PropertyInfo>().
                        Select(p => (MethodBase)p.GetGetMethod()).
                        Where(m => m != null);
                    int count = this.FindBestMethod(methods, args, out method);
                    if (count != 0)
                    {
                        return count;
                    }
                }
            }
            method = null;
            return 0;
        }

        private static IEnumerable<Type> SelfAndBaseTypes(Type type)
        {
            if (type.IsInterface)
            {
                var types = new List<Type>();
                AddInterface(types, type);
                return types;
            }
            return SelfAndBaseClasses(type);
        }

        private static IEnumerable<Type> SelfAndBaseClasses(Type type)
        {
            while (type != null)
            {
                yield return type;
                type = type.BaseType;
            }
        }

        private static void AddInterface(List<Type> types, Type type)
        {
            if (!types.Contains(type))
            {
                types.Add(type);
                foreach (Type t in type.GetInterfaces())
                {
                    AddInterface(types, t);
                }
            }
        }

        int FindBestMethod(IEnumerable<MethodBase> methods, Expression[] args, out MethodBase method)
        {
            var applicable = methods.
                Select(m => new MethodData { MethodBase = m, Parameters = m.GetParameters() }).
                Where(m => IsApplicable(m, args)).
                ToArray();

            if (applicable.Length > 1)
            {
                applicable = applicable.
                    Where(m => applicable.All(n => m == n || IsBetterThan(args, m, n))).
                    ToArray();
            }
            if (applicable.Length == 1)
            {
                var md = applicable[0];
                for (int i = 0; i < args.Length; i++)
                {
                    args[i] = md.Args[i];
                }

                method = md.MethodBase;
            }
            else
            {
                method = null;
            }
            return applicable.Length;
        }

        private bool IsApplicable(MethodData method, Expression[] args)
        {
            if (method.Parameters.Length != args.Length)
            {
                return false;
            }

            var promotedArgs = new Expression[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                var pi = method.Parameters[i];
                if (pi.IsOut)
                {
                    return false;
                }

                var promoted = this.PromoteExpression(args[i], pi.ParameterType, false);
                if (promoted == null)
                {
                    return false;
                }

                promotedArgs[i] = promoted;
            }
            method.Args = promotedArgs;
            return true;
        }

        private Expression PromoteExpression(Expression expr, Type type, bool exact)
        {
            if (expr.Type == type)
            {
                return expr;
            }

            if (expr is ConstantExpression)
            {
                var ce = (ConstantExpression)expr;
                if (ce == nullLiteral)
                {
                    if (!type.IsValueType || IsNullableType(type))
                    {
                        return Expression.Constant(null, type);
                    }
                }
                else
                {
                    string text;
                    if (literals.TryGetValue(ce, out text))
                    {
                        Type target = GetNonNullableType(type);
                        Object value = null;
                        switch (Type.GetTypeCode(ce.Type))
                        {
                            case TypeCode.Int32:
                            case TypeCode.UInt32:
                            case TypeCode.Int64:
                            case TypeCode.UInt64:
                                value = ParseNumber(text, target);
                                break;
                            case TypeCode.Double:
                                if (target == typeof(decimal))
                                {
                                    value = ParseNumber(text, target);
                                }

                                break;
                            case TypeCode.String:
                                value = ParseEnum(text, target);
                                break;
                        }
                        if (value != null)
                            return Expression.Constant(value, type);
                    }
                }
            }
            if (IsCompatibleWith(expr.Type, type))
            {
                if (type.IsValueType || exact)
                {
                    return Expression.Convert(expr, type);
                }

                return expr;
            }
            return null;
        }

        private static object ParseNumber(string text, Type type)
        {
            switch (Type.GetTypeCode(GetNonNullableType(type)))
            {
                case TypeCode.SByte:
                    sbyte sb;
                    if (sbyte.TryParse(text, out sb))
                    {
                        return sb;
                    }
                    break;
                case TypeCode.Byte:
                    byte b;
                    if (byte.TryParse(text, out b))
                    {
                        return b;
                    }
                    break;
                case TypeCode.Int16:
                    short s;
                    if (short.TryParse(text, out s))
                    {
                        return s;
                    }
                    break;
                case TypeCode.UInt16:
                    ushort us;
                    if (ushort.TryParse(text, out us))
                    {
                        return us;
                    }
                    break;
                case TypeCode.Int32:
                    int i;
                    if (int.TryParse(text, out i))
                    {
                        return i;
                    }
                    break;
                case TypeCode.UInt32:
                    uint ui;
                    if (uint.TryParse(text, out ui))
                    {
                        return ui;
                    }
                    break;
                case TypeCode.Int64:
                    long l;
                    if (long.TryParse(text, out l))
                    {
                        return l;
                    }
                    break;
                case TypeCode.UInt64:
                    ulong ul;
                    if (ulong.TryParse(text, out ul))
                    {
                        return ul;
                    }
                    break;
                case TypeCode.Single:
                    float f;
                    if (float.TryParse(text, out f))
                    {
                        return f;
                    }
                    break;
                case TypeCode.Double:
                    double d;
                    if (double.TryParse(text, out d))
                    {
                        return d;
                    }
                    break;
                case TypeCode.Decimal:
                    decimal e;
                    if (decimal.TryParse(text, out e))
                    {
                        return e;
                    }
                    break;
            }
            return null;
        }

        private static object ParseEnum(string name, Type type)
        {
            if (type.IsEnum)
            {
                var memberInfos = type.FindMembers(
                    MemberTypes.Field,
                    BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Static,
                    Type.FilterNameIgnoreCase,
                    name);

                if (memberInfos.Length != 0)
                {
                    return ((FieldInfo)memberInfos[0]).GetValue(null);
                }
            }
            return null;
        }

        private static bool IsCompatibleWith(Type source, Type target)
        {
            if (source == target)
            {
                return true;
            }

            if (!target.IsValueType)
            {
                return target.IsAssignableFrom(source);
            }

            var st = GetNonNullableType(source);
            var tt = GetNonNullableType(target);
            if (st != source && tt == target)
            {
                return false;
            }

            var sc = st.IsEnum ? TypeCode.Object : Type.GetTypeCode(st);
            var tc = tt.IsEnum ? TypeCode.Object : Type.GetTypeCode(tt);
            switch (sc)
            {
                case TypeCode.SByte:
                    switch (tc)
                    {
                        case TypeCode.SByte:
                        case TypeCode.Int16:
                        case TypeCode.Int32:
                        case TypeCode.Int64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.Byte:
                    switch (tc)
                    {
                        case TypeCode.Byte:
                        case TypeCode.Int16:
                        case TypeCode.UInt16:
                        case TypeCode.Int32:
                        case TypeCode.UInt32:
                        case TypeCode.Int64:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.Int16:
                    switch (tc)
                    {
                        case TypeCode.Int16:
                        case TypeCode.Int32:
                        case TypeCode.Int64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.UInt16:
                    switch (tc)
                    {
                        case TypeCode.UInt16:
                        case TypeCode.Int32:
                        case TypeCode.UInt32:
                        case TypeCode.Int64:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.Int32:
                    switch (tc)
                    {
                        case TypeCode.Int32:
                        case TypeCode.Int64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.UInt32:
                    switch (tc)
                    {
                        case TypeCode.UInt32:
                        case TypeCode.Int64:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.Int64:
                    switch (tc)
                    {
                        case TypeCode.Int64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.UInt64:
                    switch (tc)
                    {
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double:
                        case TypeCode.Decimal:
                            return true;
                    }
                    break;
                case TypeCode.Single:
                    switch (tc)
                    {
                        case TypeCode.Single:
                        case TypeCode.Double:
                            return true;
                    }
                    break;
                default:
                    if (st == tt)
                    {
                        return true;
                    }

                    break;
            }
            return false;
        }

        private static bool IsBetterThan(Expression[] args, MethodData m1, MethodData m2)
        {
            bool better = false;
            for (int i = 0; i < args.Length; i++)
            {
                int c = CompareConversions(
                    args[i].Type,
                    m1.Parameters[i].ParameterType,
                    m2.Parameters[i].ParameterType);

                if (c < 0)
                {
                    return false;
                }

                if (c > 0)
                {
                    better = true;
                }
            }
            return better;
        }

        // Return 1 if s -> t1 is a better conversion than s -> t2
        // Return -1 if s -> t2 is a better conversion than s -> t1
        // Return 0 if neither conversion is better
        private static int CompareConversions(Type s, Type t1, Type t2)
        {
            if (t1 == t2)
            {
                return 0;
            }

            if (s == t1)
            {
                return 1;
            }

            if (s == t2)
            {
                return -1;
            }

            var t1t2 = IsCompatibleWith(t1, t2);
            var t2t1 = IsCompatibleWith(t2, t1);
            if (t1t2 && !t2t1)
            {
                return 1;
            }

            if (t2t1 && !t1t2)
            {
                return -1;
            }

            if (IsSignedIntegralType(t1) && IsUnsignedIntegralType(t2))
            {
                return 1;
            }

            if (IsSignedIntegralType(t2) && IsUnsignedIntegralType(t1))
            {
                return -1;
            }

            return 0;
        }

        private Expression GenerateEqual(Expression left, Expression right)
        {
            return Expression.Equal(left, right);
        }

        private Expression GenerateNotEqual(Expression left, Expression right)
        {
            return Expression.NotEqual(left, right);
        }

        private Expression GenerateGreaterThan(Expression left, Expression right)
        {
            if (left.Type == typeof(string))
            {
                return Expression.GreaterThan(
                    GenerateStaticMethodCall("Compare", left, right),
                    Expression.Constant(0)
                );
            }
            return Expression.GreaterThan(left, right);
        }

        private Expression GenerateGreaterThanEqual(Expression left, Expression right)
        {
            if (left.Type == typeof(string))
            {
                return Expression.GreaterThanOrEqual(
                    GenerateStaticMethodCall("Compare", left, right),
                    Expression.Constant(0)
                );
            }
            return Expression.GreaterThanOrEqual(left, right);
        }

        private Expression GenerateLessThan(Expression left, Expression right)
        {
            if (left.Type == typeof(string))
            {
                return Expression.LessThan(
                    GenerateStaticMethodCall("Compare", left, right),
                    Expression.Constant(0)
                );
            }
            return Expression.LessThan(left, right);
        }

        private Expression GenerateLessThanEqual(Expression left, Expression right)
        {
            if (left.Type == typeof(string))
            {
                return Expression.LessThanOrEqual(
                    GenerateStaticMethodCall("Compare", left, right),
                    Expression.Constant(0)
                );
            }
            return Expression.LessThanOrEqual(left, right);
        }

        private Expression GenerateAdd(Expression left, Expression right)
        {
            if (left.Type == typeof(string) && right.Type == typeof(string))
            {
                return GenerateStaticMethodCall("Concat", left, right);
            }
            return Expression.Add(left, right);
        }

        private Expression GenerateSubtract(Expression left, Expression right)
        {
            return Expression.Subtract(left, right);
        }

        private Expression GenerateStringConcat(Expression left, Expression right)
        {
            return Expression.Call(
                null,
                typeof(string).GetMethod("Concat", new[] { typeof(object), typeof(object) }),
                new[] { left, right });
        }

        private MethodInfo GetStaticMethod(string methodName, Expression left, Expression right)
        {
            return left.Type.GetMethod(methodName, new[] { left.Type, right.Type });
        }

        private Expression GenerateStaticMethodCall(string methodName, Expression left, Expression right)
        {
            return Expression.Call(null, GetStaticMethod(methodName, left, right), new[] { left, right });
        }

        private void SetTextPos(int pos)
        {
            this.textPos = pos;
            this.ch = this.textPos < this.textLen ? this.text[this.textPos] : '\0';
        }

        private void NextChar()
        {
            if (this.textPos < this.textLen)
            {
                this.textPos++;
            }

            this.ch = this.textPos < this.textLen ? this.text[this.textPos] : '\0';
        }

        private void NextToken()
        {
            while (Char.IsWhiteSpace(this.ch))
            {
                this.NextChar();
            }

            TokenId t;
            int tokenPos = this.textPos;
            switch (this.ch)
            {
                case '!':
                    this.NextChar();
                    if (this.ch == '=')
                    {
                        this.NextChar();
                        t = TokenId.ExclamationEqual;
                    }
                    else
                    {
                        t = TokenId.Exclamation;
                    }
                    break;
                case '%':
                    this.NextChar();
                    t = TokenId.Percent;
                    break;
                case '&':
                    this.NextChar();
                    if (this.ch == '&')
                    {
                        this.NextChar();
                        t = TokenId.DoubleAmphersand;
                    }
                    else
                    {
                        t = TokenId.Amphersand;
                    }
                    break;
                case '(':
                    this.NextChar();
                    t = TokenId.OpenParen;
                    break;
                case ')':
                    this.NextChar();
                    t = TokenId.CloseParen;
                    break;
                case '*':
                    this.NextChar();
                    t = TokenId.Asterisk;
                    break;
                case '+':
                    this.NextChar();
                    t = TokenId.Plus;
                    break;
                case ',':
                    this.NextChar();
                    t = TokenId.Comma;
                    break;
                case '-':
                    this.NextChar();
                    t = TokenId.Minus;
                    break;
                case '.':
                    this.NextChar();
                    t = TokenId.Dot;
                    break;
                case '/':
                    this.NextChar();
                    t = TokenId.Slash;
                    break;
                case ':':
                    this.NextChar();
                    t = TokenId.Colon;
                    break;
                case '<':
                    this.NextChar();
                    if (this.ch == '=')
                    {
                        this.NextChar();
                        t = TokenId.LessThanEqual;
                    }
                    else if (this.ch == '>')
                    {
                        this.NextChar();
                        t = TokenId.LessGreater;
                    }
                    else
                    {
                        t = TokenId.LessThan;
                    }
                    break;
                case '=':
                    this.NextChar();
                    if (this.ch == '=')
                    {
                        this.NextChar();
                        t = TokenId.DoubleEqual;
                    }
                    else
                    {
                        t = TokenId.Equal;
                    }
                    break;
                case '>':
                    this.NextChar();
                    if (this.ch == '=')
                    {
                        this.NextChar();
                        t = TokenId.GreaterThanEqual;
                    }
                    else
                    {
                        t = TokenId.GreaterThan;
                    }
                    break;
                case '?':
                    this.NextChar();
                    t = TokenId.Question;
                    break;
                case '[':
                    this.NextChar();
                    t = TokenId.OpenBracket;
                    break;
                case ']':
                    this.NextChar();
                    t = TokenId.CloseBracket;
                    break;
                case '|':
                    this.NextChar();
                    if (this.ch == '|')
                    {
                        this.NextChar();
                        t = TokenId.DoubleBar;
                    }
                    else
                    {
                        t = TokenId.Bar;
                    }
                    break;
                case '"':
                case '\'':
                    char quote = this.ch;
                    do
                    {
                        this.NextChar();
                        while (this.textPos < this.textLen && this.ch != quote)
                        {
                            this.NextChar();
                        }

                        if (this.textPos == this.textLen)
                        {
                            throw this.ParseError(this.textPos, Res.UnterminatedStringLiteral);
                        }
                        this.NextChar();
                    } while (this.ch == quote);
                    t = TokenId.StringLiteral;
                    break;
                default:
                    if (char.IsLetter(this.ch) || this.ch == '@' || this.ch == '_')
                    {
                        do
                        {
                            this.NextChar();
                        } while (char.IsLetterOrDigit(this.ch) || this.ch == '_');
                        t = TokenId.Identifier;
                        break;
                    }
                    if (char.IsDigit(this.ch))
                    {
                        t = TokenId.IntegerLiteral;
                        do
                        {
                            this.NextChar();
                        } while (Char.IsDigit(this.ch));
                        if (this.ch == '.')
                        {
                            t = TokenId.RealLiteral;
                            this.NextChar();
                            this.ValidateDigit();
                            do
                            {
                                this.NextChar();
                            } while (char.IsDigit(ch));
                        }
                        if (this.ch == 'E' || this.ch == 'e')
                        {
                            t = TokenId.RealLiteral;
                            this.NextChar();
                            if (this.ch == '+' || this.ch == '-')
                            {
                                this.NextChar();
                            }

                            this.ValidateDigit();
                            do
                            {
                                this.NextChar();
                            } while (Char.IsDigit(this.ch));
                        }
                        if (this.ch == 'F' || this.ch == 'f')
                        {
                            this.NextChar();
                        }

                        break;
                    }
                    if (this.textPos == this.textLen)
                    {
                        t = TokenId.End;
                        break;
                    }
                    throw ParseError(this.textPos, Res.InvalidCharacter, this.ch);
            }
            this.token.Id = t;
            this.token.Text = text.Substring(tokenPos, this.textPos - tokenPos);
            this.token.Position = tokenPos;
        }

        private bool TokenIdentifierIs(string id)
        {
            return this.token.Id == TokenId.Identifier && String.Equals(id, this.token.Text, StringComparison.OrdinalIgnoreCase);
        }

        private string GetIdentifier()
        {
            this.ValidateToken(TokenId.Identifier, Res.IdentifierExpected);
            string id = this.token.Text;
            if (id.Length > 1 && id[0] == '@')
            {
                id = id.Substring(1);
            }

            return id;
        }

        private void ValidateDigit()
        {
            if (!Char.IsDigit(this.ch))
            {
                throw this.ParseError(this.textPos, Res.DigitExpected);
            }
        }

        private void ValidateToken(TokenId t, string errorMessage)
        {
            if (this.token.Id != t)
            {
                throw this.ParseError(errorMessage);
            }
        }

        private void ValidateToken(TokenId t)
        {
            if (this.token.Id != t)
            {
                throw this.ParseError(Res.SyntaxError);
            }
        }

        private Exception ParseError(string format, params object[] args)
        {
            return this.ParseError(this.token.Position, format, args);
        }

        private Exception ParseError(int pos, string format, params object[] args)
        {
            return new ParseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args), pos);
        }

        private static Dictionary<string, object> CreateKeywords()
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            d.Add("true", trueLiteral);
            d.Add("false", falseLiteral);
            d.Add("null", nullLiteral);
            d.Add(keywordIt, keywordIt);
            d.Add(keywordIif, keywordIif);
            d.Add(keywordNew, keywordNew);
            foreach (Type type in predefinedTypes)
            {
                d.Add(type.Name, type);
            }
            return d;
        }
    }
}
