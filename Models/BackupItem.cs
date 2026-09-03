using System.ComponentModel;

namespace LiveDrive.Models
{
    public sealed class BackupItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        private string _status;

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

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
