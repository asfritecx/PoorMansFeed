using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PoorMansFeed.Core.ModSecurity;

/// <summary>
/// The rules land in a server's configuration and nothing in them can be escaped the same way for ModSecurity v2 and
/// v3, so a location or file name outside its allowlist is refused and the msg's other characters become spaces.
/// </summary>
public static partial class ModSecurityRules
{
    private const int MaxFolderLength = 1_024;
    private const int MaxMsgLength = 120;
    private const string HttpsScheme = "https://";

    // \z, not $: in .NET $ also matches before a final "\n", which would let a line break into the configuration
    [GeneratedRegex(@"^[A-Za-z0-9_-][A-Za-z0-9._-]{0,199}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^/[A-Za-z0-9._/-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex FolderPattern();

    // sig as a whole run of ASCII letters in a decoded name, so a garbled sig such as "sig%20" or "x?sig" still counts
    [GeneratedRegex("(?<![A-Za-z])[Ss][Ii][Gg](?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex SigPartPattern();

    // Microsoft's documented se forms, for ske too. An offset may reach 23:59, beyond DateTimeOffset's range, so it is
    // read by hand
    [GeneratedRegex(@"^(?<year>[0-9]{4})-(?<month>[0-9]{2})-(?<day>[0-9]{2})"
                    + @"(?:T(?<hour>[0-9]{2}):(?<minute>[0-9]{2})"
                    + @"(?::(?<second>[0-9]{2})(?:\.(?<fraction>[0-9]{1,7}))?)?"
                    + @"(?:Z|(?<sign>[+-])(?<offsetHour>[0-9]{2}):(?<offsetMinute>[0-9]{2})))?\z",
                    RegexOptions.CultureInvariant)]
    private static partial Regex ExpiryPattern();

    public static ModSecurityRuleSet Build(ModSecurityRuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<string>? files = request.FileNames;
        if (files is null || files.Count == 0)
        {
            return Failed(ModSecurityRuleFailure.NoFiles);
        }

        foreach (string? file in files)
        {
            if (file is null || !IsFileName(file))
            {
                return Failed(ModSecurityRuleFailure.FileNameInvalid);
            }
        }

        ModSecurityRuleFailure idFailure = RuleIdsFor(request, files.Count, out int[] ids);
        if (idFailure != ModSecurityRuleFailure.None)
        {
            return Failed(idFailure);
        }

        ModSecurityRuleFailure locationFailure = ParseLocation(request.Location, out string basePath, out string query);
        if (locationFailure != ModSecurityRuleFailure.None)
        {
            return Failed(locationFailure);
        }

        string label = Clean(request.FeedLabel ?? "");
        // deny with 444 gives Apache a 500 page and drop alone gives nginx a 403 page, so Close pairs drop with 444
        string actions = request.Action switch
        {
            ModSecurityAction.Deny => "deny,status:403",
            ModSecurityAction.Close => "drop,status:444",
            _ => "pass",
        };
        var rules = new List<string>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            string location = basePath + "/" + file + (query.Length > 0 ? "?" + query : "");
            string id = ids[i].ToString(CultureInfo.InvariantCulture);
            rules.Add($"SecRule REMOTE_ADDR \"@ipMatchFromFile {location}\" "
                      + $"\"id:{id},phase:1,{actions},log,t:none,msg:'{Msg(label, file)}'\"");
        }

        return new ModSecurityRuleSet(ModSecurityRuleFailure.None, rules, string.Join("\n", rules) + "\n");
    }

    public static ModSecurityRuleFailure CheckLocation(string location) => ParseLocation(location, out _, out _);

    /// <summary>
    /// The SAS's expiry as UTC (its delegation key's, when that comes first), reading no clock. It is null only when
    /// <see cref="CheckLocation"/> refuses the location or the location holds no SAS.
    /// </summary>
    public static DateTimeOffset? SasExpiry(string location) => ReadSasExpiry(location, out _);

    /// <summary>
    /// True when the expiry <see cref="SasExpiry"/> reads gives a time, false when it gives a date alone, and null
    /// exactly when <see cref="SasExpiry"/> is null.
    /// </summary>
    public static bool? SasExpiryHasTime(string location) =>
        ReadSasExpiry(location, out bool hasTime) is null ? null : hasTime;

    internal static bool IsFileName(string name) => FileNamePattern().IsMatch(name);

    private static ModSecurityRuleSet Failed(ModSecurityRuleFailure failure) => new(failure, [], "");

    private static DateTimeOffset? ReadSasExpiry(string location, out bool hasTime)
    {
        hasTime = false;
        if (ParseLocation(location, out _, out string query) != ModSecurityRuleFailure.None)
        {
            return null;
        }

        List<QueryParameter> parameters = Parameters(query);
        return HasSig(parameters) && TryReadExpiry(parameters, out DateTimeOffset expiry, out hasTime) ? expiry : null;
    }

    private static ModSecurityRuleFailure RuleIdsFor(ModSecurityRuleRequest request, int count, out int[] ids)
    {
        ids = [];
        if (request.RuleIds is null)
        {
            return ModSecurityRuleFailure.RuleIdOutOfRange;
        }

        int[] given = [.. request.RuleIds];
        if (given.Length != count
            || given.Any(id => id < ModSecurityLimits.RuleIdMin || id > ModSecurityLimits.RuleIdMax))
        {
            return ModSecurityRuleFailure.RuleIdOutOfRange;
        }

        if (given.Distinct().Count() != given.Length)
        {
            return ModSecurityRuleFailure.RuleIdRepeated;
        }

        ids = given;
        return ModSecurityRuleFailure.None;
    }

    private static ModSecurityRuleFailure ParseLocation(string? location, out string basePath, out string query)
    {
        basePath = "";
        query = "";
        if (string.IsNullOrWhiteSpace(location))
        {
            return ModSecurityRuleFailure.NoLocation;
        }

        if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return ModSecurityRuleFailure.NotHttps;
        }

        if (location.StartsWith(HttpsScheme, StringComparison.Ordinal))
        {
            return ParseUrl(location, out basePath, out query);
        }

        if (location.StartsWith('/'))
        {
            return ParseFolder(location, out basePath);
        }

        return ModSecurityRuleFailure.LocationInvalid;
    }

    private static ModSecurityRuleFailure ParseFolder(string location, out string basePath)
    {
        basePath = "";
        if (location.Length > MaxFolderLength
            || !FolderPattern().IsMatch(location)
            || location.Contains("//", StringComparison.Ordinal)
            || HasDotSegment(location))
        {
            return ModSecurityRuleFailure.LocationInvalid;
        }

        basePath = location.EndsWith('/') ? location[..^1] : location;
        return ModSecurityRuleFailure.None;
    }

    private static ModSecurityRuleFailure ParseUrl(string location, out string basePath, out string query)
    {
        basePath = "";
        query = "";
        if (location.Length > ModSecurityLimits.MaxLocationLength || !UrlCharactersAllowed(location))
        {
            return ModSecurityRuleFailure.LocationInvalid;
        }

        string rest = location[HttpsScheme.Length..];
        int authorityEnd = rest.IndexOfAny(['/', '?']);
        string authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        int colon = authority.IndexOf(':');
        if ((colon < 0 ? authority : authority[..colon]).Length == 0)
        {
            return ModSecurityRuleFailure.LocationInvalid;
        }

        int mark = location.IndexOf('?');
        string path = mark < 0 ? location : location[..mark];
        if (HasDotSegment(path[(HttpsScheme.Length + authority.Length)..]) || HasEncodedSeparator(path)
            || HasQuerySeparator(path) || HasSigSegment(path))
        {
            return ModSecurityRuleFailure.LocationInvalid;
        }

        string given = mark < 0 ? "" : location[(mark + 1)..];
        ModSecurityRuleFailure sasFailure = SasFailure(given);
        if (sasFailure != ModSecurityRuleFailure.None)
        {
            return sasFailure;
        }

        query = given;
        basePath = path.TrimEnd('/');
        return ModSecurityRuleFailure.None;
    }

    // The location is kept in the server's configuration and may reach its logs, so a SAS there may only read and list
    // one container or directory, and its rights, scope and expiry must all be read from the query
    private static ModSecurityRuleFailure SasFailure(string query)
    {
        List<QueryParameter> parameters = Parameters(query);
        if (!HasSig(parameters))
        {
            return ModSecurityRuleFailure.None;
        }

        // Azure's SAS names are all lowercase letters, so any other name is a garbled or hand-edited link, or a decoy a
        // server reading names another way could take for sp or sr
        if (!parameters.All(parameter => parameter.Written.Length > 0
                                         && parameter.Written.All(char.IsAsciiLetterLower)))
        {
            return ModSecurityRuleFailure.SasRightsUnknown;
        }

        List<string> rights = Values(parameters, "sp");
        if (rights.Count == 0)
        {
            return ModSecurityRuleFailure.SasRightsUnknown;
        }

        if (rights.Any(sp => sp.Length == 0 || sp.Any(c => c is not ('r' or 'l'))))
        {
            return ModSecurityRuleFailure.SasCanWrite;
        }

        List<string> resources = Values(parameters, "sr");
        if (Values(parameters, "ss").Count > 0 || Values(parameters, "srt").Count > 0
            || resources.Count == 0 || resources.Any(sr => sr is not ("c" or "d")))
        {
            return ModSecurityRuleFailure.SasScopeTooWide;
        }

        return TryReadExpiry(parameters, out _, out _)
            ? ModSecurityRuleFailure.None
            : ModSecurityRuleFailure.SasExpiryUnknown;
    }

    private static bool HasSig(List<QueryParameter> parameters) =>
        parameters.Any(parameter => SigPartPattern().IsMatch(parameter.Name));

    // A user delegation SAS fails once its key expires, whatever its se says
    private static bool TryReadExpiry(List<QueryParameter> parameters, out DateTimeOffset expiry, out bool hasTime)
    {
        if (!TryReadTime(parameters, "se", out expiry, out hasTime))
        {
            return false;
        }

        if (Named(parameters, "skoid").Count == 0 && Named(parameters, "ske").Count == 0)
        {
            return true;
        }

        if (!TryReadTime(parameters, "ske", out DateTimeOffset keyExpiry, out bool keyHasTime))
        {
            (expiry, hasTime) = (default, false);
            return false;
        }

        if (keyExpiry < expiry)
        {
            (expiry, hasTime) = (keyExpiry, keyHasTime);
        }

        return true;
    }

    private static bool TryReadTime(List<QueryParameter> parameters, string name, out DateTimeOffset time,
        out bool hasTime)
    {
        time = default;
        hasTime = false;
        List<QueryParameter> found = Named(parameters, name);
        // A form decoder reads a + in a query as a space, so only %2B is taken as an offset's sign
        Match match = found.Count == 1 && !found[0].WrittenValue.Contains('+')
            ? ExpiryPattern().Match(found[0].Value)
            : Match.Empty;
        if (!match.Success)
        {
            return false;
        }

        int year = Number(match, "year"), month = Number(match, "month"), day = Number(match, "day");
        int hour = Number(match, "hour"), minute = Number(match, "minute"), second = Number(match, "second");
        int offsetHour = Number(match, "offsetHour"), offsetMinute = Number(match, "offsetMinute");
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59 || offsetHour > 23 || offsetMinute > 59)
        {
            return false;
        }

        // Seven decimals are ticks; an offset is the local time's lead on UTC, as ISO 8601 has it
        int fraction = match.Groups["fraction"].Success ? int.Parse(match.Groups["fraction"].Value.PadRight(7, '0'),
                                                                    CultureInfo.InvariantCulture) : 0;
        long offset = (offsetHour * 60L + offsetMinute) * TimeSpan.TicksPerMinute;
        long ticks = new DateTime(year, month, day, hour, minute, second).Ticks + fraction
                     - (match.Groups["sign"].Value == "-" ? -offset : offset);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        time = new DateTimeOffset(ticks, TimeSpan.Zero);
        hasTime = match.Groups["hour"].Success;
        return true;
    }

    private static int Number(Match match, string group) =>
        match.Groups[group].Success ? int.Parse(match.Groups[group].ValueSpan, CultureInfo.InvariantCulture) : 0;

    private readonly record struct QueryParameter(string Written, string Name, string WrittenValue, string Value);

    private static List<QueryParameter> Parameters(string query) =>
        query.Split('&').Select(parameter =>
        {
            int equals = parameter.IndexOf('=');
            string written = equals < 0 ? parameter : parameter[..equals];
            string value = equals < 0 ? "" : parameter[(equals + 1)..];
            return new QueryParameter(written, Decode(written), value, Decode(value));
        }).ToList();

    // Read only once every name has passed as plain lowercase letters, so a name is exactly its written text
    private static List<QueryParameter> Named(List<QueryParameter> parameters, string name) =>
        parameters.Where(parameter => parameter.Written == name).ToList();

    private static List<string> Values(List<QueryParameter> parameters, string name) =>
        Named(parameters, name).Select(parameter => parameter.Value).ToList();

    // Each %XX becomes the one character of that byte. + is kept: in sp or sr it is refused either way, and an expiry
    // is refused on its written value first
    private static string Decode(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length
                && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                                 out byte value))
            {
                builder.Append((char)value);
                i += 2;
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    // The loader or libcurl resolves "." and "..", and %2e is a dot, so the list would come from another path than the
    // one shown
    private static bool HasDotSegment(string path) =>
        path.Split('/').Any(segment => segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase) is "." or "..");

