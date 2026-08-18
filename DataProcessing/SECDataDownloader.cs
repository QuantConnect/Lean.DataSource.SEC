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

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using QuantConnect.Configuration;
using QuantConnect.DataSource;
using QuantConnect.Logging;
using QuantConnect.Util;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using QuantConnect.Securities;

namespace QuantConnect.DataProcessing
{
    public class SECDataDownloader
    {
        // SEC imposes rate limits of 10 requests per second. Set to 5 req / 1.1 sec just to be safe.
        private readonly RateGate _indexGate = new RateGate(1, TimeSpan.FromMilliseconds(220));
        private readonly HashSet<string> _downloadedIndexFiles = new HashSet<string>();
        private readonly Dictionary<string, SECReportIndexFile> _archiveIndexFileCache = new Dictionary<string, SECReportIndexFile>();

        /// <summary>
        /// Base URL to query the SEC website for reports
        /// </summary>
        public string BaseUrl = "https://www.sec.gov/Archives/edgar";

        /// <summary>
        /// Maximum retries to request for SEC edgar filings
        /// </summary>
        public int MaxRetries = 5;

        /// <summary>
        /// Bytes requested per ranged call when fetching a feed archive.
        /// </summary>
        /// <remarks>
        /// The SEC drops a transfer once it has been running for a while, so the practical
        /// limit is time, not size. 100 MB comfortably completes inside that window while
        /// keeping the number of requests per archive small.
        /// </remarks>
        public long ChunkSizeBytes = 100 * 1024 * 1024;

        /// <summary>
        /// SEC data downloader constructor
        /// </summary>
        public SECDataDownloader()
        {

        }

