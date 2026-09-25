## Requesting Data

SEC Whales carries Form 13F institutional holdings today. To add it to your algorithm, call the
**AddData** method with the `Symbol` of an Equity you already subscribe to. Save a reference to the dataset **Symbol** so you
can access the data later in your algorithm.

```python
class SEC13FDataAlgorithm(QCAlgorithm):

    def initialize(self) -> None:
        # Coverage starts 2013-05-20
        self.set_start_date(2020, 10, 1)
        self.set_end_date(2020, 12, 31)
        self.set_cash(100000)

        self._symbol = self.add_equity("AAPL", Resolution.DAILY).symbol
        self._dataset_symbol = self.add_data(SEC13FHoldings, self._symbol).symbol
```
```csharp
public class SEC13FDataAlgorithm : QCAlgorithm
{
    private Symbol _symbol, _datasetSymbol;

    public override void Initialize()
    {
        // Coverage starts 2013-05-20
        SetStartDate(2020, 10, 1);
        SetEndDate(2020, 12, 31);
        SetCash(100000);

        _symbol = AddEquity("AAPL", Resolution.Daily).Symbol;
        _datasetSymbol = AddData<SEC13FHoldings>(_symbol).Symbol;
    }
}
```

## Accessing Data

To get the current Form 13F data, index the current [Slice](https://www.quantconnect.com/docs/v2/writing-algorithms/key-concepts/time-modeling/timeslices)
with the dataset **Symbol**. **Slice** objects deliver unique events to your algorithm as they
happen, so check that the **Slice** contains the data you want before you index it. A point is a
`SEC13FHoldings` collection holding every position reported for that security on that filing date,
so iterate it rather than reading a single value off it; the collection's own `Value` is zero.

```python
def on_data(self, slice: Slice) -> None:
    if self._dataset_symbol in slice:
        for holding in slice[self._dataset_symbol]:
            self.log(f"CIK {holding.manager_cik}: {holding.amount} {holding.amount_type}")
```
```csharp
public override void OnData(Slice slice)
{
    if (slice.ContainsKey(_datasetSymbol))
    {
        SEC13FHoldings point = slice[_datasetSymbol];
        foreach (SEC13FHolding holding in point)
        {
            Log($"CIK {holding.ManagerCik}: {holding.Amount} {holding.AmountType}");
        }
    }
}
```

A point's `Time` is the filing date, not the quarter the position describes. Read `PeriodEnd`
whenever you need the quarter, because one day of filings can carry several different ones and the
gap between the two dates runs from zero days to several years.

The dataset states no holder count, no total shares and no total value, because no 13F filing states
any of them. Counting is the algorithm's job. To iterate through all of the dataset objects in the
current **Slice**, call the **Get** method, which is the usual shape once you subscribe to more than
one name:

```python
def on_data(self, slice: Slice) -> None:
    for dataset_symbol, holdings in slice.get(SEC13FHoldings).items():
        managers = {holding.manager_cik for holding in holdings
                    if holding.put_call is None and holding.amount_type == "SH"}
        self.log(f"{dataset_symbol}: {len(managers)} managers filed")
```
```csharp
public override void OnData(Slice slice)
{
    foreach (var kvp in slice.Get<SEC13FHoldings>())
    {
        var managers = kvp.Value.Cast<SEC13FHolding>()
            .Where(holding => !holding.PutCall.HasValue && holding.AmountType == "SH")
            .Select(holding => holding.ManagerCik).Distinct().Count();
        Log($"{kvp.Key}: {managers} managers filed");
    }
}
```

Filter on `AmountType` and `PutCall` first, as that example does: a PRN line is a principal amount
of debt and an option line states the shares underlying the contracts, so adding either into a share
total misstates the position. A manager files once per quarter on a day of its own choosing, so
breadth builds up across filing dates rather than appearing on any single one, and one manager can
report the same security on several lines when the discretion differs, so distinct `ManagerCik` is
not the same as records. One that stops appearing has not necessarily sold: a fund whose positions
move to another reporting entity files a 13F-NT, which carries none, and the new entity reports
them under its own CIK.

`ReportedValue` is the number the manager wrote, in whatever unit the filing used, and `ValueScale`
is the power of ten that turns it into dollars, which `MarketValue` applies for you. A field the
filing left empty is null, which is not a reported zero, so guard the arithmetic.

```python
value = holding.market_value          # dollars
raw = holding.reported_value          # as filed, with holding.value_scale beside it
shares = holding.amount or 0
```
```csharp
var value = holding.MarketValue;      // dollars
var raw = holding.ReportedValue;      // as filed, with holding.ValueScale beside it
var shares = holding.Amount.GetValueOrDefault();
```

An amendment, which `FormType` marks 13F-HR/A, is published beside the filing it amends and
replaces nothing. `AmendmentType` says which kind it is: `RESTATEMENT` replaces the original
filing, `NEW HOLDINGS` only adds to it, and applying either is the algorithm's job. `ConfidentialOmitted` marks a submission that withheld other positions under confidential
treatment, which makes that record a floor rather than the full picture.

## Historical Data

To get historical Form 13F data, call the **History** method with the
dataset **Symbol**. If there is no data in the period you request, the history result is empty.

```python
# pandas Series, one entry per filing date, each holding the list of that day's positions
history_series = self.history(self._dataset_symbol, timedelta(days=60), Resolution.DAILY)

# DataFrame, one row per reported position, columns named after the record's fields in lower case
history_df = self.history(SEC13FHoldings, self._dataset_symbol, timedelta(days=60), Resolution.DAILY, flatten=True)

# SEC13FHoldings objects, one per filing date, each carrying its records
history_bars = self.history[SEC13FHoldings](self._dataset_symbol, timedelta(days=60), Resolution.DAILY)
```
```csharp
var history = History<SEC13FHoldings>(_datasetSymbol, TimeSpan.FromDays(60), Resolution.Daily);
```

The three shapes differ more than usual for this dataset, because a point is a collection. Without
`flatten`, Python gives you a **Series** and not a DataFrame, so reading a column off it will not
work. With `flatten=True` you get one row per reported position, which is the form to use for
anything cross-sectional: sixty days of AAPL history read at the start of the fourth quarter of
2020 is one Series of 36 entries or a DataFrame of 4,144 rows. A reported zero occasionally comes
back there as `NaN` rather than as `0`, which is LEAN's pandas conversion rather than a gap in the
data, so treat the two alike with `history_df["votingshared"].fillna(0)`.

Ask for a time span rather than a bar count. A bar count is read in daily bars and the dataset
publishes on filing dates only, so what comes back depends on how widely the security is held
rather than on the count you asked for. AAPL carries filings on 58 of the 66 weekdays of the fourth
quarter of 2020, while a thinly held name carries them on a handful of days a year.

For more information about historical data, see [History Requests](https://www.quantconnect.com/docs/v2/writing-algorithms/historical-data/history-requests).

## Remove Subscriptions

To remove your subscription to SEC Whales data, call the **RemoveSecurity**
method.

```python
self.remove_security(self._dataset_symbol)
```
```csharp
RemoveSecurity(_datasetSymbol);
```

If you subscribe to SEC Whales data for assets in a dynamic universe,
remove the dataset subscription when the asset leaves your universe. To view a common design
pattern, see [Track Security Changes](https://www.quantconnect.com/docs/v2/writing-algorithms/algorithm-framework/alpha/key-concepts#05-Track-Security-Changes).
