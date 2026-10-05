# Lean.DataSource.SEC

LEAN data sources built from U.S. Securities and Exchange Commission filings, for the QuantConnect
data marketplace.

| Dataset | Data classes | Files |
|---|---|---|
| SEC Reports: 10-K, 10-Q and 8-K filings | `SECReport10K`, `SECReport10Q`, `SECReport8K` | `alternative/sec/<ticker>/<yyyyMMdd>_<form>.zip` |
| SEC Whales: Form 13F institutional holdings, every position as its manager filed it | `SEC13FHoldings`, a collection of `SEC13FHolding` | `alternative/sec/13f/<ticker>.zip`, one entry per publication day (the day after the EDGAR day the job reads), and `managers.csv` |

`SEC13FAlgorithm` and the `SECReport*Algorithm` files are the demonstration algorithms, in C# and
Python. The `listing-*.md` files are the marketplace listings.

## Processing

`DataProcessing` builds `process.dll`, which runs one dataset per invocation, chosen with the
`dataset-name` config key: `reports` (the default) or `13f`.

Both read the same environment and config:

| Key | Meaning |
|---|---|
| `QC_DATAFLEET_DEPLOYMENT_DATE` (environment) | The date the run is for, `yyyyMMdd` |
| `temp-output-directory` | Where the output is written; it must start empty |
| `processed-data-directory` | The published data an incremental 13F run adds to; defaults to the data folder |
| `raw-data-folder` | Where downloads are kept |
| `sec-user-agent-company-name`, `sec-user-agent-company-email` | The User-Agent the SEC asks automated readers for |
| `sec-requests-per-second` | Request rate against the SEC, 10 at most |

### Form 13F

With a deployment date the run reads that day's filings from EDGAR's daily index, and any of the
ten days before it whose index came late, and adds them to the published history. Without one, and
with `sec-13f-rebuild-history` set to `true`, it rebuilds the whole history: the SEC's Form 13F data
sets from 2013 as far as they reach, then EDGAR day by day. A full rebuild takes about an hour,
3 GB of downloads, 4 GB of output and some 10 GB of temporary disk.

The 13F run depends on three things in the LEAN data folder, none of which it downloads:

| Data | Path under the data folder | Used for |
|---|---|---|
| Map files | `equity/usa/map_files/map_files_<yyyyMMdd>.zip` | The ticker a security traded under on a filing date, which names its file. The zip is required: the run fails without one |
| Security database | `symbol-properties/security-database.csv` | Resolving a reported CUSIP, and the ISIN built from it, to a security. Without it only the N-PORT crosswalk resolves anything, and coverage falls from about 98 to 88 percent of reported value |
| Coarse universe files | `equity/usa/fundamental/coarse/<yyyyMMdd>.csv` | The close of each quarter's last trading day, from 2012. It decides whether a filing states values in dollars or thousands, vets the N-PORT crosswalk matches, and picks the fund an option on a fund family is written on. Only the quarter-end days are read, up to seven days back. Without them the run logs an error and falls back to the SEC's unit rule for the filing date |

The CUSIP to ticker crosswalk comes from the SEC's Form N-PORT data sets. It is downloaded once,
about 1.8 GB, and then kept beside the output as `nport-crosswalk.txt`.

## Tests

```
dotnet build tests/Tests.csproj
dotnet test tests/Tests.csproj
```

`SEC13FPilotTests` is explicit: it runs the processor over a folder of SEC 13F tables named by
`SEC13F_PILOT_RAW`, against the LEAN data folder named by `SEC13F_PILOT_DATA`.

## Implementing your own data source

See the [LeanDataSdk](https://github.com/QuantConnect/LeanDataSdk) repository.
