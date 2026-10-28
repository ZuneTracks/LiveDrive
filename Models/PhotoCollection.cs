namespace LiveDrive.Models
{
    public sealed class PhotoCollection
    {
        public string Title { get; set; }
        public string MonthKey { get; set; }
        public bool IsOnThisDay { get; set; }
        public int Count { get; set; }
        public string ItemType { get; set; } = "photo";

        public string CountLabel => Count == 1
            ? "1 " + ItemType
            : Count + " " + ItemType + "s";
    }
}
