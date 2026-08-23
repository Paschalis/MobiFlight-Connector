using System.Collections.Concurrent;
using System.Globalization;

namespace MobiFlight.Core.Project;

/// <summary>
/// Holds the runtime state a project needs beyond the sim itself: the most recent value of every
/// config, and the MobiFlight variables.
/// </summary>
/// <remarks>
/// Preconditions of type "config" and config reference placeholders both read from here, which is
/// why config values have to be remembered rather than just written straight to a device.
/// </remarks>
public sealed class ConfigValueStore
{
    private readonly ConcurrentDictionary<string, double> _configValues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MobiFlightVariable> _variables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records the value a config most recently produced.</summary>
    public void SetConfigValue(string guid, double value) => _configValues[guid] = value;

    /// <summary>The value a config most recently produced, or null when it has not run yet.</summary>
    public double? GetConfigValue(string guid) =>
        _configValues.TryGetValue(guid, out var value) ? value : null;

    /// <summary>Sets a numeric MobiFlight variable.</summary>
    public void SetVariable(string name, double value) =>
        _variables[name] = new MobiFlightVariable(name, value, null);

    /// <summary>Sets a string MobiFlight variable.</summary>
    public void SetVariable(string name, string text) =>
        _variables[name] = new MobiFlightVariable(name, 0, text);

    public MobiFlightVariable? GetVariable(string name) =>
        _variables.TryGetValue(name, out var variable) ? variable : null;

    /// <summary>The numeric value of a variable, or null when it is unset.</summary>
    public double? GetVariableNumber(string name) => GetVariable(name)?.Number;

    public IReadOnlyCollection<MobiFlightVariable> Variables => _variables.Values.ToList();

    public void Clear()
    {
        _configValues.Clear();
        _variables.Clear();
    }

    /// <summary>
    /// Builds the placeholder map for a config's references.
    /// </summary>
    /// <remarks>
    /// Inactive references and references to configs that have not produced a value yet are left
    /// out, so an expression using them fails to evaluate rather than silently reading zero.
    /// </remarks>
    public Dictionary<string, double> ResolvePlaceholders(IReadOnlyList<ConfigReference> references)
    {
        var placeholders = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var reference in references)
        {
            if (!reference.Active) continue;

            var value = GetConfigValue(reference.Ref);
            if (value is null) continue;

            placeholders[reference.Placeholder] = value.Value;
        }

        return placeholders;
    }

    /// <summary>
    /// Evaluates a precondition list the way the Connector does.
    /// </summary>
    /// <remarks>
    /// Two details are inherited deliberately: the logic operator applied is the one carried from
    /// the <em>preceding</em> precondition, and a condition whose subject has no value yet leaves
    /// the previous result in place rather than counting as false.
    /// </remarks>
    public bool ArePreconditionsMet(IReadOnlyList<Precondition> preconditions)
    {
        if (preconditions.Count == 0) return true;

        var finalResult = true;
        var result = true;
        var logicOr = false;

        foreach (var precondition in preconditions)
        {
            if (!precondition.Active) continue;

            switch (precondition.Kind)
            {
                case PreconditionKind.Config:
                {
                    if (string.IsNullOrWhiteSpace(precondition.Ref)) break;

                    var value = GetConfigValue(precondition.Ref);
                    if (value is null) break;

                    result = Compare(value.Value, precondition.Operand, precondition.Value);
                    break;
                }

                case PreconditionKind.Variable:
                {
                    if (string.IsNullOrWhiteSpace(precondition.Ref)) break;

                    var variable = GetVariable(precondition.Ref);
                    if (variable is null) break;

                    result = variable.Text is null
                        ? Compare(variable.Number, precondition.Operand, precondition.Value)
                        : CompareText(variable.Text, precondition.Operand, precondition.Value);
                    break;
                }

                case PreconditionKind.Pin:
                    // Arcaze hardware is Windows only, so this condition can never be satisfied here.
                    result = false;
                    break;

                case PreconditionKind.None:
                default:
                    break;
            }

            if (logicOr) finalResult |= result;
            else finalResult &= result;

            logicOr = string.Equals(precondition.Logic, "or", StringComparison.OrdinalIgnoreCase);
        }

        return finalResult;
    }

    internal static bool Compare(double value, string? operand, string? reference)
    {
        if (!double.TryParse(reference, NumberStyles.Float, CultureInfo.InvariantCulture, out var target))
        {
            return false;
        }

        return operand switch
        {
            "=" or "==" => Math.Abs(value - target) < double.Epsilon,
            "!=" or "<>" => Math.Abs(value - target) >= double.Epsilon,
            ">" => value > target,
            "<" => value < target,
            ">=" => value >= target,
            "<=" => value <= target,
            _ => false,
        };
    }

    internal static bool CompareText(string value, string? operand, string? reference)
    {
        var comparison = string.CompareOrdinal(value, reference ?? string.Empty);

        return operand switch
        {
            "=" or "==" => comparison == 0,
            "!=" or "<>" => comparison != 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            _ => false,
        };
    }
}

/// <summary>
/// A MobiFlight variable. <see cref="Text"/> is null for numeric variables.
/// </summary>
public sealed record MobiFlightVariable(string Name, double Number, string? Text)
{
    public bool IsText => Text is not null;

    public override string ToString() => IsText ? $"{Name} = \"{Text}\"" : $"{Name} = {Number}";
}