        /// <summary>
        /// Downloads the raw data from the data vendor and stores it on disk
        /// </summary>
        /// <param name="rawDestination">Destination we will write raw data to</param>
        /// <param name="start">Starting date</param>
        /// <param name="end">Ending date</param>
        public void Download(string rawDestination, DateTime start, DateTime end)
        {
            // We will be rate limited from the SEC website if we don't identify ourselves via User-Agent.
            // Also we use a global HttpClient instance to enable HTTP keep-alive, which will improve performance
            // and also reduce the chances of being rate-limited (plus, this is recommended practice).
            var companyName = Config.Get("sec-user-agent-company-name");
            var companyEmail = Config.Get("sec-user-agent-company-email");

            if (string.IsNullOrEmpty(companyName))
            {
                throw new ArgumentException("The SEC requires a company name to download data using automation. Please edit `config.json` and add a `sec-user-agent-company-name` entry with your company name");
            }
            if (string.IsNullOrEmpty(companyEmail))
            {
                throw new ArgumentException("The SEC requires a company email contact to download data using automation. Please edit `config.json` and add a `sec-user-agent-company-email` entry with your company email address");
            }

            var holiday = MarketHoursDatabase.FromDataFolder().GetEntry(Market.USA, (string)null, SecurityType.Equity).ExchangeHours.Holidays;
            using (var client = new HttpClient())
            {
                // The default 100s timeout also covers reading each response body, so at that limit
                // a 100 MB archive chunk would require a sustained 1 MB/s from the SEC. Leave room
                // for throttled transfers while still bounding a hung connection.
                client.Timeout = TimeSpan.FromMinutes(10);

                var userAgent = string.Join(" ", companyName, companyEmail);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
                
                Directory.CreateDirectory(Path.Combine(rawDestination, "indexes"));

                for (var currentDate = start; currentDate <= end; currentDate = currentDate.AddDays(1))
                {
                    // SEC does not publish documents on US federal holidays or weekends
                    if (!currentDate.IsCommonBusinessDay() || holiday.Contains(currentDate))
                    {
                        Log.Trace(
                            $"SECDataDownloader.Download(): Skipping date {currentDate:yyyy-MM-dd} because it was during the weekend or was a holiday");
                        continue;
                    }

                    // SEC files are stored by quarters on EDGAR
                    var quarter = currentDate < new DateTime(currentDate.Year, 4, 1) ? "QTR1" :
                        currentDate < new DateTime(currentDate.Year, 7, 1) ? "QTR2" :
                        currentDate < new DateTime(currentDate.Year, 10, 1) ? "QTR3" :
                        "QTR4";

                    var rawFile = Path.Combine(rawDestination, $"{currentDate:yyyyMMdd}.nc.tar.gz");
                    // Deterministic name on the destination volume: retries resume the partial
                    // instead of starting from zero, the final File.Move is a same-volume rename,
                    // and failed runs leave no orphaned Guid-named partials in the temp directory.
                    var tmpFile = $"{rawFile}.tmp";

                    // We can access the index files for any given date and filter by form type
                    var dailyIndexTmp = new FileInfo(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.idx"));
                    var dailyIndexRaw =
                        new FileInfo(Path.Combine(rawDestination, "indexes", $"{currentDate:yyyyMMdd}.idx"));

                    var cacheKey = $"{currentDate.Year}/{quarter}";
                    SECReportIndexFile indexFile;
                    if (!_archiveIndexFileCache.TryGetValue(cacheKey, out indexFile))
                    {
                        indexFile = GetArchiveIndexFile(client, currentDate.Year, quarter);
                        _archiveIndexFileCache[cacheKey] = indexFile;
                    }

                    // Attempt to parse the archive index file. Skip downloading the data for
                    // the day if we can't determine its size pre-emptively
                    string rawSize;
                    try
                    {
                        rawSize = indexFile.Directory.Items
                            .Find(item => item.Name == $"{currentDate:yyyyMMdd}.nc.tar.gz").Size;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e,
                            $"SECDataDownloader.TryGetFileSizeFromIndex(): Failed to find {currentDate:yyyyMMdd}.nc.tar.gz in the index file. Skipping...");
                        continue;
                    }

                    // Strip out kilobyte unit. All SEC data is reported in kilobytes
                    rawSize = rawSize.Replace("KB", "");

                    decimal fileSizeInKB;
                    if (!decimal.TryParse(rawSize, NumberStyles.Number, CultureInfo.InvariantCulture, out fileSizeInKB))
                    {
                        Log.Error(
                            $"SECDataDownloader.TryGetFileSizeFromIndex(): Failed to convert {rawSize} to decimal");
                        continue;
                    }

                    // Sometimes, requests to the SEC can fail for no apparent reason.
                    // We implement retry logic here to mitigate that potential issue
                    for (var retries = 0; retries < MaxRetries; retries++)
                    {
                        try
                        {
                            if (File.Exists(rawFile))
                            {
                                Log.Trace(
                                    $"SECDataDownloader.Download(): Skipping download of archive: {currentDate:yyyyMMdd}.nc.tar.gz");
                                break;
                            }

                            Log.Trace($"SECDataDownloader.Download(): Downloading temp filing archive to: {tmpFile}");
                            // *.nc.tar.gz files are massive (multiple GB), so they are fetched in ranged
                            // chunks rather than as one response: a single request for the whole archive
                            // outlives what the SEC keeps open and is dropped part-way, leaving a truncated
                            // file that the size check below discards -- so the day never converts.
                            // (Example case: 2021-05-17 for the size, 2025-11-06 for the dropped transfer.)
                            var expectedSizeBytes = DownloadArchive(client,
                                $"{BaseUrl}/Feed/{currentDate.Year}/{quarter}/{currentDate:yyyyMMdd}.nc.tar.gz",
                                tmpFile);

                            var tmpFileStat = new FileInfo(tmpFile);
                            var tmpFileSizeInKB = tmpFileStat.Length / 1024;

                            // The size the server reported while serving the download is authoritative.
                            // Fall back to index.json only when the server never said one: its sizes go
                            // stale when the SEC re-generates an archive, so there only a smaller
                            // download (-1%) indicates truncation.
                            if (expectedSizeBytes >= 0
                                ? tmpFileStat.Length != expectedSizeBytes
                                : tmpFileSizeInKB < fileSizeInKB - (fileSizeInKB * 0.01m))
                            {
                                var expected = expectedSizeBytes >= 0 ? $"{expectedSizeBytes} bytes" : $"{fileSizeInKB}KB";
                                Log.Error(
                                    $"Temporary file is {tmpFileStat.Length} bytes, but is supposed to be {expected}. Deleting temp file and retrying...");
                                tmpFileStat.Delete();
                                continue;
                            }

                            Log.Trace($"SECDataDownloader.Download(): Moving temp archive to: {rawFile}");
                            File.Move(tmpFile, rawFile);

                            Log.Trace(
                                $"SECDataDownloader.Download(): Successfully downloaded {currentDate:yyyyMMdd}.nc.tar.gz");

                            break;
                        }
                        catch (HttpRequestException err)
                        {
                            Log.Error(
                                $"SECDataDownloader.Download(): Received status code {StatusOrMessage(err)} - Retrying...");
                        }
                        catch (Exception e)
                        {
                            Log.Error(e);
                        }
                    }

                    // Sometimes, requests to the SEC can fail for no apparent reason.
                    // We implement retry logic here to mitigate that potential issue
                    for (var retries = 0; retries < MaxRetries; retries++)
                    {
                        try
                        {
                            _indexGate.WaitToProceed();

                            Log.Trace(
                                $"SECDataDownloader.Download(): Downloading temp index manifest to: {dailyIndexTmp.FullName}");
                            var indexBytes = client
                                .GetByteArrayAsync(
                                    $"{BaseUrl}/daily-index/{currentDate.Year}/{quarter}/master.{currentDate:yyyyMMdd}.idx")
                                .SynchronouslyAwaitTaskResult();

                            File.WriteAllBytes(dailyIndexTmp.FullName, indexBytes);

                            if (dailyIndexRaw.Exists)
                            {
                                Log.Trace(
                                    $"SECDataDownloader.Download(): Deleting existing index file manifest: {dailyIndexRaw.FullName}");
                                dailyIndexRaw.Delete();
                            }

                            Log.Trace(
                                $"SECDataDownloader.Download(): Moving temp index manifest to: {dailyIndexRaw.FullName}");
                            dailyIndexTmp.MoveTo(dailyIndexRaw.FullName);

                            Log.Trace(
                                $"SECDataDownloader.Download(): Successfully downloaded master.{currentDate:yyyyMMdd}.idx");

                            break;
                        }
                        catch (HttpRequestException err)
                        {
                            Log.Error(
                                $"SECDataDownloader.Download(): Got error code {StatusOrMessage(err)} attempting to download index manifest for date {currentDate:yyyy-MM-dd} - retrying");
                        }
                        catch (Exception e)
                        {
                            Log.Error(e);
                        }
                    }

                    // Skip miscellaneous header rows because it is unstructured data
                    var dailyIndexes = File.ReadAllLines(dailyIndexRaw.FullName).Skip(7);

                    // Increase max simultaneous HTTP connection count
                    ServicePointManager.DefaultConnectionLimit = 1000;

                    // Tasks of index file downloads
                    var previousCik = string.Empty;
                    var i = 0;

                    // Parse CIK from text database and download the file asynchronously if we don't already have it
                    foreach (var line in dailyIndexes)
                    {
                        i++;

                        // CIK[0] | Company Name[1] | Form Type[2] | Date Filed[3] | File Name[4]
                        var csv = line.Split('|');

                        if (csv.Length < 5)
                        {
                            Log.Error(
                                $"SECDataDownloader.Download(): Length of daily index file line is less than five");
                            continue;
                        }

                        // CIK is 10 digits long, which we use to get the index file
                        var cik = csv[0].PadLeft(10, '0');
                        var formType = csv[2];

                        switch (formType)
                        {
                            case "8-K":
                            case "10-K":
                            case "10-Q":
                                break;
                            default:
                                // To prevent duplicate log spam
                                if (!string.IsNullOrEmpty(previousCik) && cik != previousCik)
                                {
                                    Log.Error(
                                        $"SECDataDownloader.Download(): Skipping form type {formType} with CIK: {cik} - line {i}");
                                }

                                previousCik = cik;
                                continue;
                        }

                        if (_downloadedIndexFiles.Contains(cik))
                        {
                            Log.Trace(
                                $"SECDataDownloader.Download(): Skipping index file since we already downloaded it during this session: {cik}.json");
                            previousCik = cik;
                            continue;
                        }

                        DownloadIndexFile(client, cik, rawDestination).SynchronouslyAwaitTask();
                        _downloadedIndexFiles.Add(cik);
                        previousCik = cik;
                    }
                }

                // Download list of Ticker to CIK mappings from SEC website. Note that this list
                // is not complete and does not contain all historical tickers.
                var cikTickerListPath = Path.Combine(rawDestination, "cik-ticker-mappings.txt");
                var cikTickerListTempPath = $"{cikTickerListPath}.tmp";

                // Download master list of CIKs from SEC website and store on disk
                var cikLookupPath = Path.Combine(rawDestination, "cik-lookup-data.txt");
                var cikLookupTempPath = $"{cikLookupPath}.tmp";

                for (var i = 0; i < MaxRetries; i++)
                {
                    try
                    {
                        if (!File.Exists(cikTickerListPath))
                        {
                            _indexGate.WaitToProceed();

                            Log.Trace("SECDataDownloader.Download(): Downloading ticker-CIK mappings list");
                            var tickerCikMappingsBytes = client
                                .GetByteArrayAsync("https://www.sec.gov/include/ticker.txt")
                                .SynchronouslyAwaitTaskResult();

                            File.WriteAllBytes(cikTickerListTempPath, tickerCikMappingsBytes);
                            File.Move(cikTickerListTempPath, cikTickerListPath);
                            File.Delete(cikTickerListTempPath);
                        }

                        if (!File.Exists(cikLookupPath))
                        {
                            _indexGate.WaitToProceed();

                            Log.Trace("SECDataDownloader.Download(): Downloading CIK lookup data");
                            var cikLookupBytes = client.GetByteArrayAsync($"{BaseUrl}/cik-lookup-data.txt")
                                .SynchronouslyAwaitTaskResult();

                            File.WriteAllBytes(cikLookupTempPath, cikLookupBytes);
                            File.Move(cikLookupTempPath, cikLookupPath);
                            File.Delete(cikLookupTempPath);
                        }
                    }
                    catch (HttpRequestException err)
                    {
                        if (err.StatusCode == HttpStatusCode.Forbidden ||
                            err.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            Log.Trace(
                                $"SECDataDownloader.Download(): Rate limited downloading CIK-ticker mappings - retrying in 10s");
                            Thread.Sleep(10000);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Attempt to download SEC index files for a given CIK, with added
        /// fault tolerance
        /// </summary>
        /// <param name="cik">CIK of the equity</param>
        /// <param name="rawDestination">Destination where we will write to</param>
        /// <exception cref="Exception">We were unable to download the index file</exception>
        private async Task DownloadIndexFile(HttpClient client, string cik, string rawDestination)
        {
            for (var i = 0; i < MaxRetries; i++)
            {
                try
                {
                    _indexGate.WaitToProceed();
                    
                    var indexFileBytes = await client.GetByteArrayAsync($"{BaseUrl}/data/{cik}/index.json");
                    var indexPathTmp = new FileInfo(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json"));
                    var indexPath = new FileInfo(Path.Combine(rawDestination, "indexes", $"{cik}.json"));

                    await File.WriteAllBytesAsync(indexPathTmp.FullName, indexFileBytes);
                    OnIndexFileDownloaded(indexPathTmp, indexPath);

                    return;
                }
                catch (HttpRequestException err)
                {
                    if (err.StatusCode == HttpStatusCode.Forbidden || err.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        Log.Trace($"SECDataDownloader.DownloadIndexFile(): Rate limited downloading index file for {cik} ({(int)err.StatusCode}) - retrying in 10s");
                        // We've been rate limited, sleep for 10 seconds then try again
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }
                }
            }

            throw new Exception($"Failed to download index file \"{cik}.json\" after {MaxRetries} attempts");
        }

        /// <summary>
        /// Moves temporary file to permanent path
        /// </summary>
        /// <param name="source">Path we will move index file from</param>
        /// <param name="destination">Path we will move index file to</param>
        private void OnIndexFileDownloaded(FileInfo source, FileInfo destination)
        {
            try
            {
                source.MoveTo(destination.FullName, true);
                Log.Trace($"SECDataDownloader.OnIndexFileDownloaded(): Successfully downloaded {destination.FullName}");
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        /// <summary>
        /// Downloads a feed archive to <paramref name="destination"/> using ranged requests.
        /// </summary>
        /// <param name="client">HTTP client to download with</param>
        /// <param name="url">Archive URL to fetch</param>
        /// <param name="destination">File the archive is written to, resumed if it already exists</param>
        /// <returns>Size of the archive in bytes as reported by the server, or -1 if it never said</returns>
        /// <remarks>
        /// Requesting a multi-GB archive in one call ends in "the response ended prematurely"
        /// well before the file is complete, and every retry starts again from byte zero, so a
        /// large day can never land. Ranged requests keep each call short enough to finish, and
        /// a dropped connection costs only the chunk in flight: the next request resumes from
        /// the first byte still missing. A short read is therefore not an error -- the loop just
        /// continues from wherever the response actually stopped.
        /// </remarks>
        private long DownloadArchive(HttpClient client, string url, string destination)
        {
            long CurrentLength() => File.Exists(destination) ? new FileInfo(destination).Length : 0L;

            // Pick up whatever an earlier attempt already wrote instead of starting over.
            var position = CurrentLength();
            var totalLength = -1L;
            var stalledAttempts = 0;
            EntityTagHeaderValue etag = null;

            while (totalLength < 0 || position < totalLength)
            {
                if (stalledAttempts >= MaxRetries)
                {
                    throw new Exception(
                        $"SECDataDownloader.DownloadArchive(): No progress after {stalledAttempts} attempts at byte {position} of {totalLength} for {url}");
                }

                _indexGate.WaitToProceed();
                var previousPosition = position;
                var wholeBodyDrained = false;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new(position, position + ChunkSizeBytes - 1);
                    if (etag != null)
                    {
                        // If the SEC re-generates the archive between chunks the tag no longer
                        // matches, and the server answers 200 with the new file from byte zero
                        // instead of splicing bytes of two different generations together.
                        request.Headers.IfRange = new(etag);
                    }

                    using var response = client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                        .SynchronouslyAwaitTaskResult();

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        // The file already covers the requested offset; the 416 carries
                        // "Content-Range: bytes */<total>", the size this loop was missing.
                        totalLength = response.Content.Headers.ContentRange?.Length ?? position;
                        if (position > totalLength)
                        {
                            // Longer than the archive itself: a stale partial from a
                            // re-generated archive, so start over.
                            File.Delete(destination);
                        }
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();

                        // Content-Range ("bytes <start>-<end>/<total>") is the authoritative size:
                        // index.json goes stale whenever the SEC re-generates an archive.
                        var contentRange = response.Content.Headers.ContentRange;
                        if (contentRange?.Length != null)
                        {
                            totalLength = contentRange.Length.Value;
                        }

                        if (contentRange?.From is { } from && from != position)
                        {
                            throw new InvalidOperationException(
                                $"response starts at byte {from} instead of the requested {position}");
                        }

                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            // Range was ignored (or If-Range rejected a stale tag) and the whole
                            // file is coming, so restart the write under the new entity's tag.
                            totalLength = response.Content.Headers.ContentLength ?? -1L;
                            etag = response.Headers.ETag;
                            File.Delete(destination);
                        }
                        else
                        {
                            etag ??= response.Headers.ETag;
                        }

                        using var responseStream = response.Content.ReadAsStreamAsync().SynchronouslyAwaitTaskResult();
                        using var fileStream = new FileStream(destination, FileMode.Append, FileAccess.Write);
                        responseStream.CopyTo(fileStream);
                        wholeBodyDrained = response.StatusCode == HttpStatusCode.OK;
                    }
                }
                catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    // We've been rate limited: back off like the index downloads do
                    Log.Trace($"SECDataDownloader.DownloadArchive(): Rate limited ({StatusOrMessage(e)}) - retrying in 10s: {url}");
                    Thread.Sleep(10000);
                }
                catch (HttpRequestException e) when (e.StatusCode is { } status
                    && (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout)
                {
                    // A definite client error, like a 404, cannot heal on retry
                    throw;
                }
                catch (Exception e)
                {
                    // Bytes already written stay on disk; only the missing tail is re-requested.
                    Log.Error($"SECDataDownloader.DownloadArchive(): {e.Message} - resuming {url}");
                }

                // Trust the file, not the byte count: the stream may have been cut mid-copy.
                position = CurrentLength();
                if (wholeBodyDrained && totalLength < 0)
                {
                    // A 200 body drained to its end without a Content-Length is the entire
                    // entity, so what is on disk is the complete archive.
                    totalLength = position;
                }
                if (position > previousPosition)
                {
                    // Dropped transfers that still landed bytes never exhaust the retry budget;
                    // only consecutive attempts with zero progress count against it.
                    stalledAttempts = 0;
                }
                else if (++stalledAttempts < MaxRetries)
                {
                    // A stall means the server errored before sending a single byte. Give it
                    // room to recover instead of burning the whole budget within a second or
                    // two, since only the 220ms rate gate spaces the attempts otherwise.
                    Thread.Sleep(10000);
                }
            }

            Log.Trace($"SECDataDownloader.DownloadArchive(): Downloaded {position} bytes from {url}");
            return totalLength;
        }

        /// <summary>
        /// Numeric status code of a failed HTTP request for logging; the exception message
        /// when the failure happened below HTTP (connection reset, TLS drop) and no status exists.
        /// </summary>
        private static string StatusOrMessage(HttpRequestException err)
            => err.StatusCode is { } status ? ((int)status).ToString() : err.Message;

        /// <summary>
        /// Downloads the archive index file
        /// </summary>
        /// <param name="year">Year to download index file for</param>
        /// <param name="quarter">Quarter to download index file for</param>
        /// <returns>SEC index directory</returns>
        private SECReportIndexFile GetArchiveIndexFile(HttpClient client, int year, string quarter)
        {
            for (var retries = 0; retries < MaxRetries; retries++)
            {
                // Download the index file for the quarter archive files before we download the archive so we know
                // its size and make sure it's within +-1% of the original file on the server
                try
                {
                    _indexGate.WaitToProceed();
                    
                    Log.Trace($"SECDataDownloader.GetFileSize(): Downloading archive index file for file size verification");
                    var contents = client.GetStringAsync($"{BaseUrl}/Feed/{year}/{quarter}/index.json").SynchronouslyAwaitTaskResult();

                    var indexFile = JsonConvert.DeserializeObject<SECReportIndexFile>(contents);
                    Log.Trace($"SECDataDownloader.GetFileSize(): Successfully downloaded {BaseUrl}/Feed/{year}/{quarter}/index.json");

                    return indexFile;
                }
                catch (HttpRequestException err)
                {
                    Log.Error($"SECDataDownloader.GetFileSize(): Received status code {StatusOrMessage(err)} - Retrying...");
                }
                catch (Exception e)
                {
                    Log.Error(e, "Retrying...");
                }
            }

            throw new Exception("Failed to download SEC archive index file. No more retries remaining");
        }
    }
}
