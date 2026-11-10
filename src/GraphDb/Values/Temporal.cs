using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace GraphDb;

/// <summary>A Cypher duration: months, days, seconds and nanoseconds are kept separately (as in Neo4j).</summary>
public sealed record CypherDuration
{
    public long Months { get; }
    public long Days { get; }
    public long Seconds { get; }
    /// <summary>Always in [0, 999_999_999].</summary>
    public int Nanoseconds { get; }

    public CypherDuration(long months, long days, long seconds, long nanoseconds)
    {
        seconds += Math.DivRem(nanoseconds, 1_000_000_000L, out long rem);
        if (rem < 0)
        {
            rem += 1_000_000_000L;
            seconds--;
        }
        Months = months;
        Days = days;
        Seconds = seconds;
        Nanoseconds = (int)rem;
    }

    public static readonly CypherDuration Zero = new(0, 0, 0, 0);

    public static CypherDuration FromTimeSpan(TimeSpan ts) =>
        new(0, 0, ts.Ticks / TimeSpan.TicksPerSecond, ts.Ticks % TimeSpan.TicksPerSecond * 100);

    /// <summary>Approximate length using an average month of 30.436875 days.</summary>
    public TimeSpan ToTimeSpan() =>
        TimeSpan.FromTicks(Months * 26_297_460_000_000L + Days * TimeSpan.TicksPerDay + Seconds * TimeSpan.TicksPerSecond + Nanoseconds / 100);

    internal double ApproxSeconds => Months * 2_629_746.0 + Days * 86_400.0 + Seconds + Nanoseconds / 1e9;

    public CypherDuration Add(CypherDuration o) => new(Months + o.Months, Days + o.Days, Seconds + o.Seconds, (long)Nanoseconds + o.Nanoseconds);
    public CypherDuration Negate() => new(-Months, -Days, -Seconds, -(long)Nanoseconds);

    public CypherDuration Multiply(double f)
    {
        // fractional parts cascade down to smaller units, as in Neo4j
        double months = Months * f;
        long wholeMonths = (long)Math.Truncate(months);
        double days = Days * f + (months - wholeMonths) * 30.436875;
        long wholeDays = (long)Math.Truncate(days);
        double secs = Seconds * f + (days - wholeDays) * 86400 + Nanoseconds * f / 1e9;
        long wholeSecs = (long)Math.Floor(secs);
        return new CypherDuration(wholeMonths, wholeDays, wholeSecs, (long)Math.Round((secs - wholeSecs) * 1e9));
    }

    public override string ToString()
    {
        if (Months == 0 && Days == 0 && Seconds == 0 && Nanoseconds == 0) return "PT0S";
        var sb = new StringBuilder("P");
        long y = Months / 12, m = Months % 12;
        if (y != 0) sb.Append(y).Append('Y');
        if (m != 0) sb.Append(m).Append('M');
        if (Days != 0) sb.Append(Days).Append('D');
        long secs = Seconds;
        int nanos = Nanoseconds;
        if (secs < 0 && nanos > 0)
        {
            secs++;
            nanos = 1_000_000_000 - nanos;
        }
        if (secs != 0 || nanos != 0)
        {
            sb.Append('T');
            long h = secs / 3600, mi = secs % 3600 / 60, s = secs % 60;
            if (h != 0) sb.Append(h).Append('H');
            if (mi != 0) sb.Append(mi).Append('M');
            if (s != 0 || nanos != 0)
            {
                if (s == 0 && Seconds < 0) sb.Append('-');
                sb.Append(s);
                if (nanos != 0) sb.Append('.').Append(nanos.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0'));
                sb.Append('S');
            }
        }
        return sb.ToString();
    }
}

/// <summary>A point in a coordinate reference system (Cartesian 7203/9157 or WGS-84 4326/4979).</summary>
public sealed record CypherPoint(int Srid, double X, double Y, double? Z = null)
{
    public const int Cartesian = 7203, Cartesian3D = 9157, Wgs84 = 4326, Wgs84_3D = 4979;

    public bool IsGeographic => Srid is Wgs84 or Wgs84_3D;
    public string Crs => Srid switch
    {
        Cartesian => "cartesian",
        Cartesian3D => "cartesian-3d",
        Wgs84 => "wgs-84",
        Wgs84_3D => "wgs-84-3d",
        _ => "srid-" + Srid,
    };

    public double Longitude => X;
    public double Latitude => Y;

