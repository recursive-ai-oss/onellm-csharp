using System.Globalization;
using System.Text;

namespace OneLlm.Templating;

/// <summary>Go reference-time layouts (<c>2006-01-02T15:04:05Z07:00</c>) for <c>date</c> and for printing time values.</summary>
internal static class GoTime
{
    private static readonly string[] Months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    private static readonly string[] Days = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    /// <summary>The text Go prints for a time value: <c>2006-01-02 15:04:05.999999999 -0700 MST</c>.</summary>
    public static string ToGoString(DateTimeOffset t) => Format(t, "2006-01-02 15:04:05.999999999 -0700 MST");

    public static string Format(DateTimeOffset t, string layout)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < layout.Length)
        {
            int rest = layout.Length - i;
            char c = layout[i];
            bool Has(string s) => string.CompareOrdinal(layout, i, s, 0, s.Length) == 0;

            string? tok = null;
            switch (c)
            {
                case 'J':
                    if (Has("January")) { tok = "January"; }
                    else if (Has("Jan")) { tok = "Jan"; }
                    break;
                case 'M':
                    if (Has("Monday")) { tok = "Monday"; }
                    else if (Has("Mon")) { tok = "Mon"; }
                    else if (Has("MST")) { tok = "MST"; }
                    break;
                case '0':
                    foreach (string s in new[] { "002", "01", "02", "03", "04", "05", "06" })
                    {
                        if (Has(s)) { tok = s; break; }
                    }

                    break;
                case '1':
                    tok = Has("15") ? "15" : "1";
                    break;
                case '2':
                    tok = Has("2006") ? "2006" : "2";
                    break;
                case '_':
                    if (Has("_2006"))
                    {
                        // underscore followed by a year: the underscore is literal
                        sb.Append('_');
                        i++;
                        continue;
                    }

                    if (Has("__2")) { tok = "__2"; }
                    else if (Has("_2")) { tok = "_2"; }
                    break;
                case '3': tok = "3"; break;
                case '4': tok = "4"; break;
                case '5': tok = "5"; break;
                case 'P':
                    if (Has("PM")) { tok = "PM"; }
                    break;
                case 'p':
                    if (Has("pm")) { tok = "pm"; }
                    break;
                case '-':
                    foreach (string s in new[] { "-07:00:00", "-070000", "-07:00", "-0700", "-07" })
                    {
                        if (Has(s)) { tok = s; break; }
                    }

                    break;
                case 'Z':
                    foreach (string s in new[] { "Z07:00:00", "Z070000", "Z07:00", "Z0700", "Z07" })
                    {
                        if (Has(s)) { tok = s; break; }
                    }

                    break;
                case '.':
                case ',':
                    if (rest >= 2 && (layout[i + 1] == '0' || layout[i + 1] == '9'))
                    {
                        char d = layout[i + 1];
                        int j = i + 1;
                        while (j < layout.Length && layout[j] == d)
                        {
                            j++;
                        }

                        // A fraction only counts when no digit follows.
                        if (j >= layout.Length || !char.IsAsciiDigit(layout[j]))
                        {
                            AppendFraction(sb, t, c, d, j - i - 1);
                            i = j;
                            continue;
                        }
                    }

                    break;
            }

            // "Jan"/"Mon" are tokens only when not followed by a lower-case letter ("January" and "Monday" were tried first).
            if ((tok == "Jan" || tok == "Mon") && i + 3 < layout.Length && char.IsAsciiLetterLower(layout[i + 3]))
            {
                tok = null;
            }

            if (tok is null)
            {
                sb.Append(c);
                i++;
                continue;
            }

            Append(sb, t, tok);
            i += tok.Length;
        }

        return sb.ToString();
    }

    private static void AppendFraction(StringBuilder sb, DateTimeOffset t, char sep, char kind, int digits)
    {
        long ticks = t.Ticks % TimeSpan.TicksPerSecond; // 100 ns units
        string nanos = (ticks * 100).ToString("D9", CultureInfo.InvariantCulture);
        string frac = digits >= 9 ? nanos + new string('0', digits - 9) : nanos.Substring(0, digits);
        if (kind == '9')
        {
            frac = frac.TrimEnd('0');
            if (frac.Length == 0)
            {
                return;
            }
        }

        sb.Append(sep).Append(frac);
    }

    private static void Append(StringBuilder sb, DateTimeOffset t, string tok)
    {
        switch (tok)
        {
            case "January": sb.Append(Months[t.Month - 1]); break;
            case "Jan": sb.Append(Months[t.Month - 1], 0, 3); break;
            case "Monday": sb.Append(Days[(int)t.DayOfWeek]); break;
            case "Mon": sb.Append(Days[(int)t.DayOfWeek], 0, 3); break;
            case "MST":
                sb.Append(t.Offset == TimeSpan.Zero ? "UTC" : Offset(t.Offset, false, false, true));
                break;
            case "01": sb.Append(t.Month.ToString("D2", CultureInfo.InvariantCulture)); break;
            case "02": sb.Append(t.Day.ToString("D2", CultureInfo.InvariantCulture)); break;
            case "03": sb.Append(Hour12(t).ToString("D2", CultureInfo.InvariantCulture)); break;
            case "04": sb.Append(t.Minute.ToString("D2", CultureInfo.InvariantCulture)); break;
            case "05": sb.Append(t.Second.ToString("D2", CultureInfo.InvariantCulture)); break;
            case "06": sb.Append((t.Year % 100).ToString("D2", CultureInfo.InvariantCulture)); break;
            case "002": sb.Append(t.DayOfYear.ToString("D3", CultureInfo.InvariantCulture)); break;
            case "15": sb.Append(t.Hour.ToString("D2", CultureInfo.InvariantCulture)); break;
            case "1": sb.Append(t.Month.ToString(CultureInfo.InvariantCulture)); break;
            case "2006": sb.Append(t.Year.ToString("D4", CultureInfo.InvariantCulture)); break;
            case "2": sb.Append(t.Day.ToString(CultureInfo.InvariantCulture)); break;
            case "_2": sb.Append(t.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2)); break;
            case "__2": sb.Append(t.DayOfYear.ToString(CultureInfo.InvariantCulture).PadLeft(3)); break;
            case "3": sb.Append(Hour12(t).ToString(CultureInfo.InvariantCulture)); break;
            case "4": sb.Append(t.Minute.ToString(CultureInfo.InvariantCulture)); break;
            case "5": sb.Append(t.Second.ToString(CultureInfo.InvariantCulture)); break;
            case "PM": sb.Append(t.Hour >= 12 ? "PM" : "AM"); break;
            case "pm": sb.Append(t.Hour >= 12 ? "pm" : "am"); break;
            case "-070000": sb.Append(Offset(t.Offset, false, true, true)); break;
            case "-07:00:00": sb.Append(Offset(t.Offset, true, true, true)); break;
            case "-0700": sb.Append(Offset(t.Offset, false, false, true)); break;
            case "-07:00": sb.Append(Offset(t.Offset, true, false, true)); break;
            case "-07": sb.Append(OffsetHoursOnly(t.Offset)); break;
            case "Z070000": sb.Append(t.Offset == TimeSpan.Zero ? "Z" : Offset(t.Offset, false, true, true)); break;
            case "Z07:00:00": sb.Append(t.Offset == TimeSpan.Zero ? "Z" : Offset(t.Offset, true, true, true)); break;
            case "Z0700": sb.Append(t.Offset == TimeSpan.Zero ? "Z" : Offset(t.Offset, false, false, true)); break;
            case "Z07:00": sb.Append(t.Offset == TimeSpan.Zero ? "Z" : Offset(t.Offset, true, false, true)); break;
            case "Z07": sb.Append(t.Offset == TimeSpan.Zero ? "Z" : OffsetHoursOnly(t.Offset)); break;
        }
    }

    private static int Hour12(DateTimeOffset t)
    {
        int h = t.Hour % 12;
        return h == 0 ? 12 : h;
    }

    private static string OffsetHoursOnly(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("D2", CultureInfo.InvariantCulture);

    private static string Offset(TimeSpan o, bool colon, bool seconds, bool _)
    {
        var sb = new StringBuilder();
        sb.Append(o < TimeSpan.Zero ? '-' : '+');
        TimeSpan a = o.Duration();
        sb.Append(a.Hours.ToString("D2", CultureInfo.InvariantCulture));
        if (colon)
        {
            sb.Append(':');
        }

        sb.Append(a.Minutes.ToString("D2", CultureInfo.InvariantCulture));
        if (seconds)
        {
            if (colon)
            {
                sb.Append(':');
            }

            sb.Append(a.Seconds.ToString("D2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
