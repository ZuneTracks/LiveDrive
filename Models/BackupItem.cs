using System.ComponentModel;
using Windows.UI.Xaml;

namespace LiveDrive.Models
{
    public sealed class BackupItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        private string _status;
        private Visibility _queueVisibility = Visibility.Visible;

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

        public Visibility QueueVisibility
        {
            get { return _queueVisibility; }
            set
            {
                if (_queueVisibility != value)
                {
                    _queueVisibility = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueVisibility)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
