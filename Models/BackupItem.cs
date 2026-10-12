using System.ComponentModel;
using Windows.UI.Xaml;

namespace LiveDrive.Models
{
    public sealed class BackupItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        private string _status;
        private double _progress;
        private Visibility _progressVisibility = Visibility.Collapsed;
        private string _progressLabel;

        public string Status
        {
            get { return _status; }
            set
            {
                if (_status != value)
                {
                    _status = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                }
            }
        }

        public double Progress
        {
            get { return _progress; }
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
                }
            }
        }

        public Visibility ProgressVisibility
        {
            get { return _progressVisibility; }
            set
            {
                if (_progressVisibility != value)
                {
                    _progressVisibility = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressVisibility)));
                }
            }
        }

        public string ProgressLabel
        {
            get { return _progressLabel; }
            set
            {
                if (_progressLabel != value)
                {
                    _progressLabel = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressLabel)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
