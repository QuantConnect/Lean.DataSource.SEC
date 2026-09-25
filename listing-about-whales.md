## Introduction

SEC Whales is the ownership side of the SEC's filings: who holds what, reported by the holders
themselves. It collects those disclosures one source at a time and publishes each filing as it was
made. It ships today with Form 13F institutional holdings, which every manager exercising
discretion over at least 100 million dollars must file within 45 days of the end of a calendar
quarter, from the second quarter of 2013 to the present.

Nothing is summed, counted or averaged. The holders of a name, the shares institutions hold between
them, quarter over quarter change and concentration are all derivable from a day's records, and no
filing states any of them, so they are left to the algorithm rather than invented here.

The reporting lag is the product, not an inconvenience to be hidden. A position is typically 45 to
135 days old when it reaches the public record, late amendments arrive years later, and 10.4
percent of filings miss the deadline, so a record is stamped with the date it was filed and carries
the quarter it describes in `PeriodEnd`. Delivering it on that quarter end would inject every day
of the gap as look-ahead.

An algorithm receives one `SEC13FHoldings` point per security per filing date, holding every
position reported for that security that day. Several managers file on the same day, and one
manager can report the same security on more than one line when the investment discretion differs,
which Berkshire Hathaway does with Moody's. Those records stay apart.

## About the Provider

The [U.S. Securities and Exchange Commission](https://www.sec.gov) is the federal agency that
regulates the US securities markets. Form 13F is filed through EDGAR, the SEC's electronic filing
system, and the agency republishes those filings as structured, tab separated data sets in three
month batches. The history is built from those data sets, and the daily job reads each day's
filings from EDGAR itself, because a batch arrives up to three months after its first filing. The
data is public domain and needs no account and no API key.

## Getting Started

```python
self._symbol = self.add_equity("AAPL", Resolution.DAILY).symbol
self._holdings_symbol = self.add_data(SEC13FHoldings, self._symbol).symbol
```
```csharp
_symbol = AddEquity("AAPL", Resolution.Daily).Symbol;
_holdingsSymbol = AddData<SEC13FHoldings>(_symbol).Symbol;
```

## Data Summary

The following table describes the dataset properties:

| Property | Value |
| --- | --- |
| Start Date | May 2013 |
| Asset Coverage\* | 18,877 US Equities |
| Data Density | Sparse |
| Resolution\*\* | Daily |
| Timezone | America/New_York |

\* The coverage includes all assets since the start date. It increases over time. Positions are
reported by CUSIP, which is licensed and not published, and resolve to a LEAN `Symbol` for 97.0
percent of the lines of one recent week and 95.0 percent of the fourth quarter of 2020; the
crosswalk that reaches the hardest names begins in late 2019, so a security delisted before then
is the likeliest to be missing.

\*\* Positions are reported quarterly, but the managers of one quarter file across roughly fifty
different days and several quarters are live at once, so publication is close to continuous.

## Example Applications

SEC Whales lets you see what large holders actually hold and trade against how crowded a name is.
Examples include the following strategies:

- Screening for crowding by counting the distinct managers reporting each security, and for
  de-crowding by taking the securities whose count fell hardest against the previous quarter.
- Building an ownership change momentum signal from the quarter over quarter move in reported
  shares, and going long the names institutions are accumulating.
- Following one manager through `ManagerCik`, reading what a single fund reported quarter after
  quarter rather than what the market did in aggregate, which is what the demonstration algorithms
  do with Pershing Square.
- Reading the reported put and call lines alongside the share positions to see whether managers are
  hedging a name rather than simply owning it.

## Meta

| Field | Value |
| --- | --- |
| name | SEC Whales |
| url | sec-whales |
| vendorName | Securities and Exchange Commission |
| website | https://www.sec.gov |
| history | May 2013 |
| reach | 18,877 US Equities |
| shortDescription | Ownership filings from the SEC as their holders filed them, starting with Form 13F institutional holdings |
| priceCTA | Free in Cloud |
| delivery | cloud only |

Tags: Financial Market Data

Licensing card:

```html
<p>Free access to SEC Whales in QuantConnect Cloud for use in backtesting or live trading.</p>
<ul>
    <li>Every position reported on Form 13F since 2013, as each manager filed it, across 18,877 US Equities</li>
    <li>Manager, reported quarter, share or principal amount, value, option side, discretion and voting authority per position</li>
    <li>Curated, clean data</li>
</ul>
```
