namespace Historia.Services.Diff;

/// <summary>
/// Represents a parsed conflict region within a text document.
/// </summary>
public sealed class ConflictRegion
{
    public int StartLine { get; }
    public int EndLine { get; }

    public int StartMarkerOffset { get; }
    public int StartMarkerLength { get; }

    public int OursStartLine { get; }
    public int OursEndLine { get; }
    public int OursLineCount { get; }
    public int OursOffset { get; }
    public int OursLength { get; }

    public bool HasBase { get; }
    public int BaseMarkerLine { get; }
    public int BaseMarkerOffset { get; }
    public int BaseMarkerLength { get; }
    public int BaseStartLine { get; }
    public int BaseEndLine { get; }
    public int BaseLineCount { get; }
    public int BaseOffset { get; }
    public int BaseLength { get; }

    public int SeparatorLine { get; }
    public int SeparatorOffset { get; }
    public int SeparatorLength { get; }

    public int TheirsStartLine { get; }
    public int TheirsEndLine { get; }
    public int TheirsLineCount { get; }
    public int TheirsOffset { get; }
    public int TheirsLength { get; }

    public int EndMarkerOffset { get; }
    public int EndMarkerLength { get; }

    public int TotalOffset { get; }
    public int TotalLength { get; }

    public string OursLabel { get; }
    public string? BaseLabel { get; }
    public string TheirsLabel { get; }

    public IReadOnlyList<string> OursLines { get; }
    public IReadOnlyList<string>? BaseLines { get; }
    public IReadOnlyList<string> TheirsLines { get; }

    public ConflictRegion(
        int startLine,
        int endLine,
        int startMarkerOffset,
        int startMarkerLength,
        int oursStartLine,
        int oursEndLine,
        int oursLineCount,
        int oursOffset,
        int oursLength,
        bool hasBase,
        int baseMarkerLine,
        int baseMarkerOffset,
        int baseMarkerLength,
        int baseStartLine,
        int baseEndLine,
        int baseLineCount,
        int baseOffset,
        int baseLength,
        int separatorLine,
        int separatorOffset,
        int separatorLength,
        int theirsStartLine,
        int theirsEndLine,
        int theirsLineCount,
        int theirsOffset,
        int theirsLength,
        int endMarkerOffset,
        int endMarkerLength,
        int totalOffset,
        int totalLength,
        string oursLabel,
        string? baseLabel,
        string theirsLabel,
        IReadOnlyList<string> oursLines,
        IReadOnlyList<string>? baseLines,
        IReadOnlyList<string> theirsLines)
    {
        StartLine = startLine;
        EndLine = endLine;
        StartMarkerOffset = startMarkerOffset;
        StartMarkerLength = startMarkerLength;
        OursStartLine = oursStartLine;
        OursEndLine = oursEndLine;
        OursLineCount = oursLineCount;
        OursOffset = oursOffset;
        OursLength = oursLength;
        HasBase = hasBase;
        BaseMarkerLine = baseMarkerLine;
        BaseMarkerOffset = baseMarkerOffset;
        BaseMarkerLength = baseMarkerLength;
        BaseStartLine = baseStartLine;
        BaseEndLine = baseEndLine;
        BaseLineCount = baseLineCount;
        BaseOffset = baseOffset;
        BaseLength = baseLength;
        SeparatorLine = separatorLine;
        SeparatorOffset = separatorOffset;
        SeparatorLength = separatorLength;
        TheirsStartLine = theirsStartLine;
        TheirsEndLine = theirsEndLine;
        TheirsLineCount = theirsLineCount;
        TheirsOffset = theirsOffset;
        TheirsLength = theirsLength;
        EndMarkerOffset = endMarkerOffset;
        EndMarkerLength = endMarkerLength;
        TotalOffset = totalOffset;
        TotalLength = totalLength;
        OursLabel = oursLabel;
        BaseLabel = baseLabel;
        TheirsLabel = theirsLabel;
        OursLines = oursLines;
        BaseLines = baseLines;
        TheirsLines = theirsLines;
    }
}

