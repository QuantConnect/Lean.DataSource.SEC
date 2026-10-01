## Introduction

The US SEC 13F Whales dataset by the US Securities and Exchange Commission (SEC) tracks the holdings of institutional investment managers with at least $100 million in assets under management. The data covers 18,877 US Equities, starts in May 2013, and is delivered on a daily frequency. This dataset is created by parsing Form 13F filings.

This dataset depends on the [US Equity Security Master](https://www.quantconnect.com/datasets/quantconnect-us-equity-security-master) dataset because the US Equity Security Master dataset contains information on splits, dividends, and symbol changes.

## About the Provider

The mission of the U.S. Securities and Exchange Commission is to protect investors, maintain fair, orderly, and efficient markets, and facilitate capital formation. The SEC oversees the key participants in the securities world, including securities exchanges, securities brokers and dealers, investment advisors, and mutual funds. The SEC is concerned primarily with promoting the disclosure of important market-related information, maintaining fair dealing, and protecting against fraud.

## Getting Started

The following snippet demonstrates how to request data from the SEC Whales dataset:

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
| Asset Coverage* | 18,877 US Equities |
| Data Density | Sparse |
| Resolution | Daily |
| Timezone | New York |

\* The coverage includes all assets since the start date. It increases over time.

## Example Applications

The US SEC 13F Whales dataset enables you to follow what large institutional investors hold and how their positions change. Examples include the following strategies:

- Following the quarterly positions of specific fund managers on the premise that they are more informed
- Buying the securities that institutions are accumulating and selling the ones they are reducing
- Avoiding crowded securities that many managers hold at the same time

For more example algorithms, see [Examples](/datasets/sec-whales/examples).

## Supported Managers

To follow a manager, filter the holdings by its CIK with the **ManagerCik** property. The following table shows the 50 largest managers by reported value in the second quarter of 2026:

| CIK | Manager |
| --- | --- |
| 2012383 | BlackRock Inc. |
| 2100119 | VANGUARD CAPITAL MANAGEMENT LLC |
| 93751 | STATE STREET CORP |
| 315066 | FMR LLC |
| 2100121 | VANGUARD PORTFOLIO MANAGEMENT LLC |
| 895421 | MORGAN STANLEY |
| 1214717 | GEODE CAPITAL MANAGEMENT LLC |
| 19617 | JPMORGAN CHASE & CO |
| 70858 | BANK OF AMERICA CORP /DE/ |
| 914208 | Invesco Ltd. |
| 1374170 | NORGES BANK |
| 80255 | PRICE T ROWE ASSOCIATES INC /MD/ |
| 886982 | GOLDMAN SACHS GROUP INC |
| 73124 | NORTHERN TRUST CORP |
| 1422849 | Capital World Investors |
| 884546 | CHARLES SCHWAB INVESTMENT MANAGEMENT INC |
| 1422848 | Capital Research Global Investors |
| 1610520 | UBS Group AG |
| 1390777 | Bank of New York Mellon Corp |
| 1000275 | ROYAL BANK OF CANADA |
| 902219 | WELLINGTON MANAGEMENT GROUP LLP |
| 72971 | WELLS FARGO & COMPANY/MN |
| 354204 | DIMENSIONAL FUND ADVISORS LP |
| 861177 | UBS AM a distinct business unit of UBS ASSET MANAGEMENT AMERICAS LLC |
| 820027 | AMERIPRISE FINANCIAL INC |
| 1562230 | Capital International Investors |
| 764068 | Legal & General Group Plc |
| 38777 | FRANKLIN RESOURCES INC |
| 933478 | VANGUARD FIDUCIARY TRUST CO |
| 1403438 | LPL Financial LLC |
| 1407543 | ENVESTNET ASSET MANAGEMENT INC |
| 1871926 | Nuveen LLC |
| 1330387 | Amundi |
| 720005 | RAYMOND JAMES FINANCIAL INC |
| 948046 | DEUTSCHE BANK AG |
| 850529 | Fisher Asset Management LLC |
| 312069 | BARCLAYS PLC |
| 912938 | MASSACHUSETTS FINANCIAL SERVICES CO /MA/ |
| 1109448 | ALLIANCEBERNSTEIN L.P. |
| 1067983 | Berkshire Hathaway Inc |
| 1167557 | AQR CAPITAL MANAGEMENT LLC |
| 927971 | BANK OF MONTREAL /CAN/ |
| 815917 | JONES FINANCIAL COMPANIES LLLP |
| 2146052 | Jupiter Topco LLC |
| 748054 | AMERICAN CENTURY COMPANIES INC |
| 1164508 | ARROWSTREET CAPITAL LIMITED PARTNERSHIP |
| 1811242 | Vanguard Global Advisers LLC |
| 831001 | CITIGROUP INC |
| 873630 | HSBC HOLDINGS PLC |
| 1126328 | PRINCIPAL FINANCIAL GROUP INC |

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
