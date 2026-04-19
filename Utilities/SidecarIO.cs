using System;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace dubiousQOL.Utilities;

/// <summary>
/// Shared sidecar file I/O: profile-scoped path resolution, JSON read/write, and file management.
/// Feature-specific IO classes become thin wrappers around these methods.
/// </summary>
internal static class SidecarIO
{
    /// <summary>
    /// Resolves an absolute filesystem path scoped to the current profile's saves directory:
    /// {globalizedProfileSavesDir}/{featureSubdir}/{fileName}
    /// Returns null on failure (logs a warning).
    /// </summary>
    public static string? ResolvePath(string featureSubdir, string fileName)
    {
        try
        {
            int profileId = SaveManager.Instance.CurrentProfileId;
            string userPath = UserDataPathProvider.GetProfileScopedPath(profileId, UserDataPathProvider.SavesDir);
            string abs = ProjectSettings.GlobalizePath(userPath);
            return Path.Combine(abs, featureSubdir, fileName);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"SidecarIO path resolve ({featureSubdir}/{fileName}): {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a JSON sidecar file. Creates the parent directory if needed.
    /// </summary>
    public static void WriteJson<T>(string path, T data, JsonSerializerOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, options));
    }

    /// <summary>
    /// Reads and deserializes a JSON sidecar file. Returns null if the file doesn't exist
    /// or deserialization fails (logs a warning on failure).
    /// </summary>
    public static T? ReadJson<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"SidecarIO read ({path}): {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Deletes a sidecar file if it exists. Swallows exceptions with a warning.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"SidecarIO delete ({path}): {e.Message}");
        }
    }
}
