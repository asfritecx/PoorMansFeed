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
- **Keep cloud regions in countries you must not serve off your network,** such as the AWS China regions. A region is where the servers are, not who rents them (see [From AWS ranges](https://github.com/asfritecx/PoorMansFeed/wiki/Building-feeds#from-aws-ranges)).
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
- Each release's source is the commit its tag points to in this repository, so you can read it and build it yourself (see [Building from source](https://github.com/asfritecx/PoorMansFeed/wiki/Building-from-source)).

The SignPath Foundation signs open-source Windows apps for free, for projects with some track record. I'll apply once PoorMansFeed has one, and this section will say so if the Windows builds become signed.

## First run

1. The app asks for your IPinfo API token. On macOS it is saved in your login keychain. On Windows it is saved in a file protected with DPAPI, so only your Windows account can decrypt it.
2. It downloads the dataset (about 32 MB compressed) and builds the search index. The window shows progress.
3. When it finishes, search from **IPInfo Lookup**, the page the app opens on.

Each time the app opens, it checks whether a newer dataset is available. If one is, choose **Update dataset**. You can search while the check runs, but not while an update is running. IPinfo updates the data daily at about 4 PM UTC and allows 10 dataset downloads a day per IP address, so one update a day is enough.

To use a different token, open **Settings** and choose **Reset token** under API keys (on macOS, **Reset API token…** in the app menu does the same). It removes the saved token and asks for a new one.

## Guide

How to use the app is in the [wiki](https://github.com/asfritecx/PoorMansFeed/wiki), which describes the latest release:

- [Searching](https://github.com/asfritecx/PoorMansFeed/wiki/Searching): lookups by ASN or address, and checking an address with AbuseIPDB
- [Building feeds](https://github.com/asfritecx/PoorMansFeed/wiki/Building-feeds): feeds from an ASN, Google Cloud, Azure, AWS and the AbuseIPDB blacklist, large ranges, exceptions and the export folder
- [Using a feed on a FortiGate](https://github.com/asfritecx/PoorMansFeed/wiki/Using-a-feed-on-a-FortiGate): serving a feed and adding it as an external connector
- [Using a list with ModSecurity](https://github.com/asfritecx/PoorMansFeed/wiki/Using-a-list-with-ModSecurity): the lists, the rules that load them, and what to know on nginx and Apache
- [Settings and data](https://github.com/asfritecx/PoorMansFeed/wiki/Settings-and-data): every setting, where the app keeps its data, and what to do if the settings file can't be opened
- [Building from source](https://github.com/asfritecx/PoorMansFeed/wiki/Building-from-source): the build and test commands, and the project structure

## Roadmap

### Feature set 1

Planned next. AbuseIPDB is now a source (see [From the AbuseIPDB blacklist](https://github.com/asfritecx/PoorMansFeed/wiki/Building-feeds#from-the-abuseipdb-blacklist)), and every feed can now be written as a ModSecurity IP list too, with the rules to load it (see [Using a list with ModSecurity](https://github.com/asfritecx/PoorMansFeed/wiki/Using-a-list-with-ModSecurity)); the aim is to keep feeds current on your firewalls without building and copying each one by hand.

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

## Release notes

Each release's notes are on the [releases page](https://github.com/asfritecx/PoorMansFeed/releases).

## About this repository

This repository gets one commit per release. Development happens in a separate, private repository, and each release's source is published here. Issues and pull requests aren't accepted at the moment.

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
