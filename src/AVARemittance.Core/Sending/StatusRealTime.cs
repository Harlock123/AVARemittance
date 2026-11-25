using AVARemittance.Core.Billing;
using AVARemittance.Core.Status;

namespace AVARemittance.Core.Sending;

/// <summary>One claim asked about in real time: the trace, where its 277 was saved, and what the clearinghouse said.</summary>
public sealed record Asked(BilledClaim Claim, string Trace, string? Saved, CoreResponse Response);

/// <summary>
/// Asks about claims in real time, one claim per request as CORE's real-time mode carries one patient: each its own
/// 276, under the trace the whole 276 would give it, each 277 saved in <c>folder</c> (never over a file that's there).
/// </summary>
public static class StatusRealTime
{
    public static async Task<IReadOnlyList<Asked>> AskAsync(IReadOnlyList<BilledClaim> claims, StatusSettings settings, Clearinghouse ch, string password,
        CoreClient client, string folder, DateTime now, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (ch.Problem() is { } problem) throw new InvalidOperationException(problem);
        Directory.CreateDirectory(folder);
        var asked = new List<Asked>();
        var control = settings.ControlNumber;
        foreach (var c in claims)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report($"Asking {c.Payer} about {c.Account} ({asked.Count + 1} of {claims.Count})…");
            var one = StatusRequest.Build([c], settings with { ControlNumber = control++ }, now);
            var trace = one.Traces[c];
            CoreResponse response;
            try
            {
                response = await client.SendAsync(one.Text, ch, password, now, cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                response = new CoreResponse("Unreachable", e.Message, "", "", "");
            }
            string? saved = null;
            if (response.Ok)
            {
                saved = Path.Combine(folder, $"{trace}-277.x12");
                for (var n = 2; File.Exists(saved); n++) saved = Path.Combine(folder, $"{trace}-277-{n}.x12");
                await File.WriteAllTextAsync(saved, response.Payload, cancel).ConfigureAwait(false);
            }
            asked.Add(new Asked(c, trace, saved, response));
        }
        return asked;
    }
}
