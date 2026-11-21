using System.Windows;
using System.Windows.Controls;

namespace HomeRms.Controller.Views.Deploy;

/// <summary>Responsive grid helpers shared by the Deploy views (winstall: 4 columns wide, 3 / 2 when narrow).</summary>
public static class DeployLayout
{
    public static int CardColumns(double width) => width >= 1080 ? 4 : width >= 760 ? 3 : 2;
    public static int TileColumns(double width) => width >= 980 ? 5 : width >= 640 ? 3 : 2;
}

/// <summary>Base for grid views: exposes <see cref="Columns"/> / <see cref="TileColumns"/> that follow the width.</summary>
public class DeployGridView : UserControl
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(int), typeof(DeployGridView), new PropertyMetadata(4));
    public static readonly DependencyProperty TileColumnsProperty =
        DependencyProperty.Register(nameof(TileColumns), typeof(int), typeof(DeployGridView), new PropertyMetadata(5));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public int TileColumns { get => (int)GetValue(TileColumnsProperty); set => SetValue(TileColumnsProperty, value); }

    public DeployGridView()
    {
        SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged || e.NewSize.Width <= 0) return;
            var w = e.NewSize.Width - 34; // page margins
            var c = DeployLayout.CardColumns(w);
            if (Columns != c) Columns = c;
            var t = DeployLayout.TileColumns(w);
            if (TileColumns != t) TileColumns = t;
        };
    }
}
