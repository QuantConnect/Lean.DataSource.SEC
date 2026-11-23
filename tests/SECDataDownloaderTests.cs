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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.DataProcessing;
using QuantConnect.Logging;

namespace QuantConnect.DataLibrary.Tests
{
    [TestFixture]
    public class SECDataDownloaderTests
    {
        private const string Url = "https://www.sec.gov/Archives/edgar/Feed/2026/QTR4/20261116.nc.tar.gz";

        [TestCase(HttpStatusCode.Forbidden, "application/xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>",
            "403 - retrying in 0s: " + Url + " answered: <?xml version=\"1.0\" encoding=\"UTF-8\"?> <Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>")]
        [TestCase(HttpStatusCode.TooManyRequests, "text/html",
            "<html>\n  <head><title>SEC.gov | Request Rate Threshold Exceeded</title></head>\n</html>",
            "429 - retrying in 0s: " + Url + " answered: <html> <head><title>SEC.gov | Request Rate Threshold Exceeded</title></head> </html>")]
        public void DownloadArchiveLogsWhatTheRefusalSays(HttpStatusCode status, string mediaType, string body, string expectedLog)
        {
            var logs = new QueueLogHandler();
            var previousHandler = Log.LogHandler;
            Log.LogHandler = logs;
            var destination = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.nc.tar.gz.tmp");
            try
            {
                using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, null, mediaType)
                }));
                var downloader = new SECDataDownloader { MaxRetries = 1, ArchiveRetryDelay = TimeSpan.Zero };

                var error = Assert.Throws<Exception>(() => downloader.DownloadArchive(client, Url, destination));

                Assert.IsTrue(error.Message.Contains("No progress after 1 attempts"), error.Message);
                var traces = logs.Logs.Select(entry => entry.Message).ToList();
                Assert.Contains($"SECDataDownloader.DownloadArchive(): {expectedLog}", traces);
                Assert.IsFalse(File.Exists(destination));
            }
            finally
            {
                Log.LogHandler = previousHandler;
            }
        }

        [Test]
        public void DownloadArchiveLogsAtMostThreeHundredCharactersOfTheRefusal()
        {
            var logs = new QueueLogHandler();
            var previousHandler = Log.LogHandler;
            Log.LogHandler = logs;
            try
            {
                using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(new string('x', 1000))
                }));
                var downloader = new SECDataDownloader { MaxRetries = 1, ArchiveRetryDelay = TimeSpan.Zero };

                Assert.Throws<Exception>(() => downloader.DownloadArchive(client, Url,
                    Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.nc.tar.gz.tmp")));

                var refusal = logs.Logs.Select(entry => entry.Message).Single(message => message.Contains(" answered: "));
                Assert.IsTrue(refusal.EndsWith($" answered: {new string('x', 300)}"), refusal);
            }
            finally
            {
                Log.LogHandler = previousHandler;
            }
        }

        [Test]
        public void DownloadArchiveRetriesARefusalWhoseBodyNeverCompletes()
        {
            var logs = new QueueLogHandler();
            var previousHandler = Log.LogHandler;
            Log.LogHandler = logs;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var connections = new System.Collections.Concurrent.ConcurrentBag<TcpClient>();
            try
            {
                // Promises 100 bytes, sends 7 and keeps the connection open
                var server = Task.Run(async () =>
                {
                    while (true)
                    {
                        var connection = await listener.AcceptTcpClientAsync();
                        connections.Add(connection);
                        var stream = connection.GetStream();
                        var buffer = new byte[4096];
                        var request = string.Empty;
                        while (!request.Contains("\r\n\r\n"))
                        {
                            request += Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer));
                        }
                        var response = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 403 Forbidden\r\nContent-Type: application/xml\r\nContent-Length: 100\r\n\r\n<Error>");
                        await stream.WriteAsync(response);
                    }
                });
                var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/20261116.nc.tar.gz";
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
                var downloader = new SECDataDownloader
                {
                    MaxRetries = 2,
                    ArchiveRetryDelay = TimeSpan.Zero,
                    RefusalBodyTimeout = TimeSpan.FromMilliseconds(500)
                };

                var download = Task.Run(() => downloader.DownloadArchive(client, url,
                    Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.nc.tar.gz.tmp")));

                Assert.AreEqual(0, Task.WaitAny(new Task[] { download }, TimeSpan.FromSeconds(10)), "the download is stuck on the refusal's body");
                Assert.IsTrue(download.Exception.InnerException.Message.Contains("No progress after 2 attempts"),
                    download.Exception.InnerException.Message);
                var traces = logs.Logs.Select(entry => entry.Message).ToList();
                Assert.Contains($"SECDataDownloader.DownloadArchive(): 403 - retrying in 0s: {url} answered: <Error> (body incomplete after 0.5s)",
                    traces);
            }
            finally
            {
                listener.Stop();
                foreach (var connection in connections)
                {
                    connection.Dispose();
                }
                Log.LogHandler = previousHandler;
            }
        }

        private class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpResponseMessage> _respond;

            public StubHandler(Func<HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_respond());
            }
        }
    }
}