/// <summary>
/// Result of parsing conflict markers in a document.
/// </summary>
public sealed class ConflictParse
{
    public IReadOnlyList<ConflictRegion> Regions { get; }
    public int LineCount { get; }
    public bool IsMalformed { get; }
    public IReadOnlyList<int> MalformedAt { get; }
    public IReadOnlyList<string> Lines { get; }
    public IReadOnlyList<string> Delimiters { get; }

    public ConflictParse(
        IReadOnlyList<ConflictRegion> regions,
        int lineCount,
        bool isMalformed,
        IReadOnlyList<int> malformedAt,
        IReadOnlyList<string> lines,
        IReadOnlyList<string> delimiters)
    {
        Regions = regions;
        LineCount = lineCount;
        IsMalformed = isMalformed;
        MalformedAt = malformedAt;
        Lines = lines;
        Delimiters = delimiters;
    }
}

/// <summary>
/// Parses Git conflict markers (merge, diff3, zdiff3) into structured conflict regions.
/// Matches stock AvaloniaEdit line breaking rules (\r\n, \n, lone \r).
/// </summary>
public static class ConflictMarkerParser
{
    private readonly record struct LineInfo(int Index, int Offset, int Length, int DelimiterLength);

    private enum MarkerKind
    {
        None,
        Start,     // <<<<<<<
        Base,      // |||||||
        Separator, // =======
        End        // >>>>>>>
    }

