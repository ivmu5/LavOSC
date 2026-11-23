using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace Laveyana.LavOSC;

internal sealed class DashboardGrid : Grid
{
    private readonly int maximumColumns;
    private int columns;
    private double minimumSingleRowHeight;

    public double MinimumSingleRowHeight
    {
        get => minimumSingleRowHeight;
        set
        {
            if (minimumSingleRowHeight == value)
                return;
            minimumSingleRowHeight = value;
            ApplyRowHeight();
        }
    }

    public DashboardGrid(int maximumColumns)
    {
        this.maximumColumns = maximumColumns;
        ColumnSpacing = 12;
        RowSpacing = 12;
        SizeChanged += (_, _) =>
        {
            int count = GetColumnCount(Width, maximumColumns);
            if (count != columns)
                ArrangeCards(count);
        };
    }

    public void SetCards(IEnumerable<View> cards)
    {
        Children.Clear();
        foreach (View card in cards)
            Children.Add(card);
        ArrangeCards(GetColumnCount(Width, maximumColumns));
    }

    internal static int GetColumnCount(double width, int maximumColumns) =>
        Math.Min(maximumColumns, width >= 1020 ? 3 : width >= 586 ? 2 : 1);

    private void ArrangeCards(int count)
    {
        columns = count;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        for (int column = 0; column < count; column++)
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int first = 0; first < Children.Count; first += count)
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int index = 0; index < Children.Count; index++)
        {
            SetColumn(Children[index], index % count);
            SetRow(Children[index], index / count);
        }
        ApplyRowHeight();
    }

    private void ApplyRowHeight()
    {
        double height = Children.Count <= columns ? minimumSingleRowHeight : 0;
        foreach (View card in Children.OfType<View>())
            card.MinimumHeightRequest = height;
    }
}
