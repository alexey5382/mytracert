using Lab5.Models;
using System.IO;

public class StorageService
{
    private readonly string _rootPath;

    public StorageService(IConfiguration config)
    {
        _rootPath = config["Storage:RootPath"]
                    ?? throw new Exception("В appsettings.json не указан Storage:RootPath");

        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }
    }

    public string GetSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return _rootPath;

        relativePath = relativePath.Replace("/", "\\");

        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));

        if (!fullPath.StartsWith(Path.GetFullPath(_rootPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Попытка выхода за пределы хранилища.");
        }

        return fullPath;
    }
    public async Task<bool> SaveFileAsync(string relativePath, Stream contentStream)
    {
        string fullPath = GetSafePath(relativePath);
        bool isNewFile = !File.Exists(fullPath);

        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write);

        await contentStream.CopyToAsync(fileStream);

        return isNewFile;
    }
    public List<FileItem> GetDirectoryContent(string relativePath)
    {
        string fullPath = GetSafePath(relativePath);
        var result = new List<FileItem>();

        var dirInfo = new DirectoryInfo(fullPath);

        foreach (var dir in dirInfo.GetDirectories())
        {
            result.Add(new FileItem
            {
                Name = dir.Name,
                IsDirectory = true,
                LastModified = dir.LastWriteTime
            });
        }

        foreach (var file in dirInfo.GetFiles())
        {
            result.Add(new FileItem
            {
                Name = file.Name,
                IsDirectory = false,
                SizeBytes = file.Length,
                LastModified = file.LastWriteTime
            });
        }

        return result;
    }
}