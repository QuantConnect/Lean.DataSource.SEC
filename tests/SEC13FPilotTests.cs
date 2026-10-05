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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using QuantConnect.Configuration;
using QuantConnect.DataProcessing;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Runs the processor over one window of real SEC tables and reports what it wrote: how many
    /// files, how large, how long, and the worst day. This is the pilot the redesign is measured on
    /// before any history is generated, so it prints its numbers rather than only asserting on them.
    ///
    /// Explicit: it needs the SEC's own tables in the folder SEC13F_PILOT_RAW names, which is a
    /// directory holding SUBMISSION.tsv, COVERPAGE.tsv, SUMMARYPAGE.tsv and INFOTABLE.tsv, and a
    /// LEAN data folder with map files and security-database.csv in SEC13F_PILOT_DATA.
    /// </summary>
    [TestFixture, Explicit("Needs a window of real SEC tables on disk")]
    public class SEC13FPilotTests
    {
        private string _root;
        private string _previousDataFolder;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), $"sec-13f-pilot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _previousDataFolder = Config.Get("data-folder", null);
        }

        [TearDown]
        public void TearDown()
        {
            // The data folder is process wide and the resolver caches the one it was built with.
            Config.Reset();
            if (_previousDataFolder != null)
            {
                Config.Set("data-folder", _previousDataFolder);
            }

            Globals.Reset();
            Directory.Delete(_root, true);
        }

        [Test]
        public void OneWindowIsPublishedAndMeasured()
        {
            var raw = Environment.GetEnvironmentVariable("SEC13F_PILOT_RAW");
            Assert.IsTrue(Directory.Exists(raw), "set SEC13F_PILOT_RAW to a folder of SEC 13F tables");

            var data = Environment.GetEnvironmentVariable("SEC13F_PILOT_DATA");
            Assert.IsTrue(Directory.Exists(data), "set SEC13F_PILOT_DATA to a LEAN data folder");

            Config.Set("data-folder", data);
            Globals.Reset();

            var window = WindowOf(raw);
            var name = $"{window.Start:ddMMMyyyy}-{window.End:ddMMMyyyy}_form13f.zip".ToLowerInvariant();
            var archive = new SEC13FDownloader.Archive(
                name, $"https://localhost/{name}", window.Start, window.End);

            // The archive is handed over on disk in the cache the downloader reads, so the run needs
            // nothing from the network. The path is the one the job hands it, which is the raw
            // folder already narrowed to the vendor.
            var rawRoot = Path.Combine(_root, "raw", "alternative", SEC13FDownloader.VendorName);
            var archives = Path.Combine(rawRoot, SEC13FHolding.ReportFolder, "archives");
            Directory.CreateDirectory(archives);
            BuildArchive(raw, Path.Combine(archives, name));

            // The same two paths the job hands over: the temporary output and the published shelf,
            // each already narrowed to the vendor. The downloader adds the dataset folder itself.
            var destination = Path.Combine(_root, "out", "alternative", SEC13FDownloader.VendorName);
            var processed = Path.Combine(_root, "processed", "alternative", SEC13FDownloader.VendorName);
            Directory.CreateDirectory(processed);

            var clock = Stopwatch.StartNew();
            using (var downloader = new SEC13FDownloader(destination, processed, null, rawRoot))
            {
                // The N-PORT crosswalk is built from four quarterly archives of about 450 MB each.
                // SEC13F_PILOT_CROSSWALK points at one the real run already built, so the pilot uses
                // the same map without downloading 1.8 GB or asking EDGAR which quarters exist.
                // Without it the run still publishes, with the coverage that the security database
                // alone gives, and the difference is reported either way.
                downloader.TickerCrosswalk = ReadCrosswalk(
                    Environment.GetEnvironmentVariable("SEC13F_PILOT_CROSSWALK"));

                downloader.ProcessArchive(archive);
                downloader.FlushPendingRows();
                downloader.FinalizeSecurityFiles();
            }

            clock.Stop();

            var folder = Path.Combine(destination, SEC13FHolding.ReportFolder);
            Assert.IsTrue(Directory.Exists(folder), $"nothing was written to {folder}");

            Report(folder, clock.Elapsed);

            // Kept for the independent check that reads it back against the raw tables.
            var keep = Environment.GetEnvironmentVariable("SEC13F_PILOT_OUT");
            if (!string.IsNullOrWhiteSpace(keep))
            {
                CopyTree(folder, keep);
                TestContext.Out.WriteLine($"output kept in        {keep}");
            }
        }

        /// <summary>
        /// Reads a crosswalk the processor cached earlier: a header of the quarters it was built
        /// from, then one CUSIP, ticker and observation date per line. An absent path gives an empty
        /// map, which is a run on the security database alone.
        /// </summary>
        private static Dictionary<string, SEC13FTickerCrosswalk.Entry> ReadCrosswalk(string path)
        {
            var map = new Dictionary<string, SEC13FTickerCrosswalk.Entry>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                TestContext.Out.WriteLine("crosswalk             none, the security database alone");
                return map;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    TestContext.Out.WriteLine($"crosswalk quarters    {line.TrimStart('#')}");
                    continue;
                }

                var fields = line.Split(',');
                if (fields.Length == 3 && fields[0].Length == 9)
                {
                    map[fields[0]] = new SEC13FTickerCrosswalk.Entry(
                        fields[1], DateTime.ParseExact(fields[2], "yyyyMMdd", null));
                }
            }

            TestContext.Out.WriteLine($"crosswalk             {map.Count} CUSIPs");
            return map;
        }

        /// <summary>Copies the run's output somewhere it outlives the fixture's temporary folder.</summary>
        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
            }
        }

        /// <summary>Writes the tables into one zip, in the shape the processor reads them from.</summary>
        private static void BuildArchive(string raw, string path)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var table in Directory.GetFiles(raw, "*.tsv"))
            {
                zip.CreateEntryFromFile(table, Path.GetFileName(table), CompressionLevel.Fastest);
            }
        }

        /// <summary>The filing dates the window covers, read from the submissions themselves.</summary>
        private static (DateTime Start, DateTime End) WindowOf(string raw)
        {
            var submissions = Path.Combine(raw, "SUBMISSION.tsv");
            Assert.IsTrue(File.Exists(submissions), $"no SUBMISSION.tsv in {raw}");

            var lines = File.ReadLines(submissions).ToList();
            var columns = lines[0].Split('\t').Select((column, index) => (column, index))
                .ToDictionary(pair => pair.column, pair => pair.index);

            var dates = lines.Skip(1)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Split('\t')[columns["FILING_DATE"]].Trim().Split(' ')[0])
                .Select(date => DateTime.Parse(date))
                .ToList();

            return (dates.Min(), dates.Max());
        }

        /// <summary>Prints what the run wrote, which is what the pilot exists to find out.</summary>
        private static void Report(string folder, TimeSpan elapsed)
        {
            var zips = Directory.GetFiles(folder, "*.zip");
            var bytes = zips.Sum(path => new FileInfo(path).Length);

            var days = 0L;
            var rows = 0L;
            var worstDay = ("", "", 0L);

            foreach (var path in zips)
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries)
                {
                    days++;
                    using var reader = new StreamReader(entry.Open());
                    var lines = 0L;
                    while (reader.ReadLine() != null)
                    {
                        lines++;
                    }

                    rows += lines;
                    if (lines > worstDay.Item3)
                    {
                        worstDay = (Path.GetFileNameWithoutExtension(path), entry.Name, lines);
                    }
                }
            }

            var managers = Path.Combine(folder, "managers.csv");

            TestContext.Out.WriteLine($"securities            {zips.Length}");
            TestContext.Out.WriteLine($"publication days written  {days}");
            TestContext.Out.WriteLine($"reported positions    {rows}");
            TestContext.Out.WriteLine($"bytes on disk         {bytes:N0} ({bytes / 1024d / 1024d:F1} MB)");
            TestContext.Out.WriteLine($"bytes per position    {(rows == 0 ? 0 : bytes / rows)}");
            TestContext.Out.WriteLine($"busiest entry         {worstDay.Item1}#{worstDay.Item2} with {worstDay.Item3} positions");
            TestContext.Out.WriteLine($"managers.csv          {(File.Exists(managers) ? File.ReadLines(managers).Count() : 0)} names");
            TestContext.Out.WriteLine($"elapsed               {elapsed.TotalSeconds:F1}s");

            Assert.Greater(rows, 0, "the run published no positions");
        }
    }
}
