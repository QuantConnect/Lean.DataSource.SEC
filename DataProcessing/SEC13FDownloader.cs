/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using QuantConnect.Configuration;
using QuantConnect.Data.Auxiliary;
using QuantConnect.DataSource;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Util;

[assembly: InternalsVisibleTo("Tests")]

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// Converts Form 13F filings into LEAN's per-security zips, one entry per filing
    /// date. Without QC_DATAFLEET_DEPLOYMENT_DATE it rebuilds the whole history, from the SEC's
    /// structured data sets through the last window published and from EDGAR's daily indexes after
    /// it; with it, it reads that day from EDGAR, with any recent day whose index came late, and
    /// folds them into the published history.
    ///
    /// Besides the data folders it reads these config keys: sec-user-agent-company-name and
    /// sec-user-agent-company-email, which the SEC asks automated readers for; sec-13f-rebuild-history,
    /// which a run without a deployment date needs; and, for checks only, sec-13f-edgar-from,
    /// sec-13f-edgar-until and sec-13f-rebuild-from, which move the rebuild's EDGAR days and skip the
    /// older data sets. From the data folder it reads the map files and security-database.csv, which
    /// resolve CUSIPs and tickers, and the coarse universe files, whose quarter-end closes decide
    /// VALUE's unit and vet the crosswalk.
    /// </summary>
    public class SEC13FDownloader : IDisposable
    {
        /// <summary>
        /// The "dataset-name" config value that selects this downloader. Spelled the same as the
        /// data class's ReportFolder, which stays the single source of truth for the output path.
        /// </summary>
        public const string DatasetName = "13f";

        /// <summary>Vendor name (matches the alternative/&lt;vendor&gt; data path).</summary>
        public static string VendorName => "sec";

        private const string DataSetsPageUrl = "https://www.sec.gov/data-research/sec-markets-data/form-13f-data-sets";
        private const string SecBaseUrl = "https://www.sec.gov";


        // A 13F-NT is a notice that the manager's holdings are reported on someone else's filing. It
        // carries no INFOTABLE at all, so it is not a zero position and must not become one.
        private const string NoticeSubmissionTypePrefix = "13F-NT";

        // EDGAR lists a day's filings in its daily index at about 22:05 ET (02:02 to 02:07 UTC the
        // next morning over six business days measured in September 2026). The daily job reads that
        // index at 01:00 ET the next day with a one hour timeout (schedule "0 1 * * 2-6", Date
        // Offset 1), which is why a point ends at midnight after its FILING_DATE and no earlier.

        /// <summary>Config key that starts the rebuild's EDGAR days on this yyyyMMdd instead of after the last data set.</summary>
        internal const string EdgarFromKey = "sec-13f-edgar-from";

        /// <summary>Config key that ends the rebuild's EDGAR days on this yyyyMMdd instead of yesterday.</summary>
        internal const string EdgarUntilKey = "sec-13f-edgar-until";

        /// <summary>
        /// Config key that makes the rebuild skip the data sets ending before this yyyyMMdd. For checks
        /// only: the rules do not depend on the old years, and cutting them takes a rebuild from ten
        /// minutes to one or two. A published history always starts in 2013.
        /// </summary>
        internal const string RebuildFromKey = "sec-13f-rebuild-from";

        /// <summary>The EDGAR days already folded into the published history, one yyyyMMdd per line.</summary>
        private const string EdgarStateFileName = "edgar-days.txt";

        /// <summary>
        /// Where the filing managers' names live, once each, so that the reported positions can
        /// carry nothing but the CIK.
        /// </summary>
        private const string ManagerNamesFileName = "managers.csv";

        /// <summary>How far back a daily run looks for a day whose index EDGAR published late.</summary>
        private const int EdgarLookbackDays = 10;

        /// <summary>
        /// How many holdings filings one daily run fetches before leaving the rest of a gap to the
        /// runs that follow. Each filing is its own round trip, so this and not a count of days is
        /// what a run's hour buys: over the June to August 2026 quarter a day carries 65 filings at
        /// the median and 312 at the ninetieth percentile, and the 45 day deadline of 2026-08-14
        /// carries 1,835, the heaviest of the quarter. The budget is that heaviest day, so catching
        /// up never asks a run for more work than an ordinary run already does every quarter.
        /// </summary>
        internal const int MaxFilingsPerRun = 2000;

        private const string PeriodFormat = "yyyyMMdd";

        /// <summary>Where the manager's CIK sits in a published row, which is read back by column.</summary>
        private const int ManagerCikColumn = 2;

        /// <summary>Where the accession sits in a published row, which names the filing its line came from.</summary>
        private const int AccessionColumn = 1;

        // N-PORT quarters folded into the crosswalk: a year reaches every security still trading.
        private const int NPortQuartersToFold = 4;

        // The SEC rule: VALUE in thousands for filings before this date, whole dollars from it.
        // DetectValueUnits checks each filing against it.
        private static readonly DateTime ValueInWholeDollarsFrom = new(2023, 1, 1);

        // How far, in log10, a crosswalk group's median price may sit from the close times a power
        // of a thousand and still be that security: about three times either way. Farther is another
        // security, as DeFi Technologies at $2 reaching the $129 Hashdex DEFI ETF through its ticker.
        private const double PriceMatchTolerance = 0.5;

        private static readonly string[] SecDateFormats = { "dd-MMM-yyyy", "d-MMM-yyyy", "yyyy-MM-dd", "MM/dd/yyyy" };

        private static readonly Regex ArchiveLinkRegex = new(
            @"href=""(?<href>[^""]*?/(?<name>[^""/]+_form13f\.zip))""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex QuarterArchiveRegex = new(
            @"^(?<year>\d{4})q(?<quarter>[1-4])_form13f\.zip$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WindowArchiveRegex = new(
            @"^(?<start>\d{2}[a-z]{3}\d{4})-(?<end>\d{2}[a-z]{3}\d{4})_form13f\.zip$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string _destinationDirectory;
        private readonly string _processedDataDirectory;
        private readonly string _archiveCacheDirectory;
        private readonly DateTime? _deploymentDate;

        private readonly SECEdgarClient _edgar = new();

        private readonly IMapFileProvider _mapFileProvider;

        // The security database's rows by CUSIP body and by ISIN, every row kept, since the database
        // repeats identifiers across the listings one company has had. See TradingDefinition.
        private readonly Dictionary<string, List<SecurityDefinition>> _definitionsByCusip;
        private readonly Dictionary<string, List<SecurityDefinition>> _definitionsByIsin;

        /// <summary>The equity issues of each six character issuer, for resolving an option CUSIP.</summary>
        private readonly Dictionary<string, List<string>> _equityIssuesByIssuer;

        /// <summary>
        /// CUSIP and filing date to security, misses included, since each miss scans the whole
        /// security database. The date is part of the key because resolution is point in time; the
        /// cache is cleared per archive so it stays the size of one window.
        /// </summary>
        private readonly Dictionary<(string Cusip, DateTime Date), SecurityIdentifier> _resolvedCusips = new();

        /// <summary>CUSIPs already tallied in the resolution summary, so its totals stay per CUSIP.</summary>
        private readonly HashSet<string> _countedCusips = new(StringComparer.Ordinal);

        /// <summary>Map file per security, so the point-in-time ticker costs one lookup per date.</summary>
        private readonly Dictionary<SecurityIdentifier, MapFile> _mapFiles = new();

        /// <summary>Accessions already folded in, so a submission can never be counted twice.</summary>
        private readonly HashSet<string> _processedAccessions = new(StringComparer.Ordinal);

        /// <summary>
        /// Every filing manager's name by CIK, as the most recent cover page stated it. Published
        /// once in managers.csv rather than on each of the reported positions.
        /// </summary>
        private readonly Dictionary<int, (DateTime Filed, string Name)> _managerNames = new();

        /// <summary>
        /// Rows waiting to be staged, flushed per archive. Keyed by security, because several CUSIPs
        /// can reach one security and their lines all belong in its file. Each row keeps the ticker
        /// of its filing date, which is the file it lands in.
        /// </summary>
        private readonly Dictionary<string, List<(string Ticker, HoldingsRow Row)>>
            _pendingSecurityRows = new(StringComparer.Ordinal);

        /// <summary>
        /// Where rows wait, one file per security, until the finalize pass. Outside the output
        /// folder because everything there is published.
        /// </summary>
        private readonly string _stagingDirectory =
            Path.Combine(Path.GetTempPath(), "sec-13f-staging", Guid.NewGuid().ToString("N"));

        /// <summary>Every security with rows staged, which is every security this run touched.</summary>
        private readonly HashSet<string> _stagedSecurities = new(StringComparer.Ordinal);

        /// <summary>Ticker and date to the security the map files say owns that ticker then, per archive.</summary>
        private readonly Dictionary<(string Ticker, DateTime Date), string> _tickerOwners = new();

        private long _conflictingTickers;
        private decimal _conflictingValue;
        private readonly List<string> _conflictSample = new();

        /// <summary>
        /// CUSIP to the ticker the SEC's N-PORT filings report for it. Built on first use rather than
        /// at construction: the archives may all resolve through the security database, and building
        /// it downloads a few hundred megabytes per quarter folded in. Settable for tests.
        /// </summary>
        internal Dictionary<string, SEC13FTickerCrosswalk.Entry> TickerCrosswalk
        {
            // Read from the published copy and written with the output, since the raw folder is not
            // restored between runs and the next run would otherwise rebuild it from 1.8 GB of N-PORT.
            get => _tickerCrosswalk ??= SEC13FTickerCrosswalk.Load(_processedDataDirectory, _destinationDirectory,
                NPortQuartersToFold, _edgar.UrlExists, (url, name) => _edgar.DownloadFile(url, name, _archiveCacheDirectory));
            set => _tickerCrosswalk = value;
        }

        private Dictionary<string, SEC13FTickerCrosswalk.Entry> _tickerCrosswalk;

        /// <summary>CUSIP to the security its crosswalk ticker named when the funds reported it.</summary>
        private readonly Dictionary<string, SecurityIdentifier> _crosswalkSecurities = new(StringComparer.Ordinal);

        private long _resolvedByCusip;
        private long _resolvedByIsin;
        private long _resolvedByTicker;
        private long _resolvedByOptionUnderlying;
        private long _unresolvedGroups;
        private long _malformedCusips;
        private decimal _resolvedValue;
        private decimal _unresolvedValue;
        private readonly HashSet<string> _unresolvedCusips = new(StringComparer.Ordinal);

        /// <summary>
        /// CUSIPs and filing dates resolved through the crosswalk rather than the security database,
        /// cleared with the resolution cache. Their groups are checked against the close, since a fund
        /// administrator's ticker can name another company.
        /// </summary>
        private readonly HashSet<(string Cusip, DateTime Date)> _crosswalkResolutions = new();

        /// <summary>CUSIPs and filing dates resolved through the security an option is written on.</summary>
        private readonly HashSet<(string Cusip, DateTime Date)> _optionResolutions = new();

        private long _optionSidesInferred;
        private long _optionLinesByPrice;
        private long _optionLinesWithoutOneMatch;

        private long _debtCusipsRejected;
        private long _mismatchedGroups;
        private decimal _mismatchedValue;

        /// <summary>The quarter-end closes that decide VALUE's unit and vet crosswalk resolutions.</summary>
        private readonly SEC13FClosePrices _closePrices;

        /// <summary>EDGAR days already folded into the history, published so a daily run never reads one twice.</summary>
        private readonly SortedSet<DateTime> _edgarDays = new();

        /// <summary>The last day folded in before this run, which dates what the published files state.</summary>
        private DateTime _foldedThrough;

        private readonly DateTime? _edgarFrom;
        private readonly DateTime? _edgarUntil;

        /// <summary>
        /// Creates a new instance writing to <paramref name="destinationDirectory"/>, merging with any
        /// previously processed data found in <paramref name="processedDataDirectory"/>. A null
        /// <paramref name="deploymentDate"/> rebuilds the history from the data sets and EDGAR; a date
        /// reads that day's filings from EDGAR, with any recent day it has not read yet. Downloads are
        /// kept under <paramref name="rawDataDirectory"/> so the next run finds them.
        /// </summary>
        public SEC13FDownloader(string destinationDirectory, string processedDataDirectory, DateTime? deploymentDate,
            string rawDataDirectory = null)
        {
            // The folder comes from the data type rather than a literal, so the writer and the
            // reader cannot drift apart.
            _destinationDirectory = Path.Combine(destinationDirectory, SEC13FHolding.ReportFolder);
            _processedDataDirectory = Path.Combine(processedDataDirectory, SEC13FHolding.ReportFolder);
            _deploymentDate = deploymentDate;

            // Downloads land in the raw folder, which the job archives after every run but does not
            // restore before the next, so it only saves work within a run.
            _archiveCacheDirectory = rawDataDirectory == null
                ? Path.Combine(Path.GetTempPath(), "sec-13f-archives")
                : Path.Combine(rawDataDirectory, SEC13FHolding.ReportFolder, "archives");
            Directory.CreateDirectory(_archiveCacheDirectory);

            // Zip, not disk: the security master ships map_files_<yyyyMMdd>.zip, and the disk provider
            // only sees loose csv files, so it would resolve almost nothing without failing.
            _mapFileProvider = new LocalZipMapFileProvider();
            _mapFileProvider.Initialize(new DefaultDataProvider());

            // The security database is in place wherever the job runs, as the map files are. Its rows are
            // read here rather than through LEAN's resolver, which keeps only the first row of an
            // identifier the database repeats; a data folder without it, as in the unit tests, reads as empty.
            var securityDatabasePath = Path.Combine(
                Globals.GetDataFolderPath("symbol-properties"), "security-database.csv");
            SecurityDefinition.TryRead(new DefaultDataProvider(), securityDatabasePath, out var definitions);
            definitions ??= new List<SecurityDefinition>();

            _definitionsByCusip = IndexDefinitions(definitions, definition => DatabaseCusip(definition.CUSIP));
            _definitionsByIsin = IndexDefinitions(definitions, definition => definition.ISIN);

            // The equity issues each issuer has, which is what an option CUSIP is resolved through.
            _equityIssuesByIssuer = _definitionsByCusip.Keys
                .Where(cusip => cusip.Length >= IssuerLength + 2 &&
                                char.IsDigit(cusip[IssuerLength]) && char.IsDigit(cusip[IssuerLength + 1]))
                .GroupBy(cusip => cusip.Substring(0, IssuerLength), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => group.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            _closePrices = new SEC13FClosePrices(Path.Combine(Globals.DataFolder, "equity", "usa", "fundamental", "coarse"));
            if (!_closePrices.Available)
            {
                Log.Error("SEC13FDownloader(): the coarse universe files are missing from the data folder. Without the " +
                          "quarter-end closes VALUE takes the SEC unit rule of each filing's date, and a crosswalk match whose " +
                          "CUSIP carries letters in its issue number, which only a price can confirm, is dropped.");
            }

            _edgarFrom = ParseOptionalDate(Config.Get(EdgarFromKey), EdgarFromKey);
            _edgarUntil = ParseOptionalDate(Config.Get(EdgarUntilKey), EdgarUntilKey);
        }

        /// <summary>A yyyyMMdd config value, or null when the key is not set.</summary>
        private static DateTime? ParseOptionalDate(string value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParseExact(value.Trim(), DateFormat.EightCharacter, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                return date;
            }

            throw new ArgumentException($"SEC13FDownloader(): {key} '{value}' is not yyyyMMdd");
        }

        /// <summary>
        /// Runs the download/convert. Failures throw rather than return false, so the caller logs
        /// the actual reason: a guard stopping the run and a network outage are not the same thing.
        /// </summary>
        public void Run()
        {
            RequireEmptyDestination();
            RequirePublishedHistoryForIncrementalRun();
            ReadEdgarState();

            if (_deploymentDate == null)
            {
                // The data sets as far as they reach, then EDGAR day by day: the filings the daily
                // job reads, with the stamps it gives them.
                var archives = GetArchives();
                var edgarFrom = _edgarFrom ?? archives[archives.Count - 1].End.AddDays(1);
                var rebuildFrom = ParseOptionalDate(Config.Get(RebuildFromKey), RebuildFromKey);
                var selected = ArchivesBefore(archives, edgarFrom)
                    .Where(archive => rebuildFrom == null || archive.End >= rebuildFrom)
                    .ToList();
                _edgarFirstDay = edgarFrom;
                Log.Trace($"SEC13FDownloader.Run(): {archives.Count} archives published, processing {selected.Count} " +
                          $"through {selected.LastOrDefault()?.End:yyyy-MM-dd}, then EDGAR from {edgarFrom:yyyy-MM-dd}");

                foreach (var archive in selected)
                {
                    ProcessArchive(archive);
                    FlushPendingRows();
                }

                ProcessEdgarDays(EdgarDaysToRead(edgarFrom, _edgarUntil ?? YesterdayInNewYork()));
            }
            else
            {
                // The deployment date, and any recent day whose index EDGAR published late. The
                // window also reaches back to the day after the last one folded in, or the days a
                // run missed while EDGAR blocked the job fall out of the lookback and no later run
                // ever reads them, with every run since reporting success.
                ProcessEdgarDays(EdgarDaysToRead(FirstDayToCatchUp(_deploymentDate.Value), _deploymentDate.Value));
            }

            FinalizeSecurityFiles();
            WriteEdgarState();
            LogResolutionSummary();
        }

        /// <summary>
        /// Reads each day's filings from EDGAR and folds them in like an archive. A weekday without an
        /// index is a holiday or a late index; it is not recorded, so a later run can still read it.
        ///
        /// A daily run stops once it has fetched a heaviest day's worth of filings, and the days left
        /// are read by the runs that follow. Without that, a gap wider than the run's hour fails every
        /// run, and since nothing is published until the last day of it is read, the gap grows by a
        /// day each time and the data set never moves again.
        /// </summary>
        private void ProcessEdgarDays(List<DateTime> days)
        {
            var read = 0;
            var fetched = 0;
            foreach (var day in days)
            {
                var built = SEC13FEdgarDay.Build(day, _archiveCacheDirectory, _edgar.ListDirectory,
                    url => _edgar.GetText(url), FilingBudget(read, fetched));
                if (built.OverBudget)
                {
                    break;
                }

                if (built.Path == null)
                {
                    Log.Trace($"SEC13FDownloader.ProcessEdgarDays(): EDGAR lists no index for {day:yyyy-MM-dd}");
                    continue;
                }

                ProcessArchive(new Archive(Path.GetFileName(built.Path), SECEdgarIndex.IndexUrl(day), day, day, IsDaily: true));
                FlushPendingRows();
                _edgarDays.Add(day);
                fetched += built.Filings;
                read++;
            }

            Log.Trace($"SEC13FDownloader.ProcessEdgarDays(): read {read} of {days.Count} EDGAR days");
        }

        /// <summary>
        /// The data sets whose window ends before EDGAR takes over. A window that straddles the switch
        /// would have its filings read from both sources or from neither, so it stops the run.
        /// </summary>
        internal static List<Archive> ArchivesBefore(List<Archive> archives, DateTime edgarFrom)
        {
            var straddling = archives.FirstOrDefault(archive => archive.Start < edgarFrom && archive.End >= edgarFrom);
            if (straddling != null)
            {
                throw new InvalidOperationException(
                    $"SEC13FDownloader.ArchivesBefore(): {straddling.Name} covers {edgarFrom:yyyy-MM-dd}. EDGAR has to " +
                    "take over the day after a data set ends, or that window would be read twice or not at all.");
            }

            return archives.Where(archive => archive.End < edgarFrom).ToList();
        }

        /// <summary>
        /// Where an incremental run starts reading: the lookback, or further back when the last day
        /// folded in is older than that, so a gap left by an outage is caught up rather than skipped.
        /// </summary>
        internal DateTime FirstDayToCatchUp(DateTime deploymentDate)
        {
            var lookback = deploymentDate.AddDays(-EdgarLookbackDays);
            if (_edgarDays.Count == 0)
            {
                return lookback;
            }

            var afterTheLast = _edgarDays.Max.AddDays(1);
            return afterTheLast < lookback ? afterTheLast : lookback;
        }

        /// <summary>
        /// How many filings a run may still fetch, having read <paramref name="read"/> days and
        /// fetched <paramref name="fetched"/> filings. The rebuild reads its whole window by design,
        /// and a daily run gives its first day the budget whole: a day is the unit of work and cannot
        /// be read in half, and its heaviest is what an ordinary run already does every quarter.
        /// </summary>
        internal int FilingBudget(int read, int fetched)
        {
            return _deploymentDate == null || read == 0 ? int.MaxValue : MaxFilingsPerRun - fetched;
        }

        /// <summary>
        /// The weekdays from one date to another that no earlier run has folded in, never before the
        /// day EDGAR took over from the data sets. These are the days a run may read; how many of them
        /// it does read is decided as it goes, by what each one costs.
        /// </summary>
        internal List<DateTime> EdgarDaysToRead(DateTime from, DateTime until)
        {
            var days = new List<DateTime>();
            var first = from.Date < _edgarFirstDay ? _edgarFirstDay : from.Date;
            for (var day = first; day <= until.Date; day = day.AddDays(1))
            {
                if (day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday && !_edgarDays.Contains(day))
                {
                    days.Add(day);
                }
            }

            return days;
        }

        /// <summary>The last complete EDGAR day when the rebuild runs, in the SEC's time zone.</summary>
        private static DateTime YesterdayInNewYork()
        {
            return DateTime.UtcNow.ConvertFromUtc(TimeZones.NewYork).Date.AddDays(-1);
        }

        /// <summary>
        /// Stops a run into a destination that already holds files. The job hands it over empty, and
        /// whatever an earlier run left there would be read back and published again.
        /// </summary>
        internal void RequireEmptyDestination()
        {
            if (Directory.Exists(_destinationDirectory) && Directory.EnumerateFileSystemEntries(_destinationDirectory).Any())
            {
                throw new InvalidOperationException(
                    $"SEC13FDownloader.Run(): {_destinationDirectory} is not empty. The destination has to start empty, " +
                    "or the files an earlier run left there would be published again.");
            }
        }

        /// <summary>
        /// Stops an incremental run that cannot see the published history: merging into nothing would
        /// republish thirteen years as one three month window and still report success.
        /// </summary>
        internal void RequirePublishedHistoryForIncrementalRun()
        {
            if (_deploymentDate == null)
            {
                return;
            }

            var published = Directory.Exists(_processedDataDirectory)
                ? Directory.EnumerateFiles(_processedDataDirectory, "*.zip").Count()
                : 0;

            if (published == 0)
            {
                throw new InvalidOperationException(
                    $"SEC13FDownloader.Run(): the incremental run for {_deploymentDate:yyyy-MM-dd} found no published " +
                    $"history under {_processedDataDirectory}. Publishing this window on its own would truncate the " +
                    "dataset to it, so the run stops instead of reporting success.");
            }

            Log.Trace($"SEC13FDownloader.Run(): {published} published securities to merge into, " +
                      $"read from {_processedDataDirectory}");

            RequireEdgarStateForIncrementalRun();
        }

        /// <summary>
        /// Stops an incremental run that cannot tell which EDGAR days are already published: reading
        /// one again would add its filings a second time under a later stamp.
        /// </summary>
        internal void RequireEdgarStateForIncrementalRun()
        {
            var path = Path.Combine(_processedDataDirectory, EdgarStateFileName);
            if (File.Exists(path))
            {
                return;
            }

            throw new InvalidOperationException(
                $"SEC13FDownloader.Run(): the incremental run for {_deploymentDate:yyyy-MM-dd} found no EDGAR state at " +
                $"{path}, so it cannot tell a day already published from a new one. Republish the dataset with a full " +
                "history run to restore it.");
        }

        /// <summary>
        /// One archive: the zip name, its absolute URL, and the filing-receipt window it covers. A
        /// daily one is a day read from EDGAR, which may hold no holdings filing at all.
        /// </summary>
        internal sealed record Archive(string Name, string Url, DateTime Start, DateTime End, bool IsDaily = false);

        /// <summary>
        /// Scrapes the data sets page for every published archive, oldest first, so a new window is
        /// picked up without a code change.
        /// </summary>
        private List<Archive> GetArchives()
        {
            var html = _edgar.GetText(DataSetsPageUrl);
            var archives = new Dictionary<string, Archive>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in ArchiveLinkRegex.Matches(html))
            {
                var name = match.Groups["name"].Value;
                if (archives.ContainsKey(name))
                {
                    continue;
                }

                var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
                var url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? href
                    : SecBaseUrl + (href.StartsWith("/", StringComparison.Ordinal) ? href : "/" + href);

                var (start, end) = ParseArchiveWindow(name);
                archives[name] = new Archive(name, url, start, end);
            }

            if (archives.Count == 0)
            {
                throw new InvalidOperationException(
                    $"SEC13FDownloader.GetArchives(): no _form13f.zip links found on {DataSetsPageUrl}. " +
                    "The page layout changed, or the request was blocked.");
            }

            return archives.Values.OrderBy(x => x.End).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Reads the filing-receipt window out of an archive name. Two shapes are published:
        /// "2013q2_form13f.zip" for the quarterly archives and "01mar2026-31may2026_form13f.zip" for
        /// the rolling windows that replaced them.
        /// </summary>
        private static (DateTime start, DateTime end) ParseArchiveWindow(string name)
        {
            var quarter = QuarterArchiveRegex.Match(name);
            if (quarter.Success)
            {
                var year = int.Parse(quarter.Groups["year"].Value, CultureInfo.InvariantCulture);
                var number = int.Parse(quarter.Groups["quarter"].Value, CultureInfo.InvariantCulture);
                var start = new DateTime(year, number * 3 - 2, 1);
                return (start, start.AddMonths(3).AddDays(-1));
            }

            var window = WindowArchiveRegex.Match(name);
            if (window.Success)
            {
                return (ParseWindowDate(window.Groups["start"].Value), ParseWindowDate(window.Groups["end"].Value));
            }

            throw new FormatException(
                $"SEC13FDownloader.ParseArchiveWindow(): '{name}' matches neither the quarterly nor the " +
                "rolling-window naming. A third naming scheme appeared and the selection would be wrong.");
        }

        /// <summary>Parses a "01mar2026" style date out of a rolling-window archive name.</summary>
        private static DateTime ParseWindowDate(string value)
        {
            return DateTime.ParseExact(value, "ddMMMyyyy", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The first day EDGAR is responsible for: the day after the last data set the rebuild read.
        /// Earlier days came from the data sets and are not in the list, so a daily run must never
        /// read them from EDGAR; it did once, and counted their filings twice.
        /// </summary>
        private DateTime _edgarFirstDay;

        private const string EdgarFirstDayPrefix = "#from ";

        /// <summary>Loads the EDGAR days an earlier run published. A full run reads them all again and starts empty.</summary>
        internal void ReadEdgarState()
        {
            if (_deploymentDate == null)
            {
                return;
            }

            var path = Path.Combine(_processedDataDirectory, EdgarStateFileName);
            var header = false;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(EdgarFirstDayPrefix, StringComparison.Ordinal))
                {
                    _edgarFirstDay = DateTime.ParseExact(line.Substring(EdgarFirstDayPrefix.Length).Trim(),
                        DateFormat.EightCharacter, CultureInfo.InvariantCulture);
                    header = true;
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    _edgarDays.Add(DateTime.ParseExact(line.Trim(), DateFormat.EightCharacter, CultureInfo.InvariantCulture));
                }
            }

            if (!header)
            {
                throw new InvalidDataException($"SEC13FDownloader.ReadEdgarState(): {path} does not say which day EDGAR " +
                    "took over from the data sets, so a daily run could read a day twice. Republish with a full history run.");
            }

            // What the published files state, they state as of this day. A full run leaves it at
            // MinValue, since it publishes everything itself.
            _foldedThrough = _edgarDays.Max;

            Log.Trace($"SEC13FDownloader.ReadEdgarState(): EDGAR from {_edgarFirstDay:yyyy-MM-dd}, {_edgarDays.Count} " +
                      $"days already published, the last {_foldedThrough:yyyy-MM-dd}");
        }

        /// <summary>Publishes the day EDGAR took over and the days folded in so far, oldest first.</summary>
        internal void WriteEdgarState()
        {
            Directory.CreateDirectory(_destinationDirectory);
            SEC13FFiles.WriteThenMove(Path.Combine(_destinationDirectory, EdgarStateFileName), stream =>
            {
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(EdgarFirstDayPrefix + _edgarFirstDay.ToString(DateFormat.EightCharacter, CultureInfo.InvariantCulture));
                writer.Write('\n');
                foreach (var day in _edgarDays)
                {
                    writer.Write(day.ToString(DateFormat.EightCharacter, CultureInfo.InvariantCulture));
                    writer.Write('\n');
                }
            });
        }

        /// <summary>
        /// Reads one archive end to end: the submissions that carry holdings, the confidential
        /// treatment flags, and then the positions themselves, streamed rather than loaded.
        /// </summary>
        internal void ProcessArchive(Archive archive)
        {
            // One window's worth of resolutions is all that is worth holding: the key carries the
            // filing date, so entries from a window already read can never be hit again.
            _resolvedCusips.Clear();
            _crosswalkResolutions.Clear();
            _optionResolutions.Clear();
            _tickerOwners.Clear();

            var path = DownloadArchive(archive);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var submissions = ReadSubmissions(zip, archive);
            if (submissions.Count == 0 && !archive.IsDaily)
            {
                // The windows do not overlap, so an archive with no holdings means the layout moved,
                // and carrying on would publish a history with this window missing. A single EDGAR
                // day can legitimately carry none.
                throw new InvalidDataException(
                    $"SEC13FDownloader.ProcessArchive(): {archive.Name} yielded no holdings submissions. " +
                    "An archive that contributes nothing means the upstream layout moved.");
            }

            ApplyConfidentialFlags(zip, archive, submissions);
            ReadCoverPages(zip, archive, submissions);

            var holdings = ReadInfoTable(zip, archive, submissions);
            Log.Trace($"SEC13FDownloader.ProcessArchive(): {archive.Name}: {submissions.Count} submissions, " +
                      $"{holdings.Count} (security, period, release) groups");

            EmitHoldings(holdings);
        }

        internal string DownloadArchive(Archive archive)
        {
            return _edgar.DownloadFile(archive.Url, archive.Name, _archiveCacheDirectory);
        }

        /// <summary>One filing that carries holdings, keyed in the caller by accession number.</summary>
        internal sealed class Submission
        {
            public string Accession { get; init; }
            public int Cik { get; init; }
            public DateTime FilingDate { get; init; }
            public DateTime Period { get; init; }
            public bool ConfidentialOmitted { get; set; }

            /// <summary>The submission type as filed, 13F-HR or 13F-HR/A.</summary>
            public string FormType { get; init; }

            /// <summary>
            /// Whether an amendment restates the whole report or only adds holdings, as the filer
            /// declared it on the cover page. Empty on an original filing.
            /// </summary>
            public string AmendmentType { get; set; } = string.Empty;

            /// <summary>The amendment's sequence number, or null on an original filing.</summary>
            public int? AmendmentNumber { get; set; }

            /// <summary>The filing manager's name as the cover page states it.</summary>
            public string ManagerName { get; set; } = string.Empty;

            /// <summary>
            /// The date a previously confidential filing was originally made, which the cover page
            /// carries as DATEREPORTED on about two filings in a thousand.
            /// </summary>
            public DateTime? DateReported { get; set; }
        }

        /// <summary>
        /// Reads SUBMISSION.tsv, keeping only the filings that carry positions and that no earlier
        /// archive already contributed.
        /// </summary>
        private Dictionary<string, Submission> ReadSubmissions(ZipArchive zip, Archive archive)
        {
            var submissions = new Dictionary<string, Submission>(StringComparer.Ordinal);
            var notices = 0;

            foreach (var (columns, fields) in ReadTable(zip, archive, "SUBMISSION.tsv",
                         "ACCESSION_NUMBER", "FILING_DATE", "SUBMISSIONTYPE", "CIK", "PERIODOFREPORT"))
            {
                var submissionType = fields[columns["SUBMISSIONTYPE"]];
                if (submissionType.StartsWith(NoticeSubmissionTypePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    notices++;
                    continue;
                }

                var accession = fields[columns["ACCESSION_NUMBER"]];
                if (!_processedAccessions.Add(accession))
                {
                    continue;
                }

                submissions[accession] = new Submission
                {
                    Accession = accession,
                    Cik = int.Parse(fields[columns["CIK"]], NumberStyles.Integer, CultureInfo.InvariantCulture),
                    // A daily index can name a filing whose own date is an earlier day: one 13F-HR
                    // across the 2026 Q2 and Q3 indexes, filed 2026-04-27 and listed on 04-28. The
                    // row carries the day it was listed, which is the first day the job could have
                    // it, so nothing reads in a backtest before it was public. The cost is that a
                    // rebuild reads that accession from the data sets, which state 04-27, and
                    // republishes the row a day earlier.
                    FilingDate = archive.IsDaily
                        ? archive.End
                        : ParseSecDate(fields[columns["FILING_DATE"]], archive, "FILING_DATE"),
                    Period = ParseSecDate(fields[columns["PERIODOFREPORT"]], archive, "PERIODOFREPORT"),
                    FormType = submissionType.Trim()
                };
            }

            Log.Trace($"SEC13FDownloader.ReadSubmissions(): {archive.Name}: {submissions.Count} holdings filings, " +
                      $"{notices} notices skipped");
            return submissions;
        }

        /// <summary>
        /// Marks the filings that withheld positions under confidential treatment (SUMMARYPAGE
        /// ISCONFIDENTIALOMITTED). COVERPAGE CONFDENIEDEXPIRED is not read: it means the opposite,
        /// positions withheld before and disclosed in this filing.
        /// </summary>
        private void ApplyConfidentialFlags(ZipArchive zip, Archive archive, Dictionary<string, Submission> submissions)
        {
            var flagged = 0;

            foreach (var (columns, fields) in ReadTable(
                         zip, archive, "SUMMARYPAGE.tsv", "ACCESSION_NUMBER", "ISCONFIDENTIALOMITTED"))
            {
                if (!IsYes(fields[columns["ISCONFIDENTIALOMITTED"]]) ||
                    !submissions.TryGetValue(fields[columns["ACCESSION_NUMBER"]], out var submission) ||
                    submission.ConfidentialOmitted)
                {
                    continue;
                }

                submission.ConfidentialOmitted = true;
                flagged++;
            }

            Log.Trace($"SEC13FDownloader.ApplyConfidentialFlags(): {archive.Name}: {flagged} filings flagged");
        }

        /// <summary>
        /// Reads COVERPAGE.tsv for what the filer declared about the filing itself: the manager's
        /// name, whether an amendment restates or supplements, and the date a filing withheld under
        /// confidential treatment was originally made.
        ///
        /// The names are collected rather than written onto every line. A name repeated on each of
        /// the hundred and twenty three million reported positions would be the largest column in
        /// the dataset and would restate a fund's whole history the day it renames itself, so the
        /// lines carry the CIK and the names are published once, in managers.csv.
        /// </summary>
        private void ReadCoverPages(ZipArchive zip, Archive archive, Dictionary<string, Submission> submissions)
        {
            var amendments = 0;
            var reported = 0;

            foreach (var (columns, fields) in ReadTable(zip, archive, "COVERPAGE.tsv",
                         "ACCESSION_NUMBER", "AMENDMENTNO", "AMENDMENTTYPE", "DATEREPORTED", "FILINGMANAGER_NAME"))
            {
                if (!submissions.TryGetValue(fields[columns["ACCESSION_NUMBER"]], out var submission))
                {
                    // A notice, or a filing an earlier archive already contributed.
                    continue;
                }

                // No field of managers.csv may carry a comma: the file is split on every one.
                var name = Sanitize(fields[columns["FILINGMANAGER_NAME"]]);
                if (name.Length > 0)
                {
                    submission.ManagerName = name;

                    // The name of the latest filing wins, rather than of the last row read: neither
                    // a cover page table nor the order a run reads days in is sorted by filing date,
                    // so a day whose index came late would otherwise roll the name back.
                    if (!_managerNames.TryGetValue(submission.Cik, out var known) || known.Filed <= submission.FilingDate)
                    {
                        _managerNames[submission.Cik] = (submission.FilingDate, name);
                    }
                }

                var amendmentType = Sanitize(fields[columns["AMENDMENTTYPE"]]);
                if (amendmentType.Length > 0)
                {
                    submission.AmendmentType = amendmentType;
                    amendments++;
                }

                var amendmentNumber = fields[columns["AMENDMENTNO"]].Trim();
                if (amendmentNumber.Length > 0 &&
                    int.TryParse(amendmentNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    submission.AmendmentNumber = number;
                }

                // DATEREPORTED is the date a confidential filing was originally made, not a
                // timestamp for the positions: it is filled on about two filings in a thousand and
                // is left null on the rest rather than falling back to the filing date.
                var dateReported = fields[columns["DATEREPORTED"]].Trim().Split(' ')[0];
                if (dateReported.Length > 0 &&
                    DateTime.TryParseExact(dateReported, SecDateFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed))
                {
                    submission.DateReported = parsed;
                    reported++;
                }
            }

            Log.Trace($"SEC13FDownloader.ReadCoverPages(): {archive.Name}: {amendments} amendments declared, " +
                      $"{reported} filings carrying a confidential report date, {_managerNames.Count} managers known");
        }

        /// <summary>
        /// Makes a free text field safe to write into a comma separated line that LEAN splits on
        /// every comma. The alternative, quoting the field, would need every reader of the dataset
        /// to parse quotes, which the engine's own CSV path does not.
        /// </summary>
        private static string Sanitize(string value)
        {
            // The runs of whitespace the replacements leave are collapsed, or a name written
            // "Pershing Square Capital Management, L.P." would be published with two spaces.
            return Whitespace.Replace(value.Replace(',', ' ').Replace('"', ' '), " ").Trim();
        }

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        private static readonly Regex OtherManagerSeparator = new(@"[,;\s]+", RegexOptions.Compiled);

        /// <summary>
        /// The other managers' sequence numbers joined by semicolons. Filers separate them with
        /// commas, spaces or both, and write NONE or 0 when there are none, which comes out empty.
        /// </summary>
        internal static string FormatOtherManagers(string value)
        {
            return string.Join(';', OtherManagerSeparator.Split(value.Replace('"', ' ').Trim())
                .Where(token => token.Length > 0 && token != "0" &&
                                !token.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
                                !token.Equals("N/A", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// One line of one filing's information table, carried through to publication unchanged.
        /// Nothing is added to anything: two managers reporting the same security on the same day,
        /// or one manager reporting it twice, give two of these and stay apart.
        /// </summary>
        internal sealed class PositionLine
        {
            /// <summary>The filing this line was reported on, which carries the manager and the dates.</summary>
            public Submission Submission { get; init; }

            public string TitleOfClass { get; init; }
            public decimal? Amount { get; init; }
            public string AmountType { get; init; }

            /// <summary>VALUE as the manager stated it, in whatever unit the filing used.</summary>
            public decimal? ReportedValue { get; init; }

            /// <summary>The power of ten that turns <see cref="ReportedValue"/> into whole dollars.</summary>
            public int ValueScale { get; init; }

            public string PutCall { get; set; }
            public string InvestmentDiscretion { get; init; }
            public string OtherManager { get; init; }
            public decimal? VotingSole { get; init; }
            public decimal? VotingShared { get; init; }
            public decimal? VotingNone { get; init; }

            /// <summary>The line's market value in dollars, which the coverage summary totals.</summary>
            public decimal DollarValue => (ReportedValue ?? 0m) * SEC13FHolding.PowerOfTen(ValueScale);
        }

        /// <summary>
        /// The reported lines sharing one (CUSIP, period, filing date). They are grouped only so
        /// that the identity of the security is resolved once for all of them, and so that the
        /// crosswalk check has every implied price of the group to judge by; the lines themselves
        /// are published one by one.
        /// </summary>
        internal sealed class Holding
        {
            /// <summary>Every line of the group, in the order the information table listed them.</summary>
            public List<PositionLine> Lines { get; } = new();

            /// <summary>Every reported value of the group, for the coverage summary.</summary>
            public decimal ReportedValue => Lines.Sum(line => line.DollarValue);

            /// <summary>
            /// The implied price, VALUE over SSHPRNAMT as reported, of every share line in the group,
            /// which is what a crosswalk resolution is checked against the close with.
            /// </summary>
            public List<double> SharePrices { get; } = new();
        }

        /// <summary>
        /// What the lines of one group share: the CUSIP the manager named, the quarter it reported
        /// and the day the filing reached EDGAR.
        /// </summary>
        internal readonly record struct HoldingKey(string Cusip, DateTime Period, DateTime FilingDate);

        /// <summary>
        /// Streams INFOTABLE.tsv and collects every reported line into its group. Nothing is summed
        /// and no line is dropped: a line is published as the manager filed it, including the option
        /// and debt lines the aggregated model used to fold away, each with its own amount type.
        /// </summary>
        internal Dictionary<HoldingKey, Holding> ReadInfoTable(
            ZipArchive zip, Archive archive, Dictionary<string, Submission> submissions)
        {
            var holdings = new Dictionary<HoldingKey, Holding>();
            var units = DetectValueUnits(zip, archive, submissions);
            var lines = 0L;

            foreach (var (columns, fields) in ReadTable(zip, archive, "INFOTABLE.tsv",
                         "ACCESSION_NUMBER", "CUSIP", "TITLEOFCLASS", "VALUE", "SSHPRNAMT", "SSHPRNAMTTYPE",
                         "PUTCALL", "INVESTMENTDISCRETION", "OTHERMANAGER",
                         "VOTING_AUTH_SOLE", "VOTING_AUTH_SHARED", "VOTING_AUTH_NONE"))
            {
                var accession = fields[columns["ACCESSION_NUMBER"]];
                if (!submissions.TryGetValue(accession, out var submission))
                {
                    // A notice, or a filing an earlier archive already contributed.
                    continue;
                }

                var cusip = fields[columns["CUSIP"]].Trim().ToUpperInvariant();
                if (cusip.Length == 0)
                {
                    continue;
                }

                var key = new HoldingKey(cusip, submission.Period, submission.FilingDate);
                if (!holdings.TryGetValue(key, out var group))
                {
                    holdings[key] = group = new Holding();
                }

                var shareType = fields[columns["SSHPRNAMTTYPE"]].Trim();
                var putCall = fields[columns["PUTCALL"]].Trim();
                var amount = ParseOptionalDecimal(fields[columns["SSHPRNAMT"]]);
                var value = ParseOptionalDecimal(fields[columns["VALUE"]]);

                // Only a plain share line says anything about the unit the filing reports values in,
                // and only those prices are worth comparing with a close, so the crosswalk check is
                // given those and no others.
                var isShareLine = putCall.Length == 0 && shareType.Equals("SH", StringComparison.OrdinalIgnoreCase);
                if (isShareLine && value > 0m && amount > 0m)
                {
                    group.SharePrices.Add((double)(value.Value / amount.Value));
                }

                // The unit is recorded beside the value the manager reported rather than multiplied
                // into it. It runs both ways: a filing still in thousands after 2023 is scaled up, and
                // a line that overstated its value a thousandfold is brought back down. Only a share
                // line has a price to check against the close; an option or a bond at par would land
                // on a step by chance, so those keep their filing's unit.
                var scale = ScaleOf(isShareLine
                    ? units.Factor(accession, cusip, submission, value ?? 0m, amount ?? 0m)
                    : units.Filing(accession));

                group.Lines.Add(new PositionLine
                {
                    Submission = submission,
                    TitleOfClass = Sanitize(fields[columns["TITLEOFCLASS"]]),
                    Amount = amount,
                    AmountType = Sanitize(shareType),
                    ReportedValue = value,
                    ValueScale = scale,
                    PutCall = Sanitize(putCall),
                    InvestmentDiscretion = Sanitize(fields[columns["INVESTMENTDISCRETION"]]),
                    OtherManager = FormatOtherManagers(fields[columns["OTHERMANAGER"]]),
                    VotingSole = ParseOptionalDecimal(fields[columns["VOTING_AUTH_SOLE"]]),
                    VotingShared = ParseOptionalDecimal(fields[columns["VOTING_AUTH_SHARED"]]),
                    VotingNone = ParseOptionalDecimal(fields[columns["VOTING_AUTH_NONE"]])
                });

                lines++;
            }

            Log.Trace($"SEC13FDownloader.ReadInfoTable(): {archive.Name}: {lines} reported positions, " +
                      $"{units.CorrectedLines} share lines in a different unit than the rest of their filing");
            return holdings;
        }

        /// <summary>
        /// The factors that turn VALUE into whole dollars. Filers do not all follow the 2023 unit
        /// change (in Apple's December 2019 quarter 85 lines in dollars made 88 percent of the scaled
        /// total), and some mix units within one filing, so each implied price is compared with the
        /// security's close on the quarter's last trading day: per line where the close is known, and
        /// per filing, from its own lines, where it is not. Nothing here reads another filing, so a
        /// filing comes out the same read alone on its day as inside a three month window. The
        /// median of every filer in the window it replaces corrected a filing with others made weeks
        /// after it.
        /// </summary>
        internal ValueUnits DetectValueUnits(ZipArchive zip, Archive archive, Dictionary<string, Submission> submissions)
        {
            var offsetsByFiling = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var pricedByFiling = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (columns, fields) in ReadTable(zip, archive, "INFOTABLE.tsv",
                         "ACCESSION_NUMBER", "CUSIP", "VALUE", "SSHPRNAMT", "SSHPRNAMTTYPE", "PUTCALL"))
            {
                var accession = fields[columns["ACCESSION_NUMBER"]];
                if (!submissions.TryGetValue(accession, out var submission)
                    || !fields[columns["SSHPRNAMTTYPE"]].Trim().Equals("SH", StringComparison.OrdinalIgnoreCase)
                    || fields[columns["PUTCALL"]].Trim().Length > 0)
                {
                    continue;
                }

                var offset = MarketOffset(fields[columns["CUSIP"]].Trim().ToUpperInvariant(), submission,
                    ParseDecimal(fields[columns["VALUE"]]), ParseDecimal(fields[columns["SSHPRNAMT"]]));
                if (offset == null)
                {
                    continue;
                }

                pricedByFiling[accession] = pricedByFiling.GetValueOrDefault(accession) + 1;

                // A price on no unit step says nothing about the unit the filing reports in.
                if (UnitStep(offset.Value) == null)
                {
                    continue;
                }

                if (!offsetsByFiling.TryGetValue(accession, out var offsets))
                {
                    offsetsByFiling[accession] = offsets = new List<double>();
                }

                offsets.Add(offset.Value);
            }

            var units = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var againstTheRule = 0;
            foreach (var (accession, submission) in submissions)
            {
                var thousandsRule = submission.FilingDate < ValueInWholeDollarsFrom;
                units[accession] = ValueMultiplier(
                    offsetsByFiling.TryGetValue(accession, out var offsets) ? offsets : new List<double>(),
                    pricedByFiling.GetValueOrDefault(accession), thousandsRule);
                if (units[accession] != (thousandsRule ? 1000m : 1m))
                {
                    againstTheRule++;
                }
            }

            // Filings with closes to compare against whose lines mostly sit on no unit step.
            var broken = new HashSet<string>(
                pricedByFiling
                    .Where(pair => !IsUnitEvidence(offsetsByFiling.GetValueOrDefault(pair.Key)?.Count ?? 0, pair.Value))
                    .Select(pair => pair.Key),
                StringComparer.Ordinal);

            Log.Trace($"SEC13FDownloader.DetectValueUnits(): {archive.Name}: {againstTheRule} of " +
                      $"{submissions.Count} filings report VALUE in the other unit, {broken.Count} show no unit at all");

            return new ValueUnits(units, broken, MarketOffset);
        }

        /// <summary>
        /// log10 of a share line's implied price over its security's quarter-end close, or null when
        /// the line has no price, the CUSIP resolves to nothing, or the close is not known. Near zero
        /// the line is in whole dollars, near -3 in thousands.
        /// </summary>
        internal double? MarketOffset(string cusip, Submission submission, decimal value, decimal amount)
        {
            if (value <= 0m || amount <= 0m)
            {
                return null;
            }

            var security = ResolveSecurity(cusip, submission.FilingDate);
            var close = security == null ? null : _closePrices.Close(security, submission.Period, submission.FilingDate);
            return close == null ? null : Math.Log10((double)(value / amount / close.Value));
        }

        /// <summary>
        /// The whole-dollar factor of each share line: its own, measured against its security's close
        /// when that is known, or else the one decided for its whole filing.
        /// </summary>
        internal sealed class ValueUnits
        {
            private readonly Dictionary<string, decimal> _filings;
            private readonly HashSet<string> _broken;
            private readonly Func<string, Submission, decimal, decimal, double?> _marketOffset;

            /// <summary>Share lines whose factor differed from their filing's.</summary>
            public long CorrectedLines { get; private set; }

            public ValueUnits(Dictionary<string, decimal> filings, HashSet<string> broken,
                Func<string, Submission, decimal, decimal, double?> marketOffset)
            {
                _filings = filings;
                _broken = broken;
                _marketOffset = marketOffset;
            }

            /// <summary>The factor decided for a whole filing, which a line with no price of its own takes.</summary>
            public decimal Filing(string accession) => _filings[accession];

            /// <summary>The factor for one share line of VALUE <paramref name="value"/> over <paramref name="amount"/> shares.</summary>
            public decimal Factor(string accession, string cusip, Submission submission, decimal value, decimal amount)
            {
                // A filing whose lines mostly sit on no unit step keeps its unscaled factor on every
                // line: the few that land on a step do so by chance.
                var filing = _filings[accession];
                if (_broken.Contains(accession))
                {
                    return filing;
                }

                var offset = _marketOffset(cusip, submission, value, amount);
                if (offset == null)
                {
                    return filing;
                }

                // The thousandfold step the line's price sits on: one below is VALUE in thousands, one
                // above is VALUE typed a thousand times too large. That second case is a VALUE slip
                // rather than a share count one: of the 4,740 such lines of the March 2023 quarter
                // whose filer reported the same security the quarter before, 4,578 held the same
                // shares then and 162 a thousand times more. A price on no step, a millionfold one
                // included, cannot be checked, so it takes the smaller of its filing's unit and the rule
                // of its filing date. Either one alone inflates an era: the rule scaled the odd lines of
                // filers in whole dollars before 2023 ($730 billion in the September 2022 quarter), the
                // filing's unit those of filers still in thousands in early 2023 ($879 billion in
                // December 2022). Scaling by the nearest step put Seagate at $413 billion.
                var step = UnitStep(offset.Value);
                if (step == null)
                {
                    return Math.Min(filing, submission.FilingDate < ValueInWholeDollarsFrom ? 1000m : 1m);
                }

                var factor = step == 0 ? 1m : step < 0 ? 1000m : 0.001m;
                if (factor != filing)
                {
                    CorrectedLines++;
                }

                return factor;
            }
        }

        /// <summary>
        /// Whether a filing's VALUE is multiplied by a thousand. The offsets are log10 of those of its
        /// implied prices that sit on a unit step of the security's close, out of
        /// <paramref name="priced"/> lines with a close: near zero the filing reports whole dollars,
        /// near -3 thousands. They say so only when they are most of its priced lines. A filing whose
        /// lines mostly sit on no step is broken rather than in another unit, and the few that land on
        /// one do so by chance: a manager that typed its dollar values as share counts, a price of $1
        /// on every line, looks like thousands against any close near $1,000. Such a filing is never
        /// scaled up: before 2023 the rule multiplied it by a thousand, and one manager whose share
        /// counts carried VALUE in thousands, $1,000 a share on every line, put Alphabet's September
        /// 2020 quarter 9.5 percent above its close. With nothing to compare against, the rule stands.
        /// </summary>
        internal static decimal ValueMultiplier(List<double> offsets, int priced, bool thousandsRule)
        {
            if (!IsUnitEvidence(offsets.Count, priced))
            {
                return priced == 0 && thousandsRule ? 1000m : 1m;
            }

            return Median(offsets) < -1.5 ? 1000m : 1m;
        }

        /// <summary>Whether a filing's lines on a unit step are most of its priced lines.</summary>
        private static bool IsUnitEvidence(int onAStep, int priced)
        {
            return priced > 0 && onAStep * 2 > priced;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <summary>
        /// Resolves each group to a security and queues its lines for that security's file. Groups
        /// that resolve to nothing, mostly foreign issuers, are dropped.
        /// </summary>
        private void EmitHoldings(Dictionary<HoldingKey, Holding> holdings)
        {
            foreach (var day in holdings.GroupBy(entry => entry.Key.FilingDate).OrderBy(group => group.Key))
            {
                foreach (var (key, holding) in day)
                {
                    var security = ResolveSecurity(key.Cusip, key.FilingDate);
                    if (security == null && OptionUnderlyings(key.Cusip).Count > 1)
                    {
                        EmitOptionLinesByPrice(key, holding);
                        continue;
                    }

                    // The ticker is the one in force on the day the row is published, because that
                    // is the day LEAN maps the subscription to when it opens the entry. Taken on the
                    // filing date, a rename in between files the row under a ticker no one reads.
                    var published = PublishedOn(key.FilingDate);
                    var ticker = security == null ? null : ResolveTicker(security, published);
                    if (string.IsNullOrWhiteSpace(ticker) || !IsFileNameSafe(ticker))
                    {
                        _unresolvedGroups++;
                        _unresolvedValue += holding.ReportedValue;
                        continue;
                    }

                    // Whoever reads the ticker's file takes its rows to belong to the security that
                    // owns the ticker that day; a group of another security is dropped, not mixed in.
                    var owner = TickerOwner(ticker, published);
                    if (owner != security.ToString())
                    {
                        _conflictingTickers++;
                        _conflictingValue += holding.ReportedValue;
                        if (_conflictSample.Count < 10)
                        {
                            _conflictSample.Add($"{key.Cusip} {key.FilingDate:yyyy-MM-dd} {ticker} is {security}, owner {owner ?? "none"}");
                        }

                        _unresolvedGroups++;
                        _unresolvedValue += holding.ReportedValue;
                        continue;
                    }

                    // A crosswalk ticker is a fund administrator's free text, and a wrong one names
                    // another company: DeFi Technologies at $2 reached the $129 Hashdex DEFI ETF and
                    // gave it 111 holders. A group whose prices say so is dropped.
                    if (_crosswalkResolutions.Contains((key.Cusip, key.FilingDate)) &&
                        !KeepsCrosswalkGroup(key.Cusip, security, key.Period, key.FilingDate, holding.SharePrices))
                    {
                        if (IsEquityIssue(key.Cusip))
                        {
                            _mismatchedGroups++;
                        }
                        else
                        {
                            _debtCusipsRejected++;
                        }

                        _mismatchedValue += holding.ReportedValue;
                        _unresolvedGroups++;
                        _unresolvedValue += holding.ReportedValue;
                        continue;
                    }

                    _resolvedValue += holding.ReportedValue;

                    // Every line of the group, as filed. An amendment is published beside the
                    // original it restates rather than replacing it: deciding that a restatement
                    // supersedes a number is a judgement about the data, and the filer already
                    // declared which kind it is in AmendmentType for whoever wants to apply it.
                    var throughOption = _optionResolutions.Contains((key.Cusip, key.FilingDate));
                    foreach (var line in holding.Lines)
                    {
                        if (throughOption)
                        {
                            InferOptionSide(key.Cusip, line);
                        }

                        Queue(security.ToString(), ticker.ToLowerInvariant(), new HoldingsRow
                        {
                            Time = key.FilingDate,
                            Line = line
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Gives a line under an option CUSIP the side it left empty, which would otherwise read as
        /// shares of the underlying. The CUSIP states it: issue 90 is a call and 95 a put, and where
        /// the line does name a side the two agree on 24,890 of 24,915 lines of the June to August
        /// 2026 data set.
        /// </summary>
        private void InferOptionSide(string cusip, PositionLine line)
        {
            if (FormatPutCall(line.PutCall).Length > 0)
            {
                return;
            }

            line.PutCall = NormalizeCusip(cusip).Substring(IssuerLength, 2) == OptionIssues[0] ? "Call" : "Put";
            _optionSidesInferred++;
        }

        /// <summary>
        /// How far an option line's implied price may sit from a fund's close and still name it. Tight,
        /// unlike the unit check, because it has to tell the funds of one family apart, and it can be:
        /// filers value the line at that close. On the March 2026 quarter half a percent gave 1,383
        /// of 2,114 lines one fund and 63 several, where three percent gave 958 and 659.
        /// </summary>
        private const double OptionPriceTolerance = 0.005;

        /// <summary>
        /// Publishes the lines of an option CUSIP whose issuer has several equity issues, as the fund
        /// families do: every iShares fund's calls share one CUSIP, so each line is resolved on its
        /// own. An option line reports the value and the number of the underlying shares, so its
        /// implied price is the underlying's, and the line goes to the one issue whose quarter-end
        /// close it matches, in dollars or in thousands. No match, or more than one, drops the line.
        /// The close names the fund and nothing else. An option line has no price of its own, so it
        /// keeps its filing's unit, like every other option line and like the bond at par beside it.
        /// </summary>
        private void EmitOptionLinesByPrice(HoldingKey key, Holding holding)
        {
            var candidates = OptionUnderlyings(key.Cusip)
                .Select(issue => TradingDefinition(_definitionsByCusip, issue,
                    BuildUnitedStatesIsin(issue + ComputeCusipCheckDigit(issue)), key.FilingDate))
                .Where(security => security != null)
                .Distinct()
                .Select(security => (Security: security, Close: _closePrices.Close(security, key.Period, key.FilingDate)))
                .Where(candidate => candidate.Close != null)
                .ToList();

            foreach (var line in holding.Lines)
            {
                var matches = line.Amount > 0m && line.ReportedValue > 0m
                    ? candidates.Where(candidate => MatchesClose(line.ReportedValue.Value / line.Amount.Value, candidate.Close.Value)).ToList()
                    : [];

                var ticker = matches.Count == 1 ? ResolveTicker(matches[0].Security, PublishedOn(key.FilingDate)) : null;
                if (string.IsNullOrWhiteSpace(ticker) || !IsFileNameSafe(ticker))
                {
                    _optionLinesWithoutOneMatch++;
                    _unresolvedValue += line.DollarValue;
                    continue;
                }

                InferOptionSide(key.Cusip, line);
                _optionLinesByPrice++;
                _resolvedValue += line.DollarValue;
                Queue(matches[0].Security.ToString(), ticker.ToLowerInvariant(), new HoldingsRow { Time = key.FilingDate, Line = line });
            }
        }

        /// <summary>Whether a price is a close, stated in dollars or in thousands, within OptionPriceTolerance.</summary>
        internal static bool MatchesClose(decimal price, decimal close)
        {
            var ratio = price / close;
            return Math.Abs((double)ratio - 1) <= OptionPriceTolerance ||
                   Math.Abs((double)ratio * 1000 - 1) <= OptionPriceTolerance;
        }

        /// <summary>
        /// The security trading under a ticker on a date per the map files, or null. It is how the
        /// LEAN reads a ticker file, so writing applies the same test.
        /// </summary>
        private string TickerOwner(string ticker, DateTime date)
        {
            var key = (ticker.ToUpperInvariant(), date.Date);
            if (!_tickerOwners.TryGetValue(key, out var owner))
            {
                var mapFile = _mapFileProvider
                    .Get(new AuxiliaryDataKey(Market.USA, SecurityType.Equity))
                    .ResolveMapFile(key.Item1, key.Item2);

                _tickerOwners[key] = owner = mapFile.Any()
                    ? SecurityIdentifier.GenerateEquity(mapFile.FirstDate, mapFile.FirstTicker, Market.USA).ToString()
                    : null;
            }

            return owner;
        }

        /// <summary>
        /// Resolves a CUSIP to a security, point in time: by CUSIP, then by the US ISIN built from
        /// it, then through the N-PORT ticker crosswalk.
        /// </summary>
        internal SecurityIdentifier ResolveSecurity(string rawCusip, DateTime tradingDate)
        {
            var key = (rawCusip, tradingDate);
            if (_resolvedCusips.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // The summary counts CUSIPs, not lookups, and one CUSIP is now resolved once per filing
            // date rather than once per run.
            var firstSighting = _countedCusips.Add(rawCusip);

            var cusip = NormalizeCusip(rawCusip);
            if (cusip == null)
            {
                if (firstSighting)
                {
                    _malformedCusips++;
                }

                _resolvedCusips[key] = null;
                return null;
            }

            // Step 1: LEAN stores the CUSIP without its check digit, so the ninth character comes off.
            var isin = BuildUnitedStatesIsin(cusip);
            var security = TradingDefinition(_definitionsByCusip, cusip.Substring(0, 8), isin, tradingDate);
            if (security != null)
            {
                if (firstSighting)
                {
                    _resolvedByCusip++;
                }
            }
            else
            {
                // Step 2: the US ISIN reaches issuers whose CUSIP is blank in the security database.
                security = TradingDefinition(_definitionsByIsin, isin, isin, tradingDate);
                if (security != null && firstSighting)
                {
                    _resolvedByIsin++;
                }
            }

            // Step 3: the N-PORT crosswalk needs no security database, and reaches Alphabet and the
            // CINS foreign issuers the constructed ISIN cannot represent.
            if (security == null)
            {
                var identifier = ResolveThroughTicker(cusip);
                if (identifier != null)
                {
                    if (firstSighting)
                    {
                        _resolvedByTicker++;
                    }

                    _resolvedCusips[key] = identifier;
                    _crosswalkResolutions.Add(key);
                    return identifier;
                }

                // Step 4: an option's own CUSIP is in no database, so it is resolved through the
                // security it is written on. The position is still published as the option line it
                // is, with its PutCall side, rather than as a holding of the underlying.
                var underlying = UnderlyingOfOptionCusip(cusip);
                if (underlying != null)
                {
                    security = TradingDefinition(_definitionsByCusip, underlying,
                        BuildUnitedStatesIsin(underlying + ComputeCusipCheckDigit(underlying)), tradingDate);
                    if (security != null)
                    {
                        if (firstSighting)
                        {
                            _resolvedByOptionUnderlying++;
                        }

                        _resolvedCusips[key] = security;
                        _optionResolutions.Add(key);
                        return security;
                    }
                }

                // Several possible underlyings: its lines are resolved one by one, by price.
                if (OptionUnderlyings(cusip).Count <= 1)
                {
                    _unresolvedCusips.Add(cusip);
                }

                _resolvedCusips[key] = null;
                return null;
            }

            _resolvedCusips[key] = security;
            return security;
        }

        /// <summary>
        /// The security, among the database rows carrying an identifier, that trades under its own
        /// ticker on the date, or null. The database repeats identifiers across the listings one
        /// company has had, and LEAN's resolver takes the first row, mostly the one that no longer
        /// trades: Alcoa's CUSIP sits on the old Alcoa, now Howmet, and on the Alcoa spun off in 2016,
        /// and TG Therapeutics' on the listing it had as Atlantic Technology Ventures. Run on the real
        /// database that dropped Alcoa, Howmet, Vertiv and TG Therapeutics, as groups whose ticker
        /// another security owned. A row carrying the ISIN the CUSIP builds is tried first, since the
        /// old Alcoa row carries Howmet's.
        /// </summary>
        private SecurityIdentifier TradingDefinition(Dictionary<string, List<SecurityDefinition>> rowsByIdentifier,
            string identifier, string isin, DateTime tradingDate)
        {
            if (!rowsByIdentifier.TryGetValue(identifier, out var rows))
            {
                return null;
            }

            int Priority(SecurityDefinition row) => row.ISIN == null ? 1
                : string.Equals(row.ISIN, isin, StringComparison.OrdinalIgnoreCase) ? 0 : 2;

            foreach (var row in rows.OrderBy(Priority))
            {
                var ticker = ResolveTicker(row.SecurityIdentifier, tradingDate);
                if (ticker != null && TickerOwner(ticker, tradingDate) == row.SecurityIdentifier.ToString())
                {
                    return row.SecurityIdentifier;
                }
            }

            return null;
        }

        /// <summary>
        /// The security database rows by one identifier, in file order. A CUSIP is keyed by its eight
        /// character body, the form the database mostly stores; the few rows written with their check
        /// digit are keyed only when that digit holds.
        /// </summary>
        private static Dictionary<string, List<SecurityDefinition>> IndexDefinitions(
            IEnumerable<SecurityDefinition> definitions, Func<SecurityDefinition, string> identifier)
        {
            var index = new Dictionary<string, List<SecurityDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions)
            {
                var key = identifier(definition);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!index.TryGetValue(key, out var rows))
                {
                    index[key] = rows = new List<SecurityDefinition>();
                }

                rows.Add(definition);
            }

            return index;
        }

        /// <summary>The eight character body of a database CUSIP, or null when it is neither that nor a checked nine.</summary>
        private static string DatabaseCusip(string cusip)
        {
            if (cusip == null)
            {
                return null;
            }

            return cusip.Length switch
            {
                8 => cusip,
                9 when ComputeCusipCheckDigit(cusip.Substring(0, 8)) == cusip[8] => cusip.Substring(0, 8),
                _ => null
            };
        }

        /// <summary>
        /// Brings a reported CUSIP to nine characters, or null when it cannot be one. A short value
        /// has lost either its leading zero ("37833100", Apple) or its check digit ("46428722", an
        /// iShares fund). Padding the second with a zero would invent another security, so the
        /// check digit decides which repair applies.
        /// </summary>
        internal static string NormalizeCusip(string cusip)
        {
            // Anything else cannot be put in an ISIN, and one such line would stop the run there.
            if (!cusip.All(char.IsAsciiLetterOrDigit))
            {
                return null;
            }

            if (cusip.Length == 9)
            {
                return cusip;
            }

            if (cusip.Length is < 6 or > 9)
            {
                return null;
            }

            var padded = cusip.PadLeft(9, '0');
            if (ComputeCusipCheckDigit(padded.Substring(0, 8)) == padded[8])
            {
                return padded;
            }

            if (cusip.Length != 8)
            {
                return null;
            }

            var checkDigit = ComputeCusipCheckDigit(cusip);
            return checkDigit.HasValue ? cusip + checkDigit.Value : null;
        }

        /// <summary>
        /// The CUSIP check digit: a modulus ten sum over the eight character body where every
        /// second character counts double, letters standing for their position in the alphabet
        /// plus nine.
        /// </summary>
        internal static char? ComputeCusipCheckDigit(string body)
        {
            var sum = 0;
            for (var i = 0; i < body.Length; i++)
            {
                var character = body[i];
                int value;
                if (char.IsDigit(character))
                {
                    value = character - '0';
                }
                else if (char.IsLetter(character))
                {
                    value = char.ToUpperInvariant(character) - 'A' + 10;
                }
                else
                {
                    value = character switch { '*' => 36, '@' => 37, '#' => 38, _ => -1 };
                    if (value < 0)
                    {
                        // Not a character a CUSIP can hold, so there is no check digit.
                        return null;
                    }
                }

                if (i % 2 == 1)
                {
                    value *= 2;
                }

                sum += value / 10 + value % 10;
            }

            return (char)('0' + (10 - sum % 10) % 10);
        }

        /// <summary>
        /// Priced share lines a crosswalk group needs before its prices can overrule the resolution. One
        /// or two lines are usually one manager's slip, a stale price or a split, not another company:
        /// on the December 2025 quarter, dropping on any disagreement removed 14,696 holders across
        /// 3,249 tickers besides the misattributed ones, and requiring three lines halves that while
        /// still removing 565 of the 637 misattributed holders.
        /// </summary>
        private const int PricedLinesToOverrule = 3;

        /// <summary>
        /// Whether a group reached through the crosswalk stays. A CUSIP whose issue number carries
        /// letters is debt as a rule, and N-PORT tags a fund's bonds with the issuer's ticker, so
        /// Etsy's convertible notes reached Etsy's stock: 171 million of the 298 million shares its
        /// December 2022 quarter published. Such a group stays only when its prices are the security's
        /// own, which is how the iShares iBonds ETFs, whose CUSIPs carry letters too, keep their
        /// holders. Any other group is dropped only when enough of its prices say it is another
        /// company, as Centerra Gold and Enerflex reached Carlyle and Equifax through their Toronto
        /// tickers CG and EFX. A group without prices, a day of option positions only, or a security
        /// the close file does not carry, cannot be checked and stays.
        /// </summary>
        internal bool KeepsCrosswalkGroup(string cusip, SecurityIdentifier security, DateTime period, DateTime filingDate,
            List<double> prices)
        {
            // A CUSIP whose issue carries letters, whose issuer already has stock in the security
            // database, is that company's debt: the stock is the row the database holds, and this is
            // something else the same company issued. Apple's 3.45% 2045 bond, 037833BA7, reached
            // AAPL here through a fund administrator's N-PORT ticker and added a constant ten
            // thousand shares to it, the principal amount on a line the filer had typed SH.
            //
            // Letting the prices decide was the mistake: a bond near par and a stock near the same
            // number agree by coincidence. The iBonds ETFs, whose CUSIPs also carry letters, are
            // themselves in the database and resolve before the crosswalk is ever consulted, so this
            // does not reach them.
            if (!IsEquityIssue(cusip) && IssuerHasStock(cusip))
            {
                return false;
            }

            var matches = PricesMatchClose(security, period, filingDate, prices);
            if (!IsEquityIssue(cusip))
            {
                return matches == true;
            }

            return !(matches == false && prices.Count >= PricedLinesToOverrule);
        }

        /// <summary>Whether the CUSIP's six character issuer has a stock of its own in the database.</summary>
        internal bool IssuerHasStock(string cusip)
        {
            var nine = NormalizeCusip(cusip);
            return nine != null && _equityIssuesByIssuer.ContainsKey(nine.Substring(0, IssuerLength));
        }

        /// <summary>
        /// Whether a group's prices are the security's: at least half of them sit on a unit step of the
        /// quarter-end close. Counted line by line rather than through a median, because a busy day
        /// mixes managers in dollars and in thousands: Avanos on 5 February 2026 had four lines at
        /// $0.0112 and four at $11.23, and their median sat half way, on neither. Null when the group
        /// has no priced share line or the security has no close.
        /// </summary>
        private bool? PricesMatchClose(SecurityIdentifier security, DateTime period, DateTime filingDate, List<double> prices)
        {
            var close = _closePrices.Close(security, period, filingDate);
            if (close == null || prices.Count == 0)
            {
                return null;
            }

            var onAStep = prices.Count(price => UnitStep(Math.Log10(price / (double)close.Value)) != null);
            return onAStep * 2 >= prices.Count;
        }

        /// <summary>
        /// The thousandfold step a line's price sits on against the close, given as log10 of their
        /// ratio: 0 for whole dollars, -1 for thousands, 1 for a VALUE typed a thousand times too
        /// large. Null when it sits on none, within PriceMatchTolerance, which is a price that says
        /// nothing about the unit: another security, a bond at par against a stock, or a slip.
        /// Scaling those by the nearest step put Seagate's December 2025 quarter at $413 billion.
        /// </summary>
        internal static int? UnitStep(double offset)
        {
            var steps = (int)Math.Round(offset / 3);
            return Math.Abs(steps) <= 1 && Math.Abs(offset - 3 * steps) <= PriceMatchTolerance ? steps : null;
        }

        /// <summary>
        /// Whether a CUSIP's issue number is an equity one: two digits, where debt uses letters. The
        /// CUSIP is brought to nine characters first, since filers drop leading zeros and check digits.
        /// </summary>
        internal static bool IsEquityIssue(string cusip)
        {
            var nine = NormalizeCusip(cusip);
            return nine != null && char.IsDigit(nine[6]) && char.IsDigit(nine[7]);
        }

        /// <summary>
        /// Whether a CUSIP is a CINS, the form foreign issuers carry: it opens with the letter of the
        /// issuer's country or region, where a US or Canadian one opens with a digit. Many trade in
        /// the US all the same, as Accenture and Medtronic do, so it sorts the summary and filters nothing.
        /// </summary>
        internal static bool IsCins(string cusip) => cusip.Length > 0 && char.IsAsciiLetter(cusip[0]);

        /// <summary>How many characters of a CUSIP name the issuer, before the issue and the check digit.</summary>
        private const int IssuerLength = 6;

        /// <summary>The issue numbers an option carries: 90 for calls and 95 for puts.</summary>
        private static readonly string[] OptionIssues = { "90", "95" };

        /// <summary>Whether a CUSIP names an option on a security rather than the security itself.</summary>
        internal static bool IsOptionIssue(string cusip)
        {
            var nine = NormalizeCusip(cusip);
            return nine != null && OptionIssues.Contains(nine.Substring(IssuerLength, 2));
        }

        /// <summary>
        /// The security an option CUSIP is written on, as an eight character CUSIP, or null.
        ///
        /// A manager reporting options names them by the option's own CUSIP, which shares the six
        /// character issuer of the underlying and carries issue 90 for calls or 95 for puts. That
        /// CUSIP is in no security database, so the line used to resolve to nothing and the position
        /// was dropped: in the week of 3 August 2026 that lost 6,083 reported option lines.
        ///
        /// The issuer alone is not always enough. iShares writes seventy equity issues under
        /// 464287 and SPDR eleven under 81369Y, and an option CUSIP there names one of them without
        /// saying which. Guessing would file a position under the wrong fund, so the underlying is
        /// only returned when the issuer has exactly one equity issue and the answer is not a guess.
        /// </summary>
        internal string UnderlyingOfOptionCusip(string cusip)
        {
            var nine = NormalizeCusip(cusip);
            if (nine == null || !IsOptionIssue(nine))
            {
                return null;
            }

            var issues = OptionUnderlyings(nine);
            return issues.Count == 1 ? issues[0] : null;
        }

        /// <summary>The equity issues, as eight character CUSIPs, an option CUSIP can be written on.</summary>
        internal List<string> OptionUnderlyings(string cusip)
        {
            var nine = NormalizeCusip(cusip);
            return nine != null && IsOptionIssue(nine) &&
                   _equityIssuesByIssuer.TryGetValue(nine.Substring(0, IssuerLength), out var issues)
                ? issues
                : [];
        }

        /// <summary>
        /// Resolves a CUSIP through the N-PORT ticker crosswalk. The ticker is resolved on the day
        /// the funds reported it, since a ticker names a security only on a date: at a 2021 filing
        /// META named a Roundhill ETF, not Facebook. The map file is checked first because
        /// GenerateEquity never returns null for an unknown ticker. Whether the group it resolves is
        /// kept is KeepsCrosswalkGroup's call, on that day's prices.
        /// </summary>
        internal SecurityIdentifier ResolveThroughTicker(string cusip)
        {
            if (!TickerCrosswalk.TryGetValue(cusip, out var entry))
            {
                return null;
            }

            if (!_crosswalkSecurities.TryGetValue(cusip, out var security))
            {
                var mapFile = _mapFileProvider
                    .Get(new AuxiliaryDataKey(Market.USA, SecurityType.Equity))
                    .ResolveMapFile(entry.Ticker, entry.Observed);

                // The map file has to carry the ticker on that very date. Barrick reached the
                // crosswalk as ABX, its Toronto ticker, when ABX named no US security, and the
                // resolver still returned the company that took ABX months later.
                var tradedUnderIt = mapFile.Any() && string.Equals(
                    mapFile.GetMappedSymbol(entry.Observed, null), entry.Ticker, StringComparison.OrdinalIgnoreCase);

                _crosswalkSecurities[cusip] = security = tradedUnderIt
                    ? SecurityIdentifier.GenerateEquity(mapFile.FirstDate, mapFile.FirstTicker, Market.USA)
                    : null;
            }

            return security;
        }

        /// <summary>
        /// Builds the US ISIN for a nine character CUSIP: the country code, the CUSIP, and the
        /// check digit, which is arithmetic rather than a lookup. "037833100" gives "US0378331005".
        /// </summary>
        internal static string BuildUnitedStatesIsin(string cusip)
        {
            var body = "US" + cusip;
            return body + ComputeIsinCheckDigit(body);
        }

        /// <summary>
        /// Computes an ISIN check digit: expand each letter to its ordinal (A is 10 through Z is 35),
        /// then run Luhn over the resulting digits from the right.
        /// </summary>
        private static char ComputeIsinCheckDigit(string body)
        {
            var digits = new StringBuilder(body.Length * 2);
            foreach (var character in body)
            {
                if (character >= '0' && character <= '9')
                {
                    digits.Append(character);
                }
                else if (character >= 'A' && character <= 'Z')
                {
                    digits.Append((character - 'A' + 10).ToStringInvariant());
                }
                else
                {
                    throw new FormatException(
                        $"SEC13FDownloader.ComputeIsinCheckDigit(): '{body}' carries '{character}', which is neither " +
                        "a digit nor an upper case letter");
                }
            }

            var sum = 0;
            var doubled = true;
            for (var i = digits.Length - 1; i >= 0; i--)
            {
                var value = digits[i] - '0';
                if (doubled)
                {
                    value *= 2;
                    if (value > 9)
                    {
                        value -= 9;
                    }
                }

                sum += value;
                doubled = !doubled;
            }

            return (char)('0' + (10 - sum % 10) % 10);
        }

        /// <summary>
        /// The ticker a security traded under on a date, or null when its map file does not cover
        /// it. No fallback to the last ticker: managers report dead CUSIPs for years, and by then the
        /// ticker can belong to another company.
        /// </summary>
        internal string ResolveTicker(SecurityIdentifier security, DateTime tradingDate)
        {
            // Before its first row a map file answers with its first ticker, for a security that
            // did not trade yet.
            var mapFile = MapFileOf(security);
            if (mapFile == null || tradingDate < mapFile.FirstDate)
            {
                return null;
            }

            var ticker = mapFile.GetMappedSymbol(tradingDate, null);
            return string.IsNullOrEmpty(ticker) ? null : ticker;
        }

        /// <summary>The map file of a security, cached, or null when it has none.</summary>
        private MapFile MapFileOf(SecurityIdentifier security)
        {
            if (!_mapFiles.TryGetValue(security, out var mapFile))
            {
                _mapFiles[security] = mapFile = _mapFileProvider
                    .Get(AuxiliaryDataKey.Create(security))
                    .ResolveMapFile(security.Symbol, security.Date);
            }

            return mapFile;
        }

        /// <summary>
        /// Queues a row for a security, to be staged when the archive is done.
        /// </summary>
        private void Queue(string security, string ticker, HoldingsRow row)
        {
            if (!_pendingSecurityRows.TryGetValue(security, out var rows))
            {
                _pendingSecurityRows[security] = rows = new List<(string, HoldingsRow)>();
            }

            // Appended, never merged: two lines reported for the same security on the same day are
            // two positions and both are published.
            rows.Add((ticker, row));
        }

        /// <summary>
        /// Appends the archive's rows to the staging file of their security; the grouping into files
        /// per ticker and filing date happens once, in the finalize pass.
        /// </summary>
        internal void FlushPendingRows()
        {
            Directory.CreateDirectory(_stagingDirectory);
            foreach (var (security, rows) in _pendingSecurityRows)
            {
                File.AppendAllLines(
                    StagingPath(security),
                    rows.OrderBy(entry => entry.Row.Time)
                        .Select(entry => $"{entry.Ticker},{FormatRow(entry.Row)}"));
                _stagedSecurities.Add(security);
            }

            _pendingSecurityRows.Clear();
        }

        /// <summary>The staging file of a security. The identifier carries a space, so it is escaped.</summary>
        private string StagingPath(string security)
        {
            return Path.Combine(_stagingDirectory, Uri.EscapeDataString(security) + ".csv");
        }

        /// <summary>
        /// Gathers the staged rows into their ticker's zip, one entry per publication day. An incremental
        /// run adds its dates to a copy of the published zip.
        /// </summary>
        internal void FinalizeSecurityFiles()
        {
            if (_stagedSecurities.Count == 0)
            {
                // A day can carry filings whose every line fails to resolve, and it is recorded as
                // folded in all the same, so no later run reads its cover pages again: the names it
                // gave are written now or never.
                Log.Trace("SEC13FDownloader.FinalizeSecurityFiles(): no rows were staged");
                WriteManagerNames();
                return;
            }

            var tickerStaging = Path.Combine(_stagingDirectory, "tickers");
            Directory.CreateDirectory(tickerStaging);

            // A ticker file can hold the rows of more than one security, one after another as the
            // ticker changed hands, so every security is gathered into it before it is written.
            foreach (var security in _stagedSecurities)
            {
                foreach (var ticker in ReadStagedRows(StagingPath(security)).GroupBy(entry => entry.Ticker))
                {
                    File.AppendAllLines(Path.Combine(tickerStaging, $"{ticker.Key}.csv"),
                        ticker.Select(entry => FormatRow(entry.Row)));
                }

                // Deleted as it goes: the whole history staged twice, uncompressed, is tens of gigabytes.
                File.Delete(StagingPath(security));
            }

            Directory.CreateDirectory(_destinationDirectory);
            var files = Directory.GetFiles(tickerStaging, "*.csv");
            var entries = 0L;
            var rows = 0L;

            foreach (var file in files)
            {
                var ticker = Path.GetFileNameWithoutExtension(file);
                var days = ReadRows(file)
                    .GroupBy(row => PublishedOn(row.Time))
                    .OrderBy(day => day.Key)
                    .ToList();

                entries += WriteSecurityZip(ticker, days);
                rows += days.Sum(day => day.LongCount());
                File.Delete(file);
            }

            Log.Trace($"SEC13FDownloader.FinalizeSecurityFiles(): {_stagedSecurities.Count} securities written into " +
                      $"{files.Length} ticker files, {entries} publication days, {rows} reported positions");

            WriteManagerNames();
            Directory.Delete(_stagingDirectory, recursive: true);
        }

        /// <summary>
        /// The day a filing reaches algorithms, which names the entry it is published under.
        ///
        /// LEAN live asks for the entry of today and never again for a past one, and a backtest hands
        /// an entry over at midnight of its date. EDGAR lists a day's filings that night, so the
        /// earliest a job can have them is the day after, and that is the entry they go in. A daily
        /// run puts every row it reads under the day after its deployment date, a day it catches up
        /// late included, so no backtest sees a filing before the job had it and live still reads it.
        /// The rebuild stands for a job that ran every day, with one difference: the data sets date a
        /// filing by its own filing date, so the rare one an index listed a day late is published a
        /// day earlier than the daily job published it.
        /// </summary>
        internal DateTime PublishedOn(DateTime filingDate)
        {
            return (_deploymentDate ?? filingDate).Date.AddDays(1);
        }

        /// <summary>
        /// Writes one security's publication days as an entry per day inside its zip.
        ///
        /// A zip rather than a directory of loose files because a filing date holds three lines at
        /// the median and one line a third of the time: as loose files the history would be eight
        /// and a half million of them, whose tar headers alone outweigh the data.
        /// </summary>
        private long WriteSecurityZip(string ticker, List<IGrouping<DateTime, HoldingsRow>> days)
        {
            var path = Path.Combine(_destinationDirectory, $"{ticker}.zip");

            // The destination starts empty and whatever lands in it replaces the published file, so
            // an incremental run adds its dates to a copy of the published zip. Without the copy it
            // would publish the security's history as this run's dates alone.
            var published = Path.Combine(_processedDataDirectory, $"{ticker}.zip");
            if (_deploymentDate != null && File.Exists(published))
            {
                File.Copy(published, path);
            }

            // Update mode only when there is a zip to update. A new one is opened in Create mode,
            // which streams its entries out instead of holding the archive in memory.
            var updating = File.Exists(path);

            using (var zip = ZipFile.Open(path, updating ? ZipArchiveMode.Update : ZipArchiveMode.Create))
            {
                foreach (var day in days)
                {
                    var name = $"{day.Key.ToStringInvariant(PeriodFormat)}.csv";

                    // A date already published is added to, never replaced. What is published for it
                    // can come from filings this read does not carry: the daily index and the
                    // quarterly data sets do not name the same filings for a day, and an index can
                    // name a filing dated an earlier day. Taking the read for the whole of the date
                    // dropped every other manager's positions of it, and reported success. Only the
                    // lines of the filings being written are dropped, so a filing read twice is
                    // published once.
                    var kept = Array.Empty<string>();
                    var existing = updating ? zip.GetEntry(name) : null;
                    if (existing != null)
                    {
                        var rewritten = day.Select(row => row.Line.Submission.Accession).ToHashSet(StringComparer.Ordinal);
                        using (var reader = new StreamReader(existing.Open()))
                        {
                            kept = reader.ReadToEnd()
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Where(line => !rewritten.Contains(AccessionOf(line)))
                                .ToArray();
                        }

                        existing.Delete();
                    }

                    // The same bytes whichever system the job runs on.
                    using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open()) { NewLine = "\n" };
                    foreach (var line in kept)
                    {
                        writer.WriteLine(line);
                    }

                    foreach (var row in day)
                    {
                        writer.WriteLine(FormatRow(row));
                    }
                }
            }

            using var written = ZipFile.OpenRead(path);
            return written.Entries.Count;
        }

        /// <summary>
        /// Writes managers.csv, the filing managers' names by CIK. An incremental run folds in the
        /// names already published, so a manager that filed nothing this run keeps its name.
        /// </summary>
        private void WriteManagerNames()
        {
            Directory.CreateDirectory(_destinationDirectory);
            var path = Path.Combine(_destinationDirectory, ManagerNamesFileName);
            var names = new Dictionary<int, string>();

            var published = Path.Combine(_processedDataDirectory, ManagerNamesFileName);
            if (File.Exists(published))
            {
                foreach (var line in File.ReadLines(published))
                {
                    var separator = line.IndexOf(',');
                    if (separator > 0 &&
                        int.TryParse(line[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cik))
                    {
                        names[cik] = line[(separator + 1)..];
                    }
                }
            }

            var newerThanThisRun = ManagersPublishedAfter(_managerNames.Values
                .Where(manager => manager.Filed <= _foldedThrough)
                .Select(manager => manager.Filed)
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min());

            foreach (var (cik, manager) in _managerNames)
            {
                // The name a manager files under is the one it carries today, so only its latest
                // filing may name it. The published file carries no date to say when the name in it
                // was filed, and a day whose index came late is read after newer days are already
                // published, so the date is taken from the published rows themselves: a manager that
                // filed again between this filing and what is published keeps the published name.
                if (!newerThanThisRun.TryGetValue(cik, out var newer) || newer < manager.Filed)
                {
                    names[cik] = manager.Name;
                }
            }

            // Sanitized here and not only where the cover page is read, because the names folded in
            // come from a file an earlier release wrote, which carried its commas bare.
            File.WriteAllLines(path, names.OrderBy(entry => entry.Key)
                .Select(entry => $"{entry.Key.ToStringInvariant()},{Sanitize(entry.Value)}"));

            Log.Trace($"SEC13FDownloader.WriteManagerNames(): {names.Count} managers, " +
                      $"{_managerNames.Count} of them seen this run");
        }

        /// <summary>
        /// The newest filing date each manager already has published in the days after
        /// <paramref name="after"/>, read from the published rows themselves. managers.csv carries no
        /// date, so this is where the date of a published name comes from: a manager that filed again
        /// between the filing this run read and what is published is not renamed by the older one.
        ///
        /// Only a run folding in a day older than the last one already folded in asks for this, which
        /// means a day whose index EDGAR published late, so an ordinary daily run never reaches here.
        /// It costs one pass over the published zips, whose central directories answer for every date
        /// they do not hold, so only the entries of those few days are read.
        ///
        /// A manager whose every line failed to resolve has no published row and is not found here,
        /// and is then named by its older filing: a stale name rather than a wrong number.
        /// </summary>
        private Dictionary<int, DateTime> ManagersPublishedAfter(DateTime after)
        {
            var managers = new Dictionary<int, DateTime>();
            if (after >= _foldedThrough || !Directory.Exists(_processedDataDirectory))
            {
                return managers;
            }

            // An entry is named after the day its rows were published, never before they were filed,
            // so only the entries after that day can hold a newer filing; its date comes from the row.
            var read = 0;
            foreach (var file in Directory.EnumerateFiles(_processedDataDirectory, "*.zip"))
            {
                using var zip = ZipFile.OpenRead(file);
                foreach (var entry in zip.Entries)
                {
                    if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(entry.Name), PeriodFormat,
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var published) ||
                        published <= after)
                    {
                        continue;
                    }

                    using var reader = new StreamReader(entry.Open());
                    while (reader.ReadLine() is { } line)
                    {
                        var fields = line.Split(',');
                        if (fields.Length > ManagerCikColumn &&
                            DateTime.TryParseExact(fields[0], PeriodFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var filed) &&
                            filed > after &&
                            int.TryParse(fields[ManagerCikColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cik) &&
                            (!managers.TryGetValue(cik, out var known) || known < filed))
                        {
                            managers[cik] = filed;
                        }
                    }

                    read++;
                }
            }

            Log.Trace($"SEC13FDownloader.ManagersPublishedAfter(): {managers.Count} managers already published a " +
                      $"filing after {after:yyyy-MM-dd}, read from {read} entries");
            return managers;
        }

        /// <summary>The accession of a published row, or empty for a line too short to carry one.</summary>
        private static string AccessionOf(string line)
        {
            var fields = line.Split(',');
            return fields.Length > AccessionColumn ? fields[AccessionColumn] : string.Empty;
        }

        /// <summary>One published row: a single reported position, on the date it was filed.</summary>
        internal sealed class HoldingsRow
        {
            /// <summary>The filing date. The entry the row belongs in is named by PublishedOn.</summary>
            public DateTime Time { get; init; }

            /// <summary>The reported line itself, with the filing it came from.</summary>
            public PositionLine Line { get; init; }
        }

        /// <summary>Reads a staging file back: the ticker, then the row in the published layout.</summary>
        private static IEnumerable<(string Ticker, HoldingsRow Row)> ReadStagedRows(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var comma = line.IndexOf(',');
                yield return (line.Substring(0, comma), ParseRow(line.Substring(comma + 1)));
            }
        }

        /// <summary>Reads a ticker's staged rows back, which are already in the published layout.</summary>
        private static IEnumerable<HoldingsRow> ReadRows(string path)
        {
            return File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(ParseRow);
        }

        /// <summary>
        /// Reads one published line back into a row. The staging files are this processor's own
        /// output read back in the same run, so a malformed line is a bug here rather than bad input
        /// and is left to throw.
        /// </summary>
        private static HoldingsRow ParseRow(string line)
        {
            var csv = line.Split(',');

            return new HoldingsRow
            {
                Time = DateTime.ParseExact(csv[0], PeriodFormat, CultureInfo.InvariantCulture),
                Line = new PositionLine
                {
                    Submission = new Submission
                    {
                        Accession = csv[1],
                        Cik = int.Parse(csv[2], NumberStyles.Integer, CultureInfo.InvariantCulture),
                        Period = DateTime.ParseExact(csv[3], PeriodFormat, CultureInfo.InvariantCulture),
                        FormType = csv[4],
                        AmendmentType = csv[5],
                        AmendmentNumber = csv[6].Length == 0
                            ? null
                            : int.Parse(csv[6], NumberStyles.Integer, CultureInfo.InvariantCulture),
                        ConfidentialOmitted = csv[18] == "1",
                        DateReported = csv[19].Length == 0
                            ? null
                            : DateTime.ParseExact(csv[19], PeriodFormat, CultureInfo.InvariantCulture)
                    },
                    TitleOfClass = csv[7],
                    Amount = ParseOptionalDecimal(csv[8]),
                    AmountType = csv[9],
                    ReportedValue = ParseOptionalDecimal(csv[10]),
                    ValueScale = int.Parse(csv[11], NumberStyles.Integer, CultureInfo.InvariantCulture),
                    PutCall = csv[12],
                    InvestmentDiscretion = csv[13],
                    OtherManager = csv[14],
                    VotingSole = ParseOptionalDecimal(csv[15]),
                    VotingShared = ParseOptionalDecimal(csv[16]),
                    VotingNone = ParseOptionalDecimal(csv[17])
                }
            };
        }

        /// <summary>
        /// Formats one reported position, in the twenty column layout SEC13FHolding parses. The
        /// filing date leads the line, because the entry it lands in is named after the day it was
        /// published and not after the filing.
        /// </summary>
        private static string FormatRow(HoldingsRow row)
        {
            var line = row.Line;
            var submission = line.Submission;

            return string.Join(',',
                row.Time.ToStringInvariant(PeriodFormat),
                submission.Accession,
                submission.Cik.ToStringInvariant(),
                submission.Period.ToStringInvariant(PeriodFormat),
                submission.FormType,
                submission.AmendmentType,
                submission.AmendmentNumber?.ToStringInvariant(),
                line.TitleOfClass,
                FormatValue(line.Amount),
                line.AmountType,
                FormatValue(line.ReportedValue),
                line.ValueScale.ToStringInvariant(),
                FormatPutCall(line.PutCall),
                line.InvestmentDiscretion,
                line.OtherManager,
                FormatValue(line.VotingSole),
                FormatValue(line.VotingShared),
                FormatValue(line.VotingNone),
                submission.ConfidentialOmitted ? "1" : "0",
                submission.DateReported?.ToStringInvariant(PeriodFormat));
        }

        /// <summary>
        /// The power of ten a detected factor stands for. The detector only ever returns a power of
        /// ten, so this is exact; anything else would be a bug here and is left at no scaling rather
        /// than silently rounded.
        /// </summary>
        internal static int ScaleOf(decimal factor)
        {
            var scale = 0;
            while (factor >= 10m)
            {
                factor /= 10m;
                scale++;
            }

            while (factor > 0m && factor < 1m)
            {
                factor *= 10m;
                scale--;
            }

            return factor == 1m ? scale : 0;
        }

        /// <summary>
        /// Shortens the option side to a single letter. The column is written once per reported
        /// position, so the three characters saved on every option line are worth the mapping.
        /// </summary>
        /// <remarks>
        /// A row is formatted twice, once into its staging file and once into the published entry,
        /// so this has to leave an already shortened side alone rather than blanking it.
        /// </remarks>
        private static string FormatPutCall(string putCall)
        {
            if (putCall.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            {
                return "C";
            }

            return putCall.StartsWith("P", StringComparison.OrdinalIgnoreCase) ? "P" : string.Empty;
        }

        /// <summary>Reports how much of the dataset made it through identity resolution.</summary>
        private void LogResolutionSummary()
        {
            var totalValue = _resolvedValue + _unresolvedValue;
            var coverage = totalValue == 0m ? 0m : 100m * _resolvedValue / totalValue;

            Log.Trace("SEC13FDownloader.LogResolutionSummary(): working set " +
                      $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB");

            Log.Trace($"SEC13FDownloader.LogResolutionSummary(): {_resolvedByCusip} distinct CUSIPs resolved by CUSIP, " +
                      $"{_resolvedByIsin} by constructed ISIN, {_resolvedByTicker} by the N-PORT ticker crosswalk, " +
                      $"{_resolvedByOptionUnderlying} by the security an option is written on, " +
                      $"{_unresolvedCusips.Count} unresolved, {_unresolvedCusips.Count(IsCins)} of them foreign (CINS), " +
                      $"{_malformedCusips} malformed, " +
                      $"{_optionSidesInferred} option lines given the side their CUSIP states");
            Log.Trace($"SEC13FDownloader.LogResolutionSummary(): {_optionLinesByPrice} option lines of multi-issue issuers " +
                      $"resolved by price, {_optionLinesWithoutOneMatch} dropped for matching no single issue");
            Log.Trace($"SEC13FDownloader.LogResolutionSummary(): {_unresolvedGroups} groups dropped, " +
                      $"{_conflictingTickers} of them, {(totalValue == 0m ? 0m : 100m * _conflictingValue / totalValue).ToStringInvariant("F2")}% " +
                      "of reported value, because the ticker belonged to another security that day, " +
                      $"{coverage.ToStringInvariant("F1")}% of reported value covered");
            Log.Trace($"SEC13FDownloader.LogResolutionSummary(): {_mismatchedGroups} crosswalk groups dropped because " +
                      $"their prices were another security's, {_debtCusipsRejected} groups of CUSIPs with letters in the issue " +
                      "number dropped for want of a price matching the close, " +
                      $"{(totalValue == 0m ? 0m : 100m * _mismatchedValue / totalValue).ToStringInvariant("F2")}% of reported value together");

            // The domestic ones are the gaps worth chasing: a foreign issuer LEAN does not list is expected.
            if (_unresolvedCusips.Count > 0)
            {
                Log.Trace($"SEC13FDownloader.LogResolutionSummary(): unresolved domestic sample: " +
                          $"{string.Join(", ", _unresolvedCusips.Where(cusip => !IsCins(cusip)).Take(25))}");
            }

            if (_conflictSample.Count > 0)
            {
                Log.Trace($"SEC13FDownloader.LogResolutionSummary(): ticker conflict sample: {string.Join("; ", _conflictSample)}");
            }
        }

        /// <summary>One table of an archive, where a short row means the layout moved and throws.</summary>
        private static IEnumerable<(Dictionary<string, int> Columns, string[] Fields)> ReadTable(
            ZipArchive zip, Archive archive, string table, params string[] required)
        {
            return SEC13FFiles.ReadTable(zip, archive.Name, table, skipShortRows: false, required);
        }

        /// <summary>
        /// Parses a date as the SEC writes it in these tables, "31-MAR-2026", with the ISO and US
        /// shapes accepted too because the older archives are not perfectly consistent.
        /// </summary>
        private static DateTime ParseSecDate(string value, Archive archive, string column)
        {
            // Some rows carry a time of day the tables do not otherwise use.
            var date = value.Trim().Split(' ')[0];
            if (DateTime.TryParseExact(date, SecDateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }

            throw new FormatException(
                $"SEC13FDownloader.ParseSecDate(): {archive.Name} {column} '{value}' matches none of " +
                $"{string.Join(", ", SecDateFormats)}");
        }

        /// <summary>Parses a reported amount, treating a blank as zero.</summary>
        private static decimal ParseDecimal(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return 0m;
            }

            if (!decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new FormatException($"SEC13FDownloader.ParseDecimal(): '{value}' is not a number");
            }

            return parsed;
        }

        /// <summary>Parses a reported amount, where a blank is an absent reading rather than a zero.</summary>
        private static decimal? ParseOptionalDecimal(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : ParseDecimal(value);
        }

        /// <summary>True for the "Y" the SEC writes in its flag columns.</summary>
        private static bool IsYes(string value)
        {
            return value.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Formats an amount: whole numbers without a decimal point, everything else invariant, and
        /// a field the filing left empty as empty, since that is not a reported zero.
        /// </summary>
        private static string FormatValue(decimal? value)
        {
            return value switch
            {
                null => string.Empty,
                // Formatted rather than cast: filers type nonsense into VALUE, and a decimal above
                // long.MaxValue threw out of the cast and stopped the run over one bad line.
                { } whole when whole == Math.Truncate(whole) => whole.ToString("0", CultureInfo.InvariantCulture),
                _ => value.Value.ToStringInvariant()
            };
        }

        /// <summary>
        /// True when the ticker can name a file LEAN will ask for. Skips path separators and invalid
        /// file name characters, and the space and '|' a Symbol cannot hold: the map files carry
        /// "ua.c " for Under Armour's class C, and its file could never be read.
        /// </summary>
        internal static bool IsFileNameSafe(string ticker)
        {
            return ticker.IndexOf('/') < 0
                   && ticker.IndexOf('\\') < 0
                   && ticker.IndexOf('|') < 0
                   && !ticker.Any(char.IsWhiteSpace)
                   && ticker.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>Disposes unmanaged resources.</summary>
        public void Dispose()
        {
            _edgar.DisposeSafely();

            // A run that failed half way leaves its staging behind, and nothing else ever reads it.
            try
            {
                if (Directory.Exists(_stagingDirectory))
                {
                    Directory.Delete(_stagingDirectory, recursive: true);
                }
            }
            catch (IOException err)
            {
                Log.Error(err, $"SEC13FDownloader.Dispose(): could not delete {_stagingDirectory}");
            }

            GC.SuppressFinalize(this);
        }
    }
}
