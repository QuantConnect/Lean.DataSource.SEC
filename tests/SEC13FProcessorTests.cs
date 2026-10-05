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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.DataProcessing;
using QuantConnect.DataSource;
using QuantConnect.Securities;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Unit tests for the 13F processor.
    ///
    /// The data classes had full coverage while the processor, which is where the subtle logic
    /// lives, had none. Every case below is a defect that actually reached a run and was caught by
    /// hand: a CUSIP left-padded into a different security, a thousand-fold step in the middle of
    /// the value history, and a file written in quarter order that LEAN then silently truncated.
    /// </summary>
    [TestFixture]
    public class SEC13FProcessorTests
    {
        /// <summary>The filing date the counting tests file on, when the date itself is not the point.</summary>
        private static readonly DateTime Filed = new(2024, 2, 14);

        private string _root;
        private bool _configSeeded;
        private string _previousDataFolder;
        private string _previousLookupDate;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), $"sec-13f-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (_configSeeded)
            {
                // The data folder is process wide and the resolver caches the one it was built with,
                // so a fixture left behind would be read by whatever runs next. A key that was absent
                // goes back to absent: written back as an empty string it left Globals.DataFolder
                // empty for every later test in the process.
                Config.Reset();
                if (_previousDataFolder != null)
                {
                    Config.Set("data-folder", _previousDataFolder);
                }

                if (_previousLookupDate != null)
                {
                    Config.Set("map-file-provider-lookup-date", _previousLookupDate);
                }

                Globals.Reset();
                _configSeeded = false;
            }

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        // ---- CUSIP normalisation --------------------------------------------------------------

        [TestCase("037833100")]     // Apple
        [TestCase("464287226")]     // iShares Core US Aggregate Bond
        [TestCase("72201R205")]
        public void ANineCharacterCusipIsLeftAlone(string cusip)
        {
            // Most reported CUSIPs already carry their check digit and must not be touched at all.
            Assert.AreEqual(cusip, SEC13FDownloader.NormalizeCusip(cusip));
        }

        [TestCase("37833100", "037833100")]     // Apple, one leading zero eaten
        [TestCase("2824100", "002824100")]      // Abbott, two eaten
        [TestCase("84670702", "084670702")]
        public void AShortCusipMissingItsLeadingZerosIsPadded(string reported, string expected)
        {
            // The full history carries 12,372 short CUSIPs, most of them an identifier a spreadsheet
            // somewhere read as a number and stripped the leading zeros off.
            Assert.AreEqual(expected, SEC13FDownloader.NormalizeCusip(reported));
        }

        [TestCase("46428722", "464287226")]     // iShares Core US Aggregate Bond
        [TestCase("72201R20", "72201R205")]
        public void AShortCusipMissingItsCheckDigitIsCompletedInstead(string reported, string expected)
        {
            // The other reason a CUSIP arrives short, and it needs the opposite repair. Left-padding
            // "46428722" would have produced 046428722, a perfectly well-formed identifier belonging
            // to a different security, so the two cases are told apart by the check digit itself:
            // the padded reading is only accepted when it checks out.
            Assert.AreEqual(expected, SEC13FDownloader.NormalizeCusip(reported));
        }

        [TestCase("037833100")]     // Apple
        [TestCase("594918104")]     // Microsoft
        [TestCase("67066G104")]     // NVIDIA, the letter case
        [TestCase("464287226")]     // iShares Core US Aggregate Bond
        [TestCase("002824100")]     // Abbott, the leading-zero case
        public void TheCheckDigitAgreesWithRealCusips(string cusip)
        {
            // Everything above rests on the check digit being right, so it is held against real
            // published identifiers rather than against itself.
            Assert.AreEqual(cusip[8], SEC13FDownloader.ComputeCusipCheckDigit(cusip.Substring(0, 8)),
                $"the computed check digit disagrees with the published {cusip}");
        }

        [TestCase("")]
        [TestCase("0")]
        [TestCase("ABC")]
        [TestCase("12345")]
        public void JunkTooShortToBeACusipIsRejected(string cusip)
        {
            // Below six characters there is not enough of an identifier left to repair, and guessing
            // one would attribute somebody's positions to whichever security the guess landed on.
            Assert.IsNull(SEC13FDownloader.NormalizeCusip(cusip));
        }

        // ---- The constructed US ISIN ----------------------------------------------------------

        [TestCase("037833100", "US0378331005")]     // Apple
        [TestCase("02079K305", "US02079K3059")]     // Alphabet Class C
        public void TheConstructedIsinMatchesTheRealOne(string cusip, string isin)
        {
            // This is the second step of the identity chain and it is pure arithmetic, so it is
            // either exactly right or it silently resolves to nothing. It lifted measured coverage
            // from 76.1% to 88.9% of reported value, which is only true while it is exact: both of
            // these are checked against the security's real published ISIN.
            Assert.AreEqual(isin, SEC13FDownloader.BuildUnitedStatesIsin(cusip));
        }

        // ---- The VALUE unit break at 2023 ------------------------------------------------------

        [TestCase("2022-11-14", 1000000)]   // reported in thousands, scaled up
        [TestCase("2022-12-31", 1000000)]   // the last day of the old unit
        [TestCase("2023-01-01", 1000)]      // the first day of the new one, left alone
        [TestCase("2023-02-14", 1000)]
        public void ValueIsScaledOnlyForFilingsMadeBefore2023(string filingDate, decimal expected)
        {
            // VALUE changed unit with the 2023 filings: through 2022 it is thousands of dollars,
            // from 2023 whole dollars. Measured on the real archives as the median VALUE/SSHPRNAMT
            // of share lines, which is a price per share: 0.0460 in NOV-2022 against 39.86 in
            // FEB-2023. Left unscaled this puts a 1000x step in the middle of the published history
            // and breaks every comparison that crosses it. The cut is on FILING_DATE rather than on
            // the reported period, because the rule changed for filings made from January 2023.
            var filing = DateTime.Parse(filingDate);
            var holdings = ReadInfoTable(
                new[] { Line("0000000000-00-000001", "037833100", "1000", "100", "SH") },
                Submission("0000000000-00-000001", filing, new DateTime(2022, 9, 30)));

            Assert.AreEqual(1, holdings.Count);
            Assert.AreEqual(expected, holdings.Values.Single().ReportedValue);
        }

        [Test]
        public void AFilerThatReportedDollarsBefore2023IsNotScaled()
        {
            // The rule was thousands, but not every filer followed it. In Apple's December 2019
            // quarter 85 of 5,365 lines were already in dollars, and multiplied by a thousand they
            // made 88 percent of the total: an implied $2,577 a share against a $293.65 close. Each
            // line is held against the quarter-end close, which needs no other filer.
            SeedCloses("20220930", ("aapl", 150m));
            var filing = new DateTime(2022, 11, 14);
            var quarter = new DateTime(2022, 9, 30);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "15", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "15000", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 111),
                Submission("0000000000-00-000002", filing, quarter, cik: 222));

            Assert.AreEqual(15000m + 15000m, holdings.Values.Single().ReportedValue,
                "the one in thousands is scaled, the one already in dollars is not");
        }

        [Test]
        public void AFilerStillReportingThousandsAfter2023IsScaled()
        {
            // The same break from the other side: 548 of Apple's March 2026 lines were still in
            // thousands, which left the published value five percent short. The quarter ends on a
            // Sunday, so the close is the Friday's.
            SeedCloses("20231229", ("aapl", 150m));
            var filing = new DateTime(2024, 2, 14);
            var quarter = new DateTime(2023, 12, 31);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "15000", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "15", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 111),
                Submission("0000000000-00-000002", filing, quarter, cik: 222));

            Assert.AreEqual(2 * 15000m, holdings.Values.Single().ReportedValue);
        }

        [Test]
        public void OnlyAShareLineIsHeldAgainstTheClose()
        {
            // A bond at par against a stock near $1,000 sits on the thousands step by chance, and so
            // can an option line. Neither has a price of its own, so both keep their filing's unit.
            SeedCloses("20231229", ("aapl", 1000m));
            var filing = new DateTime(2024, 2, 14);
            var quarter = new DateTime(2023, 12, 31);
            var lines = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "100000", "100", "SH"),
                    Line("0000000000-00-000001", "037833100", "100", "100", "PRN"),
                    Line("0000000000-00-000001", "037833100", "100", "100", "SH", putCall: "Call")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 111)).Values.Single().Lines;

            Assert.AreEqual(new[] { 0, 0, 0 }, lines.Select(line => line.ValueScale).ToArray());
        }

        [Test]
        public void AFieldTheFilingLeftEmptyIsNotAZero()
        {
            var line = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "", "100", "SH", votingSole: "", votingShared: "0", votingNone: "")
                },
                Submission("0000000000-00-000001", Filed, new DateTime(2023, 12, 31))).Values.Single().Lines.Single();

            Assert.IsNull(line.ReportedValue);
            Assert.IsNull(line.VotingSole);
            Assert.IsNull(line.VotingNone);
            Assert.AreEqual(0m, line.VotingShared);
            Assert.AreEqual(100m, line.Amount);
        }

        [TestCase("1, 2", "1;2")]
        [TestCase("9,10,11", "9;10;11")]
        [TestCase("1 2", "1;2")]
        [TestCase("1;2", "1;2")]
        [TestCase("NONE", "")]
        [TestCase("0", "")]
        [TestCase("", "")]
        public void TheOtherManagersAreJoinedBySingleSemicolons(string filed, string expected)
        {
            // A comma became a space and every space a semicolon, so "9, 10" was published as "9;;10".
            Assert.AreEqual(expected, SEC13FDownloader.FormatOtherManagers(filed));
        }

        [TestCase("G1151C101", true)]
        [TestCase("N07059210", true)]
        [TestCase("037833100", false)]
        [TestCase("", false)]
        public void ACinsOpensWithALetter(string cusip, bool expected)
        {
            Assert.AreEqual(expected, SEC13FDownloader.IsCins(cusip));
        }

        [TestCase("03783#100")]
        [TestCase("0378-3100")]
        [TestCase("N/A")]
        public void ACusipNoIsinCanHoldResolvesToNothing(string cusip)
        {
            // Building the ISIN threw on the first such character and ended the run.
            SeedMapFiles("aapl");
            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();

            Assert.IsNull(downloader.ResolveSecurity(cusip, Filed));
        }

        [Test]
        public void TheUnitIsDecidedWithoutLookingAtAnyOtherFiling()
        {
            // The unit used to come from the median of every filing in the three month window, so a
            // filing made on its first day was corrected with filings made weeks later. Read alone,
            // the way the daily job reads a day, the same filing comes out the same.
            SeedCloses("20220930", ("aapl", 150m));
            var filing = new DateTime(2022, 10, 3);
            var quarter = new DateTime(2022, 9, 30);

            var alone = ReadInfoTable(
                new[] { Line("0000000000-00-000001", "037833100", "15000", "100", "SH") },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 111));

            Assert.AreEqual(15000m, alone.Values.Single().ReportedValue,
                "a lone filing in dollars before 2023 is not multiplied by the thousands rule");
        }

        [Test]
        public void ALineInAnotherUnitThanTheRestOfItsFilingIsCorrectedOnItsOwn()
        {
            // Some filers mix units inside one filing, so a decision per filing left Apple's June 2020
            // quarter 14 percent high: the filing is in thousands, its Apple line in dollars. Held
            // against its own close, the line is measured on its own.
            SeedCloses("20200630", ("aapl", 360m), ("msft", 200m), ("nvda", 400m));
            var filing = new DateTime(2020, 8, 14);
            var quarter = new DateTime(2020, 6, 30);

            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000099", "037833100", "36000", "100", "SH"),
                    Line("0000000000-00-000099", "594918104", "20", "100", "SH"),
                    Line("0000000000-00-000099", "67066G104", "40", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000099", filing, quarter, cik: 99));

            Assert.AreEqual(36000m, holdings.Single(pair => pair.Key.Cusip == "037833100").Value.ReportedValue,
                "the dollar line is not scaled");
            Assert.AreEqual(20000m, holdings.Single(pair => pair.Key.Cusip == "594918104").Value.ReportedValue,
                "and the rest of its filing still is");
        }

        [Test]
        public void ALineAThousandTimesTheCloseIsBroughtDown()
        {
            // Three SPY lines of the March 2023 quarter carried VALUE a thousand times the price, and 16
            // percent of the total with it. It is the VALUE that is off, not the share count: the same
            // filers reported the same number of shares the quarter before, as 4,578 of the 4,740
            // comparable lines of that quarter did.
            SeedCloses("20230331", ("spy", 409.39m));
            var filing = new DateTime(2023, 5, 15);
            var quarter = new DateTime(2023, 3, 31);

            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "78462F103", "40939", "100", "SH"),
                    Line("0000000000-00-000002", "78462F103", "40939000", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1),
                Submission("0000000000-00-000002", filing, quarter, cik: 2));

            Assert.AreEqual(2 * 40939m, holdings.Values.Single().ReportedValue);
        }

        [Test]
        public void ARunWithoutTheSecUserAgentKeysFailsBeforeAnyRequest()
        {
            // The SEC asks automated readers to identify themselves, and the reports dataset reads the
            // name and email from these two keys. Without them the run stops instead of calling out
            // anonymously.
            var previousName = Config.Get("sec-user-agent-company-name", null);
            var previousEmail = Config.Get("sec-user-agent-company-email", null);
            try
            {
                Config.Set("sec-user-agent-company-name", string.Empty);
                Config.Set("sec-user-agent-company-email", string.Empty);

                using var downloader = Downloader();
                Assert.Throws<ArgumentException>(() => downloader.Run());
            }
            finally
            {
                // As in TearDown: written back as an empty string, an absent key would stay set.
                Config.Reset();
                if (previousName != null)
                {
                    Config.Set("sec-user-agent-company-name", previousName);
                }

                if (previousEmail != null)
                {
                    Config.Set("sec-user-agent-company-email", previousEmail);
                }
            }
        }

        [Test]
        public void ALineAMillionTimesOffIsLeftToItsFiling()
        {
            // A price a thousand times below the close in a thousands filing would take a millionfold
            // factor. That is a wrong share count, not a unit, and scaling such lines put Apple's
            // December 2019 quarter ten percent above its close.
            SeedCloses("20191231", ("aapl", 290m));
            var filing = new DateTime(2020, 2, 14);
            var quarter = new DateTime(2019, 12, 31);

            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "29", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "29", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "29", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "29", "100000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1),
                Submission("0000000000-00-000002", filing, quarter, cik: 2));

            Assert.AreEqual(4 * 29000m, holdings.Values.Single().ReportedValue,
                "the odd line keeps the thousands factor its other lines prove");
        }

        [TestCase(true, 1000, 0, new double[0])]                  // nothing to compare against: the rule stands
        [TestCase(false, 1, 0, new double[0])]
        [TestCase(true, 1000, 3, new[] { -3.0, -2.9, -3.1 })]     // thousands, as the rule says
        [TestCase(true, 1, 4, new[] { 0.0, 0.1, -0.1, -3.0 })]    // whole dollars before 2023
        [TestCase(false, 1, 2, new[] { 0.1, -0.1 })]
        [TestCase(false, 1000, 3, new[] { -3.0, -2.8, 0.1 })]     // still thousands after 2023
        [TestCase(false, 1, 10, new[] { -3.0, -2.9 })]            // two of ten priced lines on a step: no evidence
        [TestCase(true, 1, 10, new[] { -3.0, -2.9 })]             // and before 2023 it is not scaled up either
        public void AFilingsUnitComesFromHowItsPricesCompareWithTheClose(bool thousandsRule,
            decimal expected, int priced, double[] offsets)
        {
            Assert.AreEqual(expected, SEC13FDownloader.ValueMultiplier(offsets.ToList(), priced, thousandsRule));
        }

        [Test]
        public void AFilingWithTheShareCountTypedAsValueIsNotScaled()
        {
            // A manager's 9 March 2026 filing carried SSHPRNAMT equal to VALUE on every line, a price of
            // exactly $1. Against a close near $1,000 that reads as thousands, so the filing was taken to
            // report in thousands and every one of its lines was multiplied: Dow's December 2025 quarter
            // went from $12.6 billion to $50.2 billion. Most of its lines sit on no unit step, which is no
            // evidence of a unit, and a line that sits on none keeps the rule of its filing date.
            SeedCloses("20251231", ("aapl", 250m), ("msft", 480m), ("nvda", 180m), ("spy", 680m));
            var filing = new DateTime(2026, 3, 9);
            var quarter = new DateTime(2025, 12, 31);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "1000000", "1000000", "SH"),
                    Line("0000000000-00-000001", "594918104", "500000", "500000", "SH"),
                    Line("0000000000-00-000001", "67066G104", "300000", "300000", "SH"),
                    Line("0000000000-00-000001", "78462F103", "200000", "200000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1964189));

            Assert.AreEqual(1000000m, holdings.Single(pair => pair.Key.Cusip == "037833100").Value.ReportedValue);
            Assert.AreEqual(300000m, holdings.Single(pair => pair.Key.Cusip == "67066G104").Value.ReportedValue);

            // MSFT at $480 and SPY at $680 put a $1 price on the thousands step by chance. In a filing
            // whose lines mostly sit on no step those landings are not evidence either.
            Assert.AreEqual(500000m, holdings.Single(pair => pair.Key.Cusip == "594918104").Value.ReportedValue);
            Assert.AreEqual(200000m, holdings.Single(pair => pair.Key.Cusip == "78462F103").Value.ReportedValue);
        }

        [Test]
        public void ADebtLineKeepsItsAmountTypeInsteadOfBeingFoldedAway()
        {
            // On a PRN line SSHPRNAMT is the principal amount and VALUE what that debt is worth.
            // The aggregated model summed the two into separate measures and lost which line each
            // came from; the line is now published as filed, with the unit beside the amount, so a
            // reader can tell a bond from a share holding.
            var holdings = ReadInfoTable(
                new[] { Line("0000000000-00-000001", "037833100", "4800000", "5000000", "PRN") },
                Submission("0000000000-00-000001", new DateTime(2024, 2, 14), new DateTime(2023, 12, 31)));

            var line = holdings.Values.Single().Lines.Single();
            Assert.AreEqual(5000000m, line.Amount);
            Assert.AreEqual("PRN", line.AmountType);
            Assert.AreEqual(4800000m, line.ReportedValue);
        }

        // ---- The incremental run has to see the published history ---------------------------------

        [Test]
        public void AnIncrementalRunWithNoPublishedHistoryFails()
        {
            // The destination arrives empty on every deployment, so a processed-data-directory that
            // is wrong or unmounted leaves an incremental run with nothing to merge into. Publishing
            // the window on its own would republish thirteen years as three months and return
            // success: the files come out the right shape, so no row count tells the two apart. The
            // guard runs before anything is fetched.
            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 8));

            Assert.Throws<InvalidOperationException>(() => downloader.Run(),
                "the run carried on with no history behind it");
        }

        [Test]
        public void ARunIntoADestinationAlreadyHoldingFilesFails()
        {
            // The job hands the destination over empty. A file an earlier run left there would be
            // published again, so a run refuses to start on top of one.
            var destination = Path.Combine(_root, "out", SEC13FHolding.ReportFolder);
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "aapl.zip"), "20240214");

            using var downloader = Downloader();

            Assert.Throws<InvalidOperationException>(() => downloader.RequireEmptyDestination());
        }

        // ---- Publication time and the EDGAR days ---------------------------------------------------

        [Test]
        public void TheRebuildHandsOverToEdgarTheDayAfterTheLastDataSet()
        {
            // The data sets come out in three month batches, so the rebuild reads them as far as they
            // reach and EDGAR after that, the way the daily job does. EDGAR can also take over
            // earlier, which is how the two sources are compared over a window both carry.
            var archives = new List<SEC13FDownloader.Archive>
            {
                new("01dec2025-28feb2026_form13f.zip", "https://localhost/a.zip", new DateTime(2025, 12, 1), new DateTime(2026, 2, 28)),
                new("01mar2026-31may2026_form13f.zip", "https://localhost/b.zip", new DateTime(2026, 3, 1), new DateTime(2026, 5, 31))
            };

            Assert.AreEqual(new[] { "01dec2025-28feb2026_form13f.zip", "01mar2026-31may2026_form13f.zip" },
                SEC13FDownloader.ArchivesBefore(archives, new DateTime(2026, 6, 1)).Select(archive => archive.Name).ToArray());
            Assert.AreEqual(new[] { "01dec2025-28feb2026_form13f.zip" },
                SEC13FDownloader.ArchivesBefore(archives, new DateTime(2026, 3, 1)).Select(archive => archive.Name).ToArray());
        }

        [Test]
        public void EdgarCannotTakeOverInTheMiddleOfADataSet()
        {
            // The window's filings before the switch would come from the data set and the rest from
            // EDGAR only if the archive were cut by date, which it is not: it would be read twice.
            var archives = new List<SEC13FDownloader.Archive>
            {
                new("01mar2026-31may2026_form13f.zip", "https://localhost/b.zip", new DateTime(2026, 3, 1), new DateTime(2026, 5, 31))
            };

            Assert.Throws<InvalidOperationException>(() => SEC13FDownloader.ArchivesBefore(archives, new DateTime(2026, 4, 15)));
        }

        [Test]
        public void ADailyRunReadsTheWeekdaysNoEarlierRunFoldedIn()
        {
            // A day already published is never read again, since its filings would be added a second
            // time under a later stamp, and a day without an index is tried again by the next run.
            // Nor is a day before EDGAR took over from the data sets: the rebuild read those from the
            // data set, so they are not in the list, and reading them from EDGAR counted them twice.
            var shelf = PublishedShelf();
            File.WriteAllText(Path.Combine(shelf, "edgar-days.txt"), "#from 20260901\n20260903\n20260904\n");

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));
            downloader.ReadEdgarState();

            Assert.AreEqual(
                new[] { "20260901", "20260902", "20260907", "20260908", "20260909" },
                downloader.EdgarDaysToRead(new DateTime(2026, 8, 30), new DateTime(2026, 9, 9))
                    .Select(day => day.ToString("yyyyMMdd")).ToArray());
        }

        [Test]
        public void AnIncrementalRunWithoutTheEdgarStateFails()
        {
            // Without the list of days already published a run cannot tell a new day from one it
            // would add a second time, so it stops instead of guessing.
            var shelf = PublishedShelf();
            File.Delete(Path.Combine(shelf, "edgar-days.txt"));
            SeedPublishedZip(shelf, "aapl", "20240215");

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));

            Assert.Throws<InvalidOperationException>(() => downloader.RequirePublishedHistoryForIncrementalRun());
        }

        [Test]
        public void AMissingDeploymentDateIsAnErrorUnlessTheRebuildIsAskedFor()
        {
            // An empty date used to mean the full history, which turned a misconfigured nightly job
            // into a five gigabyte refetch that exited zero.
            var previousDate = Environment.GetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE");
            try
            {
                Environment.SetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE", null);

                Config.Set(Program.RebuildHistoryKey, "false");
                Assert.IsFalse(SECProcessingContext.TryParseDeploymentDate(Program.RebuildHistoryKey, out _), "no date and no rebuild asked for");

                Config.Set(Program.RebuildHistoryKey, "true");
                Assert.IsTrue(SECProcessingContext.TryParseDeploymentDate(Program.RebuildHistoryKey, out var rebuild));
                Assert.IsNull(rebuild, "the rebuild runs over the whole history");

                Environment.SetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE", "20260908");
                Assert.IsTrue(SECProcessingContext.TryParseDeploymentDate(Program.RebuildHistoryKey, out var date));
                Assert.AreEqual(new DateTime(2026, 9, 8), date);
            }
            finally
            {
                Environment.SetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE", previousDate);
                Config.Set(Program.RebuildHistoryKey, "false");
            }
        }

        [Test]
        public void AnIncrementalRunAcceptsAShelfThatHoldsSecurities()
        {
            // The same guard from the other side: a published history present is not a failure. Only
            // the guard is exercised here, by handing it a deployment date and a shelf and checking
            // it does not stop the run before the first archive is fetched.
            var processed = PublishedShelf();
            SeedPublishedZip(processed, "aapl", "20240215");

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 8));

            Assert.DoesNotThrow(() => downloader.RequirePublishedHistoryForIncrementalRun());
        }

        /// <summary>
        /// A shelf directory an incremental run accepts. It used to have to carry a filer state as
        /// well, because a distinct count of managers cannot be rebuilt from published totals; with
        /// the positions published as filed there is no running count to carry, and the EDGAR days
        /// are all the run needs to tell a day already published from a new one.
        /// </summary>
        private string PublishedShelf()
        {
            var shelf = Path.Combine(_root, "processed", SEC13FHolding.ReportFolder);
            Directory.CreateDirectory(shelf);
            File.WriteAllText(Path.Combine(shelf, "edgar-days.txt"), "#from 20260601\n20260601\n");
            return shelf;
        }

        // ---- Delistings, renames and the tickers that name the files ----------------------------

        /// <summary>
        /// Points LEAN's data folder at a map file archive holding exactly the rows given per ticker,
        /// so a test can shape a listing, a rename or a delisting. See SeedMapFiles for why the data
        /// folder and lookup date are pinned.
        /// </summary>
        private void SeedMapFileRows(params (string Ticker, string[] Rows)[] files)
        {
            var dataFolder = Path.Combine(_root, "data");
            var mapFiles = Path.Combine(dataFolder, "equity", "usa", "map_files");
            Directory.CreateDirectory(mapFiles);

            var lookupDate = new DateTime(2026, 1, 2);
            using (var zip = ZipFile.Open(
                       Path.Combine(mapFiles, $"map_files_{lookupDate:yyyyMMdd}.zip"), ZipArchiveMode.Create))
            {
                foreach (var (ticker, rows) in files)
                {
                    using var entry = new StreamWriter(zip.CreateEntry($"{ticker}.csv").Open());
                    foreach (var row in rows)
                    {
                        entry.WriteLine(row);
                    }
                }
            }

            // Null when absent, so the teardown can tell a missing key from an empty one.
            if (!_configSeeded)
            {
                _previousDataFolder = Config.Get("data-folder", null);
                _previousLookupDate = Config.Get("map-file-provider-lookup-date", null);
                _configSeeded = true;
            }

            Config.Set("data-folder", dataFolder);
            Config.Set("map-file-provider-lookup-date", $"{lookupDate:yyyyMMdd}");
            Globals.Reset();
        }

        // ---- N-PORT ticker normalisation ----------------------------------------------------------

        [TestCase("GOOGL US", "GOOGL")]     // the Bloomberg style venue suffix
        [TestCase("googl", "GOOGL")]
        [TestCase("GOOGL", "GOOGL")]
        [TestCase(" brk.b ", "BRK.B")]      // share classes survive, they are the LEAN spelling
        public void ANPortTickerIsNormalisedBeforeItIsVotedOn(string raw, string expected)
        {
            // IDENTIFIER_TICKER is free text written by fund administrators, so the same security
            // arrives as "GOOGL", "GOOGL US" and "goog" across funds. It is normalised and then
            // voted on across every fund that reported the security, rather than trusted row by row.
            Assert.AreEqual(expected, SEC13FTickerCrosswalk.Normalize(raw));
        }

        [TestCase("N/A")]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("912828YV6")]             // a CUSIP typed into the ticker column
        [TestCase("NOTATICKERATALL")]       // longer than any US equity ticker
        public void ANPortTickerThatIsNotOneIsRejected(string raw)
        {
            // The crosswalk feeds a map file lookup, and a ticker the map files do not know still
            // produces a plausible looking Symbol with no data behind it, so anything that is not
            // ticker-shaped is dropped here rather than downstream.
            Assert.IsNull(SEC13FTickerCrosswalk.Normalize(raw));
        }

        [Test]
        public void ACrosswalkTickerNamesTheSecurityItNamedWhenTheFundsReportedIt()
        {
            // Facebook's CUSIP reaches the crosswalk as META, the ticker funds report today. Resolved
            // at each filing date instead, META named the Roundhill Metaverse ETF from 2021-06-30 to
            // 2022-01-28, so seven months of Facebook's holders were published as that ETF's, and
            // before it META named nothing at all.
            SeedMapFileRows(
                ("meta", new[] { "20120518,fb", "20220608,fb", "20501231,meta" }),
                ("metv", new[] { "20210630,meta", "20220128,meta", "20501231,metv" }));

            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>
            {
                ["30303M102"] = new("META", new DateTime(2026, 1, 1))
            };

            var security = downloader.ResolveThroughTicker("30303M102");

            Assert.AreEqual("FB", downloader.ResolveTicker(security, new DateTime(2021, 10, 15))?.ToUpperInvariant(),
                "Facebook's filing lands in Facebook's file, not the ETF's");
            Assert.AreEqual("FB", downloader.ResolveTicker(security, new DateTime(2016, 2, 12))?.ToUpperInvariant(),
                "and the years before the ETF existed resolve too");
            Assert.AreEqual("META", downloader.ResolveTicker(security, new DateTime(2023, 2, 14))?.ToUpperInvariant());
        }

        [Test]
        public void ACrosswalkTickerThatNamedNoUsSecurityWhenObservedResolvesToNothing()
        {
            // Barrick's CUSIP reached the crosswalk as ABX, its Toronto ticker, in data reported from
            // July 2025. No US security traded as ABX then, and the resolver still returned the
            // company that took the ticker in December, so its holders would have gone there.
            SeedMapFileRows(
                ("abx", new[] { "20200914,eres", "20230703,eres", "20251229,abl", "20501231,abx" }),
                ("b", new[] { "19980102,abx", "20181231,abx", "20250508,gold", "20501231,b" }));

            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>
            {
                ["067901108"] = new("ABX", new DateTime(2025, 7, 1))
            };

            Assert.IsNull(downloader.ResolveThroughTicker("067901108"));
        }

        // ---- security database rows repeated across lineages ----------------------------------------

        [Test]
        public void ACusipOnSeveralDatabaseRowsResolvesToTheRowCarryingTheIsinItBuilds()
        {
            // The security database repeats Alcoa's CUSIP on the old Alcoa, which trades as Howmet today and
            // carries Howmet's ISIN, and on the Alcoa spun off in 2016. LEAN's resolver takes the first row, so
            // the new Alcoa's holders went to Howmet's lineage. Both rows trade on the date; the one carrying the
            // ISIN the CUSIP builds is the security the CUSIP names.
            var oldAlcoa = SecurityIdentifier.GenerateEquity(new DateTime(1998, 1, 2), "AA", Market.USA);
            var newAlcoa = SecurityIdentifier.GenerateEquity(new DateTime(2016, 11, 1), "AA", Market.USA);
            SeedMapFileRows(
                ("hwm", new[] { "19980102,aa", "20161031,aa", "20200331,arnc", "20501231,hwm" }),
                ("aa", new[] { "20161101,aa", "20501231,aa" }));
            SeedSecurityDatabase(
                $"{oldAlcoa},01387210,,,US4432011082,4281",
                $"{newAlcoa},01387210,,,US0138721065,1675149");

            using var downloader = Downloader();

            Assert.AreEqual(newAlcoa, downloader.ResolveSecurity("013872106", new DateTime(2022, 11, 14)));
        }

        [Test]
        public void ACusipWhoseFirstDatabaseRowNoLongerTradesResolvesToTheRowThatDoes()
        {
            // TG Therapeutics' CUSIP and ISIN sit on its current listing and on the one it had as Atlantic
            // Technology Ventures, whose map file ends in 2005. The first row names a security with no ticker
            // since, so every TG holder was dropped as belonging to a ticker another security owned.
            var atlantic = SecurityIdentifier.GenerateEquity(new DateTime(1998, 1, 2), "ATLC", Market.USA);
            var tg = SecurityIdentifier.GenerateEquity(new DateTime(2012, 10, 1), "TGTX", Market.USA);
            SeedMapFileRows(
                ("mhan", new[] { "19980102,atlc", "20040630,atlc", "20051230,mhan" }),
                ("tgtx", new[] { "20121001,tgtx", "20501231,tgtx" }));
            SeedSecurityDatabase(
                $"{atlantic},88322Q10,,,US88322Q1085,",
                $"{tg},88322Q10,,,US88322Q1085,1001316");

            using var downloader = Downloader();

            Assert.AreEqual(tg, downloader.ResolveSecurity("88322Q108", new DateTime(2025, 2, 14)));
        }

        [Test]
        public void ACusipWhoseDatabaseRowsAllStoppedTradingFallsThroughToTheCrosswalk()
        {
            // A row that no longer trades used to end the search: its security was dropped later as the owner
            // of no ticker, and the N-PORT crosswalk, which knew the CUSIP, was never asked.
            var cusip = "12345678" + SEC13FDownloader.ComputeCusipCheckDigit("12345678");
            var dead = SecurityIdentifier.GenerateEquity(new DateTime(1998, 1, 2), "OLDCO", Market.USA);
            var live = SecurityIdentifier.GenerateEquity(new DateTime(2015, 3, 2), "NEWCO", Market.USA);
            SeedMapFileRows(
                ("oldco", new[] { "19980102,oldco", "20101231,oldco" }),
                ("newco", new[] { "20150302,newco", "20501231,newco" }));
            SeedSecurityDatabase($"{dead},12345678,,,,");

            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>
            {
                [cusip] = new("NEWCO", new DateTime(2025, 7, 1))
            };

            Assert.AreEqual(live, downloader.ResolveSecurity(cusip, new DateTime(2025, 11, 14)));
        }

        /// <summary>
        /// Writes the security database the downloader reads at construction into the seeded data folder,
        /// rows as the real file has them: SID, CUSIP without its check digit, FIGI, SEDOL, ISIN and CIK.
        /// </summary>
        private void SeedSecurityDatabase(params string[] rows)
        {
            var folder = Path.Combine(_root, "data", "symbol-properties");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "security-database.csv"), rows);
        }

        // ---- An option is reported under its own CUSIP ------------------------------------------

        [Test]
        public void AnOptionResolvesThroughTheSecurityItIsWrittenOn()
        {
            // A manager reporting options names them by the option's own CUSIP, which carries the
            // underlying's six character issuer and issue 90 for calls or 95 for puts. That CUSIP is
            // in no security database, so the whole line used to resolve to nothing and the position
            // was dropped: 6,083 reported option lines in the week of 3 August 2026 alone.
            var apple = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "AAPL", Market.USA);
            SeedMapFileRows(("aapl", new[] { "19801212,aapl", "20501231,aapl" }));
            SeedSecurityDatabase($"{apple},03783310,BBG000B9XRY4,2046251,US0378331005,320193");

            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();
            var filed = new DateTime(2026, 8, 7);

            Assert.AreEqual(apple, downloader.ResolveSecurity("037833100", filed), "the stock itself still resolves");
            Assert.AreEqual(apple, downloader.ResolveSecurity("037833900", filed), "calls reach Apple");
            Assert.AreEqual(apple, downloader.ResolveSecurity("037833956", filed), "puts reach Apple");
        }

        [Test]
        public void AnOptionOnAnIssuerWithSeveralStocksIsNotGuessedAt()
        {
            // iShares writes seventy equity issues under 464287 and SPDR eleven under 81369Y. An
            // option CUSIP there names one of them without saying which, and filing the position
            // under the wrong fund would be worse than not publishing it.
            SeedMapFileRows(
                ("ivv", new[] { "20000519,ivv", "20501231,ivv" }),
                ("ijh", new[] { "20000531,ijh", "20501231,ijh" }));
            SeedSecurityDatabase(
                $"{SecurityIdentifier.GenerateEquity(new DateTime(2000, 5, 19), "IVV", Market.USA)},46428710,,,US4642871010,",
                $"{SecurityIdentifier.GenerateEquity(new DateTime(2000, 5, 31), "IJH", Market.USA)},46428712,,,US4642871200,");

            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();

            Assert.IsNull(downloader.ResolveSecurity("464287902", new DateTime(2026, 8, 7)),
                "two stocks under one issuer is not an answer");
        }

        [Test]
        public void ACompanysBondNeverBecomesItsStock()
        {
            // Apple's 3.45% 2045 bond, 037833BA7, reached AAPL through a fund administrator's N-PORT
            // ticker and added a constant ten thousand shares to it: the principal amount of the
            // bond, on a line the filer had typed SH. Its prices agreed with Apple's close by
            // coincidence, a bond near par against a stock near the same number, so the price test
            // let it through. The issuer having stock of its own is what settles it instead.
            SeedCloses("20260630", ("aapl", 250m));
            SeedSecurityDatabase(
                $"{ListedSince1980("AAPL")},03783310,BBG000B9XRY4,2046251,US0378331005,320193");

            using var downloader = Downloader();
            var apple = ListedSince1980("AAPL");
            var period = new DateTime(2026, 6, 30);
            var filed = new DateTime(2026, 8, 7);

            Assert.IsTrue(downloader.IssuerHasStock("037833BA7"), "Apple's issuer has stock in the database");
            Assert.IsFalse(
                downloader.KeepsCrosswalkGroup("037833BA7", apple, period, filed, new List<double> { 250.0, 250.1 }),
                "the bond is dropped although its prices match Apple's close");
            Assert.IsTrue(
                downloader.KeepsCrosswalkGroup("037833100", apple, period, filed, new List<double> { 250.0, 250.1 }),
                "the stock itself is unaffected");
        }

        [TestCase("BRK.B", true)]
        [TestCase("AAPL", true)]
        [TestCase("UA.C ", false)]   // the map files carry a trailing space for Under Armour's class C
        [TestCase("A/B", false)]
        [TestCase("X|Y", false)]
        public void OnlyATickerLeanCanAskForBecomesAFile(string ticker, bool expected)
        {
            // A Symbol cannot hold a space or a '|', so a file named after such a ticker is one LEAN
            // never reads, and every row in it failed when the delivery archive was read back.
            Assert.AreEqual(expected, SEC13FDownloader.IsFileNameSafe(ticker));
        }

        [Test]
        public void ASecurityHasNoTickerBeforeItBeganTrading()
        {
            // Before its first row a map file answers with its first ticker, so a filing dated
            // before the listing would land under a ticker the security did not have yet.
            SeedMapFileRows(("late", new[] { "20200914,late", "20501231,late" }));

            using var downloader = Downloader();
            var security = SecurityIdentifier.GenerateEquity(new DateTime(2020, 9, 14), "LATE", Market.USA);

            Assert.IsNull(downloader.ResolveTicker(security, new DateTime(2019, 6, 3)), "not listed yet");
            Assert.AreEqual("LATE", downloader.ResolveTicker(security, new DateTime(2021, 1, 4))?.ToUpperInvariant());
        }

        [Test]
        public void TheNewestObservationOfACusipWinsWhateverOrderTheQuartersArrive()
        {
            // The quarters used to be folded newest first, so a first build kept the oldest ticker
            // of a renamed security while a refresh kept the newest.
            var older = new[] { KeyValuePair.Create("30303M102", "FB") };
            var newer = new[] { KeyValuePair.Create("30303M102", "META") };

            var olderFirst = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();
            SEC13FTickerCrosswalk.Fold(olderFirst, new DateTime(2022, 4, 1), older);
            SEC13FTickerCrosswalk.Fold(olderFirst, new DateTime(2022, 7, 1), newer);

            var newerFirst = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();
            SEC13FTickerCrosswalk.Fold(newerFirst, new DateTime(2022, 7, 1), newer);
            SEC13FTickerCrosswalk.Fold(newerFirst, new DateTime(2022, 4, 1), older);

            Assert.AreEqual("META", olderFirst["30303M102"].Ticker);
            Assert.AreEqual("META", newerFirst["30303M102"].Ticker);
        }

        // ---- Crosswalk resolutions are held against the close --------------------------------------

        [Test]
        public void ACrosswalkGroupStaysUnlessItsPricesSayItIsAnotherSecurity()
        {
            // The crosswalk is a fund administrator's ticker, so a CUSIP can reach the wrong company.
            // The group's own prices against the quarter-end close decide, on the day it is read.
            SeedCloses("20251231", ("etsy", 120m), ("defi", 129.50m), ("aapl", 250m), ("spy", 24.23m));
            using var downloader = Downloader();
            var period = new DateTime(2025, 12, 31);
            var filed = new DateTime(2026, 2, 14);
            bool Keeps(string cusip, string ticker, params double[] prices) =>
                downloader.KeepsCrosswalkGroup(cusip, ListedSince1980(ticker), period, filed, prices.ToList());

            // Etsy's convertible notes reached Etsy's stock: 171 million of its 298 million shares.
            Assert.IsFalse(Keeps("29786AAJ5", "ETSY"), "a note reported as principal carries no price");
            Assert.IsFalse(Keeps("29786AAJ5", "ETSY", 1.02, 0.98), "a note reported as shares trades near par");
            Assert.IsTrue(Keeps("46435GAA0", "SPY", 24.23, 24.20),
                "a CUSIP with letters whose issuer has no stock of its own is judged on its prices");

            // DeFi Technologies at $2 reached the $129.50 Hashdex DEFI ETF through its ticker.
            Assert.IsFalse(Keeps("244916102", "DEFI", 2.11, 2.05, 2.11), "three managers at $2 are another company");
            Assert.IsTrue(Keeps("037833100", "AAPL", 25.0), "one manager's odd price is a slip, not another company");
            Assert.IsTrue(Keeps("037833100", "AAPL", 0.25, 0.25, 250.1), "the same prices in thousands match");
            Assert.IsTrue(Keeps("037833100", "AAPL"), "a day of option lines only cannot be checked and stays");
            Assert.IsTrue(Keeps("594918104", "MSFT", 2.0, 2.0, 2.0), "nor can a security the close file does not carry");

            // Avanos on 5 February 2026: four managers in thousands and four in dollars. Their median
            // sat half way between the two units and dropped the group.
            Assert.IsTrue(Keeps("037833100", "AAPL", 0.25, 0.25, 0.25, 0.25, 250.0, 250.1, 249.9, 740.8),
                "a day that mixes the two units is still the security");
        }

        [Test]
        public void ALineThatSitsOnNoUnitStepIsNotScaled()
        {
            // A price about a hundred times below the close is neither dollars nor thousands: a bond at
            // par against its issuer's stock, or another company. Scaling it by the nearest step put
            // Seagate's December 2025 quarter at $413 billion.
            SeedCloses("20251231", ("aapl", 250m));
            var filing = new DateTime(2026, 2, 14);
            var quarter = new DateTime(2025, 12, 31);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "250000", "1000", "SH"),
                    Line("0000000000-00-000002", "037833100", "2500", "1000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1),
                Submission("0000000000-00-000002", filing, quarter, cik: 2));

            Assert.AreEqual(250000m + 2500m, holdings.Values.Single().ReportedValue,
                "the $2.50 line keeps its filing's dollar factor instead of becoming $2.5 million");
        }

        [Test]
        public void ABrokenFilingIsNeverScaledUp()
        {
            // Every line reads $1,000 a share, since SSHPRNAMT carries VALUE in thousands. Two of the
            // four prices land on a unit step by chance, not most, so the filing shows no unit. Before
            // 2023 the rule multiplied it by a thousand, and one such manager put Alphabet's September
            // 2020 quarter 9.5 percent above its close.
            SeedCloses("20200930", ("aapl", 100m), ("msft", 200m), ("nvda", 500m), ("spy", 330m));
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "1000000", "1000", "SH"),
                    Line("0000000000-00-000001", "594918104", "1000000", "1000", "SH"),
                    Line("0000000000-00-000001", "67066G104", "1000000", "1000", "SH"),
                    Line("0000000000-00-000001", "78462F103", "1000000", "1000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", new DateTime(2020, 11, 6), new DateTime(2020, 9, 30), cik: 1));

            Assert.AreEqual(4, holdings.Count);
            Assert.IsTrue(holdings.Values.All(holding => holding.ReportedValue == 1000000m),
                string.Join(", ", holdings.Values.Select(holding => holding.ReportedValue)));
        }

        [TestCase("20200814", "20200630", "20200630", "250000", "2500", 752500)] // dollars before 2023: the rule made the $2.50 line $2,500
        [TestCase("20230214", "20221231", "20221230", "250", "25", 750025)]      // thousands in 2023: the filing's unit made it 1,000 times larger
        public void ALineOnNoStepIsNeverScaledPastItsFilingOrItsDate(string filed, string quarter, string closeDay,
            string onStep, string offStep, decimal expected)
        {
            // Three lines prove the filing's unit and a fourth sits on no step. It cannot be checked, so
            // it takes the smaller of the filing's unit and the rule of the filing date: either one alone
            // inflated one era by hundreds of billions.
            SeedCloses(closeDay, ("aapl", 250m));
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", onStep, "1000", "SH"),
                    Line("0000000000-00-000001", "037833100", onStep, "1000", "SH"),
                    Line("0000000000-00-000001", "037833100", onStep, "1000", "SH"),
                    Line("0000000000-00-000001", "037833100", offStep, "1000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", Time.ParseDate(filed), Time.ParseDate(quarter), cik: 1));

            Assert.AreEqual(expected, holdings.Values.Single().ReportedValue);
        }

        [TestCase(0.0, 0)]
        [TestCase(-3.0, -1)]
        [TestCase(3.1, 1)]
        [TestCase(-2.9, -1)]
        public void AnOffsetNearAThousandfoldStepIsAUnit(double offset, int step)
        {
            Assert.AreEqual(step, SEC13FDownloader.UnitStep(offset));
        }

        [TestCase(-1.5)]    // half way between dollars and thousands
        [TestCase(-2.0)]    // a hundred times below: a bond at par against a stock
        [TestCase(1.79)]
        [TestCase(-6.0)]    // a millionfold step is a share count slip, not a unit
        public void AnOffsetOnNoStepIsNoUnit(double offset)
        {
            Assert.IsNull(SEC13FDownloader.UnitStep(offset));
        }

        [Test]
        public void TheCloseIsNeverReadAfterTheFilingDate()
        {
            // A period typed ahead of its filing date would otherwise read a price nobody had when
            // the filing was made. The quarter ends on a Tuesday; filed the Sunday before, the
            // Friday's close is the newest one there was.
            SeedCloses("20260327", ("aapl", 250m));
            var coarse = Path.Combine(_root, "data", "equity", "usa", "fundamental", "coarse");
            File.WriteAllText(Path.Combine(coarse, "20260331.csv"), $"{ListedSince1980("AAPL")},AAPL,999,1,1,True,1,1\n");
            var prices = new SEC13FClosePrices(coarse);

            Assert.AreEqual(250m, prices.Close(ListedSince1980("AAPL"), new DateTime(2026, 3, 31), new DateTime(2026, 3, 29)));
            Assert.AreEqual(999m, prices.Close(ListedSince1980("AAPL"), new DateTime(2026, 3, 31), new DateTime(2026, 4, 2)));
        }

        // ---- Reading a day from EDGAR ---------------------------------------------------------------

        [Test]
        public void TheDailyIndexYieldsTheHoldingsReportsAndTheirAmendmentsOnly()
        {
            // Notices carry no information table and the data sets' reader skips them too.
            var index = string.Join("\n",
                "Form Type   Company Name   CIK   Date Filed  File Name",
                "---------------------------------------------------------",
                "10-K             SOMETHING ELSE INC          1111111     20260814    edgar/data/1111111/0001111111-26-000001.txt",
                "13F-HR           &PARTNERS                   107136      20260814    edgar/data/107136/0001214659-26-010148.txt",
                "13F-HR/A         FUND 1 ADVISERS LLC         2222222     20260814    edgar/data/2222222/0002222222-26-000003.txt",
                "13F-NT           NOTICE FILER LP             3333333     20260814    edgar/data/3333333/0003333333-26-000004.txt");

            var entries = SEC13FEdgarDay.ParseIndex(index);

            Assert.AreEqual(new[] { "0001214659-26-010148", "0002222222-26-000003" },
                entries.Select(entry => entry.Accession).ToArray());
            Assert.AreEqual(new[] { "13F-HR", "13F-HR/A" }, entries.Select(entry => entry.FormType).ToArray());
            Assert.AreEqual(new[] { 107136, 2222222 }, entries.Select(entry => entry.Cik).ToArray());
            Assert.AreEqual(new DateTime(2026, 8, 14), entries[0].Filed);
        }

        [Test]
        public void AFilingIsReadFromItsSubmissionFile()
        {
            // The period and the confidential flag come from the primary document, the lines from the
            // information table, whatever namespace the filer's software wrote.
            var filing = SEC13FEdgarDay.ParseFiling(SampleEntry(), SampleSubmission());

            Assert.AreEqual("0001214659-26-010148", filing.Accession);
            Assert.AreEqual(new DateTime(2026, 6, 30), filing.Period);
            Assert.IsTrue(filing.ConfidentialOmitted);
            Assert.AreEqual(2, filing.Lines.Count);
            Assert.AreEqual(new[] { "88025U109", null, "274974", "7172", "SH", null, null, null, "7172", "0", "0" }, filing.Lines[0]);
            Assert.AreEqual(new[] { "037833100", "COM", "1000", "50", "SH", "Put", "DFND", "1, 2", "0", "50", null }, filing.Lines[1]);

            // The filing manager's name, not the signer's.
            Assert.AreEqual("WEALTH ADVISORS, INC.", filing.ManagerName);
            Assert.AreEqual("RESTATEMENT", filing.AmendmentType);
            Assert.AreEqual("1", filing.AmendmentNumber);
            Assert.AreEqual(new DateTime(2026, 5, 15), filing.DateReported);
        }

        [Test]
        public void ADaysArchiveIsReadByTheProcessorLikeADataSet()
        {
            // Pushed through the processor rather than read back table by table: the day's archive
            // once lacked the cover page and four columns the processor had come to require, and a
            // test listing the columns itself stayed green while every EDGAR day threw.
            SeedMapFiles("aapl");
            var day = new DateTime(2026, 8, 14);
            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                null, Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();

            ProcessEdgarDay(downloader, day);
            downloader.FinalizeSecurityFiles();

            var row = EntryLines(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "aapl.zip"), "20260815.csv").Single();
            Assert.AreEqual("20260814,0001214659-26-010148,107136,20260630,13F-HR,RESTATEMENT,1,COM,50,SH,1000,0,P,DFND,1;2,0,50,,1,20260515", row);

            // The comma the name carries comes out as one space, not the two the replacement leaves.
            Assert.AreEqual(new[] { "107136,WEALTH ADVISORS INC." },
                File.ReadAllLines(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "managers.csv")));
        }

        [Test]
        public void AnOptionCusipGivesALineTheSideItLeftEmpty()
        {
            // The option's CUSIP reaches the underlying's file, where a line without PUTCALL would
            // read as that many shares of the stock. Issue 90 is a call and 95 a put.
            var apple = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "AAPL", Market.USA);
            SeedMapFileRows(("aapl", new[] { "19801212,aapl", "20501231,aapl" }));
            SeedSecurityDatabase($"{apple},03783310,BBG000B9XRY4,2046251,US0378331005,320193");

            var day = new DateTime(2026, 8, 14);
            var filing = OptionFiling(day);
            filing.Lines.Add(["037833900", "COM", "1000", "50", "SH", null, "SOLE", null, "50", "0", "0"]);
            filing.Lines.Add(["037833956", "COM", "1000", "50", "SH", null, "SOLE", null, "50", "0", "0"]);
            filing.Lines.Add(["037833956", "COM", "1000", "50", "SH", "Call", "SOLE", null, "50", "0", "0"]);

            var rows = PublishedRows(day, filing, "aapl");

            Assert.AreEqual(new[] { "C", "P", "C" }, rows.Select(row => row.Split(',')[12]).ToArray(),
                "a side the line states is kept");
        }

        [TestCase("1000", "1000", TestName = "a whole value is published without a decimal point")]
        [TestCase("1000.50", "1000.50", TestName = "a value with cents keeps them")]
        [TestCase("99999999999999999999", "99999999999999999999", TestName = "a value past long.MaxValue is published, not thrown on")]
        public void AValueIsPublishedAsTheFilerWroteIt(string filed, string published)
        {
            // Filers type nonsense into VALUE. Cast to a long, a number past its range threw out of
            // the formatter and stopped the run over one line.
            var apple = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "AAPL", Market.USA);
            SeedMapFileRows(("aapl", new[] { "19801212,aapl", "20501231,aapl" }));
            SeedSecurityDatabase($"{apple},03783310,BBG000B9XRY4,2046251,US0378331005,320193");

            var day = new DateTime(2026, 8, 14);
            var filing = OptionFiling(day);
            filing.Lines.Add(["037833100", "COM", filed, "50", "SH", null, "SOLE", null, "50", "0", "0"]);

            Assert.AreEqual(published, PublishedRows(day, filing, "aapl").Single().Split(',')[10]);
        }

        [Test]
        public void ARowGoesUnderTheTickerInForceTheDayItIsPublished()
        {
            // LEAN opens the entry of a day in the zip of the ticker the security carries that day.
            // A filing made on Friday 14 August is published on the 15th, and the company trades as
            // NEW from Monday the 17th, so the map file already answers NEW for the 15th. Taken on
            // the filing date the row went to old.zip, where nothing ever reads that entry.
            var company = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "OLD", Market.USA);
            SeedMapFileRows(("new", new[] { "19801212,old", "20260814,old", "20501231,new" }));
            SeedSecurityDatabase($"{company},03783310,BBG000B9XRY4,2046251,US0378331005,320193");

            var day = new DateTime(2026, 8, 14);
            var filing = OptionFiling(day);
            filing.Lines.Add(["037833100", "COM", "1000", "50", "SH", null, "SOLE", null, "50", "0", "0"]);

            Assert.AreEqual(1, PublishedRows(day, filing, "new").Count);
            Assert.IsFalse(File.Exists(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "old.zip")));
        }

        [Test]
        public void AnOptionOnAFundFamilyIsResolvedByThePriceOfItsLine()
        {
            // Every fund of a family shares one option CUSIP, so the issuer does not say which fund a
            // line is written on. The line reports the underlying's value and shares, and so its price.
            var iwm = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "IWM", Market.USA);
            var eem = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "EEM", Market.USA);
            SeedMapFiles("iwm", "eem");
            SeedSecurityDatabase(
                $"{iwm},46428765,BBG000CGC9C4,2622059,US4642876555,1100663",
                $"{eem},46428723,BBG000M0P5L2,2801669,US4642872349,1100663");
            SeedCoarse("20260630", (iwm, 220m), (eem, 45m));

            var day = new DateTime(2026, 8, 14);
            var filing = OptionFiling(day);
            filing.Lines.Add(["464287905", "RUSSELL 2000 ETF", "22000", "100", "SH", "Call", "SOLE", null, "0", "0", "100"]);
            filing.Lines.Add(["464287905", "MSCI EMERG MKT", "4500", "100", "SH", null, "SOLE", null, "0", "0", "100"]);
            filing.Lines.Add(["464287905", "SOMETHING ELSE", "9900", "100", "SH", "Call", "SOLE", null, "0", "0", "100"]);

            Assert.AreEqual(1, PublishedRows(day, filing, "iwm").Count);
            var emerging = PublishedRows(day, null, "eem").Single().Split(',');
            Assert.AreEqual("MSCI EMERG MKT", emerging[7]);
            Assert.AreEqual("C", emerging[12], "the side comes from the CUSIP");
        }

        [Test]
        public void AnOptionLineNamedByItsCloseStillTakesItsFilingsUnit()
        {
            // The close names the fund, not the unit. An option line has no price of its own to hold
            // against a close, so reading the unit off the fund it matched would state two units for
            // one filing: the same manager's lines on a single-issue CUSIP never see a close. The
            // filing's unit is the one every option line takes, right or wrong, for all of them.
            var iwm = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "IWM", Market.USA);
            var eem = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "EEM", Market.USA);
            SeedMapFiles("iwm", "eem");
            SeedSecurityDatabase(
                $"{iwm},46428765,BBG000CGC9C4,2622059,US4642876555,1100663",
                $"{eem},46428723,BBG000M0P5L2,2801669,US4642872349,1100663");
            SeedCoarse("20260630", (iwm, 220m), (eem, 45m));

            var day = new DateTime(2026, 8, 14);
            var filing = OptionFiling(day);
            filing.Lines.Add(["464287905", "RUSSELL 2000 ETF", "22", "100", "SH", "Call", "SOLE", null, "0", "0", "100"]);

            var row = PublishedRows(day, filing, "iwm").Single().Split(',');
            Assert.AreEqual("22", row[10], "the manager's number is published untouched");
            Assert.AreEqual("0", row[11], "the filing is after 2023, so its rule says dollars");
        }

        [TestCase(220, 220, true)]
        [TestCase(221, 220, true)]
        [TestCase(224, 220, false)]
        [TestCase(0.221, 220, true)]
        [TestCase(200, 220, false)]
        [TestCase(45, 220, false)]
        public void AnOptionLineNamesAFundOnlyWithinHalfAPercentOfItsClose(double price, double close, bool expected)
        {
            Assert.AreEqual(expected, SEC13FDownloader.MatchesClose((decimal)price, (decimal)close));
        }

        [TestCase("20260903", "20260830", TestName = "the lookback reaches further than the last day folded in")]
        [TestCase("20260908", "20260830", TestName = "the last day folded in is inside the lookback")]
        public void AnIncrementalRunStartsAtTheLookback(string lastFolded, string expected)
        {
            var shelf = PublishedShelf();
            File.WriteAllLines(Path.Combine(shelf, "edgar-days.txt"), ["#from 20260601", lastFolded]);

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));
            downloader.ReadEdgarState();

            Assert.AreEqual(expected, downloader.FirstDayToCatchUp(new DateTime(2026, 9, 9)).ToString("yyyyMMdd"));
        }

        [Test]
        public void AManagerKeepsTheNameOfItsLatestFilingWhateverOrderTheDaysAreRead()
        {
            // A day whose index came late is read after newer ones, so the last name a run sees is
            // not the most recently filed. Taking it would roll the published name back to an older
            // one until the manager files again.
            SeedMapFiles("aapl");
            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                null, Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();

            ProcessEdgarDay(downloader, new DateTime(2026, 8, 14), NamedFiling(new DateTime(2026, 8, 14), "NEWCO ASSET MGMT"));
            ProcessEdgarDay(downloader, new DateTime(2026, 8, 10), NamedFiling(new DateTime(2026, 8, 10), "OLDCO ASSET MGMT"));
            downloader.FinalizeSecurityFiles();

            Assert.AreEqual(new[] { "7,NEWCO ASSET MGMT" },
                File.ReadAllLines(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "managers.csv")));
        }

        /// <summary>One filing of CIK 7 on a day, holding a line that resolves, under the name given.</summary>
        private static SEC13FEdgarDay.Filing NamedFiling(DateTime day, string name, int cik = 7)
        {
            var filing = new SEC13FEdgarDay.Filing
            {
                Accession = $"{cik:0000000000}-26-{day:MMdd}01", SubmissionType = "13F-HR", Cik = cik, Filed = day,
                Period = new DateTime(2026, 6, 30), ManagerName = name
            };

            filing.Lines.Add(["037833100", "COM", "1000", "50", "SH", null, "SOLE", null, "50", "0", "0"]);
            return filing;
        }

        [Test]
        public void ADayThatStagedNoRowStillPublishesTheManagersItRead()
        {
            // A quiet day can carry only filings whose every line fails to resolve. The day is
            // recorded as folded in all the same, so no later run reads its cover pages again: a
            // name not written here is lost until the manager files a line that does resolve.
            SeedMapFiles("msft");
            var day = new DateTime(2026, 8, 14);
            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                day, Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();

            ProcessEdgarDay(downloader, day, NamedFiling(day, "NEWCO ASSET MGMT"));
            downloader.FinalizeSecurityFiles();

            var destination = Path.Combine(_root, "out", SEC13FHolding.ReportFolder);
            Assert.IsEmpty(Directory.GetFiles(destination, "*.zip"), "nothing resolved");
            Assert.AreEqual(new[] { "7,NEWCO ASSET MGMT" }, File.ReadAllLines(Path.Combine(destination, "managers.csv")));
        }

        [Test]
        public void AManagerThatFiledAgainKeepsItsPublishedName()
        {
            // The run before this one folded in 09-09 and published the name the manager filed that
            // day. This run reaches back to 09-08, whose index EDGAR published late, where the same
            // manager filed under its older name. managers.csv carries no date to say the published
            // name is the newer one, so the manager's own published rows are what say it.
            var shelf = LateDayShelf(publishedName: "NEWCO ASSET MGMT", filedOn0909: 7);

            Assert.AreEqual(new[] { "7,NEWCO ASSET MGMT" }, FoldInTheLateDay(shelf, "OLDCO ASSET MGMT"));
        }

        [Test]
        public void AManagerThatDidNotFileAgainTakesTheNameOfTheLateDay()
        {
            // A manager files once a quarter, so the late day usually carries its only filing of the
            // window, and then that filing is the one that names it. Deciding from the last day the
            // run folded in rather than from this manager's own rows threw the name away for every
            // manager that had not filed since, which on a late deadline day is nearly all of them.
            var shelf = LateDayShelf(publishedName: "OLDCO ASSET MGMT", filedOn0909: 99);

            Assert.AreEqual(new[] { "7,NEWCO ASSET MGMT" }, FoldInTheLateDay(shelf, "NEWCO ASSET MGMT"));
        }

        [Test]
        public void AManagerIsNamedByItsLatestFilingWhenTwoLateDaysAreFoldedInAtOnce()
        {
            // Two indexes came late, so this run folds in 09-03 and 09-08 at once. CIK 7's published
            // rows are of 09-04, inside the window the scan covers but older than the 09-08 filing
            // this run read, so 09-08 is what names the manager. Knowing only which managers have
            // published rows in the window, without the date of each, would keep the older name here
            // and lose the rename, which is the whole reason the date is read off the rows.
            SeedMapFiles("aapl");
            var shelf = PublishedShelf();
            File.WriteAllLines(Path.Combine(shelf, "edgar-days.txt"), ["#from 20260601", "20260904", "20260909"]);
            File.WriteAllLines(Path.Combine(shelf, "managers.csv"), ["7,OLDCO ASSET MGMT"]);
            SeedPublishedZip(shelf, "aapl", 7, "20260904");

            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                new DateTime(2026, 9, 11), Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();
            downloader.ReadEdgarState();

            ProcessEdgarDay(downloader, new DateTime(2026, 9, 3),
                NamedFiling(new DateTime(2026, 9, 3), "OTHER MANAGER LP", cik: 99));
            ProcessEdgarDay(downloader, new DateTime(2026, 9, 8),
                NamedFiling(new DateTime(2026, 9, 8), "NEWCO ASSET MGMT"));
            downloader.FinalizeSecurityFiles();

            Assert.AreEqual(new[] { "7,NEWCO ASSET MGMT", "99,OTHER MANAGER LP" },
                File.ReadAllLines(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "managers.csv")));
        }

        /// <summary>
        /// A published history reaching 09-09, naming CIK 7, whose rows of 09-09 were filed by
        /// <paramref name="filedOn0909"/>. The day 09-08 is missing: EDGAR published its index late.
        /// </summary>
        private string LateDayShelf(string publishedName, int filedOn0909)
        {
            SeedMapFiles("aapl");
            var shelf = PublishedShelf();
            File.WriteAllLines(Path.Combine(shelf, "edgar-days.txt"), ["#from 20260601", "20260909"]);
            File.WriteAllLines(Path.Combine(shelf, "managers.csv"), [$"7,{publishedName}"]);
            SeedPublishedZip(shelf, "aapl", filedOn0909, "20260909");
            return shelf;
        }

        /// <summary>Folds 09-08 into that history, with CIK 7 filing under the name given.</summary>
        private string[] FoldInTheLateDay(string shelf, string name)
        {
            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                new DateTime(2026, 9, 11), Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();
            downloader.ReadEdgarState();

            ProcessEdgarDay(downloader, new DateTime(2026, 9, 8), NamedFiling(new DateTime(2026, 9, 8), name));
            downloader.FinalizeSecurityFiles();

            return File.ReadAllLines(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "managers.csv"));
        }

        [Test]
        public void AnIncrementalRunReachesBackToTheDaysAnOutageMissed()
        {
            // EDGAR blocked the runner for a fortnight, so every run in between failed. Reading only
            // the ten day lookback would step over the days in the gap and return success, and no
            // later run would ever reach them.
            var shelf = PublishedShelf();
            File.WriteAllLines(Path.Combine(shelf, "edgar-days.txt"), ["#from 20260601", "20260814"]);

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));
            downloader.ReadEdgarState();

            var days = downloader.EdgarDaysToRead(
                downloader.FirstDayToCatchUp(new DateTime(2026, 9, 9)), new DateTime(2026, 9, 9));

            Assert.AreEqual("20260817", days[0].ToString("yyyyMMdd"), "the day after the last one folded in");
            Assert.AreEqual("20260909", days[^1].ToString("yyyyMMdd"));
            Assert.IsFalse(days.Contains(new DateTime(2026, 8, 14)), "a day already folded in is not read again");
        }

        /// <summary>
        /// Holdings filings per day, counted over the June to August 2026 data set: the five weekdays
        /// leading to the 45 day deadline, the deadline itself, and a quiet day for the rest.
        /// </summary>
        private static int FilingsOn(DateTime day) => day.ToString("yyyyMMdd") switch
        {
            "20260810" => 355,
            "20260811" => 449,
            "20260812" => 461,
            "20260813" => 742,
            "20260814" => 1835,
            _ => 65
        };

        [Test]
        public void AGapWiderThanARunIsClosedWithoutOutweighingAnOrdinaryRun()
        {
            // Nineteen weekdays of outage, the 45 day deadline among them, do not fit in the run's
            // hour. Read whole they fail it, and since nothing is published until the last of them
            // is read, the next run starts over one day further behind: the gap never closes and the
            // data set never moves again. Read by weight, every run finishes and the gap shrinks.
            var shelf = PublishedShelf();
            var deployment = new DateTime(2026, 9, 9);
            var folded = new List<string> { "20260807" };
            var runs = 0;

            while (folded[^1] != "20260909")
            {
                Assert.Less(++runs, 20, "the gap has to close");
                File.WriteAllLines(Path.Combine(shelf, "edgar-days.txt"), folded.Prepend("#from 20260601"));

                using var downloader = new SEC13FDownloader(
                    Path.Combine(_root, "out"), Path.Combine(_root, "processed"), deployment);
                downloader.ReadEdgarState();

                // What ProcessEdgarDays does with what EdgarDaysToRead offers it.
                var read = 0;
                var fetched = 0;
                var first = 0;
                foreach (var day in downloader.EdgarDaysToRead(downloader.FirstDayToCatchUp(deployment), deployment))
                {
                    if (FilingsOn(day) > downloader.FilingBudget(read, fetched))
                    {
                        break;
                    }

                    first = read == 0 ? FilingsOn(day) : first;
                    fetched += FilingsOn(day);
                    read++;
                    folded.Add(day.ToString("yyyyMMdd"));
                }

                // The budget, or the first day alone where that day is heavier than the budget:
                // one exempt day and the rest inside what is left of it. A run that reached the
                // exemption twice would fetch more than this and still close the gap.
                Assert.Greater(read, 0, "a run that reads nothing never closes the gap");
                Assert.LessOrEqual(fetched, Math.Max(SEC13FDownloader.MaxFilingsPerRun, first),
                    "no run outweighs the heaviest day an ordinary run already carries");
            }
        }

        [Test]
        public void TheFirstDayOfARunIsReadWhateverItWeighs()
        {
            // A day is the unit of work: it cannot be read in half, and the heaviest of them is what
            // an ordinary run does every quarter anyway. Budgeted like any other, the deadline day
            // would never be read and the gap behind it would never close.
            using var daily = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));

            Assert.AreEqual(int.MaxValue, daily.FilingBudget(read: 0, fetched: 0));
            Assert.AreEqual(SEC13FDownloader.MaxFilingsPerRun - 1835, daily.FilingBudget(read: 1, fetched: 1835));
            Assert.Less(daily.FilingBudget(read: 2, fetched: SEC13FDownloader.MaxFilingsPerRun), 1, "nothing is left");

            using var rebuild = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), null, Path.Combine(_root, "raw"));

            Assert.AreEqual(int.MaxValue, rebuild.FilingBudget(read: 40, fetched: 500000),
                "the rebuild states the day it reaches, so it reads its whole window");
        }

        [Test]
        public void ADayThatDoesNotFitIsLeftWholeAndNothingOfItIsFetched()
        {
            // The index costs one round trip and every filing another, so the weight of a day is
            // known before the expensive part. Half a day read is worse than none: the archive is
            // written once and a later run would take it for the whole day.
            var index = string.Join('\n', Enumerable.Range(1, 40).Select(filing =>
                $"13F-HR           SOME MANAGER LP            {filing}   20260814   edgar/data/{filing}/0000000000-26-{filing:000000}.txt"));

            var fetches = new List<string>();
            var built = SEC13FEdgarDay.Build(new DateTime(2026, 8, 14), _root,
                _ => new HashSet<string> { "2026", "QTR3", "form.20260814.idx" },
                url =>
                {
                    fetches.Add(url);
                    return url.EndsWith(".idx", StringComparison.Ordinal) ? index : throw new InvalidOperationException(url);
                },
                filingBudget: 39);

            Assert.IsTrue(built.OverBudget);
            Assert.IsNull(built.Path, "nothing was written, so the next run reads the day whole");
            Assert.AreEqual(40, built.Filings, "and the caller is told what it would have cost");
            Assert.AreEqual(1, fetches.Count, "the index and not one filing");
        }

        [Test]
        public void TheFilingNamesItsOwnFilerRatherThanTheIndex()
        {
            // The index lists an accession once per CIK it names, and the first line of it can be a
            // co-filer, so the daily path would file the positions under the wrong manager and split
            // its history from what the data sets published.
            var filing = SEC13FEdgarDay.ParseFiling(
                new SECEdgarIndex.Entry("13F-HR", 50, new DateTime(2026, 8, 14), "edgar/data/50/x.txt"),
                SampleSubmission().Replace("<filerInfo>",
                    "<filerInfo><filer><credentials><cik>0000000100</cik></credentials></filer>"));

            Assert.AreEqual(100, filing.Cik);
        }

        [Test]
        public void AFilingWithNoCredentialsKeepsTheIndexCik()
        {
            Assert.AreEqual(107136, SEC13FEdgarDay.ParseFiling(SampleEntry(), SampleSubmission()).Cik);
        }

        [Test]
        public void AFilingThatCannotBeReadIsSkippedRatherThanFailingTheDay()
        {
            // Thrown, one bad filing fails the day, the day is never recorded, and every run after
            // it meets the same filing: the data set stops until someone ships code.
            var built = BuildDayOfThree(unreadable: 1);

            Assert.IsNotNull(built.Path);
            Assert.AreEqual(2, built.Filings);
        }

        [Test]
        public void ADayOfFilingsThatCannotBeReadStillThrows()
        {
            // Filing after filing unreadable is a layout change, not a bad filer, and publishing the
            // day emptied of what it held would be worse than failing.
            Assert.Throws<InvalidDataException>(() => BuildDayOfThree(unreadable: 3));
        }

        /// <summary>One EDGAR day of three filings, the first <paramref name="unreadable"/> of them broken.</summary>
        private SEC13FEdgarDay.Built BuildDayOfThree(int unreadable)
        {
            var day = new DateTime(2026, 8, 14);
            var index = string.Join('\n', Enumerable.Range(1, 3).Select(filing =>
                $"13F-HR   SOME MANAGER LP   {filing}   20260814   edgar/data/{filing}/0000000000-26-00000{filing}.txt"));

            var broken = 0;
            return SEC13FEdgarDay.Build(day, Path.Combine(_root, "day-of-three"),
                _ => new HashSet<string> { "2026", "QTR3", "form.20260814.idx" },
                url => url.EndsWith(".idx", StringComparison.Ordinal)
                    ? index
                    : ++broken <= unreadable ? "<SEC-DOCUMENT>no primary document here</SEC-DOCUMENT>" : SampleSubmission());
        }

        [Test]
        public void ANamePublishedWithItsCommaIsCleanedWhenItIsFoldedIn()
        {
            // An earlier release wrote the names with their commas, so the file on the shelf carries
            // lines of three fields. A run that folded them in untouched would republish them, and a
            // manager that does not file again would keep its broken line for good.
            SeedMapFiles("aapl");
            var shelf = PublishedShelf();
            SeedPublishedZip(shelf, "aapl", "20240215");
            File.WriteAllLines(Path.Combine(shelf, "managers.csv"), ["2230,ADAMS DIVERSIFIED EQUITY FUND, INC."]);

            var day = new DateTime(2026, 8, 14);
            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                day, Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();

            ProcessEdgarDay(downloader, day);
            downloader.FinalizeSecurityFiles();

            var published = File.ReadAllLines(
                Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "managers.csv"));

            Assert.IsTrue(published.Contains("2230,ADAMS DIVERSIFIED EQUITY FUND INC."),
                $"the folded in name reads as {published.FirstOrDefault(line => line.StartsWith("2230,"))}");
            Assert.IsEmpty(published.Where(line => line.Count(character => character == ',') != 1).ToList(),
                "a folded in name still carries a separator");
        }

        [Test]
        public void AnIncrementalRunKeepsThePublishedDatesOfASecurity()
        {
            // The destination starts empty and replaces what is published, so a run that wrote only
            // its own dates would cut the security's history down to them.
            SeedMapFiles("aapl");
            SeedPublishedZip(PublishedShelf(), "aapl", "20240215");

            var day = new DateTime(2026, 8, 14);
            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                day, Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();

            ProcessEdgarDay(downloader, day);
            downloader.FinalizeSecurityFiles();

            var destination = Path.Combine(_root, "out", SEC13FHolding.ReportFolder);
            using var zip = ZipFile.OpenRead(Path.Combine(destination, "aapl.zip"));
            Assert.AreEqual(new[] { "20240216.csv", "20260815.csv" }, zip.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray());
            Assert.IsFalse(File.Exists(Path.Combine(destination, "aapl.csv")), "the date index is gone");
        }

        [Test]
        public void AnIncrementalRunKeepsThePublishedRowsOfADateItWritesInto()
        {
            // An entry the run has rows for is not the run's to rewrite: what is published in it can
            // come from another run of the same day, the redundancy job's or one that caught up a
            // late index. Replacing the entry with this run's rows drops every other manager's
            // positions of that day, and the run reports success.
            SeedMapFiles("aapl");
            var day = new DateTime(2026, 8, 13);
            SeedPublishedEntry(PublishedShelf(), "aapl", "20260814",
                "0000000001-26-000001", "0000000002-26-000002");

            PublishEdgarDay(day, NamedFiling(day, "NEWCO ASSET MGMT"));

            var accessions = PublishedEntryLines("aapl", "20260814.csv").Select(line => line.Split(',')[1]).OrderBy(x => x).ToArray();
            Assert.AreEqual(
                new[] { "0000000001-26-000001", "0000000002-26-000002", "0000000007-26-081301" }, accessions,
                "the two published rows and the new one");
        }

        [Test]
        public void AFilingReadTwiceIsNotPublishedTwice()
        {
            // The same filing read again replaces its own lines rather than adding a second copy, so
            // a day re-read after a publish that failed half way is still idempotent.
            SeedMapFiles("aapl");
            var day = new DateTime(2026, 8, 13);
            SeedPublishedEntry(PublishedShelf(), "aapl", "20260814",
                "0000000001-26-000001", "0000000007-26-081301");

            PublishEdgarDay(day, NamedFiling(day, "NEWCO ASSET MGMT"));

            var accessions = PublishedEntryLines("aapl", "20260814.csv").Select(line => line.Split(',')[1]).ToArray();
            Assert.AreEqual(2, accessions.Length);
            Assert.AreEqual(1, accessions.Count(accession => accession == "0000000007-26-081301"), "one copy");
        }

        [Test]
        public void ADailyArchiveStampsItsRowsWithTheDayItWasRead()
        {
            // An EDGAR daily index can list a filing whose own date is an earlier day: one 13F-HR in
            // the 2026 Q2 and Q3 indexes, 2026-04-27 listed on 04-28. The row carries the day the index
            // named it and is published the day after, when the job could have it, so nothing is
            // visible in a backtest before it was public. A later rebuild reads that accession from
            // the data sets under its own filing date, so the row moves by a day.
            SeedMapFiles("aapl");
            var day = new DateTime(2026, 4, 28);

            PublishEdgarDay(day, NamedFiling(new DateTime(2026, 4, 27), "BACKDATED ASSET MGMT"));

            using var zip = ZipFile.OpenRead(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "aapl.zip"));
            Assert.AreEqual(new[] { "20260429.csv" }, zip.Entries.Select(entry => entry.Name).ToArray());
            Assert.AreEqual("20260428", PublishedEntryLines("aapl", "20260429.csv").Single().Split(',')[0]);
        }

        [Test]
        public void ARunPublishesEveryDayItReadsTheDayAfterItsDeploymentDate()
        {
            // Live asks for the entry of today and never again for a past one. A run that catches up
            // a day missed by an earlier one publishes it with its own day, so live still reads it and
            // a backtest does not see it before the job had it. Each row keeps its filing date.
            SeedMapFiles("aapl");
            var missed = new DateTime(2026, 8, 13);
            var day = new DateTime(2026, 8, 14);
            using (var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                       day, Path.Combine(_root, "raw")))
            {
                downloader.TickerCrosswalk = UnitTestCrosswalk();
                ProcessEdgarDay(downloader, missed, NamedFiling(missed, "MISSED ASSET MGMT", cik: 8));
                ProcessEdgarDay(downloader, day, NamedFiling(day, "NEWCO ASSET MGMT"));
                downloader.FinalizeSecurityFiles();
            }

            using var zip = ZipFile.OpenRead(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, "aapl.zip"));
            Assert.AreEqual(new[] { "20260815.csv" }, zip.Entries.Select(entry => entry.Name).ToArray());
            Assert.AreEqual(new[] { "20260813", "20260814" },
                PublishedEntryLines("aapl", "20260815.csv").Select(line => line.Split(',')[0]).OrderBy(x => x).ToArray());
        }

        /// <summary>An incremental run over one EDGAR day, published into the destination.</summary>
        private void PublishEdgarDay(DateTime day, SEC13FEdgarDay.Filing filing)
        {
            using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                day, Path.Combine(_root, "raw"));
            downloader.TickerCrosswalk = UnitTestCrosswalk();

            ProcessEdgarDay(downloader, day, filing);
            downloader.FinalizeSecurityFiles();
        }

        /// <summary>The lines of one published entry of a ticker's zip in the destination.</summary>
        private string[] PublishedEntryLines(string ticker, string entry)
        {
            using var zip = ZipFile.OpenRead(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, $"{ticker}.zip"));
            using var reader = new StreamReader(zip.GetEntry(entry).Open());
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>A published zip whose entry for one date holds a row of each accession given.</summary>
        private static void SeedPublishedEntry(string shelf, string ticker, string date, params string[] accessions)
        {
            using var zip = ZipFile.Open(Path.Combine(shelf, $"{ticker}.zip"), ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry($"{date}.csv").Open()) { NewLine = "\n" };
            foreach (var accession in accessions)
            {
                writer.WriteLine($"{date},{accession},1,20260630,13F-HR,,,COM,1,SH,1,0,,SOLE,,1,0,0,0,");
            }
        }

        [Test]
        public void AnEdgarDayIsPublishedOnlyWhenItsQuarterListsItsIndex()
        {
            // EDGAR answers 403 both for an index that does not exist and for a reader it has blocked,
            // so whether a day is out comes from its listings. A folder EDGAR has not created yet
            // answers 403 too, so it is looked up in its parent first: requesting any folder missing
            // from this map throws, and fails the test.
            const string root = "https://www.sec.gov/Archives/edgar/daily-index/";
            var listings = new Dictionary<string, ISet<string>>
            {
                [root] = new HashSet<string> { "2025", "2026" },
                [root + "2026/"] = new HashSet<string> { "QTR1", "QTR2", "QTR3" },
                [root + "2026/QTR3/"] = new HashSet<string> { "form.20260904.idx", "form.20260908.idx" }
            };

            Assert.IsTrue(SECEdgarIndex.IsIndexPublished(new DateTime(2026, 9, 8), url => listings[url]));
            Assert.IsFalse(SECEdgarIndex.IsIndexPublished(new DateTime(2026, 9, 7), url => listings[url]), "Labor Day");
            Assert.IsFalse(SECEdgarIndex.IsIndexPublished(new DateTime(2026, 10, 1), url => listings[url]), "a new quarter");
            Assert.IsFalse(SECEdgarIndex.IsIndexPublished(new DateTime(2027, 1, 4), url => listings[url]), "a new year");
        }

        [Test]
        public void AnEdgarBlockFailsTheDayInsteadOfSkippingIt()
        {
            // Taken for "no index", a blocked day was never recorded and fell out of reach of the
            // daily run's lookback, so the rebuild lost it for good.
            HttpRequestException Blocked() => new("Forbidden", null, HttpStatusCode.Forbidden);

            Assert.Throws<HttpRequestException>(() => SEC13FEdgarDay.Build(new DateTime(2026, 9, 8), _root,
                _ => throw Blocked(), _ => throw Blocked()));
        }

        [TestCase(HttpStatusCode.NotFound, true)]
        [TestCase(HttpStatusCode.ServiceUnavailable, true)]
        [TestCase(HttpStatusCode.TooManyRequests, true)]
        [TestCase(HttpStatusCode.Forbidden, false)]
        public void OnlyABlockIsNotAskedAgain(HttpStatusCode status, bool retried)
        {
            // Every file requested is one the SEC lists, so a 404 is a hiccup worth asking again: a
            // filing in the 24 July 2026 index answered 404 during a rebuild and 200 afterwards.
            Assert.AreEqual(retried, SECEdgarClient.IsWorthRetrying(new HttpRequestException("", null, status)));
        }

        [Test]
        public void AnEdgarListingYieldsItsNames()
        {
            const string listing = @"{""directory"":{""item"":[{""last-modified"":""09\/08\/2026 10:02:29 PM""," +
                @"""name"":""form.20260908.idx"",""type"":""file"",""href"":""form.20260908.idx"",""size"":""782 KB""}]," +
                @"""name"":""daily-index\/2026\/QTR3\/"",""parent-dir"":""..\/""}}";

            Assert.AreEqual(new[] { "form.20260908.idx" }, SECEdgarIndex.ListingNames(listing).ToArray());
            Assert.Throws<InvalidDataException>(() => SECEdgarIndex.ListingNames("{}"));
        }

        private static SECEdgarIndex.Entry SampleEntry()
        {
            return new SECEdgarIndex.Entry("13F-HR", 107136, new DateTime(2026, 8, 14),
                "edgar/data/107136/0001214659-26-010148.txt");
        }

        /// <summary>A full submission file cut down to the two documents the reader uses.</summary>
        private static string SampleSubmission()
        {
            return string.Join("\n",
                "<SEC-DOCUMENT>0001214659-26-010148.txt : 20260814",
                "<DOCUMENT>",
                "<TYPE>13F-HR",
                "<TEXT>",
                "<XML>",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<edgarSubmission xmlns=\"http://www.sec.gov/edgar/thirteenffiler\"><headerData>" +
                "<submissionType>13F-HR/A</submissionType><filerInfo><periodOfReport>06-30-2026</periodOfReport>" +
                "</filerInfo></headerData><formData><coverPage><isAmendment>true</isAmendment><amendmentNo>1</amendmentNo>" +
                "<amendmentInfo><amendmentType>RESTATEMENT</amendmentType><dateReported>05-15-2026</dateReported></amendmentInfo>" +
                "<filingManager><name>WEALTH ADVISORS, INC.</name></filingManager></coverPage>" +
                "<signatureBlock><name>Someone Else</name></signatureBlock><summaryPage><isConfidentialOmitted>true</isConfidentialOmitted>" +
                "</summaryPage></formData></edgarSubmission>",
                "</XML>",
                "</TEXT>",
                "</DOCUMENT>",
                "<DOCUMENT>",
                "<TYPE>INFORMATION TABLE",
                "<TEXT>",
                "<XML>",
                "<ns1:informationTable xmlns:ns1=\"http://www.sec.gov/edgar/document/thirteenf/informationtable\">" +
                "<ns1:infoTable><ns1:cusip>88025U109</ns1:cusip><ns1:value>274974</ns1:value><ns1:shrsOrPrnAmt>" +
                "<ns1:sshPrnamt>7172</ns1:sshPrnamt><ns1:sshPrnamtType>SH</ns1:sshPrnamtType></ns1:shrsOrPrnAmt>" +
                "<ns1:votingAuthority><ns1:Sole>7172</ns1:Sole><ns1:Shared>0</ns1:Shared><ns1:None>0</ns1:None>" +
                "</ns1:votingAuthority></ns1:infoTable>" +
                "<ns1:infoTable><ns1:titleOfClass>COM</ns1:titleOfClass><ns1:cusip>037833100</ns1:cusip><ns1:value>1000</ns1:value><ns1:shrsOrPrnAmt>" +
                "<ns1:sshPrnamt>50</ns1:sshPrnamt><ns1:sshPrnamtType>SH</ns1:sshPrnamtType></ns1:shrsOrPrnAmt>" +
                "<ns1:putCall>Put</ns1:putCall><ns1:investmentDiscretion>DFND</ns1:investmentDiscretion>" +
                "<ns1:otherManager>1, 2</ns1:otherManager><ns1:votingAuthority><ns1:Sole>0</ns1:Sole><ns1:Shared>50</ns1:Shared>" +
                "</ns1:votingAuthority></ns1:infoTable></ns1:informationTable>",
                "</XML>",
                "</TEXT>",
                "</DOCUMENT>",
                "</SEC-DOCUMENT>");
        }

        // ---- Fixtures -----------------------------------------------------------------------------


        /// <summary>One INFOTABLE.tsv line, in the column order the header below declares.</summary>
        private static string Line(string accession, string cusip, string value, string amount, string shareType,
            string putCall = "", string votingSole = "0", string votingShared = "0",
            string titleOfClass = "COM", string discretion = "SOLE", string otherManager = "",
            string votingNone = "0")
        {
            return string.Join('\t', accession, cusip, titleOfClass, value, amount, shareType, putCall,
                discretion, otherManager, votingSole, votingShared, votingNone);
        }

        /// <summary>One SUBMISSION row, keyed by its accession number the way the processor keys it.</summary>
        private static KeyValuePair<string, SEC13FDownloader.Submission> Submission(string accession,
            DateTime filingDate, DateTime period, int cik = 1)
        {
            return new KeyValuePair<string, SEC13FDownloader.Submission>(accession,
                new SEC13FDownloader.Submission
                {
                    Cik = cik,
                    FilingDate = filingDate,
                    Period = period
                });
        }

        /// <summary>
        /// Runs the real INFOTABLE reader over an in-memory archive. The unit break and the amendment
        /// rule live inside that loop, so a test that rebuilt them here would only be asserting
        /// itself; this drives the shipped code over the table shape the SEC publishes.
        /// </summary>
        private Dictionary<SEC13FDownloader.HoldingKey, SEC13FDownloader.Holding> ReadInfoTable(
            string[] lines, params KeyValuePair<string, SEC13FDownloader.Submission>[] submissions)
        {
            return ReadInfoTable(lines, false, submissions);
        }

        /// <summary>
        /// The same reader, with the four CUSIPs the unit tests use resolvable through the crosswalk
        /// when <paramref name="resolve"/> is set, so their lines are held against the closes
        /// SeedCloses wrote. Unset, no CUSIP resolves and every line falls back to its filing's rule.
        /// </summary>
        private Dictionary<SEC13FDownloader.HoldingKey, SEC13FDownloader.Holding> ReadInfoTable(
            string[] lines, bool resolve, params KeyValuePair<string, SEC13FDownloader.Submission>[] submissions)
        {
            var text = new StringBuilder()
                .AppendLine(string.Join('\t', "ACCESSION_NUMBER", "CUSIP", "TITLEOFCLASS", "VALUE",
                    "SSHPRNAMT", "SSHPRNAMTTYPE", "PUTCALL", "INVESTMENTDISCRETION", "OTHERMANAGER",
                    "VOTING_AUTH_SOLE", "VOTING_AUTH_SHARED", "VOTING_AUTH_NONE"));

            foreach (var line in lines)
            {
                text.AppendLine(line);
            }

            using var buffer = new MemoryStream();
            using (var writing = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            using (var entry = new StreamWriter(writing.CreateEntry("INFOTABLE.tsv").Open()))
            {
                entry.Write(text.ToString());
            }

            buffer.Position = 0;
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            using var downloader = Downloader();
            downloader.TickerCrosswalk = resolve
                ? UnitTestCrosswalk()
                : new Dictionary<string, SEC13FTickerCrosswalk.Entry>();

            return downloader.ReadInfoTable(
                archive,
                new SEC13FDownloader.Archive("2024q1_form13f.zip", "https://localhost/2024q1_form13f.zip",
                    new DateTime(2024, 1, 1), new DateTime(2024, 3, 31)),
                submissions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }

        /// <summary>The CUSIPs the unit tests name, to the tickers SeedCloses lists.</summary>
        private static Dictionary<string, SEC13FTickerCrosswalk.Entry> UnitTestCrosswalk()
        {
            var observed = new DateTime(2026, 1, 1);
            return new Dictionary<string, SEC13FTickerCrosswalk.Entry>
            {
                ["037833100"] = new("AAPL", observed),
                ["594918104"] = new("MSFT", observed),
                ["67066G104"] = new("NVDA", observed),
                ["78462F103"] = new("SPY", observed)
            };
        }

        /// <summary>
        /// Writes one trading day's coarse file with these closes, and map files listing the tickers
        /// over every date used, so a crosswalk CUSIP resolves to the identifier the file is keyed by.
        /// </summary>
        private void SeedCloses(string day, params (string Ticker, decimal Close)[] closes)
        {
            SeedMapFiles("aapl", "msft", "nvda", "spy", "defi", "etsy");

            var coarse = Path.Combine(_root, "data", "equity", "usa", "fundamental", "coarse");
            Directory.CreateDirectory(coarse);
            File.WriteAllLines(Path.Combine(coarse, $"{day}.csv"), closes.Select(pair =>
                $"{ListedSince1980(pair.Ticker)},{pair.Ticker.ToUpperInvariant()}," +
                $"{pair.Close.ToString(System.Globalization.CultureInfo.InvariantCulture)},100,1000,True,1,1"));
        }

        /// <summary>The identifier SeedMapFiles gives a ticker: listed on its first row, 1980-12-12.</summary>
        private static SecurityIdentifier ListedSince1980(string ticker)
        {
            return SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), ticker.ToUpperInvariant(), Market.USA);
        }


        private static SEC13FEdgarDay.Filing OptionFiling(DateTime day)
        {
            return new SEC13FEdgarDay.Filing
            {
                Accession = "0000000000-26-000001", SubmissionType = "13F-HR", Cik = 1, Filed = day,
                Period = new DateTime(2026, 6, 30), ManagerName = "A MANAGER"
            };
        }

        /// <summary>Runs a filing through the processor, when given one, and reads a ticker's rows of the day.</summary>
        private List<string> PublishedRows(DateTime day, SEC13FEdgarDay.Filing filing, string ticker)
        {
            if (filing != null)
            {
                using var downloader = new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"),
                    null, Path.Combine(_root, "raw"));
                downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();

                ProcessEdgarDay(downloader, day, filing);
                downloader.FinalizeSecurityFiles();
            }

            // A rebuild publishes a day's filings under the next day, when a daily job would have them.
            return EntryLines(Path.Combine(_root, "out", SEC13FHolding.ReportFolder, $"{ticker}.zip"), $"{day.AddDays(1):yyyyMMdd}.csv");
        }

        /// <summary>One trading day's coarse file with these closes, keyed by security.</summary>
        private void SeedCoarse(string day, params (SecurityIdentifier Security, decimal Close)[] closes)
        {
            var coarse = Path.Combine(_root, "data", "equity", "usa", "fundamental", "coarse");
            Directory.CreateDirectory(coarse);
            File.WriteAllLines(Path.Combine(coarse, $"{day}.csv"), closes.Select(pair =>
                $"{pair.Security},{pair.Security.Symbol},{pair.Close.ToString(System.Globalization.CultureInfo.InvariantCulture)},100,1000,True,1,1"));
        }

        /// <summary>Writes the sample filing as the EDGAR archive of a day and folds it in, as a run does.</summary>
        private void ProcessEdgarDay(SEC13FDownloader downloader, DateTime day, SEC13FEdgarDay.Filing filing = null)
        {
            var cache = Path.Combine(_root, "raw", SEC13FHolding.ReportFolder, "archives");
            Directory.CreateDirectory(cache);
            using (var stream = File.Create(Path.Combine(cache, SEC13FEdgarDay.ArchiveName(day))))
            {
                SEC13FEdgarDay.WriteArchive(stream, [filing ?? SEC13FEdgarDay.ParseFiling(SampleEntry(), SampleSubmission())]);
            }

            downloader.ProcessArchive(new SEC13FDownloader.Archive(
                SEC13FEdgarDay.ArchiveName(day), "https://localhost/day", day, day, IsDaily: true));
            downloader.FlushPendingRows();
        }

        /// <summary>A published security zip holding one entry per date given.</summary>
        private static void SeedPublishedZip(string shelf, string ticker, params string[] dates)
        {
            SeedPublishedZip(shelf, ticker, 1, dates);
        }

        /// <summary>
        /// A published security zip whose every row was filed by one manager, one on each filing date
        /// given, each published under the next day as the job writes it.
        /// </summary>
        private static void SeedPublishedZip(string shelf, string ticker, int cik, params string[] dates)
        {
            using var zip = ZipFile.Open(Path.Combine(shelf, $"{ticker}.zip"), ZipArchiveMode.Create);
            foreach (var date in dates)
            {
                var published = DateTime.ParseExact(date, "yyyyMMdd", null).AddDays(1);
                using var writer = new StreamWriter(zip.CreateEntry($"{published:yyyyMMdd}.csv").Open());
                writer.Write($"{date},0000000000-24-000001,{cik},20231231,13F-HR,,,COM,1,SH,1,0,,SOLE,,1,0,0,0,\n");
            }
        }

        private static List<string> EntryLines(string zipPath, string entry)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            using var reader = new StreamReader(zip.GetEntry(entry).Open());
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>
        /// Points LEAN's data folder at a map file archive this test wrote, holding one row per
        /// ticker that spans every date used here.
        ///
        /// Publication turns each ticker into a SecurityIdentifier point in time, and
        /// LocalZipMapFileProvider throws outright when it finds no archive at all, so without this
        /// the test could only be skipped on a machine with no LEAN data checkout, which is most of
        /// them. The lookup date is pinned rather than left to walk back from yesterday, so the
        /// fixture does not expire.
        /// </summary>
        private void SeedMapFiles(params string[] tickers)
        {
            SeedMapFileRows(tickers
                .Select(ticker => (ticker, new[] { $"19801212,{ticker}", $"20501231,{ticker}" }))
                .ToArray());
        }

        /// <summary>A downloader writing into this test's own temporary directories.</summary>
        private SEC13FDownloader Downloader()
        {
            return new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"), null);
        }
    }
}
