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
using System.Linq;
using NodaTime;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Util;

namespace QuantConnect.DataSource
{
    /// <summary>
    /// Every SEC Form 13F position published for one security on one day. A day's point carries
    /// one SEC13FHolding per reported line, so a manager that filed for the security appears once
    /// for each line it reported it on, and a day on which several managers filed carries all of
    /// them.
    ///
    /// The collection carries no totals of its own. How many managers hold the security and how
    /// many shares they hold between them are counts over the records, and no 13F filing states
    /// either, so they are left to the algorithm.
    /// </summary>
    public class SEC13FHoldings : BaseDataCollection
    {
        // Nothing may be declared here but the overrides below. LEAN builds the collection itself,
        // in BaseDataCollectionAggregatorReader, and sets only its symbol and its timestamps, so a
        // measure added to this class would silently stay null for every point ever read.

        private static readonly SEC13FHolding _factory = new();

        /// <summary>
        /// Location of the source file. One zip per security holds one entry per publication day, so
        /// that the dataset stays at a file per security instead of the eight and a half million a
        /// loose file per date would take. LEAN reads the entry straight out of the zip.
        /// </summary>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            return _factory.GetSource(config, date, isLiveMode);
        }

        /// <summary>
        /// Reads one line of the file into one reported position. The engine folds the lines this
        /// returns into the collection, grouping them by their end time, and every line of a file
        /// carries the same publication day, so one file gives one point.
        /// </summary>
        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            return _factory.Reader(config, line, date, isLiveMode);
        }

        /// <summary>Creates a copy of the instance.</summary>
        public override BaseData Clone()
        {
            return new SEC13FHoldings
            {
                Symbol = Symbol,
                Time = Time,
                EndTime = EndTime,
                Data = Data?.ToList(point => point.Clone())
            };
        }

        /// <summary>Data time zone (Eastern, the SEC filing time zone).</summary>
        public override DateTimeZone DataTimeZone() => _factory.DataTimeZone();

        /// <summary>Supported resolutions (Daily only, the quarterly cadence is modeled as Daily).</summary>
        public override List<Resolution> SupportedResolutions() => _factory.SupportedResolutions();

        /// <summary>Default resolution.</summary>
        public override Resolution DefaultResolution() => _factory.DefaultResolution();

        /// <summary>Sparse data: a security is only reported on the days managers file for it.</summary>
        public override bool IsSparseData() => _factory.IsSparseData();

        /// <summary>Linked to Equities, so renames and delistings are applied via map files.</summary>
        public override bool RequiresMapping() => _factory.RequiresMapping();

        /// <summary>String representation for debugging.</summary>
        public override string ToString()
        {
            return $"[{string.Join(",", Data.Select(point => point.ToString()))}]";
        }
    }
}
