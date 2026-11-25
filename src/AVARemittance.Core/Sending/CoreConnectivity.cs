using System.Globalization;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AVARemittance.Core.Sending;

/// <summary>A clearinghouse to send 276s to in real time, as the person set it up. Off until they turn it on.</summary>
public sealed record Clearinghouse
{
    public bool Enabled { get; init; }

    /// <summary>The clearinghouse's CORE real-time address. HTTPS, except on this computer (a local test server).</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>"mime" (HTTP MIME multipart) or "soap" (SOAP with WS-Security), the two CORE envelopes.</summary>
    public string Envelope { get; init; } = "mime";

    /// <summary>CORE's SenderID and ReceiverID, as the clearinghouse assigned them.</summary>
    public string SenderId { get; init; } = "";
    public string ReceiverId { get; init; } = "";
    public string Username { get; init; } = "";

    /// <summary>Why the settings can't be used, or null when they can.</summary>
    public string? Problem()
    {
        if (!Enabled) return "Sending to a clearinghouse is off (Claims › Claim status › Clearinghouse).";
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)) return "The clearinghouse's address isn't a web address.";
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) return "The clearinghouse's address must be HTTPS: the questions hold patients' details.";
        if (Envelope is not ("mime" or "soap")) return "The envelope is mime or soap.";
        if (SenderId.Length == 0 || ReceiverId.Length == 0 || Username.Length == 0) return "The clearinghouse needs the sender ID, receiver ID and user name it gave you.";
        return null;
    }
}

/// <summary>The clearinghouse's answer to one real-time request.</summary>
public sealed record CoreResponse(string ErrorCode, string ErrorMessage, string PayloadType, string PayloadId, string Payload)
{
    public bool Ok => ErrorCode == "Success" && Payload.Length > 0;
}

/// <summary>
/// CAQH CORE connectivity in real time: an X12 request sent in a CORE envelope (PayloadType, ProcessingMode RealTime,
/// a PayloadID, TimeStamp, SenderID, ReceiverID, CORERuleVersion 2.2.0, the X12 as the Payload) over HTTP MIME
/// multipart or SOAP, with the user name and password; the answer comes back the same way. Here the request is a 276
/// (X12_276_Request_005010X212) and the answer a 277. The password is passed in, never kept here. AVA Eligibility's,
/// with the payload type a parameter: fix them together.
/// </summary>
public sealed class CoreClient(HttpClient http, string requestType = CoreClient.StatusRequestType)
{
    public const string StatusRequestType = "X12_276_Request_005010X212";
    public const string StatusResponseType = "X12_277_Response_005010X212";
    public const string RuleVersion = "2.2.0";

    public string RequestType { get; } = requestType;

