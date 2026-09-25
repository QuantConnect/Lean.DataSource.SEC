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
using NodaTime;
using QuantConnect.Data;
using QuantConnect.Util;
using static QuantConnect.StringExtensions;
using System.Runtime.CompilerServices;

// The processor writes the line layout this assembly reads, so both use one implementation of it.
[assembly: InternalsVisibleTo("process")]
[assembly: InternalsVisibleTo("Tests")]

namespace QuantConnect.DataSource
{
    /// <summary>
    /// One position as a single institutional manager reported it on a single SEC Form 13F
    /// submission. Managers exercising discretion over at least 100 million dollars must file a
    /// Form 13F within 45 days of quarter end, listing the covered securities they hold.
    ///
    /// Nothing here is summed or otherwise derived: every field is the value the SEC publishes for
    /// that line of that filing's information table. A manager that reports the same security on
    /// two lines, which the rules allow when the discretion differs, produces two records, and they
    /// are left apart. Holder counts, quarter-over-quarter change and concentration are all
    /// derivable from the records of a day and are left to the algorithm.
    ///
    /// This is the factory that reads one line. The points an algorithm receives are
    /// SEC13FHoldings, the collection of every record a security carries for one
    /// filing date.
    /// </summary>
    public class SEC13FHolding : BaseData
    {
        // The frozen layout: the filing date, the filing's identity, the reported quarter, the
        // position, the voting authority and the two confidential treatment columns.
        private const int ExpectedColumns = 20;

        /// <summary>Format of the filing date column, which is also the name of the file.</summary>
        public const string FilingDateFormat = "yyyyMMdd";

        /// <summary>
        /// EDGAR accession number of the submission this line was reported on, such as
        /// 0001067983-26-000012. It identifies the filing on the SEC's own site and is what groups
        /// the records of one submission back together.
        /// </summary>
        public string AccessionNumber { get; set; }

        /// <summary>
        /// Central Index Key of the manager that filed the submission. It is the stable identity of
        /// a fund across name changes, which is why it, and not the name, is carried on every
        /// line of the files. The names live once in managers.csv beside the dataset.
        /// </summary>
        public int ManagerCik { get; set; }

        /// <summary>
        /// Name of the manager as its most recent cover page states it, read from managers.csv, with
        /// any comma taken out because that file is split on every one. It is the current name even
        /// on an old filing, and null for a CIK the file does not carry.
        /// </summary>
        public string ManagerName { get; set; }

        /// <summary>
        /// The quarter the position is reported for, which is the SEC PERIODOFREPORT. It is carried
        /// rather than derived from Time because the two are unrelated: late
        /// filings and amendments mean one filing date carries several different reported quarters,
        /// and the gap between them runs from zero to years.
        /// </summary>
        public DateTime PeriodEnd { get; set; }

        /// <summary>
        /// Submission type, which is 13F-HR for a holdings report and 13F-HR/A for an amendment.
        /// A 13F-NT notice reports no positions and so contributes no records at all.
        /// </summary>
        public string FormType { get; set; }

        /// <summary>
        /// For an amendment, whether it restates the whole report or only adds holdings:
        /// RESTATEMENT replaces the original filing and NEW HOLDINGS supplements it. The SEC
        /// leaves the distinction to the filer to declare. Empty on an original filing.
        /// </summary>
        public string AmendmentType { get; set; }

        /// <summary>Sequence number of the amendment, or null on an original filing.</summary>
        public int? AmendmentNumber { get; set; }

        /// <summary>
        /// Class of the security as the manager titled it, such as COM or CL A. It is free text
        /// that the filer writes, so it varies between managers for the same security.
        /// </summary>
        public string TitleOfClass { get; set; }

        /// <summary>
        /// Size of the position, which is a number of shares when AmountType is SH
        /// and a principal amount when it is PRN. The two are not comparable and are deliberately
        /// left in one field with its unit beside it, as the SEC reports them.
        /// </summary>
        public decimal? Amount { get; set; }

        /// <summary>Unit of Amount: SH for shares, PRN for a principal amount.</summary>
        public string AmountType { get; set; }

        /// <summary>
        /// Market value of the position exactly as the manager reported it, in the unit the filing
        /// used. Before 2023 the SEC asked for thousands of dollars and since then for whole
        /// dollars, and filers on both sides of that change ignore the instruction, so the number
        /// is published untouched with ValueScale beside it.
        /// </summary>
        public decimal? ReportedValue { get; set; }

