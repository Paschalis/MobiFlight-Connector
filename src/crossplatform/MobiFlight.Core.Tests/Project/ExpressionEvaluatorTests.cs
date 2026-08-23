using MobiFlight.Core.Project;

namespace MobiFlight.Core.Tests.Project;

[TestClass]
public sealed class ExpressionEvaluatorTests
{
    [TestMethod]
    public void PassThroughReturnsCurrentValue()
    {
        Assert.AreEqual(42, ExpressionEvaluator.Evaluate("$", 42));
        Assert.AreEqual(42, ExpressionEvaluator.Evaluate("", 42), "an empty expression is a pass through");
        Assert.AreEqual(42, ExpressionEvaluator.Evaluate(null, 42));
    }

    [TestMethod]
    public void EvaluatesArithmeticOnCurrentValue()
    {
        Assert.AreEqual(110, ExpressionEvaluator.Evaluate("$+10", 100));
        Assert.AreEqual(90, ExpressionEvaluator.Evaluate("$-10", 100));
        Assert.AreEqual(200, ExpressionEvaluator.Evaluate("$*2", 100));
        Assert.AreEqual(50, ExpressionEvaluator.Evaluate("$/2", 100));
    }

    [TestMethod]
    public void RespectsPrecedenceAndParentheses()
    {
        Assert.AreEqual(7, ExpressionEvaluator.Evaluate("1+2*3"));
        Assert.AreEqual(9, ExpressionEvaluator.Evaluate("(1+2)*3"));
        Assert.AreEqual(1, ExpressionEvaluator.Evaluate("10-3*3"));
    }

    [TestMethod]
    public void HandlesNegativeCurrentValue()
    {
        // "$-5" with $ = -3 must be (-3)-5, not -3-5 parsed as something else.
        Assert.AreEqual(-8, ExpressionEvaluator.Evaluate("$-5", -3));
        Assert.AreEqual(-3, ExpressionEvaluator.Evaluate("$", -3));
        Assert.AreEqual(6, ExpressionEvaluator.Evaluate("$*-2", -3));
    }

    [TestMethod]
    public void EvaluatesRealWorldEncoderExpression()
    {
        // Taken verbatim from the bundled example configs.
        const string expression = "if($<3697,$+100,$-1800)";

        Assert.AreEqual(1100, ExpressionEvaluator.Evaluate(expression, 1000), "below the threshold");
        Assert.AreEqual(2200, ExpressionEvaluator.Evaluate(expression, 4000), "above the threshold");
    }

    [TestMethod]
    public void EvaluatesComparisons()
    {
        Assert.AreEqual(1, ExpressionEvaluator.Evaluate("$>5", 10));
        Assert.AreEqual(0, ExpressionEvaluator.Evaluate("$>5", 1));
        Assert.AreEqual(1, ExpressionEvaluator.Evaluate("$=5", 5));
        Assert.AreEqual(1, ExpressionEvaluator.Evaluate("$>=5", 5));
        Assert.AreEqual(1, ExpressionEvaluator.Evaluate("$<>5", 6));
        Assert.AreEqual(0, ExpressionEvaluator.Evaluate("$!=5", 5));
    }

    [TestMethod]
    public void SubstitutesTriggerValue()
    {
        // '@' is the value coming from the input device.
        Assert.AreEqual(7, ExpressionEvaluator.Evaluate("@", 0, 7));
        Assert.AreEqual(14, ExpressionEvaluator.Evaluate("@*2", 0, 7));
        Assert.AreEqual(10, ExpressionEvaluator.Evaluate("$+@", 3, 7));
    }

    [TestMethod]
    public void SupportsNestedIf()
    {
        const string expression = "if($<10,1,if($<20,2,3))";

        Assert.AreEqual(1, ExpressionEvaluator.Evaluate(expression, 5));
        Assert.AreEqual(2, ExpressionEvaluator.Evaluate(expression, 15));
        Assert.AreEqual(3, ExpressionEvaluator.Evaluate(expression, 25));
    }

    [TestMethod]
    public void ToleratesWhitespace()
    {
        Assert.AreEqual(110, ExpressionEvaluator.Evaluate("  $ + 10  ", 100));
        Assert.AreEqual(1, ExpressionEvaluator.Evaluate("if( $ < 10 , 1 , 2 )", 5));
    }

    [TestMethod]
    public void ReturnsNullForMalformedExpressions()
    {
        Assert.IsNull(ExpressionEvaluator.Evaluate("$+", 1));
        Assert.IsNull(ExpressionEvaluator.Evaluate("(1+2", 1));
        Assert.IsNull(ExpressionEvaluator.Evaluate("1+2)", 1), "trailing junk must not be ignored");
        Assert.IsNull(ExpressionEvaluator.Evaluate("if(1,2)", 1), "if needs three arguments");
        Assert.IsNull(ExpressionEvaluator.Evaluate("nonsense", 1));
    }

    [TestMethod]
    public void ReturnsNullOnDivisionByZero()
    {
        Assert.IsNull(ExpressionEvaluator.Evaluate("$/0", 1));
    }

    [TestMethod]
    public void HandlesDecimals()
    {
        Assert.AreEqual(1.5, ExpressionEvaluator.Evaluate("$+0.5", 1));
        Assert.AreEqual(121.8, ExpressionEvaluator.Evaluate("$/100", 12180)!.Value, 0.0001);
    }
}
