using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GiroServerOps
{
    public class KpiCard : INotifyPropertyChanged
    {
        private string _title = "";
        private string _value = "";
        private string _delta = "";
        private KpiStatus _status;

        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
        public string Delta { get => _delta; set { _delta = value; OnPropertyChanged(); } }
        public KpiStatus Status { get => _status; set { _status = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
