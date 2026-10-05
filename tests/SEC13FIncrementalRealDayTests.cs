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
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.DataProcessing;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Runs one real day of EDGAR filings as an incremental run, on top of a real published history.
    ///
    /// The two bugs the review found were exactly here and neither unit test caught them: the day's
    /// archive lacked tables the processor reads, so every daily run threw, and an incremental run
    /// published each touched ticker's zip holding that day alone, cutting the security's history
    /// down to it. Both are covered by unit tests now, on archives this repository writes itself.
    /// This one uses the archive EDGAR really served and a shelf the processor really produced, so
    /// the day is read as the job reads it.
    ///
    /// Explicit: SEC13F_DAY_RAW is the raw folder holding archives/edgar-yyyyMMdd_form13f.zip,
    /// SEC13F_DAY_PUBLISHED a published dataset folder, SEC13F_DAY_DATA a LEAN data folder and
    /// SEC13F_DAY the day to read.
    /// </summary>
    [TestFixture, Explicit("Needs a cached EDGAR day and a published history on disk")]
    public class SEC13FIncrementalRealDayTests
    {
        private string _root;
        private string _previousDataFolder;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), $"sec-13f-day-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _previousDataFolder = Config.Get("data-folder", null);
        }

        [TearDown]
        public void TearDown()
        {
            Config.Reset();
            if (_previousDataFolder != null)
            {
                Config.Set("data-folder", _previousDataFolder);
            }

            Globals.Reset();
            Directory.Delete(_root, true);
        }

        [Test]
        public void ARealEdgarDayIsAddedToARealPublishedHistory()
        {
            var raw = Environment.GetEnvironmentVariable("SEC13F_DAY_RAW");
            var published = Environment.GetEnvironmentVariable("SEC13F_DAY_PUBLISHED");
            var data = Environment.GetEnvironmentVariable("SEC13F_DAY_DATA");
            Assert.IsTrue(Directory.Exists(raw), "set SEC13F_DAY_RAW to the raw folder of the cached archives");
            Assert.IsTrue(Directory.Exists(published), "set SEC13F_DAY_PUBLISHED to a published dataset folder");
            Assert.IsTrue(Directory.Exists(data), "set SEC13F_DAY_DATA to a LEAN data folder");

            var day = DateTime.ParseExact(Environment.GetEnvironmentVariable("SEC13F_DAY") ?? "",
                "yyyyMMdd", CultureInfo.InvariantCulture);

            Config.Set("data-folder", data);
            Globals.Reset();

            // A day the raw folder does not already hold is fetched, which is what makes this a test
            // of the archive the processor writes rather than of one already on disk. The SEC's user
            // agent then has to be configured, as it does for a real run.

            // The shelf the job hands over, holding what is published today. Copied rather than read
            // in place, since a run that wrote into it would be rewriting the live dataset.
            var shelf = Path.Combine(_root, "processed", "alternative", "sec");
            var shelfDataset = Path.Combine(shelf, SEC13FHolding.ReportFolder);
            Directory.CreateDirectory(shelfDataset);
            foreach (var file in Directory.GetFiles(published))
            {
                File.Copy(file, Path.Combine(shelfDataset, Path.GetFileName(file)));
            }

            var before = Dates(Path.Combine(shelfDataset, "aapl.zip"));
            Assert.IsNotEmpty(before, "the shelf carries no AAPL history to add to");

            var destination = Path.Combine(_root, "out", "alternative", "sec");
            var archive = new SEC13FDownloader.Archive(
                SEC13FEdgarDay.ArchiveName(day), $"https://localhost/{SEC13FEdgarDay.ArchiveName(day)}",
                day, day, IsDaily: true);

            using (var downloader = new SEC13FDownloader(destination, shelf, day, raw))
            {
                downloader.ProcessArchive(archive);
                downloader.FlushPendingRows();
                downloader.FinalizeSecurityFiles();
            }

            var written = Path.Combine(destination, SEC13FHolding.ReportFolder);
            var touched = Directory.GetFiles(written, "*.zip");
            TestContext.Out.WriteLine($"{touched.Length} securities touched by {day:yyyy-MM-dd}");

            Assert.IsNotEmpty(touched, "the day published nothing at all, which is the bug that threw");

            // Every zip this run wrote must still hold what was published for that security, or the
            // run has just cut its history down to one day.
            // The run publishes the day under the next one, when the job has it.
            var entry = $"{day.AddDays(1):yyyyMMdd}.csv";
            var lost = 0;
            var gained = 0;
            foreach (var path in touched)
            {
                var ticker = Path.GetFileNameWithoutExtension(path);
                var now = Dates(path);
                var was = Dates(Path.Combine(shelfDataset, $"{ticker}.zip"));

                if (was.Except(now).Any())
                {
                    lost++;
                }

                if (now.Contains(entry))
                {
                    gained++;
                }
            }

            TestContext.Out.WriteLine($"{gained} of them carry {entry}, {lost} lost a published date");
            Assert.AreEqual(touched.Length, gained, $"a touched security does not carry {entry}");
            Assert.AreEqual(0, lost, "a security lost dates it had published");

            var after = Dates(Path.Combine(written, "aapl.zip"));
            TestContext.Out.WriteLine($"aapl: {before.Count} dates published, {after.Count} after the day");
            Assert.IsEmpty(before.Except(after).ToList(), "AAPL lost published dates");
            Assert.IsTrue(after.Contains(entry), "AAPL did not gain the day");
        }

        /// <summary>The entry names a security's zip holds, or an empty set when there is no zip.</summary>
        private static HashSet<string> Dates(string path)
        {
            if (!File.Exists(path))
            {
                return new HashSet<string>();
            }

            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Select(entry => entry.Name).ToHashSet();
        }
    }
}
