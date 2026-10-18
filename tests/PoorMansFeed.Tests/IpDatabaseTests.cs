using System.Net;
using PoorMansFeed.Core;
using Microsoft.Data.Sqlite;

namespace PoorMansFeed.Tests;

public sealed class IpDatabaseTests : IDisposable
{
    private static readonly DateOnly Downloaded = new(2026, 10, 7);
    private static readonly DateTimeOffset Imported = new(2026, 10, 7, 16, 5, 0, TimeSpan.Zero);

    private readonly DbTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static IpInfoEntry Row(string network, string asn, string? asName = null)
    {
        var (start, end) = Cidr.Parse(network);
        return new IpInfoEntry
        {
            Network = network,
            Country = "Testland",
            CountryCode = "TL",
            Continent = "Europe",
            ContinentCode = "EU",
            Asn = asn,
            AsName = asName ?? "Test AS",
            AsDomain = "example.com",
            StartIp = start,
            EndIp = end,
        };
    }

    private static IpInfoEntry[] SixRows() =>
    [
        Row("8.8.8.0/24", "AS15169", "Google LLC"),
        Row("8.8.4.0/24", "AS15169", "Google LLC"),
        Row("2001:4860::/32", "AS15169", "Google LLC"),
        Row("1.1.1.0/24", "AS13335", "Cloudflare, Inc."),
        Row("1.0.0.0/24", "AS13335", "Cloudflare, Inc."),
        Row("2a02:26f0::/29", "AS5606", "BSkyB"),
    ];

    private static void BuildAndPromote(IpDatabase live, string nextPath, string checksum, params IpInfoEntry[] rows)
    {
        using (var builder = IpDatabase.CreateBuilder(nextPath, 2))
        {
            foreach (IpInfoEntry row in rows)
                builder.Add(row);
            builder.Complete(checksum, Downloaded, Imported);
        }
        live.Promote(nextPath);
    }

