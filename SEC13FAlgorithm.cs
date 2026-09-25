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
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Example algorithm using the SEC Form 13F institutional holdings dataset as a source of
    /// alpha. It follows one manager, Pershing Square, through seven of the names it reports: it
    /// holds them all when the first quarter arrives, and from then on only those the manager
    /// added to. No filing states a change, so the comparison between two reported quarters is
    /// worked out here.
    /// </summary>
    public class SEC13FAlgorithm : QCAlgorithm
    {
        /// <summary>
        /// Pershing Square Capital Management, and Pershing Square Inc., which has reported the
        /// same positions since the June 2026 quarter. A change of reporting entity is a change
        /// of CIK.
        /// </summary>
        private static readonly HashSet<int> Managers = [1336528, 2026053];

        private readonly Dictionary<Symbol, SortedDictionary<DateTime, decimal>> _sharesByEquity = [];
        private DateTime _latestPeriod;
        private bool _rebalance;

        public override void Initialize()
        {
            // Two filings fall in this window: the March 2026 quarter, filed on 15 May, and the June
            // quarter, filed on 14 August. Each reaches the algorithm at midnight after its filing date.
            SetStartDate(2026, 5, 1);
            SetEndDate(2026, 8, 31);
            SetCash(100000);

            foreach (var ticker in new[] { "META", "UBER", "QSR", "MSFT", "BN", "HTZ", "AMZN" })
            {
                var equity = AddEquity(ticker, Resolution.Daily).Symbol;
                AddData<SEC13FHoldings>(equity);
                _sharesByEquity[equity] = [];
            }
        }

        public override void OnData(Slice slice)
        {
            foreach (var (dataSymbol, point) in slice.Get<SEC13FHoldings>())
            {
                // One point per filing date, carrying every position every manager reported for the
                // security that day. An amendment restates lines already counted and an option line
                // states the shares under the contracts, so both are left out of the share count.
                foreach (var holding in point.OfType<SEC13FHolding>().Where(holding =>
                             Managers.Contains(holding.ManagerCik) && holding.FormType == "13F-HR" &&
                             holding.AmountType == "SH" && !holding.PutCall.HasValue))
                {
                    var equity = dataSymbol.Underlying;
                    var shares = _sharesByEquity[equity];
                    shares[holding.PeriodEnd] = shares.GetValueOrDefault(holding.PeriodEnd) + (holding.Amount ?? 0);
                    _latestPeriod = holding.PeriodEnd > _latestPeriod ? holding.PeriodEnd : _latestPeriod;
                    _rebalance = true;

                    Log($"{Time:yyyy-MM-dd} {equity.Value} - {holding.ManagerName} reports {holding.Amount:N0} shares, " +
                        $"{holding.MarketValue:C0}, for {holding.PeriodEnd:yyyy-MM-dd}");
                }
            }

            // A 13F point arrives at midnight the day after its filing date, which is not
            // necessarily a day the equities print a bar, so the orders wait for prices.
            if (!_rebalance || slice.Bars.Count == 0)
            {
                return;
            }

            _rebalance = false;

            // With one quarter known, hold what the manager holds. With two, hold what it added to.
            var selected = _sharesByEquity
                .Select(kvp => (Equity: kvp.Key, Quarters: QuartersOf(kvp.Value)))
                .Where(entry => entry.Quarters.Count > 0)
                .Where(entry => entry.Quarters.Count == 1
                    ? entry.Quarters[0] > 0
                    : entry.Quarters[^1] > entry.Quarters[^2])
                .Select(entry => entry.Equity)
                .ToList();

            if (selected.Count == 0)
            {
                Liquidate();
                return;
            }

            SetHoldings(selected.Select(symbol => new PortfolioTarget(symbol, 1m / selected.Count)).ToList(),
                liquidateExistingHoldings: true);
        }

        /// <summary>
        /// The shares reported for one equity, oldest quarter first, with a closing zero for a name
        /// the manager has stopped reporting. A position sold out of has no line in the new quarter,
        /// so its newest period stays behind the newest the manager reported anywhere; taken for the
        /// name's own latest quarter, it would be compared with the quarter before it and held
        /// forever. Not being reported is a report of no shares.
        /// </summary>
        private List<decimal> QuartersOf(SortedDictionary<DateTime, decimal> shares)
        {
            var quarters = shares.Values.ToList();
            if (shares.Count > 0 && shares.Keys.Last() < _latestPeriod)
            {
                quarters.Add(0m);
            }

            return quarters;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                Debug($"{Time} - Filled: {orderEvent.Symbol} {orderEvent.FillQuantity}");
            }
        }
    }
}
