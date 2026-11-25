using AVARemittance.Core.Remittance;

namespace AVARemittance.Tests;

/// <summary>The balancing checks: every sample that should balance does, and the unbalanced one finds each planted problem.</summary>
public sealed class ChecksTests
{
    [Theory]
    [InlineData("harborview-835-1110.x12")]
    [InlineData("harborview-835-1117.x12")]
    [InlineData("tallgrass-835-1112.x12")]
    public void The_payers_samples_balance(string file) => Assert.Empty(Checks.Run(Samples.Load(file)));

    [Fact]
    public void The_unbalanced_sample_finds_each_planted_problem()
    {
        var found = Checks.Run(Samples.Load("problems/unbalanced-835.x12"));
        Assert.Equal(
        [
            "B1: The payment is $100.00, but the claims pay $62.40: $37.60 less than was sent.",
            "B2: Claim RB26199: the charge $150.00 less $85.60 of adjustments is $64.40, but $62.40 was paid: $2.00 unexplained.",
            "B5: Claim RB26199: the patient's share says $10.00, but its PR adjustments come to $0.00.",
            "B3: Claim RB26199, line 1 (99213): the charge $150.00 less $85.60 of adjustments is $64.40, but $62.40 was paid.",
            "E1: SE01 says 23 segments, but the transaction has 22.",
        ], found.Select(f => f.ToString()));
        Assert.All(found, f => Assert.NotEmpty(f.Segments));
    }

    [Fact]
    public void Every_rule_has_an_id_a_why_and_a_source()
    {
        Assert.Equal(Checks.Rules.Count, Checks.Rules.Select(r => r.Id).Distinct().Count());
        Assert.All(Checks.Rules, r =>
        {
            Assert.NotEmpty(r.Title);
            Assert.True(r.Why.Length > 40, r.Id);
            Assert.NotEmpty(r.Source);
        });
    }
}
