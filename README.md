# PoorMansFeed

Build your own IP threat feeds from freely available data.

PoorMansFeed is a desktop app for Windows and macOS. It keeps a local copy of the free [IPinfo Lite](https://ipinfo.io/products/free-ip-database) dataset and uses it to turn an AS number, or the address ranges that Google Cloud, Azure and AWS publish, into a feed your firewall can load and use in its policies. Cloud ranges are checked against IPinfo before they are written, so a range that IPinfo places with another network is shown to you unticked instead of going into the feed unseen. It can also keep a rolling list of the addresses on AbuseIPDB's blacklist and write a feed from that.

Feeds are written for FortiGate and for ModSecurity. Formats for nginx and other firewalls are planned (see [Roadmap](#roadmap)).

| Firewall | Feed | Status |
|---|---|---|
| FortiGate (FortiOS) | IP address threat feed, loaded through an external connector | Supported |
| ModSecurity (v3 with nginx, v2.9 with Apache) | IP list for the `@ipMatchFromFile` operator, with a rule to load it | Supported |
| nginx | `deny` or `geo` include files | Planned |
| Other firewalls | | Planned |

## Why this exists

Small organisations often have no budget for threat intelligence tools, or management needs convincing before it will pay for one. Either way, you are still the one protecting the services, and you have to do as much as you can to keep unwanted traffic out.

More of that traffic now comes from data centres. Scanners, scrapers and automated attacks, increasingly driven by AI, run on cheap rented cloud servers. If your service has no reason to be reached from a cloud provider's network, closing off its ranges, a whole Google Cloud region for example, is a reasonable choice, and this tool builds that block list for you.

A paid IPinfo plan would let you automate much of this, with more flexibility, for example for keeping data centres or VPNs away from your services. PoorMansFeed does what it can with the free tier: it builds on the free [IPinfo Lite](https://ipinfo.io/products/free-ip-database) dataset and the ranges the cloud providers publish, so you can watch the networks that matter to you and build your own threat feeds without paying for a plan.

The tool is in active use. It is still a manual process today: you build a feed in the app, then copy the file to a web server your firewall reads from. Publishing feeds straight to Azure Blob Storage is planned (see [Roadmap](#roadmap)), so that a feed can be created, kept up to date and linked to a FortiGate external threat feed in a few steps.

### What you can use it for

- **Close off cloud regions your users never come from.** Write a feed of a Google Cloud region, an Azure service tag or an AWS region, and deny it at the firewall.
- **Block a hosting provider that keeps turning up in your logs.** Search an address from a log, see which AS holds it, and export every network of that AS as a feed.
- **Check an address before you act on it.** A lookup shows its network, country, ASN and the AS owner's domain, so you block the provider you meant to.
- **Allow only the cloud services you depend on.** Use a feed as the source of an allow rule, so that only one Azure service tag, say, can reach an endpoint.
- **Keep cloud regions in countries you must not serve off your network,** such as the AWS China regions. A region is where the servers are, not who rents them (see [From AWS ranges](#from-aws-ranges)).
- **Block the addresses reported for abuse.** Download AbuseIPDB's blacklist and write it as a feed. An address stays in it until no download has listed it for the number of days you set (30 by default).
- **Make the case to management.** The ASN report lists every network a provider holds, with its country and domain, which gives a proposal for better tooling something concrete to point at.

Before you deny a provider, check what you rely on that runs there: payment and webhook callbacks, uptime monitors, email delivery, crawlers you want to let in, partner integrations, and staff or customers on VPNs and cloud desktops. Watching what a feed would match, with a policy that allows and logs, before you set it to deny avoids surprises.

## Features

- **ASN feeds:** every network IPinfo lists for an AS (`AS15169` or `15169`), written as a feed and a text report
- **Cloud provider feeds:** Google Cloud ranges for one region, Azure ranges for one service tag, and AWS ranges for one service and region, each confirmed against the local dataset
- **FortiGate and ModSecurity:** every feed is written for each feed type you tick in Settings, as a FortiGate threat feed, a ModSecurity IP list or both, with the same entries
- **ModSecurity rules:** after a write, **ModSecurity rules…** gives one ready-made rule per list, which denies the matches, closes the connection with no reply or only logs them, to copy into your nginx or Apache configuration; each list keeps its rule id from one write to the next
- **Lookups:** search by ASN or by IPv4 or IPv6 address, copy the results to the clipboard, tab-separated, for a spreadsheet, and open a result on ipinfo.io or AbuseIPDB's website from its right-click menu
- **Abuse scores:** check a searched address against [AbuseIPDB](https://www.abuseipdb.com) for its abuse confidence score and reports, only when you press **Check AbuseIPDB**; private and reserved addresses are never sent
- **AbuseIPDB blacklist feed:** download AbuseIPDB's blacklist into a rolling list kept on your computer, write it as a feed, and see which networks hold the listed addresses
- **Large range check:** networks broader than a set size are listed before anything is written, and are left out unless you tick them
- **Exceptions:** networks that must never be blocked, such as your offices and partners, are cut out of every feed, FortiGate and ModSecurity, before it is written
- **Safe updates:** the dataset is checked against the SHA-256 that IPinfo publishes, built alongside the current copy and swapped in only when complete, so a cancelled or failed update leaves the existing data in use
- **Saved cloud files:** the Azure service tags file and the AWS ranges file are kept locally and checked for a newer version when their page opens
- **Per-user storage:** no administrator rights needed and nothing written to the program folder; the IPinfo API token and the AbuseIPDB API key are kept by the operating system (DPAPI on Windows, the login keychain on macOS), and your settings and exceptions are kept in one encrypted file whose key the operating system keeps the same way

## Data sources

All of the data is free. IPinfo needs an account, for a free API token, and AbuseIPDB needs one, for a free API key, if you check addresses there or download its blacklist.

| Source | What it provides |
|---|---|
| [IPinfo Lite](https://ipinfo.io/products/free-ip-database) | Every network with its country, continent, ASN, AS name and AS domain. Updated daily, licensed under CC BY-SA 4.0 (see [Attribution](#attribution)). |
| Google Cloud [`cloud.json`](https://www.gstatic.com/ipranges/cloud.json) | Google Cloud ranges by region |
| Microsoft [Azure IP Ranges and Service Tags – Public Cloud](https://www.microsoft.com/en-us/download/details.aspx?id=56519) | Azure ranges by service tag, published weekly |
| AWS [`ip-ranges.json`](https://ip-ranges.amazonaws.com/ip-ranges.json) | AWS ranges by service and region |
| [AbuseIPDB](https://www.abuseipdb.com) check API | One address's abuse confidence score and reports from the last 30 days, sent only when you press **Check AbuseIPDB** |
| [AbuseIPDB](https://www.abuseipdb.com) blacklist API | The most reported addresses (10,000 on the free plan, up to 5 downloads a day), sent only when you press **Download blacklist** |

## Requirements

- Windows (x64) or macOS (Apple silicon). Release builds are self-contained, so .NET does not need to be installed.
- An IPinfo API token ([free signup](https://ipinfo.io/signup)).
- For **Check AbuseIPDB** and the **AbuseIPDB Blacklist** page only: an API key from a free [AbuseIPDB](https://www.abuseipdb.com) account.
- Internet access to `ipinfo.io` for the dataset and, for the cloud pages, `www.gstatic.com` (Google Cloud), `www.microsoft.com` and `download.microsoft.com` (Azure) and `ip-ranges.amazonaws.com` (AWS), and for **Check AbuseIPDB** and **Download blacklist**, `api.abuseipdb.com`.
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), 10.0.400 or later (`global.json`).

## Installing

Download the file for your platform from the [latest release](https://github.com/asfritecx/PoorMansFeed/releases/latest):

- **Windows:** `PoorMansFeed-{version}-win-x64.exe`, a single-file app. Run it directly.
- **macOS:** `PoorMansFeed-{version}-osx-arm64.zip`, which holds `PoorMansFeed.app`. Unzip it, move the app to Applications and open it.

Each release also has `SHA256SUMS.txt`, the SHA-256 of each file, so you can check a download before you run it. On macOS, `shasum -a 256 -c SHA256SUMS.txt --ignore-missing` in the download folder checks the files you have. On Windows, compare the output of `Get-FileHash <file>` in PowerShell with the line for that file.

Neither build is code signed (see [Code signing policy](#code-signing-policy)), so Windows and macOS warn, or block, the first time you open each new version. [Opening the app the first time](#opening-the-app-the-first-time) shows how to get past them.

Run the app as your normal user account. It keeps everything in your user profile and will not start elevated (as administrator on Windows, or as root on macOS). Only one copy runs at a time.

There is no update check in the app yet. To update, download the new release and replace the old file. Your dataset, token, settings and exceptions are kept in your user profile, so they carry over. When you update from 1.0.0, the first launch moves your settings from `settings.json` into the app's settings file, `config.db`, and renames the old file `settings.json.migrated`. On macOS, the first time a new version starts, macOS may ask whether PoorMansFeed may use the key to its settings file in your keychain; choose **Always Allow**. If you choose Deny, the app doesn't start and says so; open it again and allow access. In the same way, the first time a new version reads the saved token, macOS asks whether PoorMansFeed may use its item in your keychain; choose **Always Allow**. The app is ad-hoc signed, so macOS treats each new version as a different app. If you choose Deny there, the update check says a token is needed; choose **Reset token** in **Settings** to enter it again. The AbuseIPDB key is a second keychain item, so macOS may ask again the first time a new version checks an address or downloads the blacklist.

### Opening the app the first time

Check the download against `SHA256SUMS.txt` first, then:

**Windows**

1. Your browser may say the file isn't commonly downloaded. Keep it: in Edge, choose **…** beside the download, then **Keep**, **Show more** and **Keep anyway**.
2. Open the exe. If SmartScreen says **Windows protected your PC**, choose **More info**, check that the app it names is `PoorMansFeed-{version}-win-x64.exe` from an unknown publisher, then choose **Run anyway**. Or, before you open it, right-click the file, choose **Properties**, tick **Unblock** at the foot of the **General** tab and choose **OK**, and Windows opens it without asking.
3. If Smart App Control is on (in Windows Security, under **App & browser control**), Windows blocks every unsigned app and offers no **Run anyway**. Unblocking the file as above may let it through. If it doesn't, PoorMansFeed can't run on that PC while Smart App Control is on; turning it off lowers the protection for every app, so that is your decision.

Each new version is a new file, so Windows asks again.

**macOS**

1. Unzip the download, move `PoorMansFeed.app` to Applications and open it. macOS says it couldn't verify that PoorMansFeed is free of malware. Choose **Done**, not **Move to Trash**.
2. Open **System Settings**, choose **Privacy & Security** and scroll down to **Security**. Beside the line saying PoorMansFeed was blocked, choose **Open Anyway** (or **Open**), then confirm with **Open Anyway** and your login password. The button is there for about an hour after you tried to open the app.
3. From macOS 15, right-clicking the app and choosing **Open** no longer gets past this.

Or, in Terminal, once the zip matches its line in `SHA256SUMS.txt`, run `xattr -dr com.apple.quarantine /Applications/PoorMansFeed.app`. It removes the mark macOS puts on downloaded files, and the app then opens without asking.

Each new version asks again, and then asks about its keychain items, as described above.

## Code signing policy

The release builds aren't code signed:

- The Windows exe has no Authenticode signature, so Windows shows its publisher as unknown.
- The macOS app is ad-hoc signed, which lets it run on Apple silicon, but it isn't signed with an Apple Developer ID or notarised by Apple. That is also why macOS asks about the keychain again after each update.

I'm the poor man in PoorMansFeed. I build it in my own time, and I don't have the money to pay for code signing: Apple charges US$99 a year for the membership that signs and notarises a Mac app, and a Windows code signing certificate costs money every year too.

So that you can still check what you download:

- Each release lists the SHA-256 of every file in `SHA256SUMS.txt` (see [Installing](#installing)).
- Each release's source is the commit its tag points to in this repository, so you can read it and build it yourself (see [Building from source](#building-from-source)).

The SignPath Foundation signs open-source Windows apps for free, for projects with some track record. I'll apply once PoorMansFeed has one, and this section will say so if the Windows builds become signed.

## First run

1. The app asks for your IPinfo API token. On macOS it is saved in your login keychain. On Windows it is saved in a file protected with DPAPI, so only your Windows account can decrypt it.
2. It downloads the dataset (about 32 MB compressed) and builds the search index. The window shows progress.
3. When it finishes, search from **IPInfo Lookup**, the page the app opens on.

Each time the app opens, it checks whether a newer dataset is available. If one is, choose **Update dataset**. You can search while the check runs, but not while an update is running. IPinfo updates the data daily at about 4 PM UTC and allows 10 dataset downloads a day per IP address, so one update a day is enough.

To use a different token, open **Settings** and choose **Reset token** under API keys (on macOS, **Reset API token…** in the app menu does the same). It removes the saved token and asks for a new one.

## Searching

Type into the search box and press Enter or choose **Search**:

- `AS15169` or `15169` lists every network for that AS
- `8.8.8.8` lists the networks that contain that IPv4 address
- An IPv6 address works the same way

Addresses are read only in their plain form. A legacy form such as `010.0.0.1` or `192.168.1` is rejected rather than searched as a different address.

The list shows up to 300 networks. The summary above it gives the full count.

Right-click a result (on a Mac, click with two fingers) to open the searched address or AS on ipinfo.io, or the address on AbuseIPDB's website, in your browser. Both websites are free to use. The address items can be chosen only for a public address, and nothing leaves the app until you pick one.

### Checking an address with AbuseIPDB

After an address search, **Check AbuseIPDB** in the results header sends that address to AbuseIPDB and shows its abuse confidence score for the last 30 days, with the number of reports, how many users made them and the date of the last one (UTC). Nothing is sent until you press it, and a private, reserved or documentation address is never sent.

The first time, the app asks for your AbuseIPDB API key (you'll find it in your AbuseIPDB account under API) and keeps it like the IPinfo token. If AbuseIPDB doesn't accept the saved key, the app asks for a new one. The free tier allows 1,000 checks a day; when the limit is reached, the app says when it lifts. With IPv6 turned off in Settings, IPv6 addresses aren't searched, so they can't be checked either.

## Building a feed

Each feed is written once for every feed type ticked under **Write feed** in Settings, under the same name: FortiGate threat feeds into the `fortigate/` folder inside the export folder, and ModSecurity IP lists into its `modsecurity/` folder. The FortiGate files are written first. The two copies of a feed hold the same entries and differ only in their first line, which says which kind of file it is. The paths below name the `fortigate/` copy; the `modsecurity/` copy has the same name.

A feed is plain text: a few `#` comment lines saying what it holds, the address family, when it was written (UTC), the number of entries, what your exceptions changed (only when they changed something) and the IPinfo attribution (the AbuseIPDB feed has none), then one network or address per line. File names carry no date, so writing a feed again replaces the file at the same path. When a file can't be written, the result line names it and any written before it. If a FortiGate file can't be written, the ModSecurity copies after it aren't written either.

If no feed type is ticked, **Write feed**, and **Export** while **Write the ticked feed types with each ASN export** is on, first show a dialog saying so, with **Open Settings**, which takes you to the feed types, and **Close**. After **Close**, **Write feed** writes nothing, and **Export** writes only the ASN report. A type you untick keeps its files as they were: they are no longer written or deleted, so they stop updating.

### From an ASN

Search for the AS, then choose **Export**. Export writes every network for the AS, not only the rows on screen:

- `AS{number}.txt`: a report with a table of networks, country and domain, and the IPinfo attribution at the foot.
- `fortigate/AS{number}_ipv4.txt` and `fortigate/AS{number}_ipv6.txt`, or `fortigate/AS{number}.txt` when IPv4 and IPv6 are not split, and the same names in `modsecurity/`. The feed is written while **Write the ticked feed types with each ASN export** is on in Settings, once for each ticked feed type: **Fortigate threat feeds, in the fortigate folder** and **ModSecurity IP lists, in the modsecurity folder**.

### From Google Cloud ranges

The list on the left of the window switches between **IPInfo Lookup**, **Google Cloud Lookup**, **Azure Lookup**, **AWS Lookup** and **AbuseIPDB Blacklist**. The window can be resized, and the results lists grow with it.

Google Cloud Lookup reads the ranges from `https://www.gstatic.com/ipranges/cloud.json`. Choose a region, then **Check ranges**. A prefix is confirmed when every overlapping network's AS domain is google.com. Confirmed rows start ticked. **Write feed** asks about large ranges, then writes into the folder of each ticked feed type, `fortigate/` or `modsecurity/`:

- `GoogleCloud_{region}_ipv4.txt` and `GoogleCloud_{region}_ipv6.txt`
- `GoogleCloud_{region}_global_ipv4.txt` and `GoogleCloud_{region}_global_ipv6.txt` when global ranges were added to a region

A family with no ranges writes no file. IPv6 ranges are left out when IPv6 is off in Settings.

### From Azure service tags

Azure Lookup builds the same kind of feed from Microsoft's service tags file, which Microsoft publishes weekly.

- When Azure Lookup opens, the app shows the copy it saved last time, then reads the download page. If the page names a newer file, the app downloads it and replaces the saved copy. **Check for updates** does the same on request. If the page can't be read, the saved copy stays in use and the status line says so.
- The status line names the file's date, its change number and when it was last checked.
- Choose a **Service** (such as AzureCloud, Storage or Sql) and a **Region** (All regions covers the whole service), then **Check ranges**. A prefix is confirmed when every overlapping network's AS domain is microsoft.com. Confirmed rows start ticked. Other Microsoft networks show as mixed and start unticked.
- **Write feed** asks about large ranges, then writes `fortigate/Azure_{tag}_ipv4.txt` and `fortigate/Azure_{tag}_ipv6.txt`. The dot in the tag name becomes an underscore, so `Storage.SoutheastAsia` is `Azure_Storage_SoutheastAsia_ipv4.txt`.

### From AWS ranges

AWS Lookup builds the same kind of feed from AWS's `ip-ranges.json`, the file AWS publishes for its public address ranges. It needs no sign-in.

- When AWS Lookup opens, the app shows the copy it saved last time, then asks AWS whether the file changed. If it did, the app downloads it and replaces the saved copy. If not, nothing is downloaded. **Check for updates** does the same on request. If AWS can't be reached, the saved copy stays in use and the status line says so.
- The status line names the file's creation time (UTC) and when it was last checked.
- Choose a **Service** (EC2 is selected to start with) and a **Region** (All regions covers the whole service), then **Check ranges**. One region is one feed, so to block several countries, write one feed per region and add them all to your firewall. Regions are shown by their AWS id, such as `cn-north-1` or `ap-east-1`, not by a country name: where AWS puts a region is not always where IPinfo places its addresses (IPinfo lists `me-west-1` in Saudi Arabia, not Israel, and `sa-west-1` in Chile). The results list shows IPinfo's country for each range, so check it before you write.
- A prefix is confirmed when every overlapping network's AS domain is amazon.com, or one of the other operators of AWS regions: sinnet.com.cn (`cn-north-1`), westcloudvalley.com (`cn-northwest-1`) and aws.eu (`eusc-de-east-1`). Confirmed rows start ticked. Ranges that overlap other networks (China Telecom and China Mobile blocks, for example) show as mixed and start unticked; tick them by hand if you want them.
- **Write feed** asks about large ranges, then writes `fortigate/AWS_{service}_{region}_ipv4.txt` and `fortigate/AWS_{service}_{region}_ipv6.txt`, or `AWS_{service}_all_ipv4.txt` and `_ipv6.txt` for all regions, for example `AWS_EC2_cn-north-1_ipv4.txt`.
- A region is where the data centre is, not who the customer is. A feed of the China and Hong Kong regions blocks instances hosted there. It does not stop someone in a banned country from using EC2 in `us-east-1`, and AWS has no regions in most sanctioned countries.

### From the AbuseIPDB blacklist

**AbuseIPDB Blacklist** keeps a rolling list of the addresses AbuseIPDB's blacklist has named, and writes a feed from it. It uses the same API key as **Check AbuseIPDB**, and asks for one when none is saved or AbuseIPDB doesn't accept it.

- **Download blacklist** downloads the free blacklist (the 10,000 most reported addresses) and adds it to the list on this computer, without duplicates. With **Include IPv6 networks** off in Settings, it asks for IPv4 addresses only, so all 10,000 are IPv4. The free plan allows 5 downloads a day. The page shows how many are left; when none are, it says when the next can be sent, and pressing the button sends nothing.
- **Write feed** writes `fortigate/AbuseIPDB_rolling_ipv4.txt` and `fortigate/AbuseIPDB_rolling_ipv6.txt`: the addresses listed in the days before the last download, as many days as Settings sets (30 by default), less every address your exceptions cover, and at most 131,072 a file (the most recently listed are kept). When a family has no addresses and its file exists, the file is rewritten with none, so a FortiGate or ModSecurity that reads it follows the list down to empty. With IPv6 off, an existing IPv6 file is emptied.
- **Empty feed**, after a confirmation, rewrites the feed files that exist with no addresses and keeps the list; the next **Write feed** fills them again. It empties the files in both `fortigate/` and `modsecurity/`, whatever is ticked, so to retire the list of a type you unticked, press **Empty feed** and then **Write feed**, which fills the ticked type's files again. **Clear list**, after a confirmation, removes every downloaded address from this computer; the download count, the API key and the feed files are kept.
- **Offending networks** groups the listed addresses by AS using the IPinfo dataset, with each network's name, domain, address count and countries. It is a report, like the ASN report, so exceptions aren't applied. The search button on a row looks the AS up on **IPInfo Lookup**, where **Export** writes its feed. Right-click a row to open its AS on ipinfo.io.
- The feed header names AbuseIPDB as the source and says the data is for your own firewalls only. AbuseIPDB's terms don't allow it to be republished, so serve the file only where your own firewalls read it.
- **New addresses**, at the top right of the page, lists the addresses the last download added to the list: each address, its AbuseIPDB score and the day of its last report, at most 300 lines shown, with **Copy** for all of them. Right-click an address to open it on ipinfo.io or AbuseIPDB's website. It is a report, so exceptions aren't applied.
- Closing the app while the page is downloading, clearing the list, or writing or emptying the feed asks first.

### Large ranges

Before an export or a feed is written, networks broader than the thresholds are listed in a dialog. The defaults are IPv4 `/16` and IPv6 `/32`, and a network counts as large only when its prefix is shorter, so a `/16` is not large and a `/15` is. Every one starts unticked, so it is left out unless you tick it. This stops a very broad range from being added to a block list by accident.

### Exceptions

**Exceptions**, on the **Settings** page, holds networks that no feed may block, such as your offices, payment providers, uptime monitors and partners. Type a network (`203.0.113.0/24`) or a single address (`203.0.113.7`) and a note on why, choose **Add**, then **Save exceptions**. A network typed with host bits set, such as `203.0.113.7/24`, is added as `203.0.113.0/24`, and the page says so.

- Every feed, FortiGate or ModSecurity, from an ASN, a cloud provider or the AbuseIPDB blacklist, has the exceptions cut out before it is written (the AbuseIPDB feed leaves out each address an exception covers). A range that holds an exception is replaced, in its place in the feed, by the fewest networks that cover the rest of it (a `/16` less one address is 16 networks). A range that an exception covers is left out. The ASN report and the AbuseIPDB offending networks still list everything.
- A feed that exceptions changed says so in its header, for example `# Exceptions: 2 ranges cut, 1 removed`, and the line under the results says how many were cut or removed. A feed that no exception touched is written exactly as it would be without them.
- When exceptions remove every range of one address family, no file is written for that family. A file left by an earlier write stays where it is, and the app names it, because your firewall still reads it. The AbuseIPDB feed instead rewrites that file with no addresses.
- The list holds up to 5,000 networks and is kept with your settings in the app's settings file (see [Where data is stored](#where-data-is-stored)).

### Export folder

Exports go to `Documents/PoorMansFeed` by default. You can choose another folder in Settings. Feeds already written to the earlier folder stay there and aren't updated, and the Settings page says so when you save. **Open folder** opens the export folder.

Inside it, FortiGate threat feeds go into the `fortigate` folder and ModSecurity IP lists into the `modsecurity` folder. Each folder is made by the first feed written into it, and the folder of a type that isn't ticked is left alone, except by **Empty feed** on **AbuseIPDB Blacklist**.

Keep the export folder one that only your account can write. The app won't write a feed through a link someone put in place of the file, but another account that can write the folder could replace the `fortigate` or `modsecurity` folder itself with a link and send every feed somewhere else.

## Using a feed on a FortiGate

FortiOS loads an IP address threat feed (called an external feed from FortiOS 8.0) from a web server, so the feed files need to be served over HTTP or HTTPS from somewhere the FortiGate can reach. The app writes the files; it does not serve them.

1. Write the feed, then publish the file from the `fortigate/` folder on your web server. Because the file name stays the same, rewriting the feed and copying it again updates what the FortiGate loads, at its next refresh. This step is manual for now (see [Roadmap](#roadmap)).
2. Add the feed in **Security Fabric > External Connectors > Create New > IP Address**, or in the CLI:

   ```
   config system external-resource
       edit "AWS_EC2_cn-north-1_ipv4"
           set type address
           set resource "https://feeds.example.com/fortigate/AWS_EC2_cn-north-1_ipv4.txt"
           set refresh-rate 60
       next
   end
   ```

   `refresh-rate` is in minutes (1 to 43200, default 5). Edit the connector and choose **View Entries** to check what it loaded.
3. Use the feed as the source or destination of a firewall policy, or of a local-in policy, with the action set to deny.

A feed can hold IPv4 and IPv6 in one file, so the unsplit ASN feed works as one connector. FortiOS reads at most 10 MB or 131,072 entries per feed, whichever comes first, and leaves out anything beyond that. From FortiOS 7.4.4, every IP address feed on a FortiGate shares one pool of entries (300,000 on entry-level models), so two full AbuseIPDB files, 262,144 addresses, take most of it. See Fortinet's [IP address external feed](https://docs.fortinet.com/document/fortigate/8.0.0/administration-guide/891236/ip-address-external-feed) guide for the details for your version.

## Using a list with ModSecurity

ModSecurity v3 with nginx and ModSecurity v2.9 with Apache read a list through the `@ipMatchFromFile` operator, from a folder on the web server or from an https link. The app writes the lists and gives you the rules that load them; it doesn't copy the lists to the server or change the server's configuration.

1. In **Settings**, under **Write feed**, tick **ModSecurity IP lists, in the modsecurity folder**.
2. In **Where your server reads the lists**, enter the folder on the web server that will hold the lists, starting with `/` (such as `/etc/modsecurity/lists`), or a link starting with `https://`. A link may carry a read-only SAS for one Azure Blob Storage container or directory (`sp=r` or `rl`, `sr=c` or `d`), with its expiry (`se`) in the link. The note under the box says why a location can't be used, or when the link's SAS expires, and warns from 14 days before it does.
3. Choose the **Rule action**, **Log only** (the default), **Deny with status 403** or **Close with no reply (444)**, and the **First rule id for new lists** (10,000 by default, up to 99,999), then choose **Save settings**.
4. Write the feed. Once a write has made ModSecurity lists, **ModSecurity rules…** shows beside the results: on **IPInfo Lookup** after **Export**, and on the cloud pages and **AbuseIPDB Blacklist** after **Write feed**. It opens a dialog with one rule per list, built from the saved location and action, and **Copy** puts them on the clipboard. You can try another location or action in the dialog; nothing there is saved.
5. Copy the lists from the `modsecurity/` folder to that folder on the server, or publish them at the link.
6. Put the rules in a file and load it once, with `modsecurity_rules_file` on nginx or `Include` on Apache. Never paste them inline into the server configuration: each rule's message is quoted, and inside nginx's `modsecurity_rules '...'` the quote ends the string.
7. Reload the server: `nginx -s reload`, or `apachectl graceful` on Apache. The server reads the lists only when it starts and when it reloads, so reload it after each write. A reload that a list fails is refused, and the old lists stay in use.

A rule that denies looks like this (with **Log only**, `deny,status:403` is `pass`, and with **Close with no reply (444)** it is `drop,status:444`):

```
SecRule REMOTE_ADDR "@ipMatchFromFile /etc/modsecurity/lists/AS64500_ipv4.txt" "id:10000,phase:1,deny,status:403,log,t:none,msg:'PoorMansFeed (AS64500_ipv4.txt)'"
```

Things to know:

- **Rule ids.** Each list keeps the rule id it was first given, and a new list takes the next id above every one kept. When you copy the rules again, replace a list's earlier rule rather than adding a second one: ModSecurity won't load two rules with the same id, and including one rules file twice in nginx repeats every id. Leave the OWASP CRS ranges alone.
- **After Start fresh,** the app no longer knows the ids it gave, so raise **First rule id for new lists** above every rule id already in your server's configuration.
- **An https link** ties the server's start to that host and to the SAS expiry: if the list can't be fetched, the server won't start, and a reload is refused, keeping the lists it had. The whole link, SAS included, ends up in the server's configuration and can appear in its logs, so use a read-only SAS, never the one that publishes the lists. ModSecurity v2 refuses `http://`. A Windows folder can't be used as the location.
- **Behind a reverse proxy, load balancer or CDN,** ModSecurity sees the proxy's address, not the visitor's, so the rules match nothing until the web server takes the visitor's address from the proxy's header: `mod_remoteip` on Apache, or the realip module (`set_real_ip_from` and `real_ip_header` in the `http` or `server` block) on nginx, trusting only your proxy's addresses.
- **On nginx, keep the two lines** `listen 80;` and `listen [::]:80;`. With a single `listen [::]:80 ipv6only=off;` instead, ModSecurity v3 sees every IPv4 visitor as `::ffff:a.b.c.d`, and the IPv4 lists match nothing.
- **Where matches are logged.** On nginx, ModSecurity writes a log-only match to the `error_log` at `info`, so an `error_log` at `notice` or above hides it; the audit log has it too while `SecAuditEngine` is `On`. On Apache it writes log-only, deny and close matches at `error`. When a list fails to load, ModSecurity v3 names only the rules file and line: the rule on that line names the list.
- **Close with no reply (444)** sends nothing back, so the server spends no response on a listed address. nginx logs the request as 444; Apache logs it as 403, and its error log says the connection was closed. Behind a reverse proxy, load balancer or CDN, it is the proxy's connection that closes: the proxy answers the visitor with its own error, and may first try another server.
- **Don't edit the lists by hand.** One line ModSecurity can't read can fail the whole list, and by default a server won't start with a list that fails.
- The AbuseIPDB list keeps FortiOS's cap of 131,072 addresses a family, as its FortiGate twin does.

The help button in the bar at the top of the window (**How to use PoorMansFeed**) has these notes too, and the rules dialog repeats the ones about the rules.

## Settings

**Settings** is at the foot of the list on the left, with the gear icon. On macOS, **Settings…** in the app menu opens it too. The page has three parts.

**Options**, saved with **Save settings** (**Revert** goes back to what is saved):

- **Write feed**: the feed types that **Write feed** and **Export** write, **Fortigate threat feeds, in the fortigate folder** and **ModSecurity IP lists, in the modsecurity folder**. Each feed is written once for every type ticked. A new install starts with neither ticked, and an update from an earlier version keeps FortiGate ticked. Under ModSecurity, and open to edits only while it is ticked (see [Using a list with ModSecurity](#using-a-list-with-modsecurity)):
  - **Where your server reads the lists**: a folder on the web server or an https link. The note under it says why a location can't be used, or when a link's SAS expires, with a warning from 14 days before. While ModSecurity is ticked, a location that can't be used stops **Save settings** until you correct it or clear it.
  - **Rule action**: **Log only** (the default), **Deny with status 403** or **Close with no reply (444)**.
  - **First rule id for new lists** (10,000 by default, 1 to 99,999). A list keeps the id it was first given; a new list takes this id, or the next one above every id kept.
- **ASN export**: whether **Export** writes the ticked feed types as well as the report, whether IPv4 and IPv6 are included, and whether they are written to separate files (all on by default)
- the export folder (**Choose folder**, or **Use default**)
- **Include IPv6 networks** (on by default). When off, IPv6 networks are left out of the dataset, searches and exports, an IPv6 address search says so, and the AbuseIPDB blacklist download asks for IPv4 addresses only. Turning it back on needs a dataset update, which the app offers.
- whether to ask before exporting large ranges, and the IPv4 and IPv6 prefix thresholds (whole numbers only)
- how many days of AbuseIPDB listings go into its feed: an address is in the feed while it was listed within this many days of the last download (30 by default, 1 to 365). Each address is kept in the list for 365 days after it was last listed, so raising the number brings back addresses it had left out (see [From the AbuseIPDB blacklist](#from-the-abuseipdb-blacklist)).

**API keys:** **Reset token** removes the saved IPinfo token and asks for a new one. **Set key** saves an AbuseIPDB key and **Remove key** removes it. Both are kept in your operating system's key store, never in the app's files.

**Exceptions:** the networks no feed may block (see [Exceptions](#exceptions)), saved with **Save exceptions**, with their own **Revert**.

Changes stay on the page until you save or revert them, also while you use the other pages. While something isn't saved, the Settings item in the list says **Not saved**, and closing the app asks before it discards the changes. Logging out, signing out or shutting down the computer closes the app without asking, and the changes are lost. A feed written while changes aren't saved uses the saved settings, and its result line says so.

## Where data is stored

Nothing is written to the program folder.

| What | Where |
|------|-------|
| Dataset database, the settings file `config.db` (your settings, your exceptions, the AbuseIPDB download count and the ModSecurity rule id given to each list, encrypted), the AbuseIPDB rolling list `abuseipdb.db`, the saved Azure service tags file and AWS ranges file | Your per-user app data folder: `%LOCALAPPDATA%\PoorMansFeed` on Windows, and `~/Library/Application Support/PoorMansFeed` on macOS |
| API token | Windows: `ipinfo_token` in the app data folder, protected with DPAPI for your Windows account. macOS: the `PoorMansFeed` item in your login keychain (Keychain Access lists it); the empty `ipinfo_token` file in the app data folder only marks that a token is saved. |
| AbuseIPDB API key | Windows: `abuseipdb_key` in the app data folder, protected with DPAPI for your Windows account. macOS: a second item in your login keychain under `PoorMansFeed` (account `abuseipdb-api-key`); the empty `abuseipdb_key` file in the app data folder only marks that a key is saved. |
| Settings file key | Windows: `config_key` in the app data folder, protected with DPAPI for your Windows account. macOS: a third item in your login keychain under `PoorMansFeed` (account `config-key`); the empty `config_key` file in the app data folder only marks that the key is saved. |
| Exports and feeds | The export folder (default `Documents/PoorMansFeed`): the ASN reports in the folder itself, FortiGate threat feeds in its `fortigate` subfolder and ModSecurity IP lists in its `modsecurity` subfolder |

After an update from 1.0.0, `settings.json.migrated` in the app data folder is your old settings file. It is kept as it was, and the app doesn't read it again.

### If the settings file can't be opened

If `config.db` can't be used when the app starts, the app shows what is wrong in place of its pages and does nothing else:

- If the file was made by a newer version, is damaged, can't be opened with the key saved on this computer (or was changed outside the app), or is missing although the app has run before, **Start fresh**, after a confirmation, keeps the old file as `config.unreadable.db` and makes a new one with the default settings and no exceptions, and the AbuseIPDB download count starts again. The dataset, the AbuseIPDB list and your API keys are kept. If you have a copy of a missing file, put it back in the app data folder first, and Start fresh opens it as it is.
- If the file is in use by another program, can't be written, or its key can't be read now (for example after you denied a keychain prompt), the app says so and changes nothing. Fix the cause and open the app again.
- If an `exceptions.json` was put in the app data folder before the app first made `config.db`, and it can't be read, the app offers **Start without exceptions**. Only if you choose it, the file is kept as `exceptions.unreadable.json`, so the networks it held can be copied back by hand, and the app starts with an empty list. Or fix `exceptions.json`, then close the app and open it again.

## Building from source

Run the app:

```bash
dotnet run --project src/PoorMansFeed
```

Build and test:

```bash
dotnet build
dotnet test
```

Build the release files with the publish script (PowerShell 7 on Windows, `build/publish.ps1`):

```bash
./build/publish.sh                  # win-x64 and osx-arm64
./build/publish.sh osx-arm64        # one runtime
```

Outputs, in `artifacts/` (git-ignored):

- `artifacts/win-x64/PoorMansFeed.exe`: a self-contained single-file app.
- `artifacts/osx-arm64/PoorMansFeed.app`: a self-contained single-file app in a macOS bundle, with the app icon. It is built on a Mac.

The version number comes from `<Version>` in `Directory.Build.props`.

## Roadmap

### Feature set 1

Planned next. AbuseIPDB is now a source (see [From the AbuseIPDB blacklist](#from-the-abuseipdb-blacklist)), and every feed can now be written as a ModSecurity IP list too, with the rules to load it (see [Using a list with ModSecurity](#using-a-list-with-modsecurity)); the aim is to keep feeds current on your firewalls without building and copying each one by hand.

**Keeping feeds current**

- **Saved feeds:** the app remembers every feed you write, with the ranges you ticked. **Refresh all** rebuilds them from the latest data under the same rules as when you wrote them: confirmed ranges go in, and new mixed or large ranges stay out until you tick them.
- **Publishing to Azure Blob Storage:** feeds are uploaded to a private container with a SAS URL that can only write to it, kept by the operating system. A feed is uploaded only when its ranges have changed. Each feed has a read-only URL to give to a FortiGate external connector or a ModSecurity rule, and the app warns before a SAS URL expires. AbuseIPDB's terms don't allow its data to be republished, so its feed goes only to private storage.
- **Refreshing with the window closed:** a command-line option runs Refresh all and the uploads without opening the window, so Windows Task Scheduler or macOS launchd can keep feeds current on a schedule. While the app is open, it can also refresh on a timer.

**Smaller feeds**

- **Merged feeds:** several sources combined into one feed, with overlapping and adjacent ranges joined, so a feed holds fewer entries and stays within FortiOS's limit of 131,072.

**More free sources**

- **Spamhaus DROP and Tor exits:** the [Spamhaus DROP](https://www.spamhaus.org/blocklists/do-not-route-or-peer/) list of netblocks hijacked or leased by criminal operations, and the Tor Project's list of Tor exit addresses, with each range's AS and country shown from the IPinfo data.

### Later

- nginx `deny` and `geo` files, and feed formats for other firewalls

## Project structure

```
PoorMansFeed/
├── PoorMansFeed.slnx
├── Directory.Build.props        # shared settings and the version number
├── global.json                  # SDK version
├── nuget.config                 # package source
├── build/                       # publish.sh and publish.ps1
├── src/
│   ├── PoorMansFeed.Core/   # dataset, database, search, export, exceptions, the cloud range tools and AbuseIPDB (no UI)
│   └── PoorMansFeed/        # Avalonia desktop app
├── tests/
│   └── PoorMansFeed.Tests/
├── LICENSE                      # MIT licence
└── THIRD-PARTY-NOTICES.txt      # licences of the components in the release builds
```

## Release notes

Each release's notes are on the [releases page](https://github.com/asfritecx/PoorMansFeed/releases).

## Disclaimer

PoorMansFeed is provided as is, without warranty of any kind. You use it, and the feeds it builds, at your own risk (see [LICENSE](LICENSE)).

- **A feed can block traffic you want.** A range you block can hold your own users, partners or services you rely on. Try a feed on a policy that allows and logs before you set it to deny (see [What you can use it for](#what-you-can-use-it-for)).
- **The source data can be wrong or out of date.** The ranges and addresses come from IPinfo, Google, Microsoft, AWS and AbuseIPDB, and networks change hands and move between countries after a feed is written. Rebuild your feeds regularly.
- **This project isn't affiliated with, endorsed by or sponsored by IPinfo, AbuseIPDB, Fortinet, Google, Microsoft or Amazon.** Their names are used only to say which data and products the app works with, and they remain their owners' trademarks.

## Licence

PoorMansFeed is released under the [MIT licence](LICENSE).

The release builds also carry the .NET runtime, Avalonia, SQLite and other components, each under its own licence. Their licences and notices are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which is published with each release.

The IP address data isn't part of this project. It comes from IPinfo under CC BY-SA 4.0 (see [Attribution](#attribution)).

## Attribution

IP address data powered by [IPinfo](https://ipinfo.io), licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). The app shows this attribution in its window, and every exported file whose entries come from IPinfo carries it: the ASN report and every ASN and cloud feed. The AbuseIPDB feed holds no IPinfo data and carries none.

The app's fonts are [Geist and Geist Mono](https://vercel.com/font) by Vercel, under the SIL Open Font License 1.1 (`src/PoorMansFeed/GraphiteUi/Fonts/OFL.txt`).