        /// <summary>
        /// The power of ten that turns ReportedValue into whole dollars: 3 for a value
        /// stated in thousands, 0 for one already in dollars, and -3 for a line that overstated its
        /// value a thousandfold, which happens often enough to matter.
        ///
        /// This is the one reading in the record that the SEC does not publish. It comes from the
        /// filing's period and from the size of the value against the security's close, because
        /// filers disagree with the instruction often enough that the period alone is wrong. It is
        /// carried beside the reported number rather than multiplied into it, so that what the
        /// manager filed stays readable and this judgement stays separable from it. Use
        /// MarketValue to apply it.
        /// </summary>
        public int ValueScale { get; set; }

        /// <summary>
        /// Market value of the position in whole dollars. Worked out from ReportedValue
        /// and ValueScale rather than carried as a column of its own, so the three can never disagree.
        /// </summary>
        public decimal? MarketValue => ReportedValue * PowerOfTen(ValueScale);

        /// <summary>Ten to the <paramref name="scale"/>, in decimal so the result stays exact.</summary>
        internal static decimal PowerOfTen(int scale)
        {
            var power = 1m;
            for (var i = 0; i < Math.Abs(scale); i++)
            {
                power *= 10m;
            }

            return scale < 0 ? 1m / power : power;
        }

        /// <summary>
        /// Whether the position is an option on the security rather than the security itself, and
        /// on which side. Null for a holding of the security. An option line states the shares
        /// underlying the contracts, not the number of contracts.
        /// </summary>
        public OptionRight? PutCall { get; set; }

        /// <summary>
        /// Who exercises investment discretion over the position: SOLE for the filing manager
        /// alone, DFND when it is defined by other managers, OTR otherwise.
        /// </summary>
        public string InvestmentDiscretion { get; set; }

        /// <summary>
        /// The other managers that share the position, as the sequence numbers the filing gives
        /// them on its cover page, separated by semicolons. Empty when the manager reports alone.
        /// </summary>
        public string OtherManager { get; set; }

        /// <summary>Shares over which the manager holds sole voting authority.</summary>
        public decimal? VotingSole { get; set; }

        /// <summary>Shares over which the manager shares voting authority.</summary>
        public decimal? VotingShared { get; set; }

        /// <summary>Shares over which the manager holds no voting authority.</summary>
        public decimal? VotingNone { get; set; }

        /// <summary>
        /// True when the submission this line belongs to withheld other positions under
        /// confidential treatment. The filing is then incomplete by design and the withheld
        /// positions surface in a later one, so the flag is carried rather than silently ignored.
        /// </summary>
        public bool ConfidentialOmitted { get; set; }

        /// <summary>
        /// The date a previously confidential filing was originally made, which the SEC publishes
        /// as DATEREPORTED. It is filled on about two filings in a thousand and is null on the
        /// rest, so it marks positions that were withheld and later released rather than serving as
        /// a timestamp. The timestamp is Time, the filing date.
        /// </summary>
        public DateTime? DateReported { get; set; }

        /// <summary>
        /// The record covers the filing date it is stamped with, ending at midnight that night.
        ///
        /// LEAN emits a point at its end time rather than at its time, so this is what decides when
        /// an algorithm sees the filing: the day's filings all arrive at 00:00 the following day,
        /// after EDGAR has finished listing that day at about 22:05 ET. Nothing is readable before
        /// it was filed, and a whole day of filings arrives at once instead of trickling in.
        /// </summary>
        public override DateTime EndTime => Time.AddDays(1);

        /// <summary>Name of the dataset's folder under alternative/sec/, which is where its files live.</summary>
        public static string ReportFolder => "13f";

        /// <summary>Creates a new default instance.</summary>
        public SEC13FHolding()
        {
        }

