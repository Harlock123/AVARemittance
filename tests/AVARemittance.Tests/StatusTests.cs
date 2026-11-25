using AVARemittance.Core.Billing;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Sending;
using AVARemittance.Core.Status;
using AVARemittance.Core.X12;

namespace AVARemittance.Tests;

/// <summary>Claim status: the 276 for claims with no answer, the 277s, and asking in real time (only ever the mock).</summary>
public sealed class StatusTests
{
    public static readonly DateTime SampleTime = new(2026, 11, 24, 9, 0, 0);

    public static List<BilledClaim> Waiting() =>
        [.. MatchingTests.Reconcile().Claims.Where(m => m.State == BilledState.NoAnswer).Select(m => m.Billed)];

    private static Clearinghouse Settings(MockClearinghouse mock, string envelope = "mime") => new()
    {
        Enabled = true, Endpoint = mock.Address, Envelope = envelope, SenderId = "RIVERBEND", ReceiverId = "MOCKCH", Username = MockClearinghouse.User,
    };

    [Fact]
    public void The_276_for_the_claims_with_no_answer_is_the_samples_byte_for_byte()
    {
        var waiting = Waiting();
        Assert.Equal(["RB26116", "RB26117"], waiting.Select(c => c.Account));
        Assert.Equal(new DateOnly(1980, 12, 7), waiting[0].BirthDate);
        Assert.Equal(("RIVERBEND FAMILY MEDICINE", "1588667638", "RB0001"), (waiting[0].ProviderName, waiting[0].ProviderNpi, waiting[0].SubmitterId));
        var built = StatusRequest.Build(waiting, new StatusSettings(), SampleTime);
        Assert.Equal(File.ReadAllText(Samples.Path("status/riverbend-276-1124.x12")), built.Text);
        Assert.Equal(2, built.Transactions);
        Assert.Equal("RB26117-261124", built.Traces[waiting[1]]);
    }

    [Fact]
    public void The_277s_say_where_each_claim_is_and_what_to_do()
    {
        var answers = Directory.GetFiles(Samples.Path("status"), "*-277-*.x12").Order(StringComparer.Ordinal)
            .SelectMany(f => StatusReader.Read(X12File.Load(f), Path.GetFileName(f))).ToList();
        Assert.True(StatusReader.Is277(X12File.Load(Samples.Path("status/tallgrass-277-1124.x12"))));
        Assert.Equal(2, answers.Count);
        var waiting = Waiting();
        Assert.Equal("277 of 2026-11-24: P1 20: Pending/In Process-The claim or encounter is in the adjudication system. Accepted for processing. (payer claim HV26111900077). In process: wait, and ask again in a couple of weeks.",
            StatusReader.Describe(StatusReader.For(waiting[0], answers)!));
        var quinn = StatusReader.For(waiting[1], answers)!;
        Assert.Equal(("A4", "35", 150m), (quinn.Statuses[0].Category, quinn.Statuses[0].Code, quinn.Statuses[0].Charge));
        Assert.Equal("The payer has no record of it: send it again now, before the time to file runs out.", quinn.Meaning);
    }

    [Fact]
    public void Sending_is_off_until_turned_on_and_only_over_https_off_this_computer()
    {
        var on = new Clearinghouse { Enabled = true, Endpoint = "https://ch.example/core", SenderId = "S", ReceiverId = "R", Username = "u" };
        Assert.Null(on.Problem());
        Assert.Equal("Sending to a clearinghouse is off (Claims › Claim status › Clearinghouse).", (on with { Enabled = false }).Problem());
        Assert.Equal("The clearinghouse's address must be HTTPS: the questions hold patients' details.", (on with { Endpoint = "http://ch.example/core" }).Problem());
        Assert.Null((on with { Endpoint = "http://127.0.0.1:9/core" }).Problem());
        Assert.Throws<InvalidOperationException>(() => new CoreClient(new HttpClient()).SendAsync("x", new Clearinghouse(), "p", DateTime.Now).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("mime")]
    [InlineData("soap")]
    public void A_276_goes_in_a_core_envelope_and_the_277_comes_back(string envelope)
    {
        using var mock = new MockClearinghouse();
        var one = StatusRequest.Build([Waiting()[1]], new StatusSettings(), SampleTime);
        using var http = new HttpClient();
        var r = new CoreClient(http).SendAsync(one.Text, Settings(mock, envelope), MockClearinghouse.Password, SampleTime).GetAwaiter().GetResult();
        Assert.True(r.Ok, $"{r.ErrorCode} {r.ErrorMessage}");
        Assert.Equal(CoreClient.StatusResponseType, r.PayloadType);
        var answer = Assert.Single(StatusReader.Read(X12File.Parse(r.Payload), "x"));
        Assert.Equal(("RB26117", "RB26117-261124", "A4"), (answer.Account, answer.Trace, answer.Statuses[0].Category));
        var sent = Assert.Single(mock.Requests);
        Assert.Equal(("X12_276_Request_005010X212", "RealTime", "RIVERBEND", "MOCKCH", "2.2.0"), (sent["PayloadType"], sent["ProcessingMode"], sent["SenderID"], sent["ReceiverID"], sent["CORERuleVersion"]));
        Assert.Equal(one.Text, sent["Payload"]);
        var wrong = new CoreClient(http).SendAsync(one.Text, Settings(mock, envelope), "guess", SampleTime).GetAwaiter().GetResult();
        Assert.Equal(("UserNameAndPasswordInvalid", false), (wrong.ErrorCode, wrong.Ok));
    }

    [Fact]
    public void Each_claim_is_asked_alone_and_its_277_saved()
    {
        using var mock = new MockClearinghouse { Failures = 1 };
        var folder = Directory.CreateTempSubdirectory("remit-status-").FullName;
        using var http = new HttpClient();
        var asked = StatusRealTime.AskAsync(Waiting(), new StatusSettings { ControlNumber = 40 }, Settings(mock), MockClearinghouse.Password, new CoreClient(http), folder, SampleTime).GetAwaiter().GetResult();
        Assert.Equal(2, asked.Count);
        Assert.Equal(("RB26116", "Receiver Down", (string?)null), (asked[0].Claim.Account, asked[0].Response.ErrorCode, asked[0].Saved));
        Assert.Equal(Path.Combine(folder, "RB26117-261124-277.x12"), asked[1].Saved);
        Assert.Equal(["000000040", "000000041"], mock.Requests.Select(r => X12File.Parse(r["Payload"]).Segments[0][13]));
    }

    [Fact]
    public void The_password_is_kept_in_a_file_only_its_owner_can_read()
    {
        var folder = Directory.CreateTempSubdirectory("remit-secret-").FullName;
        var store = new PasswordStore(folder);
        Assert.Null(store.Read());
        store.Save("p4ss");
        Assert.Equal("p4ss", store.Read());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, "clearinghouse.secret")));
        store.Forget();
        Assert.Null(store.Read());
    }
}