    /// <summary>
    /// Parses conflict markers in <paramref name="text"/>.
    /// </summary>
    public static ConflictParse Parse(string? text, int markerSize = 7)
    {
        text ??= string.Empty;
        var lines = SplitLines(text);
        int lineCount = lines.Count;

        var regions = new List<ConflictRegion>();
        var malformedAt = new List<int>();

        int i = 0;
        while (i < lineCount)
        {
            var (kind, _) = IdentifyMarker(text, lines[i], markerSize);
            if (kind != MarkerKind.Start)
            {
                i++;
                continue;
            }

            int startLine = i;
            var (_, startLabel) = IdentifyMarker(text, lines[startLine], markerSize);

            int baseMarkerLine = -1;
            string? baseLabel = null;
            int separatorLine = -1;
            int endLine = -1;
            string theirsLabel = string.Empty;

            int state = 1; // 1 = Ours, 2 = Base, 3 = Theirs
            bool isMalformed = false;
            int nextResumeIndex = startLine + 1;

            int j = startLine + 1;
            while (j < lineCount)
            {
                var (mKind, mLabel) = IdentifyMarker(text, lines[j], markerSize);
                if (mKind == MarkerKind.Start)
                {
                    isMalformed = true;
                    nextResumeIndex = j;
                    break;
                }

                if (state == 1)
                {
                    if (mKind == MarkerKind.Base)
                    {
                        baseMarkerLine = j;
                        baseLabel = mLabel;
                        state = 2;
                    }
                    else if (mKind == MarkerKind.Separator)
                    {
                        separatorLine = j;
                        state = 3;
                    }
                    else if (mKind == MarkerKind.End)
                    {
                        isMalformed = true;
                        nextResumeIndex = j + 1;
                        break;
                    }
                }
                else if (state == 2)
                {
                    if (mKind == MarkerKind.Separator)
                    {
                        separatorLine = j;
                        state = 3;
                    }
                    else if (mKind == MarkerKind.Base || mKind == MarkerKind.End)
                    {
                        isMalformed = true;
                        nextResumeIndex = j + 1;
                        break;
                    }
                }
                else if (state == 3)
                {
                    if (mKind == MarkerKind.End)
                    {
                        endLine = j;
                        theirsLabel = mLabel;
                        nextResumeIndex = j + 1;
                        break;
                    }
                    else if (mKind == MarkerKind.Base || mKind == MarkerKind.Separator)
                    {
                        isMalformed = true;
                        nextResumeIndex = j + 1;
                        break;
                    }
                }

                j++;
            }

            if (!isMalformed && endLine < 0)
            {
                isMalformed = true;
                nextResumeIndex = lineCount;
            }

            if (isMalformed)
            {
                malformedAt.Add(startLine);
                i = nextResumeIndex;
            }
            else
            {
                bool hasBase = baseMarkerLine >= 0;
                int startMarkerOffset = lines[startLine].Offset;
                int startMarkerLength = lines[startLine].Length + lines[startLine].DelimiterLength;

                int oursStartLine = startLine + 1;
                int oursEndLine = hasBase ? baseMarkerLine - 1 : separatorLine - 1;
                int oursLineCount = Math.Max(0, oursEndLine - oursStartLine + 1);
                int oursOffset = oursLineCount > 0 ? lines[oursStartLine].Offset : lines[hasBase ? baseMarkerLine : separatorLine].Offset;
                int oursLength = oursLineCount > 0
                    ? (lines[oursEndLine].Offset + lines[oursEndLine].Length + lines[oursEndLine].DelimiterLength) - oursOffset
                    : 0;

                int baseMarkerOffset = hasBase ? lines[baseMarkerLine].Offset : 0;
                int baseMarkerLength = hasBase ? lines[baseMarkerLine].Length + lines[baseMarkerLine].DelimiterLength : 0;
                int baseStartLine = hasBase ? baseMarkerLine + 1 : -1;
                int baseEndLine = hasBase ? separatorLine - 1 : -1;
                int baseLineCount = hasBase ? Math.Max(0, baseEndLine - baseStartLine + 1) : 0;
                int baseOffset = (hasBase && baseLineCount > 0) ? lines[baseStartLine].Offset : (hasBase ? lines[separatorLine].Offset : 0);
                int baseLength = (hasBase && baseLineCount > 0)
                    ? (lines[baseEndLine].Offset + lines[baseEndLine].Length + lines[baseEndLine].DelimiterLength) - baseOffset
                    : 0;

                int separatorOffset = lines[separatorLine].Offset;
                int separatorLength = lines[separatorLine].Length + lines[separatorLine].DelimiterLength;

                int theirsStartLine = separatorLine + 1;
                int theirsEndLine = endLine - 1;
                int theirsLineCount = Math.Max(0, theirsEndLine - theirsStartLine + 1);
                int theirsOffset = theirsLineCount > 0 ? lines[theirsStartLine].Offset : lines[endLine].Offset;
                int theirsLength = theirsLineCount > 0
                    ? (lines[theirsEndLine].Offset + lines[theirsEndLine].Length + lines[theirsEndLine].DelimiterLength) - theirsOffset
                    : 0;

                int endMarkerOffset = lines[endLine].Offset;
                int endMarkerLength = lines[endLine].Length + lines[endLine].DelimiterLength;

                int totalOffset = startMarkerOffset;
                int totalLength = (endMarkerOffset + endMarkerLength) - totalOffset;

                var oursLines = new List<string>(oursLineCount);
                for (int l = oursStartLine; l <= oursEndLine; l++)
                {
                    oursLines.Add(text.Substring(lines[l].Offset, lines[l].Length));
                }

                List<string>? baseLines = null;
                if (hasBase)
                {
                    baseLines = new List<string>(baseLineCount);
                    for (int l = baseStartLine; l <= baseEndLine; l++)
                    {
                        baseLines.Add(text.Substring(lines[l].Offset, lines[l].Length));
                    }
                }

                var theirsLines = new List<string>(theirsLineCount);
                for (int l = theirsStartLine; l <= theirsEndLine; l++)
                {
                    theirsLines.Add(text.Substring(lines[l].Offset, lines[l].Length));
                }

                var region = new ConflictRegion(
                    startLine,
                    endLine,
                    startMarkerOffset,
                    startMarkerLength,
                    oursStartLine,
                    oursEndLine,
                    oursLineCount,
                    oursOffset,
                    oursLength,
                    hasBase,
                    baseMarkerLine,
                    baseMarkerOffset,
                    baseMarkerLength,
                    baseStartLine,
                    baseEndLine,
                    baseLineCount,
                    baseOffset,
                    baseLength,
                    separatorLine,
                    separatorOffset,
                    separatorLength,
                    theirsStartLine,
                    theirsEndLine,
                    theirsLineCount,
                    theirsOffset,
                    theirsLength,
                    endMarkerOffset,
                    endMarkerLength,
                    totalOffset,
                    totalLength,
                    startLabel,
                    baseLabel,
                    theirsLabel,
                    oursLines,
                    baseLines,
                    theirsLines);

                regions.Add(region);
                i = endLine + 1;
            }
        }
        var parsedLines = new string[lineCount];
        var parsedDelimiters = new string[lineCount];
        for (int k = 0; k < lineCount; k++)
        {
            var l = lines[k];
            parsedLines[k] = text.Substring(l.Offset, l.Length);
            parsedDelimiters[k] = l.DelimiterLength > 0 ? text.Substring(l.Offset + l.Length, l.DelimiterLength) : string.Empty;
        }

        return new ConflictParse(regions, lineCount, malformedAt.Count > 0, malformedAt, parsedLines, parsedDelimiters);
    }