    private static void WriteMetadataFile(string path, params (string Key, string Value)[] pairs)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString);
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ip_data (network TEXT NOT NULL, start_ip BLOB NOT NULL, end_ip BLOB NOT NULL);";
            create.ExecuteNonQuery();
        }
        foreach (var (key, value) in pairs)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO metadata (key, value) VALUES (@key, @value);";
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@value", value);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void GetStatus_Is_Missing_When_No_File()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));

        Assert.Equal(DatabaseStatus.Missing, db.GetStatus());
        Assert.Null(db.ReadInfo());
    }

    [Fact]
    public void Built_And_Promoted_Database_Is_Usable_And_Reports_Info()
    {
        string live = _temp.FilePath("ipinfo.db");
        var db = new IpDatabase(live);

        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        Assert.Equal(DatabaseStatus.Usable, db.GetStatus());
        DatasetInfo? info = db.ReadInfo();
        Assert.NotNull(info);
        Assert.Equal("abc123", info.Checksum);
        Assert.Equal(6L, info.RangeCount);
        Assert.Equal(Downloaded, info.DownloadedOn);
        Assert.Equal(Imported, info.ImportedUtc);
    }

    [Fact]
    public void QueryAsn_Returns_IPv4_Before_IPv6_Ordered_By_Start()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        string[] networks = db.QueryAsn(15169, null).Select(r => r.Network).ToArray();

        Assert.Equal(new[] { "8.8.4.0/24", "8.8.8.0/24", "2001:4860::/32" }, networks);
        Assert.Equal("Google LLC", db.QueryAsn(15169, null)[0].AsName);
    }

    [Fact]
    public void QueryAsn_Honours_Limit()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        string[] networks = db.QueryAsn(15169, 2).Select(r => r.Network).ToArray();

        Assert.Equal(new[] { "8.8.4.0/24", "8.8.8.0/24" }, networks);
    }

    [Fact]
    public void CountAsn_Counts_Matching_Rows()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        Assert.Equal(3, db.CountAsn(15169));
        Assert.Equal(2, db.CountAsn(13335));
        Assert.Equal(0, db.CountAsn(64512));
    }

    [Fact]
    public void QueryIp_Finds_Containing_Range()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        NetworkRecord[] ipv4 = db.QueryIp(new byte[] { 8, 8, 8, 8 }, 10).ToArray();
        Assert.Equal(new[] { "8.8.8.0/24" }, ipv4.Select(r => r.Network));

        byte[] ipv6Bytes = IPAddress.Parse("2001:4860::8888").GetAddressBytes();
        Assert.Equal(new[] { "2001:4860::/32" }, db.QueryIp(ipv6Bytes, 10).Select(r => r.Network));

        Assert.Empty(db.QueryIp(new byte[] { 9, 9, 9, 9 }, 10));
    }

    [Fact]
    public void QueryIp_Ignores_IPv6_Range_For_IPv4_Address()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", Row("2a02:26f0::/29", "AS5606"));

        // 42.2.38.241 is 2a 02 26 f1, which sorts inside the 16-byte range 2a02:26f0::/29 without the length check.
        List<NetworkRecord> hits = db.QueryIp(new byte[] { 42, 2, 38, 241 }, 10);

        Assert.Empty(hits);
    }

    [Fact]
    public void QueryIp_Finds_An_IPv6_Range_From_Its_First_To_Its_Last_Address()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123",
            Row("2001:db8::/32", "AS64500"),
            Row("2a02:26f0::1", "AS5606"));

        Assert.Equal(["2001:db8::/32"], db.QueryIp(Bytes("2001:db8::"), 10).Select(r => r.Network));
        Assert.Equal(["2001:db8::/32"], db.QueryIp(Bytes("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff"), 10).Select(r => r.Network));
        Assert.Empty(db.QueryIp(Bytes("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff"), 10));
        Assert.Empty(db.QueryIp(Bytes("2001:db9::"), 10));
        Assert.Equal(["2a02:26f0::1"], db.QueryIp(Bytes("2a02:26f0::1"), 10).Select(r => r.Network));
        Assert.Empty(db.QueryIp(Bytes("2a02:26f0::2"), 10));
    }

    [Fact]
    public void QueryIp_Ignores_IPv4_Range_For_IPv6_Address()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", Row("32.1.13.0/24", "AS64500"));

        // 32.1.13.0/24 is 20 01 0d 00 to 20 01 0d ff, which brackets the 16 bytes of 2001:db8:: without the length check.
        List<NetworkRecord> hits = db.QueryIp(Bytes("2001:db8::"), 10);

        Assert.Empty(hits);
    }

    [Fact]
    public void QueryIp_Returns_Nested_Networks_Highest_Start_First_Then_Broadest_First()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123",
            Row("10.0.0.0/24", "AS1"),
            Row("10.0.0.0/16", "AS2"),
            Row("10.0.0.5", "AS3"),
            Row("10.0.0.0/8", "AS4"),
            Row("10.0.0.0/16", "AS5"),
            Row("10.0.0.128/25", "AS6"),
            Row("10.0.1.0/24", "AS7"));

        // A shared start puts the broadest first, and the same network twice comes back last added first
        string[] expected = ["10.0.0.5 AS3", "10.0.0.0/8 AS4", "10.0.0.0/16 AS5", "10.0.0.0/16 AS2", "10.0.0.0/24 AS1"];
        Assert.Equal(expected, db.QueryIp([10, 0, 0, 5], 10).Select(r => r.Network + " " + r.Asn));
        Assert.Equal(expected[..2], db.QueryIp([10, 0, 0, 5], 2).Select(r => r.Network + " " + r.Asn));
        Assert.Empty(db.QueryIp([10, 0, 0, 5], 0));
    }

    [Fact]
    public void QueryIp_Finds_A_Broad_Network_Past_Unrelated_Rows_Below_The_Address()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123",
            Row("0.0.0.0/0", "AS1"),
            Row("10.0.0.0/8", "AS2"),
            Row("10.4.0.0/16", "AS3"),
            Row("10.4.9.0/24", "AS4"),
            Row("128.0.0.0/1", "AS5"),
            Row("223.255.255.0/24", "AS6"),
            Row("::/0", "AS7"),
            Row("2001:db8::/32", "AS8"),
            Row("2001:db8:1::/48", "AS9"));

        Assert.Equal(["10.0.0.0/8", "0.0.0.0/0"], db.QueryIp([10, 5, 0, 1], 10).Select(r => r.Network));
        Assert.Equal(["223.255.255.0/24", "128.0.0.0/1", "0.0.0.0/0"], db.QueryIp([223, 255, 255, 255], 10).Select(r => r.Network));
        Assert.Equal(["0.0.0.0/0"], db.QueryIp([0, 0, 0, 0], 10).Select(r => r.Network));
        Assert.Equal(["2001:db8::/32", "::/0"], db.QueryIp(Bytes("2001:db8:2::1"), 10).Select(r => r.Network));
        Assert.Equal(["::/0"], db.QueryIp(Bytes("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), 10).Select(r => r.Network));
        Assert.Equal(["::/0"], db.QueryIp(Bytes("::"), 10).Select(r => r.Network));
    }

    [Fact]
    public void QueryIp_Matches_Every_Containing_Row_In_Random_Nested_Networks()
    {
        var random = new Random(20261018);
        var rows = new List<IpInfoEntry>();
        for (int i = 0; i < 400; i++)
        {
            // Small address spaces (10.0.0.0/14, and 2001:db8::/38 with only the last byte set below it), so many rows nest
            bool ipv6 = i % 2 == 1;
            byte[] address;
            int prefix;
            if (ipv6)
            {
                address = Bytes("2001:db8::");
                address[4] = (byte)random.Next(4);
                address[15] = (byte)random.Next(256);
                prefix = random.Next(28, 129);
            }
            else
            {
                address = [10, (byte)random.Next(4), (byte)random.Next(256), (byte)random.Next(256)];
                prefix = random.Next(8, 33);
            }

            rows.Add(Row(new IPAddress(address) + "/" + prefix.ToString(System.Globalization.CultureInfo.InvariantCulture), "AS" + i));
        }

        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", [.. rows]);

        var probes = new List<byte[]>();
        foreach (IpInfoEntry row in rows)
        {
            probes.Add(row.StartIp!);
            probes.Add(row.EndIp!);
            probes.Add(Step(row.StartIp!, -1));
            probes.Add(Step(row.EndIp!, +1));
        }

        foreach (byte[] ip in probes)
        {
            // Every row holding the address, by start, then end, then the order added, all descending
            string[] expected = rows
                .Select((row, index) => (row, index))
                .Where(x => x.row.StartIp!.Length == ip.Length
                         && x.row.StartIp.AsSpan().SequenceCompareTo(ip) <= 0
                         && x.row.EndIp.AsSpan().SequenceCompareTo(ip) >= 0)
                .OrderByDescending(x => x.row.StartIp!, ByteOrder)
                .ThenByDescending(x => x.row.EndIp!, ByteOrder)
                .ThenByDescending(x => x.index)
                .Select(x => x.row.Asn!)
                .ToArray();

            Assert.Equal(expected, db.QueryIp(ip, 1000).Select(r => r.Asn));
        }
    }

    [Fact]
    public void Schema_Version_1_Is_Outdated_With_No_Info()
    {
        string path = _temp.FilePath("old.db");
        WriteMetadataFile(path, ("schema_version", "1"), ("last_updated", "2026-10-01"));
        var db = new IpDatabase(path);

        Assert.Equal(DatabaseStatus.Outdated, db.GetStatus());
        Assert.Null(db.ReadInfo());
    }

    [Fact]
    public void Missing_Metadata_Is_Outdated()
    {
        string path = _temp.FilePath("bare.db");
        WriteMetadataFile(path);
        var db = new IpDatabase(path);

        Assert.Equal(DatabaseStatus.Outdated, db.GetStatus());
        Assert.Null(db.ReadInfo());
    }

    [Fact]
    public void Schema_Version_3_Is_Newer_With_No_Info()
    {
        string path = _temp.FilePath("new.db");
        WriteMetadataFile(path, ("schema_version", "3"));
        var db = new IpDatabase(path);

        Assert.Equal(DatabaseStatus.Newer, db.GetStatus());
        Assert.Null(db.ReadInfo());
    }

    [Fact]
    public void Promote_Replaces_Live_Database_And_Queries_See_New_Rows()
    {
        string live = _temp.FilePath("ipinfo.db");
        string next1 = _temp.FilePath("ipinfo.next1.db");
        string next2 = _temp.FilePath("ipinfo.next2.db");
        var db = new IpDatabase(live);

        BuildAndPromote(db, next1, "old", Row("8.8.8.0/24", "AS15169"));
        Assert.Single(db.QueryAsn(15169, null));

        BuildAndPromote(db, next2, "new", Row("1.1.1.0/24", "AS13335"));

        Assert.Empty(db.QueryAsn(15169, null));
        Assert.Equal(new[] { "1.1.1.0/24" }, db.QueryIp(new byte[] { 1, 1, 1, 1 }, 10).Select(r => r.Network));
        Assert.Equal("new", db.ReadInfo()?.Checksum);
    }

    [Fact]
    public void Promote_Removes_The_Next_File()
    {
        string next = _temp.FilePath("ipinfo.next.db");
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));

        BuildAndPromote(db, next, "abc123", SixRows());

        Assert.False(File.Exists(next));
        Assert.True(File.Exists(_temp.FilePath("ipinfo.db")));
    }

    [Fact]
    public void QueryAsn_With_IPv6_Off_Returns_Only_IPv4()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        string[] networks = db.QueryAsn(15169, null, includeIPv6: false).Select(r => r.Network).ToArray();

        Assert.Equal(new[] { "8.8.4.0/24", "8.8.8.0/24" }, networks);
    }

    [Fact]
    public void QueryAsn_With_IPv6_Off_Still_Honours_Limit()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        Assert.Single(db.QueryAsn(15169, 1, includeIPv6: false));
    }

    [Fact]
    public void CountAsn_With_IPv6_Off_Excludes_IPv6()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        Assert.Equal(2, db.CountAsn(15169, includeIPv6: false));
        Assert.Equal(3, db.CountAsn(15169, includeIPv6: true));
    }

    [Fact]
    public void Builder_Records_IPv6_Off_In_Metadata()
    {
        string live = _temp.FilePath("ipinfo.db");
        var db = new IpDatabase(live);
        string next = _temp.FilePath("ipinfo.next.db");
        using (var builder = IpDatabase.CreateBuilder(next, 2, includeIPv6: false))
        {
            Assert.False(builder.IncludesIPv6);
            builder.Add(Row("8.8.8.0/24", "AS15169", "Google LLC"));
            builder.Complete("abc123", Downloaded, Imported);
        }
        db.Promote(next);

        Assert.False(db.ReadInfo()!.IncludesIPv6);
    }

    [Fact]
    public void Builder_Records_IPv6_On_In_Metadata()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", SixRows());

        Assert.True(db.ReadInfo()!.IncludesIPv6);
    }

    [Fact]
    public void Database_Without_Includes_Key_Reports_IPv6_On()
    {
        string live = _temp.FilePath("ipinfo.db");
        WriteMetadataFile(live,
            ("schema_version", "2"),
            ("last_updated", "2026-10-07"),
            ("dataset_checksum", "abc123"),
            ("row_count", "0"),
            ("imported_utc", Imported.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        var db = new IpDatabase(live);

        DatasetInfo? info = db.ReadInfo();

        Assert.NotNull(info);
        Assert.True(info.IncludesIPv6);
    }

    [Fact]
    public void QueryOverlaps_Returns_The_Ancestor_That_Contains_The_Block()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", Entry("10.0.0.0/8", "ancestor.example"));

        List<NetworkOverlap> overlaps = db.QueryOverlaps([Block("10.1.2.0/24")], includeIPv6: true);

        Assert.Single(overlaps);
        Assert.Equal("10.1.2.0/24", overlaps[0].Block.Text);
        OverlapGroup group = Assert.Single(overlaps[0].Groups);
        Assert.Equal("ancestor.example", group.AsDomain);
        Assert.Equal(1, group.NetworkCount);
    }

    [Fact]
    public void QueryOverlaps_Returns_A_More_Specific_Network_Inside_The_Block()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", Entry("10.1.2.128/25", "child.example"));

        List<NetworkOverlap> overlaps = db.QueryOverlaps([Block("10.1.2.0/24")], includeIPv6: true);

        OverlapGroup group = Assert.Single(Assert.Single(overlaps).Groups);
        Assert.Equal("child.example", group.AsDomain);
        Assert.Equal(1, group.NetworkCount);
    }

    [Fact]
    public void QueryOverlaps_Returns_An_Empty_Group_List_When_Nothing_Overlaps()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123", Entry("192.168.0.0/24", "other.example"));

        List<NetworkOverlap> overlaps = db.QueryOverlaps([Block("10.1.2.0/24")], includeIPv6: true);

        NetworkOverlap overlap = Assert.Single(overlaps);
        Assert.Equal("10.1.2.0/24", overlap.Block.Text);
        Assert.Empty(overlap.Groups);
    }

    [Fact]
    public void QueryOverlaps_Does_Not_Return_An_IPv6_Row_For_An_IPv4_Block()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123",
            Entry("10.1.2.0/24", "v4.example"),
            Entry("0a01:0200::/64", "v6.example"));

        List<NetworkOverlap> overlaps = db.QueryOverlaps([Block("10.1.2.0/24")], includeIPv6: true);

        OverlapGroup group = Assert.Single(Assert.Single(overlaps).Groups);
        Assert.Equal("v4.example", group.AsDomain);
        Assert.Equal(1, group.NetworkCount);
    }

    [Fact]
    public void QueryOverlaps_Finds_An_IPv6_Ancestor_And_An_IPv6_Network_Inside_The_Block()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123",
            Entry("2001:db8::/32", "ancestor.example"),
            Entry("2001:db8:1234:5600::/56", "child.example"),
            Entry("2001:db8:1235::/48", "sibling.example"),
            Entry("2001:db9::/32", "other.example"));

        List<NetworkOverlap> overlaps = db.QueryOverlaps([Block("2001:db8:1234::/48")], includeIPv6: true);

        NetworkOverlap overlap = Assert.Single(overlaps);
        Assert.Equal(["ancestor.example", "child.example"], overlap.Groups.Select(group => group.AsDomain).Order());
    }

    [Fact]
    public void QueryOverlaps_Does_Not_Return_An_IPv4_Row_For_An_IPv6_Block()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123",
            Entry("32.1.13.184/32", "v4.example"),
            Entry("2001:db8::/32", "v6.example"));

        // 32.1.13.184 is 20 01 0d b8, which sorts between the 16-byte start and end of 2001:db0::/28 without the length check.
        List<NetworkOverlap> overlaps = db.QueryOverlaps([Block("2001:db0::/28")], includeIPv6: true);

        OverlapGroup group = Assert.Single(Assert.Single(overlaps).Groups);
        Assert.Equal("v6.example", group.AsDomain);
    }

    [Fact]
    public void QueryOverlaps_IncludeIPv6_False_Does_Not_Return_IPv6_Blocks()
    {
        var db = new IpDatabase(_temp.FilePath("ipinfo.db"));
        BuildAndPromote(db, _temp.FilePath("ipinfo.next.db"), "abc123",
            Entry("10.1.2.0/24", "v4.example"),
            Entry("2001:4860::/32", "v6.example"));

        List<NetworkOverlap> mixed = db.QueryOverlaps(
            [Block("10.1.2.0/24"), Block("2001:4860::/32")],
            includeIPv6: false);

        NetworkOverlap only = Assert.Single(mixed);
        Assert.Equal("10.1.2.0/24", only.Block.Text);
        Assert.Equal("v4.example", Assert.Single(only.Groups).AsDomain);
        Assert.Empty(db.QueryOverlaps([Block("2001:4860::/32")], includeIPv6: false));
    }

    private static IpInfoEntry Entry(string network, string domain)
    {
        var (start, end) = Cidr.Parse(network);
        return new IpInfoEntry
        {
            Network = network,
            Country = "United States",
            CountryCode = "US",
            Continent = "North America",
            ContinentCode = "NA",
            Asn = "AS15169",
            AsName = "Google LLC",
            AsDomain = domain,
            StartIp = start,
            EndIp = end,
        };
    }

    private static byte[] Bytes(string address) => IPAddress.Parse(address).GetAddressBytes();

    private static readonly Comparer<byte[]> ByteOrder = Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right));

    // The address one above or below, wrapping at the ends of the family
    private static byte[] Step(byte[] address, int direction)
    {
        byte[] stepped = (byte[])address.Clone();
        for (int i = stepped.Length - 1; i >= 0; i--)
        {
            stepped[i] = (byte)(stepped[i] + direction);
            if (stepped[i] != (direction > 0 ? 0 : 255))
                break;
        }
        return stepped;
    }

    private static CidrBlock Block(string text)
    {
        Assert.True(CidrBlock.TryParse(text, out CidrBlock? block));
        return block!;
    }
}