    /// <summary>Euclidean distance (cartesian) or great-circle distance in metres (WGS-84).</summary>
    public double DistanceTo(CypherPoint o)
    {
        if (o.Srid != Srid) return double.NaN;
        if (!IsGeographic)
        {
            double dx = X - o.X, dy = Y - o.Y, dz = (Z ?? 0) - (o.Z ?? 0);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        const double R = 6_378_140.0;
        double lat1 = Y * Math.PI / 180, lat2 = o.Y * Math.PI / 180;
        double dLat = lat2 - lat1, dLon = (o.X - X) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double d = 2 * R * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        if (Z.HasValue && o.Z.HasValue)
        {
            double dh = Z.Value - o.Z.Value;
            d = Math.Sqrt(d * d + dh * dh);
        }
        return d;
    }

    public override string ToString()
    {
        var sb = new StringBuilder("point({srid:").Append(Srid);
        if (IsGeographic)
        {
            sb.Append(", longitude:").Append(Value.FormatFloat(X)).Append(", latitude:").Append(Value.FormatFloat(Y));
            if (Z.HasValue) sb.Append(", height:").Append(Value.FormatFloat(Z.Value));
        }
        else
        {
            sb.Append(", x:").Append(Value.FormatFloat(X)).Append(", y:").Append(Value.FormatFloat(Y));
            if (Z.HasValue) sb.Append(", z:").Append(Value.FormatFloat(Z.Value));
        }
        return sb.Append("})").ToString();
    }
}

/// <summary>Time zone of a Time/DateTime value: fixed offset or named (IANA / Windows) zone.</summary>
internal sealed class ZoneInfo
{
    private static readonly ConcurrentDictionary<int, ZoneInfo> Offsets = new();
    private static readonly ConcurrentDictionary<string, ZoneInfo> Named = new(StringComparer.OrdinalIgnoreCase);
    public static readonly ZoneInfo Utc = FromOffset(0);

    public readonly string? Name;
    public readonly int FixedOffset;
    public readonly TimeZoneInfo? Tz;

    private ZoneInfo(string? name, int offset, TimeZoneInfo? tz)
    {
        Name = name;
        FixedOffset = offset;
        Tz = tz;
    }

    public static ZoneInfo FromOffset(int seconds) => Offsets.GetOrAdd(seconds, s => new ZoneInfo(null, s, null));

    public static ZoneInfo FromName(string name)
    {
        if (name.Equals("Z", StringComparison.OrdinalIgnoreCase) || name.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return Utc;
        if (Temporal.TryParseOffset(name, out int off)) return FromOffset(off);
        return Named.GetOrAdd(name, n =>
        {
            try
            {
                return new ZoneInfo(n, 0, TimeZoneInfo.FindSystemTimeZoneById(n));
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new CypherRuntimeException($"Unknown time zone '{n}'");
            }
        });
    }

    public int OffsetAt(long utcTicks) =>
        Tz is null ? FixedOffset : (int)Tz.GetUtcOffset(new System.DateTime(Math.Clamp(utcTicks, 0, System.DateTime.MaxValue.Ticks), DateTimeKind.Utc)).TotalSeconds;

    public int OffsetForLocal(long localTicks) =>
        Tz is null ? FixedOffset : (int)Tz.GetUtcOffset(new System.DateTime(Math.Clamp(localTicks, 0, System.DateTime.MaxValue.Ticks), DateTimeKind.Unspecified)).TotalSeconds;

    public static string FormatOffset(int seconds)
    {
        if (seconds == 0) return "Z";
        char sign = seconds < 0 ? '-' : '+';
        int a = Math.Abs(seconds);
        var s = $"{sign}{a / 3600:D2}:{a % 3600 / 60:D2}";
        if (a % 60 != 0) s += $":{a % 60:D2}";
        return s;
    }
}

/// <summary>Parsing, formatting, arithmetic and accessors of temporal values.</summary>
internal static class Temporal
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const long TicksPerDay = TimeSpan.TicksPerDay;

    private static CypherRuntimeException Err(string msg) => new(msg);

    public static DateTimeOffset ToOffset(long utcTicks, ZoneInfo z)
    {
        int off = z.OffsetAt(utcTicks);
        return new DateTimeOffset(utcTicks + off * TicksPerSecond, TimeSpan.FromSeconds(off));
    }

    /// <summary>Local ticks of a DateTime value.</summary>
    public static long LocalTicks(in Value v) => v.Num + v.Zone.OffsetAt(v.Num) * TicksPerSecond;

    /// <summary>Time value normalized to UTC (for comparison).</summary>
    public static long TimeUtcTicks(in Value v)
    {
        long t = v.Num - v.Zone.FixedOffset * TicksPerSecond;
        return ((t % TicksPerDay) + TicksPerDay) % TicksPerDay;
    }

    public static Value MakeDateTime(long localTicks, ZoneInfo zone)
    {
        long utc = localTicks - zone.OffsetForLocal(localTicks) * TicksPerSecond;
        return Value.FromZonedUtc(utc, zone);
    }

    // ------------------------------------------------------------------ formatting

