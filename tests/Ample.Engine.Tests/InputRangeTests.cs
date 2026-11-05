namespace Ample.Core.Tests;

/// <summary>A mixer or bus with three inputs is a normal thing to write and gets no range warning.</summary>
public class InputRangeTests
{
    [Theory]
    [InlineData("mixer")]
    [InlineData("bus")]
    public void A_block_with_three_inputs_gets_no_range_warning(string type)
    {
        string text = "composition \"i\" { ample 1  tempo 120  master-seed 3 }\n" + type + " m {\n  inputs 3\n}\n";
        Assert.DoesNotContain(EngineRig.Parse(text).Diagnostics, d => d.Code == "AMP2021");
    }
}