    public async Task<CoreResponse> SendAsync(string x12, Clearinghouse ch, string password, DateTime now, CancellationToken cancel = default)
    {
        if (ch.Problem() is { } problem) throw new InvalidOperationException(problem);
        var id = Guid.NewGuid().ToString();
        var stamp = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, ch.Endpoint);
        if (ch.Envelope == "mime")
        {
            var form = new MultipartFormDataContent();
            void Field(string name, string value) => form.Add(new StringContent(value, Encoding.UTF8), name);
            Field("PayloadType", RequestType);
            Field("ProcessingMode", "RealTime");
            Field("PayloadID", id);
            Field("TimeStamp", stamp);
            Field("UserName", ch.Username);
            Field("Password", password);
            Field("SenderID", ch.SenderId);
            Field("ReceiverID", ch.ReceiverId);
            Field("CORERuleVersion", RuleVersion);
            Field("Payload", x12);
            request.Content = form;
        }
        else
        {
            request.Content = new StringContent(Soap(x12, ch, password, id, stamp), Encoding.UTF8, "application/soap+xml");
        }
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new CoreResponse($"HTTP {(int)response.StatusCode}", response.ReasonPhrase ?? "", "", "", "");
        return response.Content.Headers.ContentType?.MediaType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true || body.TrimStart().StartsWith('<')
            ? ReadSoap(body)
            : await ReadMimeAsync(response, body, cancel).ConfigureAwait(false);
    }

    private string Soap(string x12, Clearinghouse ch, string password, string id, string stamp)
    {
        static string X(string s) => SecurityElement.Escape(s) ?? "";
        return $"""
            <soapenv:Envelope xmlns:soapenv="http://www.w3.org/2003/05/soap-envelope" xmlns:cor="http://www.caqh.org/SOAP/WSDL/CORERule2.2.0.xsd">
              <soapenv:Header>
                <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
                  <wsse:UsernameToken><wsse:Username>{X(ch.Username)}</wsse:Username><wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText">{X(password)}</wsse:Password></wsse:UsernameToken>
                </wsse:Security>
              </soapenv:Header>
              <soapenv:Body>
                <cor:COREEnvelopeRealTimeRequest>
                  <PayloadType>{RequestType}</PayloadType>
                  <ProcessingMode>RealTime</ProcessingMode>
                  <PayloadID>{X(id)}</PayloadID>
                  <TimeStamp>{X(stamp)}</TimeStamp>
                  <SenderID>{X(ch.SenderId)}</SenderID>
                  <ReceiverID>{X(ch.ReceiverId)}</ReceiverID>
                  <CORERuleVersion>{RuleVersion}</CORERuleVersion>
                  <Payload>{X(x12)}</Payload>
                </cor:COREEnvelopeRealTimeRequest>
              </soapenv:Body>
            </soapenv:Envelope>
            """;
    }

    private static CoreResponse ReadSoap(string body)
    {
        var doc = XDocument.Parse(body);
        string Get(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        if (doc.Descendants().Any(e => e.Name.LocalName == "Fault"))
            return new CoreResponse("Fault", Get("Text") is { Length: > 0 } t ? t : Get("faultstring"), "", "", "");
        return new CoreResponse(Get("ErrorCode"), Get("ErrorMessage"), Get("PayloadType"), Get("PayloadID"), Get("Payload"));
    }

    /// <summary>The named parts of a multipart body: each part's Content-Disposition name and its text.</summary>
    public static IEnumerable<(string Name, string Value)> Parts(string body, string boundary)
    {
        foreach (var chunk in body.Split("--" + boundary).Skip(1))
        {
            if (chunk.StartsWith("--", StringComparison.Ordinal)) yield break;
            var split = chunk.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var gap = 4;
            if (split < 0) { split = chunk.IndexOf("\n\n", StringComparison.Ordinal); gap = 2; }
            if (split < 0) continue;
            var headers = chunk[..split];
            var disposition = headers.Split('\n').Select(h => h.Trim()).FirstOrDefault(h => h.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase));
            if (disposition == null) continue;
            var name = ContentDispositionHeaderValue.Parse(disposition["Content-Disposition:".Length..].Trim()).Name?.Trim('"') ?? "";
            var value = chunk[(split + gap)..];
            if (value.EndsWith("\r\n", StringComparison.Ordinal)) value = value[..^2];
            else if (value.EndsWith('\n')) value = value[..^1];
            yield return (name, value);
        }
    }

    private static Task<CoreResponse> ReadMimeAsync(HttpResponseMessage response, string body, CancellationToken cancel)
    {
        _ = cancel;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.Content.Headers.ContentType is { MediaType: "multipart/form-data" or "multipart/mixed" } type
            && type.Parameters.FirstOrDefault(p => p.Name == "boundary")?.Value?.Trim('"') is { } boundary)
        {
            foreach (var (name, value) in Parts(body, boundary)) fields[name] = value;
        }
        else
        {
            try
            {
                foreach (var p in JsonDocument.Parse(body).RootElement.EnumerateObject()) fields[p.Name] = p.Value.ToString();
            }
            catch (JsonException)
            {
                return Task.FromResult(new CoreResponse("Unreadable", "The clearinghouse's answer wasn't a CORE envelope.", "", "", ""));
            }
        }
        string Get(string n) => fields.TryGetValue(n, out var v) ? v : "";
        return Task.FromResult(new CoreResponse(Get("ErrorCode"), Get("ErrorMessage"), Get("PayloadType"), Get("PayloadID"), Get("Payload")));
    }
}

/// <summary>The clearinghouse password, in a file of its own in the settings folder, readable by the owner only.</summary>
public sealed class PasswordStore(string folder)
{
    private string Path => System.IO.Path.Combine(folder, "clearinghouse.secret");

    public string? Read() => File.Exists(Path) ? File.ReadAllText(Path) : null;

    public void Save(string password)
    {
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            using (File.Create(Path)) { }
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.WriteAllText(Path, password);
    }

    public void Forget()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
