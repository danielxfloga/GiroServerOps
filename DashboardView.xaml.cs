using System.Windows.Controls;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GiroServerOps
{
    public partial class DashboardView : UserControl
    {
        public DashboardView()
        {
            InitializeComponent();

            string cs;
            if (!DbUdl.TryEnsureValidUdl(out cs))
                cs = "";

            DataContext = new DashboardViewModel(cs);
        }
    }
    public class StatusToAccentBrushConverter : IValueConverter
    {
        public Brush OkBrush { get; set; } = Brushes.LimeGreen;
        public Brush WarningBrush { get; set; } = Brushes.Orange;
        public Brush CriticalBrush { get; set; } = Brushes.Red;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value is KpiStatus ks ? ks : KpiStatus.Ok;

            return s switch
            {
                KpiStatus.Warning => WarningBrush,
                KpiStatus.Critical => CriticalBrush,
                _ => OkBrush
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class StatusToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value is KpiStatus ks ? ks : KpiStatus.Ok;

            return s switch
            {
                KpiStatus.Warning => "Warning",
                KpiStatus.Critical => "Critical",
                _ => "OK"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