    private static void AppendTime(StringBuilder sb, long ticksOfDay)
    {
        var t = new TimeOnly(ticksOfDay);
        sb.Append(t.Hour.ToString("D2")).Append(':').Append(t.Minute.ToString("D2")).Append(':').Append(t.Second.ToString("D2"));
        long frac = ticksOfDay % TicksPerSecond;
        if (frac != 0) sb.Append('.').Append((frac * 100).ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0'));
    }

    private static void AppendDate(StringBuilder sb, DateOnly d) =>
        sb.Append(d.Year.ToString("D4")).Append('-').Append(d.Month.ToString("D2")).Append('-').Append(d.Day.ToString("D2"));

    public static string Format(in Value v)
    {
        var sb = new StringBuilder();
        switch (v.Kind)
        {
            case ValueKind.Date:
                AppendDate(sb, DateOnly.FromDayNumber((int)v.Num));
                break;
            case ValueKind.LocalTime:
                AppendTime(sb, v.Num);
                break;
            case ValueKind.Time:
                AppendTime(sb, v.Num);
                sb.Append(ZoneInfo.FormatOffset(v.Zone.FixedOffset));
                break;
            case ValueKind.LocalDateTime:
                {
                    var dt = new System.DateTime(v.Num);
                    AppendDate(sb, DateOnly.FromDateTime(dt));
                    sb.Append('T');
                    AppendTime(sb, dt.TimeOfDay.Ticks);
                    break;
                }
            case ValueKind.DateTime:
                {
                    int off = v.Zone.OffsetAt(v.Num);
                    var dt = new System.DateTime(v.Num + off * TicksPerSecond);
                    AppendDate(sb, DateOnly.FromDateTime(dt));
                    sb.Append('T');
                    AppendTime(sb, dt.TimeOfDay.Ticks);
                    sb.Append(ZoneInfo.FormatOffset(off));
                    if (v.Zone.Name != null) sb.Append('[').Append(v.Zone.Name).Append(']');
                    break;
                }
            case ValueKind.Duration:
                return v.AsDuration().ToString();
            case ValueKind.Point:
                return v.AsPoint().ToString();
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ parsing

    internal static bool TryParseOffset(ReadOnlySpan<char> s, out int seconds)
    {
        seconds = 0;
        if (s.Length == 0) return false;
        if (s is "Z" or "z") return true;
        if (s[0] != '+' && s[0] != '-') return false;
        int sign = s[0] == '-' ? -1 : 1;
        var body = s[1..];
        int h, m = 0, sec = 0;
        if (body.Length == 2 && int.TryParse(body, out h)) { }
        else if (body.Length == 4 && int.TryParse(body[..2], out h) && int.TryParse(body[2..], out m)) { }
        else if (body.Length == 5 && body[2] == ':' && int.TryParse(body[..2], out h) && int.TryParse(body[3..], out m)) { }
        else if (body.Length == 8 && body[2] == ':' && body[5] == ':' && int.TryParse(body[..2], out h) && int.TryParse(body[3..5], out m) &&
                 int.TryParse(body[6..], out sec)) { }
        else return false;
        seconds = sign * (h * 3600 + m * 60 + sec);
        return true;
    }

    public static DateOnly ParseDate(ReadOnlySpan<char> s)
    {
        try
        {
            if (s.Length == 4) return new DateOnly(int.Parse(s), 1, 1);
            if (s.Length == 7 && s[4] == '-') return new DateOnly(int.Parse(s[..4]), int.Parse(s[5..7]), 1);
            if (s.Length == 8 && !s.Contains('-')) return new DateOnly(int.Parse(s[..4]), int.Parse(s[4..6]), int.Parse(s[6..8]));
            if (s.Length == 10 && s[4] == '-' && s[7] == '-') return new DateOnly(int.Parse(s[..4]), int.Parse(s[5..7]), int.Parse(s[8..10]));
            if (s.Length == 8 && s[4] == '-') // ordinal yyyy-DDD
                return new DateOnly(int.Parse(s[..4]), 1, 1).AddDays(int.Parse(s[5..]) - 1);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            throw Err($"Invalid date '{s}': {ex.Message}");
        }
        throw Err($"Invalid date '{s}'");
    }

    /// <summary>Parses "HH[:MM[:SS[.fff]]]" or "HHMMSS[.fff]" into ticks of day.</summary>
    public static long ParseLocalTime(ReadOnlySpan<char> s)
    {
        int h, m = 0, sec = 0;
        long frac = 0;
        int dot = s.IndexOfAny('.', ',');
        var main = dot >= 0 ? s[..dot] : s;
        if (dot >= 0)
        {
            var f = s[(dot + 1)..];
            if (f.Length == 0 || f.Length > 9 || !long.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var fv))
                throw Err($"Invalid time '{s}'");
            frac = fv * (long)Math.Pow(10, 9 - f.Length) / 100; // nanos -> ticks
        }
        bool ok = main.Length switch
        {
            2 => int.TryParse(main, out h),
            5 when main[2] == ':' => int.TryParse(main[..2], out h) && int.TryParse(main[3..], out m),
            4 => int.TryParse(main[..2], out h) && int.TryParse(main[2..], out m),
            8 when main[2] == ':' && main[5] == ':' => int.TryParse(main[..2], out h) && int.TryParse(main[3..5], out m) && int.TryParse(main[6..], out sec),
            6 => int.TryParse(main[..2], out h) && int.TryParse(main[2..4], out m) && int.TryParse(main[4..], out sec),
            _ => (h = 0) != 0,
        };
        if (!ok || h > 24 || m > 59 || sec > 59) throw Err($"Invalid time '{s}'");
        return h * TimeSpan.TicksPerHour + m * TimeSpan.TicksPerMinute + sec * TicksPerSecond + frac;
    }

    /// <summary>Splits "time[offset][[zone]]" into its parts.</summary>
    private static (long Ticks, ZoneInfo? Zone) ParseTimeWithZone(ReadOnlySpan<char> s)
    {
        ZoneInfo? zone = null;
        int br = s.IndexOf('[');
        string? named = null;
        if (br >= 0)
        {
            if (s[^1] != ']') throw Err($"Invalid time zone in '{s}'");
            named = s[(br + 1)..^1].ToString();
            s = s[..br];
        }
        int z = s.IndexOfAny('Z', 'z');
        int pm = s.LastIndexOfAny('+', '-');
        int cut = z >= 0 ? z : pm;
        long ticks;
        if (cut > 0)
        {
            if (!TryParseOffset(s[cut..], out int off)) throw Err($"Invalid offset in '{s}'");
            ticks = ParseLocalTime(s[..cut]);
            zone = ZoneInfo.FromOffset(off);
        }
        else ticks = ParseLocalTime(s);
        if (named != null) zone = ZoneInfo.FromName(named);
        return (ticks, zone);
    }

    public static Value ParseTime(string s, bool local)
    {
        var (ticks, zone) = ParseTimeWithZone(s.AsSpan().Trim());
        if (local) return Value.FromLocalTime(new TimeOnly(ticks % TicksPerDay));
        return Value.FromTimeTicks(ticks % TicksPerDay, zone ?? ZoneInfo.Utc);
    }

    public static Value ParseDateTime(string text, bool local)
    {
        var s = text.AsSpan().Trim();
        int t = s.IndexOfAny('T', 't');
        DateOnly date = ParseDate(t < 0 ? s : s[..t]);
        long ticks = 0;
        ZoneInfo? zone = null;
        if (t >= 0) (ticks, zone) = ParseTimeWithZone(s[(t + 1)..]);
        long localTicks = date.ToDateTime(TimeOnly.MinValue).Ticks + ticks;
        if (local) return Value.FromLocalDateTime(new System.DateTime(localTicks));
        zone ??= ZoneInfo.Utc;
        if (zone.Tz is null) return Value.FromZonedUtc(localTicks - zone.FixedOffset * TicksPerSecond, zone);
        return MakeDateTime(localTicks, zone);
    }

    public static CypherDuration ParseDuration(string text)
    {
        var s = text.AsSpan().Trim();
        bool neg = false;
        if (s.Length > 0 && s[0] == '-') { neg = true; s = s[1..]; }
        if (s.Length < 2 || (s[0] != 'P' && s[0] != 'p')) throw Err($"Invalid duration '{text}'");
        s = s[1..];
        double months = 0, days = 0, seconds = 0;
        bool time = false;
        while (s.Length > 0)
        {
            if (s[0] is 'T' or 't') { time = true; s = s[1..]; continue; }
            int i = 0;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or ',' or '-' or '+')) i++;
            if (i == 0 || i >= s.Length) throw Err($"Invalid duration '{text}'");
            double n = double.Parse(s[..i].ToString().Replace(',', '.'), CultureInfo.InvariantCulture);
            char unit = char.ToUpperInvariant(s[i]);
            s = s[(i + 1)..];
            switch (unit)
            {
                case 'Y' when !time: months += n * 12; break;
                case 'M' when !time: months += n; break;
                case 'W' when !time: days += n * 7; break;
                case 'D' when !time: days += n; break;
                case 'H' when time: seconds += n * 3600; break;
                case 'M' when time: seconds += n * 60; break;
                case 'S' when time: seconds += n; break;
                default: throw Err($"Invalid duration '{text}'");
            }
        }
        var d = new CypherDuration(0, 0, 0, 0).Add(new CypherDuration((long)months, 0, 0, 0)).Add(
            new CypherDuration(0, (long)days, 0, 0)).Add(FromSeconds(seconds));
        if (months % 1 != 0) d = d.Add(new CypherDuration(0, 0, 0, 0).Add(FromSeconds((months % 1) * 2_629_746)));
        if (days % 1 != 0) d = d.Add(FromSeconds((days % 1) * 86400));
        return neg ? d.Negate() : d;
    }

