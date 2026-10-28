namespace Historia.Diagnostics;

/// <summary>
/// The text a log interpolated-string handler builds. With <c>enabled</c> false nothing is
/// allocated and the handler's <c>shouldAppend</c> comes back false, so the compiler skips every
/// <c>Append*</c> call, and so every hole's <c>ToString</c>. Formats under the current culture,
/// exactly as an interpolated <see cref="string"/> does. <see cref="AppLog.MessageHandler"/> and
/// <see cref="PerfLog.MessageHandler"/> wrap this with their own enabled gate.
/// </summary>
internal ref struct LogMessageText
{
    private DefaultInterpolatedStringHandler _text;

    public LogMessageText(int literalLength, int formattedCount, bool enabled)
    {
        Active = enabled;
        _text = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>True when the message was started enabled and its holes were formatted.</summary>
    public bool Active { get; }

    public void AppendLiteral(string value) => _text.AppendLiteral(value);
    public void AppendFormatted(string? value) => _text.AppendFormatted(value);
    public void AppendFormatted(string? value, int alignment = 0, string? format = null) => _text.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => _text.AppendFormatted(value);
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _text.AppendFormatted(value, alignment, format);
    public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _text.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _text.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _text.AppendFormatted(value, alignment, format);

    public string ToStringAndClear() => _text.ToStringAndClear();
}
