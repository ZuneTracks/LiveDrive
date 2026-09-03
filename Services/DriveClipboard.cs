using System.Collections.Generic;
using System.Linq;
using LiveDrive.Models;

namespace LiveDrive.Services
{
    public enum DriveClipboardOperation
    {
        Copy,
        Move
    }

    public sealed class DriveClipboard
    {
        private readonly List<DriveItem> _items = new List<DriveItem>();

        public IReadOnlyList<DriveItem> Items => _items;
        public DriveClipboardOperation? Operation { get; private set; }
        public string SourceFolderId { get; private set; }
        public bool HasItems => Operation.HasValue && _items.Count > 0;

        public void Store(IEnumerable<DriveItem> items, string sourceFolderId, DriveClipboardOperation operation)
        {
            _items.Clear();
            _items.AddRange(items);
            SourceFolderId = sourceFolderId;
            Operation = operation;
        }

        public bool CanPasteInto(string destinationFolderId)
        {
            return HasItems && SourceFolderId != destinationFolderId &&
                   !_items.Any(item => item.Id == destinationFolderId);
        }

        public void Clear()
        {
            _items.Clear();
            Operation = null;
            SourceFolderId = null;
        }
    }
}