    private static CypherDuration FromSeconds(double s)
    {
        long whole = (long)Math.Floor(s);
        return new CypherDuration(0, 0, whole, (long)Math.Round((s - whole) * 1e9));
    }

    // ------------------------------------------------------------------ construction from maps

    private static long? GetInt(IReadOnlyDictionary<string, Value> m, string key)
    {
        foreach (var kv in m)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return kv.Value.IsNull ? null : kv.Value.AsInt64();
        return null;
    }

    private static Value? Get(IReadOnlyDictionary<string, Value> m, string key)
    {
        foreach (var kv in m)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static DateOnly DateFromMap(IReadOnlyDictionary<string, Value> m, DateOnly? fallback)
    {
        if (Get(m, "date") is { IsNull: false } dv) fallback = dv.AsDate();
        var baseDate = fallback ?? new DateOnly(1970, 1, 1);
        int year = (int)(GetInt(m, "year") ?? baseDate.Year);
        if (GetInt(m, "week") is { } week)
        {
            int dow = (int)(GetInt(m, "dayOfWeek") ?? 1);
            var jan4 = new DateOnly(year, 1, 4);
            var monday = jan4.AddDays(-(((int)jan4.DayOfWeek + 6) % 7));
            return monday.AddDays((int)(week - 1) * 7 + dow - 1);
        }
        if (GetInt(m, "ordinalDay") is { } od) return new DateOnly(year, 1, 1).AddDays((int)od - 1);
        if (GetInt(m, "quarter") is { } q)
        {
            int dq = (int)(GetInt(m, "dayOfQuarter") ?? 1);
            return new DateOnly(year, (int)(q - 1) * 3 + 1, 1).AddDays(dq - 1);
        }
        int month = (int)(GetInt(m, "month") ?? (fallback.HasValue ? baseDate.Month : 1));
        int day = (int)(GetInt(m, "day") ?? (fallback.HasValue ? baseDate.Day : 1));
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { throw Err($"Invalid date {year}-{month}-{day}"); }
    }

    private static long TimeFromMap(IReadOnlyDictionary<string, Value> m, long fallback)
    {
        if (Get(m, "time") is { IsNull: false } tv) fallback = tv.AsLocalTime().Ticks;
        var ft = new TimeOnly(fallback);
        long h = GetInt(m, "hour") ?? ft.Hour;
        long mi = GetInt(m, "minute") ?? ft.Minute;
        long s = GetInt(m, "second") ?? ft.Second;
        long ns = 0;
        bool anyFrac = false;
        if (GetInt(m, "millisecond") is { } ms) { ns += ms * 1_000_000; anyFrac = true; }
        if (GetInt(m, "microsecond") is { } us) { ns += us * 1_000; anyFrac = true; }
        if (GetInt(m, "nanosecond") is { } n) { ns += n; anyFrac = true; }
        long frac = anyFrac ? ns / 100 : fallback % TicksPerSecond;
        if (h > 23 || mi > 59 || s > 59) throw Err("Invalid time components");
        return h * TimeSpan.TicksPerHour + mi * TimeSpan.TicksPerMinute + s * TicksPerSecond + frac;
    }

    private static ZoneInfo? ZoneFromMap(IReadOnlyDictionary<string, Value> m)
    {
        var tz = Get(m, "timezone");
        return tz is { IsNull: false } ? ZoneInfo.FromName(tz.Value.AsString()) : null;
    }

    // ------------------------------------------------------------------ constructor functions

    private static readonly System.DateTime Epoch = new(1970, 1, 1);

    public static Value Construct(string kind, Value[] args) => Construct(kind, args, System.DateTime.UtcNow);

    /// <param name="now">Current instant (UTC): the statement clock.</param>
    public static Value Construct(string kind, Value[] args, System.DateTime now)
    {
        if (args.Length == 0)
        {
            return kind switch
            {
                "date" => Value.FromDate(DateOnly.FromDateTime(now)),
                "localtime" => Value.FromLocalTime(TimeOnly.FromDateTime(now)),
                "time" => Value.FromTimeTicks(now.TimeOfDay.Ticks, ZoneInfo.Utc),
                "localdatetime" => Value.FromLocalDateTime(now),
                _ => Value.FromZonedUtc(now.Ticks, ZoneInfo.Utc),
            };
        }
        var a = args[0];
        if (a.IsNull) return Value.Null;
        if (a.Kind == ValueKind.String)
        {
            var s = a.Str;
            return kind switch
            {
                "date" => Value.FromDate(s.Contains('T') ? ParseDateTime(s, true).AsDate() : ParseDate(s.AsSpan().Trim())),
                "localtime" => ParseTime(s, true),
                "time" => ParseTime(s, false),
                "localdatetime" => ParseDateTime(s, true),
                _ => ParseDateTime(s, false),
            };
        }
        if (a.IsTemporal) return Convert(kind, a, null);
        if (a.Kind != ValueKind.Map) throw Err($"{kind}() expects a string, a map or a temporal value");
        var m = a.MapItems;
        switch (kind)
        {
            case "date":
                return Value.FromDate(DateFromMap(m, null));
            case "localtime":
                return Value.FromLocalTime(new TimeOnly(TimeFromMap(m, 0)));
            case "time":
                {
                    var zone = ZoneFromMap(m) ?? (Get(m, "time") is { Kind: ValueKind.Time } t0 ? t0.Zone : ZoneInfo.Utc);
                    return Value.FromTimeTicks(TimeFromMap(m, 0), zone.Tz is null ? zone : ZoneInfo.FromOffset(zone.OffsetAt(now.Ticks)));
                }
            case "localdatetime":
                {
                    var dt = Get(m, "datetime");
                    DateOnly? fd = dt is { IsNull: false } ? dt.Value.AsDate() : null;
                    long ft = dt is { IsNull: false } ? dt.Value.AsLocalTime().Ticks : 0;
                    return Value.FromLocalDateTime(DateFromMap(m, fd).ToDateTime(TimeOnly.MinValue).AddTicks(TimeFromMap(m, ft)));
                }
            default:
                {
                    if (GetInt(m, "epochMillis") is { } ms)
                        return Value.FromZonedUtc(Epoch.Ticks + ms * TimeSpan.TicksPerMillisecond, ZoneFromMap(m) ?? ZoneInfo.Utc);
                    if (GetInt(m, "epochSeconds") is { } es)
                        return Value.FromZonedUtc(Epoch.Ticks + es * TicksPerSecond + (GetInt(m, "nanosecond") ?? 0) / 100, ZoneFromMap(m) ?? ZoneInfo.Utc);
                    var dt = Get(m, "datetime");
                    DateOnly? fd = dt is { IsNull: false } ? dt.Value.AsDate() : null;
                    long ft = dt is { IsNull: false } ? dt.Value.AsLocalTime().Ticks : 0;
                    var zone = ZoneFromMap(m) ?? (dt is { Kind: ValueKind.DateTime } d0 ? d0.Zone : ZoneInfo.Utc);
                    long local = DateFromMap(m, fd).ToDateTime(TimeOnly.MinValue).Ticks + TimeFromMap(m, ft);
                    return MakeDateTime(local, zone);
                }
        }
    }

    /// <summary>Converts between temporal kinds (e.g. date(datetimeValue)).</summary>
    public static Value Convert(string kind, in Value v, ZoneInfo? zone)
    {
        long local = v.Kind switch
        {
            ValueKind.Date => v.AsDate().ToDateTime(TimeOnly.MinValue).Ticks,
            ValueKind.LocalDateTime => v.Num,
            ValueKind.DateTime => LocalTicks(v),
            ValueKind.LocalTime or ValueKind.Time => v.Num,
            _ => throw Err($"Cannot convert {v.Kind} to {kind}"),
        };
        bool hasDate = v.Kind is ValueKind.Date or ValueKind.LocalDateTime or ValueKind.DateTime;
        switch (kind)
        {
            case "date":
                if (!hasDate) throw Err($"Cannot convert {v.Kind} to date");
                return Value.FromDate(DateOnly.FromDateTime(new System.DateTime(local)));
            case "localtime":
                return Value.FromLocalTime(new TimeOnly(local % TicksPerDay));
            case "time":
                {
                    var z = zone ?? (v.Kind is ValueKind.Time or ValueKind.DateTime ? v.Zone : ZoneInfo.Utc);
                    int off = v.Kind == ValueKind.DateTime ? v.Zone.OffsetAt(v.Num) : z.OffsetAt(0);
                    return Value.FromTimeTicks(local % TicksPerDay, ZoneInfo.FromOffset(off));
                }
            case "localdatetime":
                if (!hasDate) throw Err($"Cannot convert {v.Kind} to localdatetime");
                return Value.FromLocalDateTime(new System.DateTime(local));
            default:
                if (!hasDate) throw Err($"Cannot convert {v.Kind} to datetime");
                if (v.Kind == ValueKind.DateTime && zone is null) return v;
                return MakeDateTime(local, zone ?? ZoneInfo.Utc);
        }
    }

    public static Value ConstructDuration(in Value a)
    {
        if (a.IsNull) return Value.Null;
        if (a.Kind == ValueKind.String) return Value.FromDuration(ParseDuration(a.Str));
        if (a.Kind == ValueKind.Duration) return a;
        if (a.Kind != ValueKind.Map) throw Err("duration() expects a string or a map");
        var m = a.MapItems;
        double D(string k) => Get(m, k) is { IsNull: false } v ? v.AsDouble() : 0;
        double months = D("years") * 12 + D("quarters") * 3 + D("months");
        double days = D("weeks") * 7 + D("days");
        double secs = D("hours") * 3600 + D("minutes") * 60 + D("seconds");
        double nanos = D("milliseconds") * 1e6 + D("microseconds") * 1e3 + D("nanoseconds");
        var d = new CypherDuration((long)Math.Truncate(months), (long)Math.Truncate(days), 0, 0)
            .Add(FromSeconds((months % 1) * 2_629_746 + (days % 1) * 86400 + secs))
            .Add(new CypherDuration(0, 0, 0, (long)nanos));
        return Value.FromDuration(d);
    }

    // ------------------------------------------------------------------ arithmetic

    private static long AddToLocal(long localTicks, CypherDuration d, bool dateOnly)
    {
        var dt = new System.DateTime(localTicks);
        if (d.Months != 0) dt = dt.AddMonths(checked((int)d.Months));
        if (d.Days != 0) dt = dt.AddDays(d.Days);
        if (dateOnly)
        {
            long extra = d.Seconds / 86400;
            return dt.AddDays(extra).Ticks;
        }
        return dt.Ticks + d.Seconds * TicksPerSecond + d.Nanoseconds / 100;
    }

    public static Value AddDuration(in Value t, CypherDuration d)
    {
        try
        {
            switch (t.Kind)
            {
                case ValueKind.Date:
                    return Value.FromDate(DateOnly.FromDateTime(new System.DateTime(AddToLocal(t.AsDate().ToDateTime(TimeOnly.MinValue).Ticks, d, true))));
                case ValueKind.LocalDateTime:
                    return Value.FromLocalDateTime(new System.DateTime(AddToLocal(t.Num, d, false)));
                case ValueKind.DateTime:
                    return MakeDateTime(AddToLocal(LocalTicks(t), d, false), t.Zone);
                case ValueKind.LocalTime:
                case ValueKind.Time:
                    {
                        long ticks = ((t.Num + d.Seconds * TicksPerSecond + d.Nanoseconds / 100) % TicksPerDay + TicksPerDay) % TicksPerDay;
                        return t.Kind == ValueKind.LocalTime ? Value.FromLocalTime(new TimeOnly(ticks)) : Value.FromTimeTicks(ticks, t.Zone);
                    }
                case ValueKind.Duration:
                    return Value.FromDuration(t.AsDuration().Add(d));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Err("Temporal arithmetic overflow");
        }
        throw Err($"Cannot add a duration to {t.Kind}");
    }

    private static (long Local, bool HasDate, bool HasTime) Instant(in Value v) => v.Kind switch
    {
        ValueKind.Date => (v.AsDate().ToDateTime(TimeOnly.MinValue).Ticks, true, false),
        ValueKind.LocalDateTime => (v.Num, true, true),
        ValueKind.DateTime => (v.Num, true, true), // compare in UTC
        ValueKind.LocalTime => (v.Num, false, true),
        ValueKind.Time => (TimeUtcTicks(v), false, true),
        _ => throw Err($"Expected a temporal value, got {v.Kind}"),
    };

    public static Value Between(in Value a, in Value b, string unit)
    {
        if (a.IsNull || b.IsNull) return Value.Null;
        var (x, xd, _) = Instant(a);
        var (y, yd, _) = Instant(b);
        bool dates = xd && yd;
        if (!dates)
        {
            x %= TicksPerDay;
            y %= TicksPerDay;
        }
        long diff = y - x;
        switch (unit)
        {
            case "seconds":
                return Value.FromDuration(new CypherDuration(0, 0, diff / TicksPerSecond, diff % TicksPerSecond * 100));
            case "days":
                return Value.FromDuration(new CypherDuration(0, diff / TicksPerDay, 0, 0));
            case "months":
            case "between":
                {
                    if (!dates) return Value.FromDuration(new CypherDuration(0, 0, diff / TicksPerSecond, diff % TicksPerSecond * 100));
                    var dx = new System.DateTime(x);
                    var dy = new System.DateTime(y);
                    int months = (dy.Year - dx.Year) * 12 + dy.Month - dx.Month;
                    if (months > 0 && dx.AddMonths(months) > dy) months--;
                    if (months < 0 && dx.AddMonths(months) < dy) months++;
                    if (unit == "months") return Value.FromDuration(new CypherDuration(months, 0, 0, 0));
                    long rest = dy.Ticks - dx.AddMonths(months).Ticks;
                    long days = rest / TicksPerDay;
                    long rem = rest % TicksPerDay;
                    return Value.FromDuration(new CypherDuration(months, days, rem / TicksPerSecond, rem % TicksPerSecond * 100));
                }
        }
        throw Err($"Unknown unit {unit}");
    }

    // ------------------------------------------------------------------ truncation

    public static Value Truncate(string kind, string unit, in Value v)
    {
        if (v.IsNull) return Value.Null;
        long local = v.Kind switch
        {
            ValueKind.DateTime => LocalTicks(v),
            ValueKind.Date => v.AsDate().ToDateTime(TimeOnly.MinValue).Ticks,
            ValueKind.LocalDateTime or ValueKind.LocalTime or ValueKind.Time => v.Num,
            _ => throw Err($"Cannot truncate {v.Kind}"),
        };
        var dt = new System.DateTime(local);
        System.DateTime r = unit.ToLowerInvariant() switch
        {
            "millennium" => new System.DateTime(dt.Year / 1000 * 1000 == 0 ? 1 : dt.Year / 1000 * 1000, 1, 1),
            "century" => new System.DateTime(Math.Max(1, dt.Year / 100 * 100), 1, 1),
            "decade" => new System.DateTime(Math.Max(1, dt.Year / 10 * 10), 1, 1),
            "year" => new System.DateTime(dt.Year, 1, 1),
            "quarter" => new System.DateTime(dt.Year, (dt.Month - 1) / 3 * 3 + 1, 1),
            "month" => new System.DateTime(dt.Year, dt.Month, 1),
            "week" => dt.Date.AddDays(-(((int)dt.DayOfWeek + 6) % 7)),
            "day" => dt.Date,
            "hour" => new System.DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0),
            "minute" => new System.DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0),
            "second" => new System.DateTime(dt.Ticks - dt.Ticks % TicksPerSecond),
            "millisecond" => new System.DateTime(dt.Ticks - dt.Ticks % TimeSpan.TicksPerMillisecond),
            "microsecond" => new System.DateTime(dt.Ticks - dt.Ticks % 10),
            _ => throw Err($"Unknown truncation unit '{unit}'"),
        };
        var tmp = Value.FromLocalDateTime(r);
        if (v.Kind == ValueKind.DateTime && kind == "datetime") return MakeDateTime(r.Ticks, v.Zone);
        if (kind == "time") return Value.FromTimeTicks(r.TimeOfDay.Ticks, v.Kind is ValueKind.Time or ValueKind.DateTime ? ZoneInfo.FromOffset(v.Zone.OffsetAt(v.Num)) : ZoneInfo.Utc);
        if (kind == "localtime") return Value.FromLocalTime(TimeOnly.FromTimeSpan(r.TimeOfDay));
        return Convert(kind, tmp, null);
    }

