using MobiFlight.Core.Project;

namespace MobiFlight.Core.Tests.Project;

[TestClass]
public sealed class ConfigValueStoreTests
{
    [TestMethod]
    public void RemembersConfigValues()
    {
        var store = new ConfigValueStore();

        Assert.IsNull(store.GetConfigValue("a"), "nothing has run yet");

        store.SetConfigValue("a", 42);
        Assert.AreEqual(42, store.GetConfigValue("a"));
    }

    [TestMethod]
    public void StoresNumericAndTextVariables()
    {
        var store = new ConfigValueStore();

        store.SetVariable("Altitude", 3500);
        store.SetVariable("Callsign", "DLH400");

        Assert.AreEqual(3500, store.GetVariableNumber("Altitude"));
        Assert.IsFalse(store.GetVariable("Altitude")!.IsText);

        Assert.AreEqual("DLH400", store.GetVariable("Callsign")!.Text);
        Assert.IsTrue(store.GetVariable("Callsign")!.IsText);
    }

    [TestMethod]
    public void VariableNamesAreCaseInsensitive()
    {
        var store = new ConfigValueStore();
        store.SetVariable("MyVar", 1);

        Assert.AreEqual(1, store.GetVariableNumber("myvar"));
    }

    [TestMethod]
    public void NoPreconditionsMeansAlwaysRun()
    {
        Assert.IsTrue(new ConfigValueStore().ArePreconditionsMet([]));
    }

    [TestMethod]
    public void InactivePreconditionsAreIgnored()
    {
        var store = new ConfigValueStore();
        store.SetConfigValue("ref", 0);

        var preconditions = new[] { Config("ref", "=", "1", active: false) };

        Assert.IsTrue(store.ArePreconditionsMet(preconditions));
    }

    [TestMethod]
    public void EvaluatesConfigPrecondition()
    {
        var store = new ConfigValueStore();
        store.SetConfigValue("ref", 1);

        Assert.IsTrue(store.ArePreconditionsMet([Config("ref", "=", "1")]));
        Assert.IsFalse(store.ArePreconditionsMet([Config("ref", "=", "0")]));
        Assert.IsTrue(store.ArePreconditionsMet([Config("ref", ">", "0")]));
        Assert.IsTrue(store.ArePreconditionsMet([Config("ref", "!=", "5")]));
    }

    [TestMethod]
    public void EvaluatesVariablePrecondition()
    {
        var store = new ConfigValueStore();
        store.SetVariable("Mode", 2);

        Assert.IsTrue(store.ArePreconditionsMet([Variable("Mode", "=", "2")]));
        Assert.IsFalse(store.ArePreconditionsMet([Variable("Mode", "=", "3")]));
    }

    [TestMethod]
    public void EvaluatesTextVariablePrecondition()
    {
        var store = new ConfigValueStore();
        store.SetVariable("Page", "MENU");

        Assert.IsTrue(store.ArePreconditionsMet([Variable("Page", "=", "MENU")]));
        Assert.IsFalse(store.ArePreconditionsMet([Variable("Page", "=", "MAP")]));
        Assert.IsTrue(store.ArePreconditionsMet([Variable("Page", "!=", "MAP")]));
    }

    [TestMethod]
    public void CombinesWithAnd()
    {
        var store = new ConfigValueStore();
        store.SetConfigValue("a", 1);
        store.SetConfigValue("b", 1);

        // The operator applied comes from the preceding entry, matching the Connector.
        Assert.IsTrue(store.ArePreconditionsMet([Config("a", "=", "1", logic: "and"), Config("b", "=", "1")]));
        Assert.IsFalse(store.ArePreconditionsMet([Config("a", "=", "1", logic: "and"), Config("b", "=", "0")]));
    }

    [TestMethod]
    public void CombinesWithOr()
    {
        var store = new ConfigValueStore();
        store.SetConfigValue("a", 1);
        store.SetConfigValue("b", 0);

        Assert.IsTrue(store.ArePreconditionsMet([Config("a", "=", "1", logic: "or"), Config("b", "=", "1")]));
        Assert.IsTrue(store.ArePreconditionsMet([Config("a", "=", "0", logic: "or"), Config("b", "=", "0")]));
        Assert.IsFalse(store.ArePreconditionsMet([Config("a", "=", "0", logic: "or"), Config("b", "=", "1")]));
    }

