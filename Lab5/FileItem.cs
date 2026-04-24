namespace Lab5.Models
{
    public class FileItem
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long? SizeBytes { get; set; }
        public DateTime LastModified { get; set; }
    }
}