    // ------------------------------------------------------------------ accessors

    public static Value Component(in Value v, string key)
    {
        if (v.Kind == ValueKind.Duration) return DurationComponent(v.AsDuration(), key);
        if (v.Kind == ValueKind.Point) return PointComponent(v.AsPoint(), key);
        long local = v.Kind switch
        {
            ValueKind.DateTime => LocalTicks(v),
            ValueKind.Date => v.AsDate().ToDateTime(TimeOnly.MinValue).Ticks,
            _ => v.Num,
        };
        var dt = new System.DateTime(local);
        bool hasDate = v.Kind is ValueKind.Date or ValueKind.LocalDateTime or ValueKind.DateTime;
        bool hasTime = v.Kind is not ValueKind.Date;
        long frac = local % TicksPerSecond;
        switch (key.ToLowerInvariant())
        {
            case "year" when hasDate: return dt.Year;
            case "quarter" when hasDate: return (dt.Month - 1) / 3 + 1;
            case "month" when hasDate: return dt.Month;
            case "week" when hasDate: return ISOWeek.GetWeekOfYear(dt);
            case "weekyear" when hasDate: return ISOWeek.GetYear(dt);
            case "day" when hasDate: return dt.Day;
            case "ordinalday" when hasDate: return dt.DayOfYear;
            case "dayofweek" or "weekday" when hasDate: return ((int)dt.DayOfWeek + 6) % 7 + 1;
            case "dayofquarter" when hasDate: return (dt - new System.DateTime(dt.Year, (dt.Month - 1) / 3 * 3 + 1, 1)).Days + 1;
            case "hour" when hasTime: return dt.Hour;
            case "minute" when hasTime: return dt.Minute;
            case "second" when hasTime: return dt.Second;
            case "millisecond" when hasTime: return frac / TimeSpan.TicksPerMillisecond;
            case "microsecond" when hasTime: return frac / 10;
            case "nanosecond" when hasTime: return frac * 100;
            case "timezone" when v.Kind is ValueKind.DateTime or ValueKind.Time:
                return v.Zone.Name ?? ZoneInfo.FormatOffset(v.Zone.OffsetAt(v.Num));
            case "offset" when v.Kind is ValueKind.DateTime or ValueKind.Time:
                return ZoneInfo.FormatOffset(v.Zone.OffsetAt(v.Kind == ValueKind.DateTime ? v.Num : 0));
            case "offsetminutes" when v.Kind is ValueKind.DateTime or ValueKind.Time:
                return v.Zone.OffsetAt(v.Kind == ValueKind.DateTime ? v.Num : 0) / 60;
            case "offsetseconds" when v.Kind is ValueKind.DateTime or ValueKind.Time:
                return v.Zone.OffsetAt(v.Kind == ValueKind.DateTime ? v.Num : 0);
            case "epochseconds" when v.Kind == ValueKind.DateTime: return (v.Num - Epoch.Ticks) / TicksPerSecond;
            case "epochmillis" when v.Kind == ValueKind.DateTime: return (v.Num - Epoch.Ticks) / TimeSpan.TicksPerMillisecond;
        }
        throw Err($"{v.Kind} has no component '{key}'");
    }

