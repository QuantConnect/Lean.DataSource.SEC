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

using QuantConnect.Configuration;
using QuantConnect.Logging;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// Console program to convert from raw SEC data to a formatted form usable by LEAN
    /// </summary>
    public class Program
    {
        public static void Main()
        {
            var processingDateValue = Environment.GetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE");
            var processingDate = DateTime.ParseExact(processingDateValue, "yyyyMMdd", CultureInfo.InvariantCulture);
            var temporaryFolder = Config.Get("temp-output-directory", "/temp-output-directory");
            var rawDataDirectory = Config.Get("raw-data-folder", "/raw");
            var secDataDirectory = Path.Combine(rawDataDirectory, "alternative", "sec");
            Log.Trace($"DataProcessing.Main(): Processing {processingDate:yyyy-MM-dd}");

            var timer = Stopwatch.StartNew();

            try 
            {
                var download = new SECDataDownloader();
                Log.Trace("DataProcessing.Main(): Begin downloading raw files from SEC website...");
                download.Download(secDataDirectory, processingDate, processingDate);
                timer.Stop();
                Log.Trace($"DataProcessing.Main(): {processingDate} Downloading finished in time {timer.Elapsed}");
            }
            catch (Exception err) 
            {
                Log.Error(err, $"DataProcessing.Main(): {processingDate} Exception occurred while downloading SEC data");
            }

            timer.Restart();
            try
            {
                var processor = new SECDataConverter(secDataDirectory, temporaryFolder);
                processor.Process(processingDate);
                timer.Stop();
                Log.Trace($"DataProcessing.Main(): {processingDate} Conversion finished in time {timer.Elapsed}");
            }
            catch (Exception e)
            {
                Log.Error(e, $"DataProcessing.Main(): {processingDate} Exception while processing SEC data");
                Environment.Exit(1);
            }

            Environment.Exit(0);
        }
    }
}
