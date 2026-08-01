using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace PCHelper.Reporting;

/// <summary>Einfache RGB-Farbe fuer den PDF-Export (Werte 0..1).</summary>
public readonly record struct PdfColor(double R, double G, double B)
{
    public static PdfColor FromHex(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        return new PdfColor(c.R / 255.0, c.G / 255.0, c.B / 255.0);
    }

    public string Op => $"{F(R)} {F(G)} {F(B)}";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Erzeugt ein PDF ohne externe Bibliothek.
///
/// Verwendet werden die PDF-Standardschriften Helvetica und Courier, die in
/// jedem Betrachter vorhanden sind - dadurch muss keine Schrift eingebettet
/// werden. Die Textbreiten werden mit Arial gemessen, das metrisch identisch
/// zu Helvetica ist; der Zeilenumbruch stimmt damit exakt.
/// </summary>
public sealed class PdfWriter
{
    // A4 in Punkt
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double MarginLeft = 52;
    private const double MarginRight = 52;
    private const double MarginTop = 56;
    private const double MarginBottom = 58;

    private static readonly PdfColor Text = PdfColor.FromHex("#1A1D23");
    private static readonly PdfColor Dim = PdfColor.FromHex("#5C6572");
    private static readonly PdfColor Line = PdfColor.FromHex("#D5DAE1");
    private static readonly PdfColor Panel = PdfColor.FromHex("#F2F4F7");

    private readonly List<StringBuilder> _pages = new();
    private readonly string _title;
    private readonly string _subtitle;

    private StringBuilder _current = null!;
    private double _y;

    public PdfWriter(string title, string subtitle)
    {
        _title = title;
        _subtitle = subtitle;
        NewPage(firstPage: true);
    }

    public double ContentWidth => PageWidth - MarginLeft - MarginRight;

    // ---------------------------------------------------------------- Inhalte

    public void Heading(string text, int level = 1)
    {
        double size = level switch { 1 => 15, 2 => 11.5, _ => 10 };
        Space(level == 1 ? 14 : 10);
        EnsureSpace(size * 1.6);

        DrawText(text, MarginLeft, size, bold: true, Text);
        _y += size * 1.35;

        if (level == 1)
        {
            _y += 4;
            HorizontalRule();
        }
        _y += 4;
    }

    public void Paragraph(string text, bool muted = false, double size = 9.3, double indent = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        double width = ContentWidth - indent;
        foreach (var line in Wrap(text, width, size, bold: false))
        {
            EnsureSpace(size * 1.45);
            DrawText(line, MarginLeft + indent, size, false, muted ? Dim : Text);
            _y += size * 1.45;
        }
        _y += 3;
    }

    /// <summary>Beschriftung und Wert nebeneinander, wie in einer Datenblattzeile.</summary>
    public void KeyValue(string key, string value)
    {
        const double size = 9.3;
        const double keyWidth = 132;

        var valueLines = Wrap(value, ContentWidth - keyWidth, size, false);
        EnsureSpace(size * 1.45 * Math.Max(1, valueLines.Count));

        DrawText(key, MarginLeft, size, false, Dim);
        for (int i = 0; i < valueLines.Count; i++)
        {
            if (i > 0) EnsureSpace(size * 1.45);
            DrawText(valueLines[i], MarginLeft + keyWidth, size, false, Text);
            _y += size * 1.45;
        }
    }

    /// <summary>Vorformatierter Block (Messwerte, Protokollauszuege) auf grauem Grund.</summary>
    public void Mono(string text, double size = 7.6)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
            lines.AddRange(WrapMono(raw, ContentWidth - 16, size));

        const double pad = 7;
        double lineHeight = size * 1.42;

        int index = 0;
        while (index < lines.Count)
        {
            EnsureSpace(lineHeight * 3 + pad * 2);

            // So viele Zeilen wie auf dieser Seite noch passen.
            double available = PageHeight - MarginBottom - _y - pad * 2;
            int fit = Math.Max(1, (int)(available / lineHeight));
            int take = Math.Min(fit, lines.Count - index);

            double boxHeight = take * lineHeight + pad * 2;
            FillRect(MarginLeft, _y, ContentWidth, boxHeight, Panel);

            double textY = _y + pad;
            for (int i = 0; i < take; i++)
            {
                DrawText(lines[index + i], MarginLeft + 8, size, false, Text, mono: true, overrideY: textY);
                textY += lineHeight;
            }

            _y += boxHeight + 5;
            index += take;
        }
    }

    /// <summary>Farbige Kopfzeile eines Befunds: Einstufung, Titel, Kategorie.</summary>
    public void FindingHeader(string badge, PdfColor badgeColor, string title, string? right)
    {
        const double size = 10.5;
        EnsureSpace(30);

        // Farbstreifen links
        FillRect(MarginLeft, _y - 2, 2.5, 15, badgeColor);

        double x = MarginLeft + 10;
        DrawText(badge.ToUpperInvariant(), x, 7.2, true, badgeColor);
        x += MeasureHelvetica(badge.ToUpperInvariant(), 7.2, true) + 9;

        var titleLines = Wrap(title, PageWidth - MarginRight - x - 90, size, true);
        DrawText(titleLines[0], x, size, true, Text);

        if (!string.IsNullOrWhiteSpace(right))
        {
            double w = MeasureHelvetica(right, 8, false);
            DrawText(right, PageWidth - MarginRight - w, 8, false, Dim);
        }

        _y += size * 1.5;

        for (int i = 1; i < titleLines.Count; i++)
        {
            EnsureSpace(size * 1.4);
            DrawText(titleLines[i], x, size, true, Text);
            _y += size * 1.4;
        }
        _y += 2;
    }

    /// <summary>Tabelle mit fester Spaltenaufteilung; Zellen brechen um.</summary>
    public void Table(string[] headers, IReadOnlyList<string[]> rows, double[] relativeWidths)
    {
        const double size = 8.2;
        double total = relativeWidths.Sum();
        var widths = relativeWidths.Select(w => w / total * ContentWidth).ToArray();
        double lineHeight = size * 1.4;

        void DrawRow(string[] cells, bool bold, PdfColor color)
        {
            var wrapped = new List<string>[cells.Length];
            int maxLines = 1;

            for (int i = 0; i < cells.Length && i < widths.Length; i++)
            {
                wrapped[i] = Wrap(cells[i] ?? "", widths[i] - 8, size, bold);
                maxLines = Math.Max(maxLines, wrapped[i].Count);
            }

            double rowHeight = maxLines * lineHeight + 4;
            EnsureSpace(rowHeight + 2);

            double top = _y;
            for (int i = 0; i < cells.Length && i < widths.Length; i++)
            {
                double x = MarginLeft + widths.Take(i).Sum();
                double cellY = top;
                foreach (var line in wrapped[i])
                {
                    DrawText(line, x, size, bold, color, overrideY: cellY);
                    cellY += lineHeight;
                }
            }

            _y = top + rowHeight;
            FillRect(MarginLeft, _y - 2, ContentWidth, 0.5, Line);
        }

        DrawRow(headers, bold: true, Dim);
        foreach (var row in rows) DrawRow(row, bold: false, Text);

        _y += 6;
    }

    public void Divider()
    {
        Space(6);
        HorizontalRule();
        Space(6);
    }

    public void Space(double height)
    {
        _y += height;
        if (_y > PageHeight - MarginBottom) NewPage();
    }

    // ------------------------------------------------------------- Speichern

    public void Save(string path)
    {
        AddFooters();

        var objects = new List<byte[]>();
        var buffer = new MemoryStream();

        void Obj(string content) => objects.Add(Latin1.GetBytes(content));

        int pageCount = _pages.Count;
        int firstPageObj = 7;

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{firstPageObj + i * 2} 0 R"));

        Obj($"<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        Obj($"<< /Title ({Escape(_title)}) /Producer (PC Helper) /CreationDate ({PdfDate(DateTime.Now)}) >>");

        for (int i = 0; i < pageCount; i++)
        {
            int contentObj = firstPageObj + i * 2 + 1;
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents {contentObj} 0 R >>");

            var streamBytes = Latin1.GetBytes(_pages[i].ToString());
            var head = Latin1.GetBytes($"<< /Length {streamBytes.Length} >>\nstream\n");
            var tail = Latin1.GetBytes("\nendstream");

            var full = new byte[head.Length + streamBytes.Length + tail.Length];
            Buffer.BlockCopy(head, 0, full, 0, head.Length);
            Buffer.BlockCopy(streamBytes, 0, full, head.Length, streamBytes.Length);
            Buffer.BlockCopy(tail, 0, full, head.Length + streamBytes.Length, tail.Length);
            objects.Add(full);
        }

        void Write(string s) { var b = Latin1.GetBytes(s); buffer.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n%âãÏÓ\n");

        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            Write($"{i + 1} 0 obj\n");
            buffer.Write(objects[i], 0, objects[i].Length);
            Write("\nendobj\n");
        }

        long xref = buffer.Position;
        Write($"xref\n0 {objects.Count + 1}\n");
        Write("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        File.WriteAllBytes(path, buffer.ToArray());
    }

    // --------------------------------------------------------------- Intern

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private void NewPage(bool firstPage = false)
    {
        _current = new StringBuilder();
        _pages.Add(_current);
        _y = MarginTop;

        if (firstPage)
        {
            DrawText(_title, MarginLeft, 19, true, Text);
            _y += 24;
            DrawText(_subtitle, MarginLeft, 9, false, Dim);
            _y += 16;
            HorizontalRule();
            _y += 10;
        }
        else
        {
            DrawText(_title, MarginLeft, 8, false, Dim);
            _y += 14;
            HorizontalRule();
            _y += 12;
        }
    }

    private void EnsureSpace(double needed)
    {
        if (_y + needed > PageHeight - MarginBottom) NewPage();
    }

    private void HorizontalRule() => FillRect(MarginLeft, _y, ContentWidth, 0.6, Line);

    private void FillRect(double x, double y, double w, double h, PdfColor color)
        => _current.Append(CultureInfo.InvariantCulture,
            $"{color.Op} rg {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re f\n");

    private void DrawText(string text, double x, double size, bool bold, PdfColor color,
                          bool mono = false, double? overrideY = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        double y = PageHeight - (overrideY ?? _y) - size;
        var font = mono ? "/F3" : bold ? "/F2" : "/F1";

        _current.Append(CultureInfo.InvariantCulture,
            $"BT {font} {N(size)} Tf {color.Op} rg 1 0 0 1 {N(x)} {N(y)} Tm ({Escape(text)}) Tj ET\n");
    }

    private void AddFooters()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            _current = _pages[i];
            var label = $"Seite {i + 1} von {_pages.Count}";
            double w = MeasureHelvetica(label, 7.5, false);

            FillRect(MarginLeft, PageHeight - MarginBottom + 22, ContentWidth, 0.5, Line);
            DrawText(label, PageWidth - MarginRight - w, 7.5, false, Dim,
                     overrideY: PageHeight - MarginBottom + 30);
            DrawText("Erstellt mit PC Helper", MarginLeft, 7.5, false, Dim,
                     overrideY: PageHeight - MarginBottom + 30);
        }
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string PdfDate(DateTime t) => $"D:{t:yyyyMMddHHmmss}";

    /// <summary>Maskiert Sonderzeichen und ersetzt Zeichen ausserhalb von WinAnsi.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var raw in text)
        {
            var c = raw switch
            {
                '–' or '—' => '-',
                '‘' or '’' => '\'',
                '“' or '”' or '„' => '"',
                ' ' => ' ',
                '\t' => ' ',
                _ => raw,
            };

            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append(' ');
            else if (c > 255) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // --------------------------------------------------------- Textmessung

    private static Typeface? _regular, _bold;

    private static Typeface Face(bool bold)
    {
        if (bold) return _bold ??= new Typeface(new FontFamily("Arial"),
            System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);
        return _regular ??= new Typeface(new FontFamily("Arial"),
            System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
    }

    /// <summary>
    /// Breite eines Textes in Punkt. Arial und Helvetica haben identische
    /// Metriken, daher stimmt die Messung mit der Darstellung im PDF ueberein.
    /// </summary>
    internal static double MeasureHelvetica(string text, double size, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var ft = new System.Windows.Media.FormattedText(
                text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                Face(bold), size, Brushes.Black, 1.0);
            return ft.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            // Rueckfallebene, falls keine Schriftmessung moeglich ist.
            return text.Length * size * 0.5;
        }
    }

    private static List<string> Wrap(string text, double maxWidth, double size, bool bold)
    {
        var result = new List<string>();
        if (maxWidth <= 10) { result.Add(text); return result; }

        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (paragraph.Length == 0) { result.Add(""); continue; }

            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (MeasureHelvetica(candidate, size, bold) <= maxWidth)
                {
                    line.Clear().Append(candidate);
                    continue;
                }

                if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); }

                // Einzelnes Wort laenger als die Zeile (z. B. ein Dateipfad).
                var rest = word;
                while (MeasureHelvetica(rest, size, bold) > maxWidth && rest.Length > 1)
                {
                    int take = rest.Length;
                    while (take > 1 && MeasureHelvetica(rest[..take], size, bold) > maxWidth) take--;
                    result.Add(rest[..take]);
                    rest = rest[take..];
                }
                line.Append(rest);
            }

            result.Add(line.ToString());
        }

        if (result.Count == 0) result.Add("");
        return result;
    }

    private static List<string> WrapMono(string text, double maxWidth, double size)
    {
        var result = new List<string>();
        int maxChars = Math.Max(8, (int)(maxWidth / (size * 0.6)));

        if (text.Length <= maxChars) { result.Add(text); return result; }

        var rest = text;
        while (rest.Length > maxChars)
        {
            result.Add(rest[..maxChars]);
            rest = rest[maxChars..];
        }
        if (rest.Length > 0) result.Add(rest);
        return result;
    }
}
