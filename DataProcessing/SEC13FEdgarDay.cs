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
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QuantConnect.Logging;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// One day of Form 13F filings read straight from EDGAR and written as an archive with the
    /// tables the SEC data sets carry, so the processor reads it like any other window.
    ///
    /// The data sets come out in three month batches a few days after each window closes. A daily
    /// job waiting for them publishes a filing up to three months after it was public, while a
    /// backtest sees it the day after it was filed. EDGAR lists each day's filings in its daily
    /// index that night, and their information tables carry the lines the data sets do: 48 of 48
    /// filings compared line by line, and the same accession numbers on six sample days, so reading
    /// EDGAR is what lets the live job publish on the date the history uses.
    /// </summary>
    internal static class SEC13FEdgarDay
    {
        private const string FormPattern = "13F-HR(?:/A)?";

        private const string PrimaryDocumentTypePrefix = "13F-HR";
        private const string InformationTableDocumentType = "INFORMATION TABLE";

        /// <summary>What the archive's tables carry for one filing.</summary>
        internal sealed class Filing
        {
            public string Accession { get; init; }
            public string SubmissionType { get; init; }
            public int Cik { get; init; }
            public DateTime Filed { get; init; }
            public DateTime Period { get; init; }
            public bool ConfidentialOmitted { get; init; }

            /// <summary>The cover page: the manager's name and what an amendment declares about itself.</summary>
            public string ManagerName { get; init; }
            public string AmendmentNumber { get; init; }
            public string AmendmentType { get; init; }
            public DateTime? DateReported { get; init; }

            /// <summary>INFOTABLE rows, in the order of <see cref="LineColumns"/>.</summary>
            public List<string[]> Lines { get; } = new();
        }

        private static readonly string[] LineColumns =
        [
            "CUSIP", "TITLEOFCLASS", "VALUE", "SSHPRNAMT", "SSHPRNAMTTYPE", "PUTCALL", "INVESTMENTDISCRETION",
            "OTHERMANAGER", "VOTING_AUTH_SOLE", "VOTING_AUTH_SHARED", "VOTING_AUTH_NONE"
        ];

        private const string XmlDateFormat = "MM-dd-yyyy";

        /// <summary>
        /// The share of a day's filings that may be unreadable before the day fails. A filing the SEC
        /// accepted and this reader cannot is skipped, since failing the day over it would stop the
        /// data set for good; a whole day of them is a layout change and must still throw. One filing
        /// is always allowed, so a quiet day of three does not fail over its one bad one.
        /// </summary>
        private const double MaxUnreadableShare = 0.05;

        /// <summary>The name a day's archive is cached and logged under.</summary>
        public static string ArchiveName(DateTime day) => $"edgar-{day.ToString(DateFormat.EightCharacter, CultureInfo.InvariantCulture)}_form13f.zip";

        /// <summary>What building one day came to: its archive, or why there is none.</summary>
        /// <param name="Path">The day's archive, or null when the day was not read.</param>
        /// <param name="Filings">The holdings filings fetched, which is what reading the day cost.</param>
        /// <param name="OverBudget">The day was left unread because it did not fit in what the run had left.</param>
        public readonly record struct Built(string Path, int Filings, bool OverBudget);

        /// <summary>
        /// Writes the day's archive into <paramref name="directory"/> and returns its path, or no path
        /// when EDGAR has not published an index for the day, which is every weekend and holiday and a
        /// day whose index is late. <paramref name="listDirectory"/> returns the names in one of EDGAR's
        /// directory listings and <paramref name="getText"/> a file; both throw on any failure.
        ///
        /// <paramref name="filingBudget"/> is how many filings the caller has room left to fetch. The
        /// index costs one round trip and each filing another, so the whole cost of a day is known
        /// before the expensive part starts: a day that does not fit is left whole for the next run
        /// rather than read in half.
        /// </summary>
        public static Built Build(DateTime day, string directory, Func<string, ISet<string>> listDirectory,
            Func<string, string> getText, int filingBudget = int.MaxValue)
        {
            var path = System.IO.Path.Combine(directory, ArchiveName(day));
            if (File.Exists(path))
            {
                // Fetched already, by an earlier run or a test: it costs no round trip and no budget.
                return new Built(path, 0, OverBudget: false);
            }

            if (!SECEdgarIndex.IsIndexPublished(day, listDirectory))
            {
                return new Built(null, 0, OverBudget: false);
            }

            // The index lists a filing once for every CIK it names.
            var entries = SECEdgarIndex.DistinctFilings(ParseIndex(getText(SECEdgarIndex.IndexUrl(day))));
            if (entries.Count > filingBudget)
            {
                Log.Trace($"SEC13FEdgarDay.Build(): {day:yyyy-MM-dd} carries {entries.Count} holdings filings and " +
                          $"{filingBudget} are left in this run, so it is left whole for the next one");
                return new Built(null, entries.Count, OverBudget: true);
            }

            var filings = new List<Filing>(entries.Count);
            var unreadable = 0;
            foreach (var entry in entries)
            {
                try
                {
                    filings.Add(ParseFiling(entry, getText(SECEdgarIndex.ArchivesBaseUrl + entry.Path)));
                }
                catch (Exception error) when (error is InvalidDataException or FormatException)
                {
                    // One filing the SEC accepted and this reader cannot is not worth the data set
                    // for: thrown, it fails the day, the day is never recorded, and every run after
                    // it meets the same filing and fails again until someone ships code. An
                    // HttpRequestException is not caught, so a network failure still fails the day
                    // and the day is read again.
                    Log.Error($"SEC13FEdgarDay.Build(): {day:yyyy-MM-dd} {entry.Accession} cannot be read, " +
                              $"skipping it: {error.Message}");
                    unreadable++;
                }
            }

            // A layout change reads as filing after filing being unreadable, and that has to fail
            // loudly rather than publish a day emptied of most of what it held.
            if (unreadable > Math.Max(1, entries.Count * MaxUnreadableShare))
            {
                throw new InvalidDataException(
                    $"SEC13FEdgarDay.Build(): {day:yyyy-MM-dd}: {unreadable} of {entries.Count} filings could not be " +
                    "read, which is more than a bad filing or two. The layout of the primary document has likely changed.");
            }

            Directory.CreateDirectory(directory);
            SEC13FFiles.WriteThenMove(path, stream => WriteArchive(stream, filings));

            Log.Trace($"SEC13FEdgarDay.Build(): {day:yyyy-MM-dd}: {filings.Count} holdings filings, " +
                      $"{filings.Sum(filing => filing.Lines.Count)} information table lines");
            return new Built(path, filings.Count, OverBudget: false);
        }

        /// <summary>
        /// The holdings reports and their amendments listed in a daily index. Notices are left out:
        /// they carry no information table, and the processor skips them in the data sets too.
        /// </summary>
        internal static List<SECEdgarIndex.Entry> ParseIndex(string text)
        {
            return SECEdgarIndex.ParseIndex(text, FormPattern);
        }

        /// <summary>
        /// Reads one full submission file: the period, the cover page and the confidential treatment
        /// flag from the primary document, and every line of its information tables. The form type, filer and
        /// filing date come from the index, as they do in the data sets.
        /// </summary>
        internal static Filing ParseFiling(SECEdgarIndex.Entry entry, string text)
        {
            XElement primary = null;
            var tables = new List<XElement>();

            foreach (var document in SECEdgarIndex.Documents(text))
            {
                if (document.Xml == null)
                {
                    continue;
                }

                if (document.Type.StartsWith(PrimaryDocumentTypePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    primary = SECEdgarIndex.ParseXml(document.Xml, entry.Path);
                }
                else if (document.Type.Equals(InformationTableDocumentType, StringComparison.OrdinalIgnoreCase))
                {
                    tables.Add(SECEdgarIndex.ParseXml(document.Xml, entry.Path));
                }
            }

            if (primary == null)
            {
                throw new InvalidDataException($"SEC13FEdgarDay.ParseFiling(): {entry.Path} carries no primary document");
            }

            var period = Value(primary, "periodOfReport") ?? Value(primary, "reportCalendarOrQuarter")
                ?? throw new InvalidDataException($"SEC13FEdgarDay.ParseFiling(): {entry.Path} carries no period of report");

            var filing = new Filing
            {
                Accession = entry.Accession,
                SubmissionType = entry.FormType,

                // The filing names its own filer. The index lists an accession once per CIK it
                // names, so the first line of it can be a co-filer whose name sorts earlier, and
                // the data sets take the CIK from the filing: the two paths have to agree.
                Cik = FilerCik(primary) ?? entry.Cik,
                Filed = entry.Filed,
                Period = DateTime.ParseExact(period, XmlDateFormat, CultureInfo.InvariantCulture),
                ConfidentialOmitted = IsTrue(Value(primary, "isConfidentialOmitted")),

                // Scoped to the filing manager: the signature and the other managers carry a name too.
                ManagerName = SECEdgarIndex.Elements(primary, "filingManager")
                    .Select(manager => Value(manager, "name")).FirstOrDefault(),
                AmendmentNumber = Value(primary, "amendmentNo"),
                AmendmentType = Value(primary, "amendmentType"),
                DateReported = DateTime.TryParseExact(Value(primary, "dateReported"), XmlDateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var reported) ? reported : null
            };

            foreach (var line in tables.SelectMany(table => SECEdgarIndex.Elements(table, "infoTable")))
            {
                filing.Lines.Add(new[]
                {
                    Value(line, "cusip"),
                    Value(line, "titleOfClass"),
                    Value(line, "value"),
                    Value(line, "sshPrnamt"),
                    Value(line, "sshPrnamtType"),
                    Value(line, "putCall"),
                    Value(line, "investmentDiscretion"),
                    Value(line, "otherManager"),
                    Value(line, "Sole"),
                    Value(line, "Shared"),
                    Value(line, "None")
                });
            }

            return filing;
        }

        /// <summary>
        /// The filer's CIK as the primary document states it, or null when it carries none. Scoped to
        /// the credentials of the filer info, which is the only place that element appears: a search
        /// of the whole document would also reach the CIKs of the other managers on the cover page.
        /// </summary>
        private static int? FilerCik(XElement primary)
        {
            var credentials = SECEdgarIndex.Elements(primary, "credentials").FirstOrDefault();
            return credentials != null &&
                   int.TryParse(SECEdgarIndex.Value(credentials, "cik"), NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out var cik)
                ? cik
                : null;
        }

        /// <summary>Writes the four tables the processor reads, in the data sets' layout.</summary>
        internal static void WriteArchive(Stream stream, IEnumerable<Filing> filings)
        {
            var submissions = new StringBuilder("ACCESSION_NUMBER\tFILING_DATE\tSUBMISSIONTYPE\tCIK\tPERIODOFREPORT\n");
            var summaries = new StringBuilder("ACCESSION_NUMBER\tISCONFIDENTIALOMITTED\n");
            var covers = new StringBuilder("ACCESSION_NUMBER\tAMENDMENTNO\tAMENDMENTTYPE\tDATEREPORTED\tFILINGMANAGER_NAME\n");
            var lines = new StringBuilder("ACCESSION_NUMBER\t").AppendJoin('\t', LineColumns).Append('\n');

            foreach (var filing in filings)
            {
                submissions.Append(string.Join('\t', filing.Accession,
                    filing.Filed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), filing.SubmissionType,
                    filing.Cik.ToString(CultureInfo.InvariantCulture), filing.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append('\n');
                summaries.Append(filing.Accession).Append('\t').Append(filing.ConfidentialOmitted ? "Y" : "N").Append('\n');
                covers.Append(string.Join('\t', filing.Accession, Clean(filing.AmendmentNumber), Clean(filing.AmendmentType),
                    filing.DateReported?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    Clean(filing.ManagerName))).Append('\n');

                foreach (var line in filing.Lines)
                {
                    lines.Append(filing.Accession);
                    foreach (var field in line)
                    {
                        lines.Append('\t').Append(Clean(field));
                    }

                    lines.Append('\n');
                }
            }

            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var (name, content) in new[] { ("SUBMISSION.tsv", submissions), ("SUMMARYPAGE.tsv", summaries), ("COVERPAGE.tsv", covers), ("INFOTABLE.tsv", lines) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
                writer.Write(content.ToString());
            }
        }

        private static string Value(XElement root, string name) => SECEdgarIndex.Value(root, name);

        private static bool IsTrue(string value)
        {
            return value != null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("Y", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A field as a table cell: tabs and line breaks would split the row.</summary>
        private static string Clean(string value)
        {
            return value == null ? string.Empty : Regex.Replace(value, @"\s+", " ").Trim();
        }
    }
}
