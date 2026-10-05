## Requesting Data

To add SEC Whales data to your algorithm, call the **AddData** method. Save a reference to the dataset **Symbol** so you can access the data later in your algorithm.

```python
class SEC13FDataAlgorithm(QCAlgorithm):

    def initialize(self) -> None:
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
        SetStartDate(2020, 10, 1);
        SetEndDate(2020, 12, 31);
        SetCash(100000);

        _symbol = AddEquity("AAPL", Resolution.Daily).Symbol;
        _datasetSymbol = AddData<SEC13FHoldings>(_symbol).Symbol;
    }
}
```

## Accessing Data

To get the current SEC Whales data, index the current [**Slice**](https://www.quantconnect.com/docs/v2/writing-algorithms/key-concepts/time-modeling/timeslices) with the dataset **Symbol**. **Slice** objects deliver unique events to your algorithm as they happen, but the **Slice** may not contain data for your dataset at every time step. To avoid issues, check if the **Slice** contains the data you want before you index it.

```python
def on_data(self, slice: Slice) -> None:
    if slice.contains_key(self._dataset_symbol):
        for holding in slice[self._dataset_symbol]:
            self.log(f"{self._dataset_symbol} CIK {holding.manager_cik} at {slice.time}: {holding.amount} {holding.amount_type}")
```

```csharp
public override void OnData(Slice slice)
{
    if (slice.ContainsKey(_datasetSymbol))
    {
        foreach (SEC13FHolding holding in slice[_datasetSymbol])
        {
            Log($"{_datasetSymbol} CIK {holding.ManagerCik} at {slice.Time}: {holding.Amount} {holding.AmountType}");
        }
    }
}
```

The **FilingDate** property is the day the manager filed the report, and the data point arrives at midnight after EDGAR lists the filing. The **PeriodEnd** property is the quarter the report describes.

To iterate through all of the dataset objects in the current **Slice**, call the **Get** method.

```python
def on_data(self, slice: Slice) -> None:
    for dataset_symbol, holdings in slice.get(SEC13FHoldings).items():
        for holding in holdings:
            self.log(f"{dataset_symbol} CIK {holding.manager_cik} at {slice.time}: {holding.amount} {holding.amount_type}")
```

```csharp
public override void OnData(Slice slice)
{
    foreach (var kvp in slice.Get<SEC13FHoldings>())
    {
        var datasetSymbol = kvp.Key;
        foreach (SEC13FHolding holding in kvp.Value)
        {
            Log($"{datasetSymbol} CIK {holding.ManagerCik} at {slice.Time}: {holding.Amount} {holding.AmountType}");
        }
    }
}
```

## Historical Data

To get historical SEC Whales data, call the **History** method with the dataset **Symbol**. If there is no data in the period you request, the history result is empty.

```python
# DataFrame
history_df = self.history(SEC13FHoldings, self._dataset_symbol, timedelta(days=60), Resolution.DAILY, flatten=True)

# Dataset objects
history_bars = self.history[SEC13FHoldings](self._dataset_symbol, timedelta(days=60), Resolution.DAILY)
```

```csharp
var history = History<SEC13FHoldings>(_datasetSymbol, TimeSpan.FromDays(60), Resolution.Daily);
```

For more information about historical data, see [History Requests](https://www.quantconnect.com/docs/v2/writing-algorithms/historical-data/history-requests).

## Remove Subscriptions

To remove your subscription to SEC Whales data, call the **RemoveSecurity** method.

```python
self.remove_security(self._dataset_symbol)
```

```csharp
RemoveSecurity(_datasetSymbol);
```

If you subscribe to SEC Whales data for assets in a dynamic universe, remove the dataset subscription when the asset leaves your universe. To view a common design pattern, see [Track Security Changes](https://www.quantconnect.com/docs/v2/writing-algorithms/algorithm-framework/alpha/key-concepts#05-Track-Security-Changes).