        /// <summary>
        /// Location of the source file. One zip per security holds one entry per filing date, so
        /// that the dataset stays at a file per security instead of the eight and a half million a
        /// loose file per date would take. LEAN reads the entry straight out of the zip.
        /// </summary>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            return new SubscriptionDataSource(
                Path.Combine(
                    Globals.DataFolder,
                    "alternative",
                    "sec",
                    ReportFolder,
                    $"{config.Symbol.Value.ToLowerInvariant()}.zip#{date.ToStringInvariant(FilingDateFormat)}.csv"
                ),
                SubscriptionTransportMedium.LocalFile,
                FileFormat.FoldingCollection
            );
        }

        /// <summary>Parses one line of the file into one reported position.</summary>
        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            var csv = line.Split(',');

            // A truncated line is skipped rather than thrown on: LEAN takes an exception out of
            // Reader as a reader error and drops the line, so throwing would turn a silent skip
            // into a logged one and nothing more. The test is "fewer than" and not "not equal to",
            // so a column appended in a later revision of the file leaves every existing one
            // readable instead of muting the whole dataset.
            if (csv.Length < ExpectedColumns)
            {
                return null;
            }

            var point = Parse(csv);
            point.Symbol = config.Symbol;
            point.ManagerName = SEC13FManagerNameProvider.GetName(point.ManagerCik);
            return point;
        }

        /// <summary>Reads one already split line into a point that has no symbol yet.</summary>
        internal static SEC13FHolding Parse(string[] csv)
        {
            var point = new SEC13FHolding
            {
                Time = DateTime.ParseExact(csv[0], FilingDateFormat, CultureInfo.InvariantCulture),
                AccessionNumber = csv[1],
                ManagerCik = int.Parse(csv[2], NumberStyles.Integer, CultureInfo.InvariantCulture),
                PeriodEnd = DateTime.ParseExact(csv[3], FilingDateFormat, CultureInfo.InvariantCulture),
                FormType = csv[4],
                AmendmentType = csv[5],
                AmendmentNumber = ParseCount(csv[6]),
                TitleOfClass = csv[7],
                Amount = ParseMeasure(csv[8]),
                AmountType = csv[9],
                ReportedValue = ParseMeasure(csv[10]),
                ValueScale = int.Parse(csv[11], NumberStyles.Integer, CultureInfo.InvariantCulture),
                PutCall = ParsePutCall(csv[12]),
                InvestmentDiscretion = csv[13],
                OtherManager = csv[14],
                VotingSole = ParseMeasure(csv[15]),
                VotingShared = ParseMeasure(csv[16]),
                VotingNone = ParseMeasure(csv[17]),
                ConfidentialOmitted = csv[18] == "1",
                DateReported = ParseOptionalDate(csv[19])
            };

            // The point's value is the position in dollars, which is the one measure of a holding
            // that is comparable between managers and between securities.
            point.Value = point.MarketValue ?? 0m;
            return point;
        }

        /// <summary>Parses one measure column, where an empty field is an absent reading.</summary>
        internal static decimal? ParseMeasure(string value)
        {
            return value.IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture));
        }

        /// <summary>Parses one whole number column, where an empty field is an absent reading.</summary>
        internal static int? ParseCount(string value)
        {
            return value.IfNotNullOrEmpty<int?>(s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));
        }

        /// <summary>Parses one date column, where an empty field is an absent reading.</summary>
        internal static DateTime? ParseOptionalDate(string value)
        {
            return value.IfNotNullOrEmpty<DateTime?>(s => DateTime.ParseExact(s, FilingDateFormat, CultureInfo.InvariantCulture));
        }

        /// <summary>Parses the option column, where an empty field means the security itself.</summary>
        internal static OptionRight? ParsePutCall(string value)
        {
            return value switch
            {
                "C" => OptionRight.Call,
                "P" => OptionRight.Put,
                _ => null
            };
        }

        /// <summary>Data time zone (Eastern, the SEC filing time zone).</summary>
        public override DateTimeZone DataTimeZone() => TimeZones.NewYork;

        /// <summary>Supported resolutions (Daily only, the quarterly cadence is modeled as Daily).</summary>
        public override List<Resolution> SupportedResolutions() => DailyResolution;

        /// <summary>Default resolution.</summary>
        public override Resolution DefaultResolution() => Resolution.Daily;

        /// <summary>Sparse data: a security is only reported on the days managers file for it.</summary>
        public override bool IsSparseData() => true;

        /// <summary>Linked to Equities, so renames and delistings are applied via map files.</summary>
        public override bool RequiresMapping() => true;

        /// <summary>Creates a copy of the instance.</summary>
        public override BaseData Clone()
        {
            return new SEC13FHolding
            {
                Symbol = Symbol,
                Time = Time,
                Value = Value,
                AccessionNumber = AccessionNumber,
                ManagerCik = ManagerCik,
                ManagerName = ManagerName,
                PeriodEnd = PeriodEnd,
                FormType = FormType,
                AmendmentType = AmendmentType,
                AmendmentNumber = AmendmentNumber,
                TitleOfClass = TitleOfClass,
                Amount = Amount,
                AmountType = AmountType,
                ReportedValue = ReportedValue,
                ValueScale = ValueScale,
                PutCall = PutCall,
                InvestmentDiscretion = InvestmentDiscretion,
                OtherManager = OtherManager,
                VotingSole = VotingSole,
                VotingShared = VotingShared,
                VotingNone = VotingNone,
                ConfidentialOmitted = ConfidentialOmitted,
                DateReported = DateReported
            };
        }

        /// <summary>String representation for debugging.</summary>
        public override string ToString()
        {
            return Invariant($"{Symbol} - {ManagerName ?? $"CIK {ManagerCik}"} for {PeriodEnd:yyyy-MM-dd}: {Amount} {AmountType}, {MarketValue:C0}");
        }
    }
}
