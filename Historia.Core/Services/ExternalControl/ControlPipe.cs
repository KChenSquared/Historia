namespace Historia.Services.ExternalControl;

public enum ControlConnectOutcome
{
    Connected,
    /// <summary>Nothing is listening (or it never answered within the timeout).</summary>
    Unreachable,
    /// <summary>A Historia is listening but refuses a duplex client — a build that predates control requests.</summary>
    Incompatible,
}

public sealed record ControlConnectResult(ControlConnectOutcome Outcome, NamedPipeClientStream? Stream);

public enum ControlReadKind
{
    /// <summary>The connection closed without sending anything.</summary>
    Empty,
    /// <summary>A versioned control request.</summary>
    Request,
    /// <summary>No protocol header: every line is a repository path from a second launch.</summary>
    Legacy,
    /// <summary>The request could not be read; <see cref="ControlRead.Failure"/> says why.</summary>
    Invalid,
}

public sealed record ControlRead(
    ControlReadKind Kind,
    ControlRequest? Request = null,
    IReadOnlyList<string>? Lines = null,
    ControlResponse? Failure = null);

/// <summary>
/// Framing for the per-user control pipe. A control client sends the header line
/// <see cref="ProtocolHeader"/> and then one JSON request line; the server answers with one JSON
/// line and closes. Any other first line is a second launch forwarding repository paths, one per
/// line. Server construction (the Windows ACL) stays in the App.
/// </summary>
public static class ControlPipe
{
    public const string ProtocolHeader = "historia-control/1";
    public const string DefaultPipeName = "Historia.SingleInstance.Pipe";
    public const string PipeNameVariable = "HISTORIA_CONTROL_PIPE";

    /// <summary>Longest line either side accepts, in characters.</summary>
    public const int MaxLineChars = 64 * 1024;

    /// <summary>The pipe name: <c>HISTORIA_CONTROL_PIPE</c> when set (tests), else the single-instance pipe.</summary>
    public static string PipeName
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(PipeNameVariable);
            return string.IsNullOrWhiteSpace(overridden) ? DefaultPipeName : overridden;
        }
    }

    public static UTF8Encoding Utf8 { get; } = new(encoderShouldEmitUTF8Identifier: false);

    public static StreamWriter CreateWriter(Stream stream) =>
        new(stream, Utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = false };

    public static StreamReader CreateReader(Stream stream) =>
        new(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

    // ── Client ──────────────────────────────────────────────────────────────────────────

    public static async Task<ControlConnectResult> ConnectClientAsync(
        TimeSpan timeout, CancellationToken ct = default, string? pipeName = null)
    {
        var client = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(timeout, ct).ConfigureAwait(false);
            return new ControlConnectResult(ControlConnectOutcome.Connected, client);
        }
        catch (UnauthorizedAccessException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return new ControlConnectResult(ControlConnectOutcome.Incompatible, null);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return new ControlConnectResult(ControlConnectOutcome.Unreachable, null);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task WriteRequestAsync(TextWriter writer, ControlRequest request, CancellationToken ct = default)
    {
        await writer.WriteLineAsync(ProtocolHeader.AsMemory(), ct).ConfigureAwait(false);
        await writer.WriteLineAsync(ControlJson.Serialize(request).AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The server's reply, or null when the connection closed without a readable one.</summary>
    public static async Task<ControlResponse?> ReadResponseAsync(TextReader reader, CancellationToken ct = default)
    {
        var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
        if (line.Text is null || line.Overflow)
            return null;

        try
        {
            return ControlJson.DeserializeResponse(line.Text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Server ──────────────────────────────────────────────────────────────────────────

    /// <summary>Reads one connection: a control request when it opens with the protocol header,
    /// otherwise every line as a legacy argument.</summary>
    public static async Task<ControlRead> ReadAsync(TextReader reader, CancellationToken ct = default)
    {
        var first = await ReadLineAsync(reader, ct).ConfigureAwait(false);
        if (first.Text is null)
            return new ControlRead(ControlReadKind.Empty);

        if (first.Overflow)
            return Invalid("The request line is too long.");

        if (first.Text != ProtocolHeader)
        {
            var lines = new List<string> { first.Text };
            while (true)
            {
                var next = await ReadLineAsync(reader, ct).ConfigureAwait(false);
                if (next.Text is null)
                    break;
                if (next.Overflow)
                    return Invalid("A line is too long.");
                lines.Add(next.Text);
            }
            return new ControlRead(ControlReadKind.Legacy, Lines: lines);
        }

        var body = await ReadLineAsync(reader, ct).ConfigureAwait(false);
        if (body.Text is null)
            return Invalid("The request is missing.");
        if (body.Overflow)
            return Invalid("The request line is too long.");

        try
        {
            var request = ControlJson.DeserializeRequest(body.Text);
            return request is null
                ? Invalid("The request is empty.")
                : new ControlRead(ControlReadKind.Request, Request: request);
        }
        catch (JsonException)
        {
            return Invalid("The request is not valid JSON.");
        }

        static ControlRead Invalid(string message) =>
            new(ControlReadKind.Invalid, Failure: ControlResponse.Fail(ControlError.Usage, message));
    }

    public static async Task WriteResponseAsync(TextWriter writer, ControlResponse response, CancellationToken ct = default)
    {
        await writer.WriteLineAsync(ControlJson.Serialize(response).AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    // ── Shared ──────────────────────────────────────────────────────────────────────────

    private readonly record struct BoundedLine(string? Text, bool Overflow);

    /// <summary>Reads up to a line feed, never buffering more than <see cref="MaxLineChars"/>.
    /// Text is null at end of stream; an overlong line is drained and reported as overflow.</summary>
    private static async Task<BoundedLine> ReadLineAsync(TextReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new char[1];
        var overflow = false;
        var any = false;

        while (true)
        {
            var read = await reader.ReadAsync(one.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            any = true;
            var c = one[0];
            if (c == '\n')
                return new BoundedLine(Finish(sb, overflow), overflow);

            if (sb.Length >= MaxLineChars)
                overflow = true;
            else
                sb.Append(c);
        }

        return any ? new BoundedLine(Finish(sb, overflow), overflow) : new BoundedLine(null, false);

        static string Finish(StringBuilder sb, bool overflow)
        {
            if (overflow)
                return string.Empty;
            if (sb.Length > 0 && sb[^1] == '\r')
                sb.Length--;
            return sb.ToString();
        }
    }
}
