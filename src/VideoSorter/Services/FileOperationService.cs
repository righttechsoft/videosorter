using VideoSorter.Helpers;

namespace VideoSorter.Services;

public sealed class FileOperationService
{
    public IReadOnlyList<string> EnumerateVideoFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return [];
        return Directory.EnumerateFiles(folderPath)
            .Where(VideoFileExtensions.IsVideoFile)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> EnumerateSubfolders(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return [];
        try
        {
            return Directory.EnumerateDirectories(folderPath)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool RenameFile(string oldPath, string newFileName)
    {
        var dir = Path.GetDirectoryName(oldPath);
        if (dir is null) return false;
        var newPath = Path.Combine(dir, newFileName);
        if (File.Exists(newPath)) return false;
        File.Move(oldPath, newPath);
        return true;
    }

    public void CopyFile(string sourcePath, string destinationFolder)
    {
        var fileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(destinationFolder, fileName);
        if (File.Exists(destPath))
            destPath = GetUniqueFileName(destPath);
        File.Copy(sourcePath, destPath);
    }

    public void DeleteFile(string filePath)
    {
        File.Delete(filePath);
    }

    public void MoveFile(string sourcePath, string destinationFolder)
    {
        var fileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(destinationFolder, fileName);
        if (File.Exists(destPath))
            destPath = GetUniqueFileName(destPath);
        File.Move(sourcePath, destPath);
    }

    private static string GetUniqueFileName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(newPath));
        return newPath;
    }
}