    private static (MarkerKind Kind, string Label) IdentifyMarker(string text, LineInfo line, int markerSize)
    {
        if (line.Length < markerSize)
        {
            return (MarkerKind.None, string.Empty);
        }

        char first = text[line.Offset];
        MarkerKind kind = first switch
        {
            '<' => MarkerKind.Start,
            '|' => MarkerKind.Base,
            '=' => MarkerKind.Separator,
            '>' => MarkerKind.End,
            _ => MarkerKind.None
        };

        if (kind == MarkerKind.None)
        {
            return (MarkerKind.None, string.Empty);
        }

        for (int i = 1; i < markerSize; i++)
        {
            if (text[line.Offset + i] != first)
            {
                return (MarkerKind.None, string.Empty);
            }
        }

        if (line.Length == markerSize)
        {
            return (kind, string.Empty);
        }

        if (text[line.Offset + markerSize] == ' ')
        {
            int labelStart = line.Offset + markerSize + 1;
            int labelLen = line.Length - (markerSize + 1);
            string label = labelLen > 0 ? text.Substring(labelStart, labelLen) : string.Empty;
            return (kind, label);
        }

        return (MarkerKind.None, string.Empty);
    }

    private static List<LineInfo> SplitLines(string text)
    {
        var lines = new List<LineInfo>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(new LineInfo(0, 0, 0, 0));
            return lines;
        }

        int offset = 0;
        int length = text.Length;
        int lineIndex = 0;

        while (offset < length)
        {
            int lineStart = offset;
            int nextBreak = -1;
            int delimLen = 0;

            for (int i = offset; i < length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    nextBreak = i;
                    if (i + 1 < length && text[i + 1] == '\n')
                    {
                        delimLen = 2;
                    }
                    else
                    {
                        delimLen = 1;
                    }
                    break;
                }
                if (c == '\n')
                {
                    nextBreak = i;
                    delimLen = 1;
                    break;
                }
            }

            if (nextBreak >= 0)
            {
                lines.Add(new LineInfo(lineIndex++, lineStart, nextBreak - lineStart, delimLen));
                offset = nextBreak + delimLen;
                if (offset == length)
                {
                    lines.Add(new LineInfo(lineIndex, offset, 0, 0));
                    break;
                }
            }
            else
            {
                lines.Add(new LineInfo(lineIndex, lineStart, length - lineStart, 0));
                break;
            }
        }

        return lines;
    }
}