    [TestMethod]
    public void UnknownReferenceLeavesPreviousResultInPlace()
    {
        // The Connector breaks out without assigning when the referenced config has no value,
        // which carries the previous result forward. Replicated deliberately.
        var store = new ConfigValueStore();
        store.SetConfigValue("known", 0);

        var preconditions = new[] { Config("known", "=", "0", logic: "and"), Config("missing", "=", "1") };

        Assert.IsTrue(store.ArePreconditionsMet(preconditions));
    }

    [TestMethod]
    public void ArcazePinPreconditionNeverPasses()
    {
        var store = new ConfigValueStore();

        var pin = new Precondition(PreconditionKind.Pin, true, "1", "=", "1", "and");

        Assert.IsFalse(store.ArePreconditionsMet([pin]), "Arcaze hardware does not exist off Windows");
    }

    [TestMethod]
    public void ResolvesActivePlaceholdersOnly()
    {
        var store = new ConfigValueStore();
        store.SetConfigValue("cfg-a", 10);
        store.SetConfigValue("cfg-b", 20);

        var references = new[]
        {
            new ConfigReference(true, "cfg-a", "A"),
            new ConfigReference(false, "cfg-b", "B"),
            new ConfigReference(true, "cfg-missing", "C"),
        };

        var placeholders = store.ResolvePlaceholders(references);

        Assert.AreEqual(10, placeholders["A"]);
        Assert.IsFalse(placeholders.ContainsKey("B"), "inactive references are skipped");
        Assert.IsFalse(placeholders.ContainsKey("C"), "references without a value yet are skipped");
    }

    [TestMethod]
    public void ClearForgetsEverything()
    {
        var store = new ConfigValueStore();
        store.SetConfigValue("a", 1);
        store.SetVariable("v", 1);

        store.Clear();

        Assert.IsNull(store.GetConfigValue("a"));
        Assert.IsNull(store.GetVariable("v"));
    }

    private static Precondition Config(string reference, string operand, string value,
                                       bool active = true, string logic = "and")
    {
        return new Precondition(PreconditionKind.Config, active, reference, operand, value, logic);
    }

    private static Precondition Variable(string reference, string operand, string value,
                                         bool active = true, string logic = "and")
    {
        return new Precondition(PreconditionKind.Variable, active, reference, operand, value, logic);
    }
}

[TestClass]
public sealed class PlaceholderSubstitutionTests
{
    [TestMethod]
    public void SubstitutesConfigReferencePlaceholder()
    {
        var placeholders = new Dictionary<string, double> { ["A"] = 5 };

        Assert.AreEqual(15, ExpressionEvaluator.Evaluate("$+A", 10, null, placeholders));
        Assert.AreEqual(5, ExpressionEvaluator.Evaluate("A", 0, null, placeholders));
    }

    [TestMethod]
    public void DoesNotCorruptFunctionNames()
    {
        // A placeholder of "i" or "f" must not break "if(".
        var placeholders = new Dictionary<string, double> { ["i"] = 1, ["f"] = 2 };

        Assert.AreEqual(7, ExpressionEvaluator.Evaluate("if($>0,7,8)", 1, null, placeholders));
    }

    [TestMethod]
    public void LongerPlaceholdersWinOverShorterPrefixes()
    {
        var placeholders = new Dictionary<string, double> { ["A"] = 1, ["AB"] = 99 };

        Assert.AreEqual(99, ExpressionEvaluator.Evaluate("AB", 0, null, placeholders));
    }

    [TestMethod]
    public void HandlesNegativePlaceholderValues()
    {
        var placeholders = new Dictionary<string, double> { ["A"] = -3 };

        Assert.AreEqual(-8, ExpressionEvaluator.Evaluate("A-5", 0, null, placeholders));
    }

    [TestMethod]
    public void UnresolvedPlaceholderFailsRatherThanReadingZero()
    {
        // "B" was never resolved, so the expression must not silently evaluate as if it were 0.
        Assert.IsNull(ExpressionEvaluator.Evaluate("$+B", 10, null, new Dictionary<string, double>()));
    }

    [TestMethod]
    public void ReplaceStandaloneRespectsWordBoundaries()
    {
        Assert.AreEqual("1+1", ExpressionEvaluator.ReplaceStandalone("A+A", "A", "1"));
        Assert.AreEqual("ABC", ExpressionEvaluator.ReplaceStandalone("ABC", "A", "1"));
        Assert.AreEqual("1*(1)", ExpressionEvaluator.ReplaceStandalone("A*(A)", "A", "1"));
    }
}