    private static Value DurationComponent(CypherDuration d, string key) => key.ToLowerInvariant() switch
    {
        "years" => d.Months / 12,
        "quarters" => d.Months / 3,
        "months" => d.Months,
        "weeks" => d.Days / 7,
        "days" => d.Days,
        "hours" => d.Seconds / 3600,
        "minutes" => d.Seconds / 60,
        "seconds" => d.Seconds,
        "milliseconds" => d.Seconds * 1000 + d.Nanoseconds / 1_000_000,
        "microseconds" => d.Seconds * 1_000_000 + d.Nanoseconds / 1000,
        "nanoseconds" => d.Seconds * 1_000_000_000 + d.Nanoseconds,
        "quartersofyear" => d.Months % 12 / 3,
        "monthsofquarter" => d.Months % 3,
        "monthsofyear" => d.Months % 12,
        "daysofweek" => d.Days % 7,
        "minutesofhour" => d.Seconds / 60 % 60,
        "secondsofminute" => d.Seconds % 60,
        "millisecondsofsecond" => d.Nanoseconds / 1_000_000,
        "microsecondsofsecond" => d.Nanoseconds / 1000,
        "nanosecondsofsecond" => d.Nanoseconds,
        _ => throw Err($"Duration has no component '{key}'"),
    };

