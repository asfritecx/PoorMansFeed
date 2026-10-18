# PoorMansFeed

Build your own IP threat feeds from freely available data.

PoorMansFeed is a desktop app for Windows and macOS. It keeps a local copy of the free [IPinfo Lite](https://ipinfo.io/products/free-ip-database) dataset and uses it to turn an AS number, or the address ranges that Google Cloud, Azure and AWS publish, into a feed your firewall can load and use in its policies. Cloud ranges are checked against IPinfo before they are written, so a range that IPinfo places with another network is shown to you unticked instead of going into the feed unseen.

Feeds are written for FortiGate today. Formats for ModSecurity, nginx and other firewalls are planned (see [Roadmap](#roadmap)).

| Firewall | Feed | Status |
|---|---|---|
| FortiGate (FortiOS) | IP address threat feed, loaded through an external connector | Supported |
| ModSecurity (with nginx or Apache) | IP list for the `@ipMatchFromFile` operator | Planned (feature set 1) |
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
- **Make the case to management.** The ASN report lists every network a provider holds, with its country and domain, which gives a proposal for better tooling something concrete to point at.

Before you deny a provider, check what you rely on that runs there: payment and webhook callbacks, uptime monitors, email delivery, crawlers you want to let in, partner integrations, and staff or customers on VPNs and cloud desktops. Watching what a feed would match, with a policy that allows and logs, before you set it to deny avoids surprises.

## Features

- **ASN feeds:** every network IPinfo lists for an AS (`AS15169` or `15169`), written as a FortiGate feed and a text report
- **Cloud provider feeds:** Google Cloud ranges for one region, Azure ranges for one service tag, and AWS ranges for one service and region, each confirmed against the local dataset
- **Lookups:** search by ASN or by IPv4 or IPv6 address, and copy the results to the clipboard, tab-separated, for a spreadsheet
- **Large range check:** networks broader than a set size are listed before anything is written, and are left out unless you tick them
- **Safe updates:** the dataset is checked against the SHA-256 that IPinfo publishes, built alongside the current copy and swapped in only when complete, so a cancelled or failed update leaves the existing data in use
- **Saved cloud files:** the Azure service tags file and the AWS ranges file are kept locally and checked for a newer version when their page opens
- **Per-user storage:** no administrator rights needed and nothing written to the program folder; the API token is kept by the operating system (DPAPI on Windows, the login keychain on macOS)

## Data sources

All of the data is free. Only IPinfo needs an account, for a free API token.

| Source | What it provides |
|---|---|
| [IPinfo Lite](https://ipinfo.io/products/free-ip-database) | Every network with its country, continent, ASN, AS name and AS domain. Updated daily, licensed under CC BY-SA 4.0 (see [Attribution](#attribution)). |
| Google Cloud [`cloud.json`](https://www.gstatic.com/ipranges/cloud.json) | Google Cloud ranges by region |
| Microsoft [Azure IP Ranges and Service Tags – Public Cloud](https://www.microsoft.com/en-us/download/details.aspx?id=56519) | Azure ranges by service tag, published weekly |
| AWS [`ip-ranges.json`](https://ip-ranges.amazonaws.com/ip-ranges.json) | AWS ranges by service and region |

## Requirements

- Windows (x64) or macOS (Apple silicon). Release builds are self-contained, so .NET does not need to be installed.
- An IPinfo API token ([free signup](https://ipinfo.io/signup)).
- Internet access to `ipinfo.io` for the dataset and, for the cloud pages, `www.gstatic.com` (Google Cloud), `www.microsoft.com` and `download.microsoft.com` (Azure) and `ip-ranges.amazonaws.com` (AWS).
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), 10.0.400 or later (`global.json`).

## Installing

Download the file for your platform from the [latest release](https://github.com/asfritecx/PoorMansFeed/releases/latest):

- **Windows:** `PoorMansFeed-{version}-win-x64.exe`, a single-file app. Run it directly.
- **macOS:** `PoorMansFeed-{version}-osx-arm64.zip`, which holds `PoorMansFeed.app`. Unzip it, move the app to Applications and open it.

Each release also has `SHA256SUMS.txt`, the SHA-256 of each file, so you can check a download before you run it. On macOS, `shasum -a 256 -c SHA256SUMS.txt --ignore-missing` in the download folder checks the files you have. On Windows, compare the output of `Get-FileHash <file>` in PowerShell with the line for that file.

The macOS app is ad-hoc signed, not notarised, so Gatekeeper may warn or block its first launch; allow it in System Settings under Privacy & Security. The Windows exe is not signed, so SmartScreen may warn too.

Run the app as your normal user account. It keeps everything in your user profile and will not start elevated (as administrator on Windows, or as root on macOS). Only one copy runs at a time.

There is no update check in the app yet. To update, download the new release and replace the old file. Your dataset, token and settings are kept in your user profile, so they carry over. On macOS, the first time a new version reads the saved token, macOS asks whether PoorMansFeed may use its item in your keychain; choose **Always Allow**. The app is ad-hoc signed, so macOS treats each new version as a different app. If you choose Deny, the update check says a token is needed; choose **Reset API token** to enter it again.

## First run

1. The app asks for your IPinfo API token. On macOS it is saved in your login keychain. On Windows it is saved in a file protected with DPAPI, so only your Windows account can decrypt it.
2. It downloads the dataset (about 32 MB compressed) and builds the search index. The window shows progress.
3. When it finishes, search from **IPInfo Lookup**, the page the app opens on.

Each time the app opens, it checks whether a newer dataset is available. If one is, choose **Update dataset**. You can search while the check runs, but not while an update is running. IPinfo updates the data daily at about 4 PM UTC and allows 10 dataset downloads a day per IP address, so one update a day is enough.

To use a different token, choose **Reset API token** in the app bar. It removes the saved token and asks for a new one.

## Searching

Type into the search box and press Enter or choose **Search**:

- `AS15169` or `15169` lists every network for that AS
- `8.8.8.8` lists the networks that contain that IPv4 address
- An IPv6 address works the same way

Addresses are read only in their plain form. A legacy form such as `010.0.0.1` or `192.168.1` is rejected rather than searched as a different address.

The list shows up to 300 networks. The summary above it gives the full count.

## Building a feed

Every feed goes into the `fortigate/` folder inside the export folder. A feed is plain text: a few `#` comment lines saying what it holds, the address family, when it was written (UTC), the number of entries and the IPinfo attribution, then one network per line. File names carry no date, so writing a feed again replaces the file at the same path.

### From an ASN

Search for the AS, then choose **Export**. Export writes every network for the AS, not only the rows on screen:

- `AS{number}.txt`: a report with a table of networks, country and domain, and the IPinfo attribution at the foot.
- `fortigate/AS{number}_ipv4.txt` and `fortigate/AS{number}_ipv6.txt`, or `fortigate/AS{number}.txt` when IPv4 and IPv6 are not split. The feed is written while **Write a Fortigate threat feed with each export** is on in Settings, which it is by default.

### From Google Cloud ranges

The list on the left of the window switches between **IPInfo Lookup**, **Google Cloud Lookup**, **Azure Lookup** and **AWS Lookup**. The window can be resized, and the results lists grow with it.

Google Cloud Lookup reads the ranges from `https://www.gstatic.com/ipranges/cloud.json`. Choose a region, then **Check ranges**. A prefix is confirmed when every overlapping network's AS domain is google.com. Confirmed rows start ticked. **Write feed** asks about large ranges, then writes into `fortigate/`:

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

### Large ranges

Before an export or a feed is written, networks broader than the thresholds are listed in a dialog. The defaults are IPv4 `/16` and IPv6 `/32`, and a network counts as large only when its prefix is shorter, so a `/16` is not large and a `/15` is. Every one starts unticked, so it is left out unless you tick it. This stops a very broad range from being added to a block list by accident.

### Export folder

Exports go to `Documents/PoorMansFeed` by default. You can choose another folder in Settings. **Open folder** opens the export folder.

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

A feed can hold IPv4 and IPv6 in one file, so the unsplit ASN feed works as one connector. FortiOS reads at most 10 MB or 131,072 entries per feed, whichever comes first, and leaves out anything beyond that. See Fortinet's [IP address external feed](https://docs.fortinet.com/document/fortigate/8.0.0/administration-guide/891236/ip-address-external-feed) guide for the details for your version.

## Settings

Open Settings from the app bar. You can change:

- **Include IPv6 networks** (on by default). When off, IPv6 networks are left out of the dataset, searches and exports, and an IPv6 address search says so. Turning it back on needs a dataset update, which the app offers.
- the export folder
- whether a Fortigate threat feed is written with each export, whether IPv4 and IPv6 are included, and whether they are written to separate files (all on by default)
- whether to ask before exporting large ranges, and the IPv4 and IPv6 prefix thresholds

## Where data is stored

Nothing is written to the program folder.

| What | Where |
|------|-------|
| Dataset database, settings, the saved Azure service tags file and AWS ranges file | Your per-user app data folder: `%LOCALAPPDATA%\PoorMansFeed` on Windows, and `~/Library/Application Support/PoorMansFeed` on macOS |
| API token | Windows: `ipinfo_token` in the app data folder, protected with DPAPI for your Windows account. macOS: the `PoorMansFeed` item in your login keychain (Keychain Access lists it); the empty `ipinfo_token` file in the app data folder only marks that a token is saved. |
| Exports and feeds | The export folder (default `Documents/PoorMansFeed`) |

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

Planned next. The aim is to add AbuseIPDB as a source, and to keep feeds current on your firewalls without building and copying each one by hand.

**AbuseIPDB**

This needs a free [AbuseIPDB](https://www.abuseipdb.com) account. Its API key is kept by the operating system, like the IPinfo token.

- **Rolling blacklist:** each download of the free blacklist (the 10,000 most reported addresses, up to 5 downloads a day) is merged into a list the app keeps, without duplicates. An address stays until it has been off the blacklist for the number of days you set, such as 30 or 60. The feed therefore holds more than a single download's 10,000 addresses, and drops the ones that have gone quiet.
- **Offending networks:** the list is grouped by AS and country using the IPinfo data, so you can see which providers hold the most reported addresses, and export a whole AS from there.
- **Abuse scores in lookups:** an address search also shows the address's AbuseIPDB confidence score and when it was last reported, from the free tier's 1,000 checks a day.

AbuseIPDB's terms don't allow its data to be republished, so these feeds are for your own firewalls, and they are only uploaded to private storage.

**ModSecurity**

- **ModSecurity lists:** every feed can also be written as a list for ModSecurity's `@ipMatchFromFile` operator, with nginx or Apache, together with a sample rule that denies the matches or only logs them. ModSecurity can read the list from disk or from an HTTPS address, such as a feed published to Azure Blob Storage.

**Keeping feeds current**

- **Saved feeds:** the app remembers every feed you write, with the ranges you ticked. **Refresh all** rebuilds them from the latest data under the same rules as when you wrote them: confirmed ranges go in, and new mixed or large ranges stay out until you tick them.
- **Publishing to Azure Blob Storage:** feeds are uploaded to a private container with a SAS URL that can only write to it, kept by the operating system. A feed is uploaded only when its ranges have changed. Each feed has a read-only URL to give to a FortiGate external connector or a ModSecurity rule, and the app warns before a SAS URL expires.
- **Refreshing with the window closed:** a command-line option runs Refresh all and the uploads without opening the window, so Windows Task Scheduler or macOS launchd can keep feeds current on a schedule. While the app is open, it can also refresh on a timer.

**Safer and smaller feeds**

- **Exceptions:** a list of networks that must never be blocked, such as your offices, payment providers, uptime monitors and partners. They are cut out of every feed before it is written, and the app shows which feeds they changed.
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
│   ├── PoorMansFeed.Core/   # dataset, database, search, export and the cloud range tools (no UI)
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
- **The source data can be wrong or out of date.** The ranges come from IPinfo, Google, Microsoft and AWS, and networks change hands and move between countries after a feed is written. Rebuild your feeds regularly.
- **This project isn't affiliated with, endorsed by or sponsored by IPinfo, Fortinet, Google, Microsoft or Amazon.** Their names are used only to say which data and products the app works with, and they remain their owners' trademarks.

## Licence

PoorMansFeed is released under the [MIT licence](LICENSE).

The release builds also carry the .NET runtime, Avalonia, SQLite and other components, each under its own licence. Their licences and notices are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which is published with each release.

The IP address data isn't part of this project. It comes from IPinfo under CC BY-SA 4.0 (see [Attribution](#attribution)).

## Attribution

IP address data powered by [IPinfo](https://ipinfo.io), licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). The app shows this attribution in its window, and every exported file carries it.

The app's fonts are [Geist and Geist Mono](https://vercel.com/font) by Vercel, under the SIL Open Font License 1.1 (`src/PoorMansFeed/GraphiteUi/Fonts/OFL.txt`).