    // A server that decodes %2f or %5c before it resolves dot segments would read another path; a sig often holds %2F,
    // so only the part before the query is looked at
    private static bool HasEncodedSeparator(string path) =>
        path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || path.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    // The portal and az give a SAS token without its ?, so pasted after a folder it would land here, past every SAS
    // check; a SAS always holds several &
    private static bool HasQuerySeparator(string path) => Decode(path).IndexOfAny(['&', '?']) >= 0;

    // A token whose ? was typed after its sig would put the signature here, past every SAS check; a folder such as
    // year=2026 or signal=1 is kept
    private static bool HasSigSegment(string path) =>
        path.Split('/').Select(Decode).Any(segment =>
            segment.Contains('=') && SigPartPattern().IsMatch(segment[..segment.IndexOf('=')]));

    private static bool UrlCharactersAllowed(string location)
    {
        for (int i = HttpsScheme.Length; i < location.Length; i++)
        {
            char c = location[i];
            if (c == '%')
            {
                if (i + 2 >= location.Length || !char.IsAsciiHexDigit(location[i + 1])
                                             || !char.IsAsciiHexDigit(location[i + 2]))
                {
                    return false;
                }

                i += 2;
            }
            else if (!char.IsAsciiLetterOrDigit(c) && "-._~/:?&=+,".IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            char kept = char.IsAsciiLetterOrDigit(c) || " ._:/()+-".IndexOf(c) >= 0 ? c : ' ';
            if (kept != ' ' || builder.Length == 0 || builder[^1] != ' ')
            {
                builder.Append(kept);
            }
        }

        return builder.ToString().Trim(' ');
    }

    private static string Msg(string label, string file)
    {
        if (label.Length == 0)
        {
            return Cut(file, MaxMsgLength);
        }

        string whole = $"{label} ({file})";
        if (whole.Length <= MaxMsgLength)
        {
            return whole;
        }

        int room = MaxMsgLength - file.Length - 3;
        if (room < 1)
        {
            return Cut(file, MaxMsgLength);
        }

        return $"{Cut(label, room).TrimEnd(' ')} ({file})";
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