    private static Value PointComponent(CypherPoint p, string key) => key.ToLowerInvariant() switch
    {
        "x" => p.X,
        "y" => p.Y,
        "z" => p.Z.HasValue ? Value.FromFloat(p.Z.Value) : Value.Null,
        "longitude" when p.IsGeographic => p.X,
        "latitude" when p.IsGeographic => p.Y,
        "height" when p.IsGeographic => p.Z.HasValue ? Value.FromFloat(p.Z.Value) : Value.Null,
        "srid" => p.Srid,
        "crs" => p.Crs,
        _ => throw Err($"Point has no component '{key}'"),
    };

    public static Value MakePoint(in Value arg)
    {
        if (arg.IsNull) return Value.Null;
        if (arg.Kind != ValueKind.Map) throw Err("point() expects a map");
        var m = arg.MapItems;
        double? Num(string k) => Get(m, k) is { IsNull: false } v ? v.AsDouble() : null;
        string? crs = Get(m, "crs") is { IsNull: false } c ? c.AsString().ToLowerInvariant() : null;
        int? srid = Get(m, "srid") is { IsNull: false } s ? (int)s.AsInt64() : null;
        if (Num("longitude") is { } lon && Num("latitude") is { } lat)
        {
            var h = Num("height");
            return Value.FromPoint(new CypherPoint(srid ?? (h.HasValue || crs == "wgs-84-3d" ? CypherPoint.Wgs84_3D : CypherPoint.Wgs84), lon, lat, h));
        }
        if (Num("x") is { } x && Num("y") is { } y)
        {
            var z = Num("z");
            int sr = srid ?? crs switch
            {
                "wgs-84" => CypherPoint.Wgs84,
                "wgs-84-3d" => CypherPoint.Wgs84_3D,
                "cartesian-3d" => CypherPoint.Cartesian3D,
                _ => z.HasValue ? CypherPoint.Cartesian3D : CypherPoint.Cartesian,
            };
            return Value.FromPoint(new CypherPoint(sr, x, y, z));
        }
        throw Err("point() requires x/y or longitude/latitude");
    }
}
