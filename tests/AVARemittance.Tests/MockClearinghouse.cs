using System.Net;
using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using AVARemittance.Core.Sending;

namespace AVARemittance.Tests;

/// <summary>
/// A clearinghouse on this computer, for tests only: it takes CORE real-time requests (MIME multipart or SOAP), checks
/// the user name and password and the envelope's fields, and answers each 276 with the samples' 277 about that claim
/// (by its REF*EJ account), under the trace the 276 asked with. Nothing ever goes off this computer.
/// </summary>
internal sealed class MockClearinghouse : IDisposable
{
    public const string User = "maple-test";
    public const string Password = "not-a-real-password-9";
    private readonly HttpListener _listener = new();
    private readonly Task _serving;

    public string Address { get; }
    public List<Dictionary<string, string>> Requests { get; } = [];
    public int Failures { get; set; }

    public MockClearinghouse()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            Address = $"http://127.0.0.1:{port}/core/realtime/";
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(Address);
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 20) { }
        }
        _serving = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try
            {
                c = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            try
            {
                await Answer(c);
            }
            catch (Exception e)
            {
                // A mistake in the mock shows as a 500 with its message, never as a request left hanging.
                c.Response.StatusCode = 500;
                c.Response.StatusDescription = e.GetType().Name;
            }
            finally
            {
                c.Response.Close();
            }
        }
    }

    private async Task Answer(HttpListenerContext c)
    {
        var body = await new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
        var soap = c.Request.ContentType?.Contains("soap", StringComparison.OrdinalIgnoreCase) == true;
        Dictionary<string, string> fields;
        if (soap)
        {
            var doc = XDocument.Parse(body);
            fields = doc.Descendants().Where(e => !e.HasElements).ToDictionary(e => e.Name.LocalName, e => e.Value);
            fields["UserName"] = fields.GetValueOrDefault("Username", "");
        }
        else
        {
            var boundary = c.Request.ContentType!.Split("boundary=")[1].Trim('"');
            fields = CoreClient.Parts(body, boundary).ToDictionary(p => p.Name, p => p.Value);
        }
        lock (Requests) Requests.Add(fields);

        string code = "Success", message = "", payload = "";
        if (fields.GetValueOrDefault("UserName") != User || fields.GetValueOrDefault("Password") != Password) (code, message) = ("UserNameAndPasswordInvalid", "The user name or password is wrong.");
        else if (fields.GetValueOrDefault("PayloadType") != CoreClient.StatusRequestType || fields.GetValueOrDefault("ProcessingMode") != "RealTime") (code, message) = ("PayloadTypeIncorrect", "Only real-time 276s.");
        else if (Failures > 0) { Failures--; (code, message) = ("Receiver Down", "Try again later."); }
        else payload = Answer277(fields["Payload"]);

        var response = new Dictionary<string, string>
        {
            ["PayloadType"] = code == "Success" ? CoreClient.StatusResponseType : "CoreEnvelopeError", ["ProcessingMode"] = "RealTime", ["PayloadID"] = Guid.NewGuid().ToString(),
            ["TimeStamp"] = "2026-11-25T08:16:00Z", ["SenderID"] = fields.GetValueOrDefault("ReceiverID", ""), ["ReceiverID"] = fields.GetValueOrDefault("SenderID", ""),
            ["CORERuleVersion"] = CoreClient.RuleVersion, ["ErrorCode"] = code, ["ErrorMessage"] = message, ["Payload"] = payload,
        };
        byte[] bytes;
        if (soap)
        {
            XNamespace s = "http://www.w3.org/2003/05/soap-envelope", cor = "http://www.caqh.org/SOAP/WSDL/CORERule2.2.0.xsd";
            var xml = new XElement(s + "Envelope", new XElement(s + "Body", new XElement(cor + "COREEnvelopeRealTimeResponse", response.Select(kv => new XElement(kv.Key, kv.Value)))));
            bytes = Encoding.UTF8.GetBytes(xml.ToString());
            c.Response.ContentType = "application/soap+xml; charset=utf-8";
        }
        else
        {
            const string boundary = "mock-core-boundary";
            var b = new StringBuilder();
            foreach (var (k, v) in response) b.Append("--").Append(boundary).Append("\r\nContent-Disposition: form-data; name=\"").Append(k).Append("\"\r\n\r\n").Append(v).Append("\r\n");
            b.Append("--").Append(boundary).Append("--\r\n");
            bytes = Encoding.UTF8.GetBytes(b.ToString());
            c.Response.ContentType = $"multipart/form-data; boundary={boundary}";
        }
        c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>The sample 277 about the claim asked (by REF*EJ), with the 276's trace in its TRN*2.</summary>
    private static string Answer277(string request)
    {
        var account = Regex.Match(request, @"REF\*EJ\*([^~*]+)").Groups[1].Value;
        var trace = Regex.Match(request, @"TRN\*1\*([^~*]+)").Groups[1].Value;
        var sample = Directory.GetFiles(Samples.Path("status"), "*-277-*.x12").Select(File.ReadAllText).Single(t => t.Contains($"REF*EJ*{account}~"));
        return Regex.Replace(sample, @"TRN\*2\*[^~]+", "TRN*2*" + trace);
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        _serving.Wait(TimeSpan.FromSeconds(5));
    }
}
