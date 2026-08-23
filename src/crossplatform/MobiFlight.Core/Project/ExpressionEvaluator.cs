using System.Globalization;

namespace MobiFlight.Core.Project;

/// <summary>
/// Evaluates the small expression language used in MobiFlight configs.
/// </summary>
/// <remarks>
/// <para>
/// The Windows Connector delegates this to NCalc, which is a full expression engine. This is a
/// focused reimplementation covering what actually appears in config files: arithmetic on the
/// current value, comparisons, and <c>if(condition, then, else)</c>.
/// </para>
/// <para>
/// Placeholders are substituted before parsing: <c>$</c> is the current value and <c>@</c> is the
/// value coming from the input device.
/// </para>
/// </remarks>
public static class ExpressionEvaluator
{
    /// <summary>
    /// Evaluates an expression, substituting <paramref name="current"/> for <c>$</c>,
    /// <paramref name="trigger"/> for <c>@</c>, and any config reference placeholders.
    /// </summary>
    /// <returns>The result, or null when the expression could not be evaluated.</returns>
    public static double? Evaluate(
        string? expression,
        double current = 0,
        double? trigger = null,
        IReadOnlyDictionary<string, double>? placeholders = null)
    {
        if (string.IsNullOrWhiteSpace(expression)) return current;

        var substituted = Substitute(expression, current, trigger, placeholders);

        try
        {
            var parser = new Parser(substituted);
            var value = parser.ParseExpression();

            return parser.AtEnd ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the placeholders with literal numbers, wrapping negatives in parentheses so
    /// "$-5" with a current value of -3 becomes "(-3)-5" rather than "-3-5".
    /// </summary>
    internal static string Substitute(
        string expression,
        double current,
        double? trigger,
        IReadOnlyDictionary<string, double>? placeholders = null)
    {
        var result = expression;

        // Config reference placeholders go first: they are named, so substituting them after the
        // numeric placeholders risks matching digits that were just inserted.
        if (placeholders is not null)
        {
            // Longest first, so a placeholder "AB" is not clobbered by "A".
            foreach (var (name, value) in placeholders.OrderByDescending(p => p.Key.Length))
            {
                if (string.IsNullOrEmpty(name)) continue;

                result = ReplaceStandalone(result, name, Literal(value));
            }
        }

        if (result.Contains('@'))
        {
            result = result.Replace("@", Literal(trigger ?? 0));
        }

        if (result.Contains('$'))
        {
            result = result.Replace("$", Literal(current));
        }

        return result;
    }

    /// <summary>
    /// Replaces a placeholder only where it is not part of a longer word.
    /// </summary>
    /// <remarks>
    /// A blind Replace would corrupt function names, turning "if(" into something unparseable when
    /// a placeholder happens to be "i" or "f".
    /// </remarks>
    internal static string ReplaceStandalone(string input, string name, string replacement)
    {
        var builder = new System.Text.StringBuilder(input.Length);
        var position = 0;

        while (position < input.Length)
        {
            var index = input.IndexOf(name, position, StringComparison.Ordinal);
            if (index < 0)
            {
                builder.Append(input, position, input.Length - position);
                break;
            }

            var before = index == 0 ? ' ' : input[index - 1];
            var afterIndex = index + name.Length;
            var after = afterIndex >= input.Length ? ' ' : input[afterIndex];

            var isStandalone = !char.IsLetterOrDigit(before) && before != '_'
                            && !char.IsLetterOrDigit(after) && after != '_';

            builder.Append(input, position, index - position);
            builder.Append(isStandalone ? replacement : name);

            position = afterIndex;
        }

        return builder.ToString();
    }

    private static string Literal(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return value < 0 ? $"({text})" : text;
    }

    /// <summary>
    /// Recursive descent parser. Precedence, loosest first: comparison, additive, multiplicative,
    /// unary, primary.
    /// </summary>
    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _position;

        public bool AtEnd
        {
            get
            {
                SkipWhitespace();
                return _position >= _text.Length;
            }
        }

        public double ParseExpression() => ParseComparison();

        private double ParseComparison()
        {
            var left = ParseAdditive();

            SkipWhitespace();

            foreach (var op in new[] { "<=", ">=", "==", "!=", "<>", "<", ">", "=" })
            {
                if (!Matches(op)) continue;

                _position += op.Length;
                var right = ParseAdditive();

                var result = op switch
                {
                    "<" => left < right,
                    ">" => left > right,
                    "<=" => left <= right,
                    ">=" => left >= right,
                    "=" or "==" => Math.Abs(left - right) < double.Epsilon,
                    "!=" or "<>" => Math.Abs(left - right) >= double.Epsilon,
                    _ => throw new FormatException($"Unknown operator {op}")
                };

                // Booleans are numbers here, matching how the configs use them.
                return result ? 1 : 0;
            }

            return left;
        }

        private double ParseAdditive()
        {
            var value = ParseMultiplicative();

            while (true)
            {
                SkipWhitespace();
                if (_position >= _text.Length) return value;

                var op = _text[_position];
                if (op is not ('+' or '-')) return value;

                _position++;
                var right = ParseMultiplicative();
                value = op == '+' ? value + right : value - right;
            }
        }

        private double ParseMultiplicative()
        {
            var value = ParseUnary();

            while (true)
            {
                SkipWhitespace();
                if (_position >= _text.Length) return value;

                var op = _text[_position];
                if (op is not ('*' or '/' or '%')) return value;

                _position++;
                var right = ParseUnary();

                value = op switch
                {
                    '*' => value * right,
                    '/' => right == 0 ? throw new FormatException("Division by zero") : value / right,
                    _ => right == 0 ? throw new FormatException("Modulo by zero") : value % right,
                };
            }
        }

        private double ParseUnary()
        {
            SkipWhitespace();

            if (_position < _text.Length && _text[_position] == '-')
            {
                _position++;
                return -ParseUnary();
            }

            if (_position < _text.Length && _text[_position] == '+')
            {
                _position++;
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();

            if (_position >= _text.Length) throw new FormatException("Unexpected end of expression");

            if (_text[_position] == '(')
            {
                _position++;
                var value = ParseExpression();
                Expect(')');
                return value;
            }

            if (MatchesIdentifier("if"))
            {
                return ParseIf();
            }

            return ParseNumber();
        }

        private double ParseIf()
        {
            _position += 2;
            Expect('(');

            var condition = ParseExpression();
            Expect(',');
            var whenTrue = ParseExpression();
            Expect(',');
            var whenFalse = ParseExpression();

            Expect(')');

            return condition != 0 ? whenTrue : whenFalse;
        }

        private double ParseNumber()
        {
            SkipWhitespace();

            var start = _position;

            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
            {
                _position++;
            }

            if (start == _position) throw new FormatException($"Expected a number at position {start}");

            var slice = _text[start.._position];

            return double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"'{slice}' is not a number");
        }

        private bool Matches(string op)
        {
            SkipWhitespace();
            return _position + op.Length <= _text.Length
                && string.CompareOrdinal(_text, _position, op, 0, op.Length) == 0;
        }

        private bool MatchesIdentifier(string name)
        {
            SkipWhitespace();

            if (_position + name.Length > _text.Length) return false;
            if (string.Compare(_text, _position, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;

            var after = _position + name.Length;
            return after < _text.Length && _text[after] == '(';
        }

        private void Expect(char character)
        {
            SkipWhitespace();

            if (_position >= _text.Length || _text[_position] != character)
            {
                throw new FormatException($"Expected '{character}' at position {_position}");
            }

            _position++;
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        }
    }
}
