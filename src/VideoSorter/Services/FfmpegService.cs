using System.Diagnostics;
using System.Text.Json;
using VideoSorter.Models;

namespace VideoSorter.Services;

public sealed class FfmpegService
{
    public async Task<VideoFileInfo?> ExtractMetadataAsync(string filePath, string cacheKey, CancellationToken ct = default)
    {
        var args = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"";
        var json = await RunProcessAsync("ffprobe", args, ct);
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var format = root.GetProperty("format");
            var duration = TimeSpan.Zero;
            if (format.TryGetProperty("duration", out var durElem) && double.TryParse(durElem.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var durSec))
                duration = TimeSpan.FromSeconds(durSec);

            var formatName = format.TryGetProperty("format_name", out var fn) ? fn.GetString() ?? "" : "";
            long bitRate = 0;
            if (format.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var brVal))
                bitRate = brVal;

            var tracks = new List<VideoTrackInfo>();
            int width = 0, height = 0;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() ?? "" : "";
                    var type = codecType switch
                    {
                        "video" => "video",
                        "audio" => "audio",
                        "subtitle" => "subtitle",
                        _ => ""
                    };
                    if (string.IsNullOrEmpty(type)) continue;

                    var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";
                    var lang = "";
                    var title = "";
                    if (stream.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("language", out var langElem))
                            lang = langElem.GetString() ?? "";
                        if (tags.TryGetProperty("title", out var titleElem))
                            title = titleElem.GetString() ?? "";
                    }

                    int idx = stream.TryGetProperty("index", out var idxElem) ? idxElem.GetInt32() : 0;
                    int w = 0, h = 0, channels = 0, sampleRate = 0;

                    if (type == "video")
                    {
                        w = stream.TryGetProperty("width", out var wElem) ? wElem.GetInt32() : 0;
                        h = stream.TryGetProperty("height", out var hElem) ? hElem.GetInt32() : 0;
                        if (width == 0) { width = w; height = h; }
                    }
                    else if (type == "audio")
                    {
                        channels = stream.TryGetProperty("channels", out var chElem) ? chElem.GetInt32() : 0;
                        if (stream.TryGetProperty("sample_rate", out var srElem) && int.TryParse(srElem.GetString(), out var srVal))
                            sampleRate = srVal;
                    }

                    tracks.Add(new VideoTrackInfo
                    {
                        Id = idx,
                        Type = type,
                        Codec = codec,
                        Language = lang,
                        Title = title,
                        Width = w,
                        Height = h,
                        Channels = channels,
                        SampleRate = sampleRate
                    });
                }
            }

            var fi = new FileInfo(filePath);
            return new VideoFileInfo
            {
                FilePath = filePath,
                FileName = fi.Name,
                Extension = fi.Extension.TrimStart('.').ToUpperInvariant(),
                FileSize = fi.Length,
                LastModifiedUtc = fi.LastWriteTimeUtc,
                CreationTimeUtc = fi.CreationTimeUtc,
                CacheKey = cacheKey,
                Duration = duration,
                FormatName = formatName,
                Width = width,
                Height = height,
                BitRate = bitRate,
                Tracks = tracks
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Generates all thumbnails in a single scan: both the 4 list thumbnails (at 5%/25%/50%/90%)
    /// and the evenly-spaced spread thumbnails for the info panel.
    /// Returns (listThumbPaths, spreadThumbPaths).
    /// </summary>
    public async Task<(List<string> ListThumbs, List<string> SpreadThumbs)> GenerateAllThumbnailsAsync(
        string filePath, TimeSpan duration, string outputDir,
        int spreadCount, int mode3d = 0, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var listPaths = new List<string>();
        var spreadPaths = new List<string>();

        if (duration.TotalSeconds < 1)
            return (listPaths, spreadPaths);

        // Build video filter: crop for 3D, then scale
        var vf320 = mode3d switch
        {
            1 => "crop=iw/2:ih:0:0,scale=320:-1",  // SBS: left half
            2 => "crop=iw:ih/2:0:0,scale=320:-1",   // TB: top half
            _ => "scale=320:-1"
        };
        var vf240 = mode3d switch
        {
            1 => "crop=iw/2:ih:0:0,scale=240:-1",
            2 => "crop=iw:ih/2:0:0,scale=240:-1",
            _ => "scale=240:-1"
        };

        // 3D prefix for list thumbs
        var listPrefix = mode3d switch
        {
            1 => "thumb_sbs_",
            2 => "thumb_tb_",
            _ => "thumb_"
        };

        var totalSec = duration.TotalSeconds;

        // List thumb positions (fraction of total duration)
        var listPositions = new[]
        {
            Math.Min(5, totalSec * 0.05) / totalSec,
            0.25,
            0.50,
            0.90
        };

        // Spread thumb positions
        var spreadInterval = spreadCount > 0 ? 1.0 / spreadCount : 0;

        // Merge all timestamps into a single sorted list
        // isSpread: false = list thumb, true = spread thumb
        var jobs = new List<(double fraction, string path, string vf, bool isSpread)>();

        for (int i = 0; i < listPositions.Length; i++)
        {
            var outPath = Path.Combine(outputDir, $"{listPrefix}{i}.jpg");
            jobs.Add((listPositions[i], outPath, vf320, false));
        }

        for (int i = 0; i < spreadCount; i++)
        {
            var outPath = Path.Combine(outputDir, $"spread_{spreadCount}_{i}.jpg");
            var frac = spreadInterval * i + spreadInterval / 2;
            jobs.Add((frac, outPath, vf240, true));
        }

        // Sort by position for sequential seeking (faster for ffmpeg)
        jobs.Sort((a, b) => a.fraction.CompareTo(b.fraction));

        foreach (var (frac, outPath, vf, isSpread) in jobs)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(outPath))
            {
                var ts = frac * totalSec;
                var args = $"-ss {ts:F2} -i \"{filePath}\" -vframes 1 -vf {vf} -q:v 2 -y \"{outPath}\"";
                await RunProcessAsync("ffmpeg", args, ct);
            }

            if (File.Exists(outPath))
            {
                if (isSpread)
                    spreadPaths.Add(outPath);
                else
                    listPaths.Add(outPath);
            }
        }

        // Re-sort by index (they were interleaved during generation)
        spreadPaths.Sort(StringComparer.OrdinalIgnoreCase);
        listPaths.Sort(StringComparer.OrdinalIgnoreCase);

        return (listPaths, spreadPaths);
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct, int timeoutMs = 30_000)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        // Read both stdout and stderr concurrently to avoid pipe buffer deadlock.
        // ffmpeg writes progress/status to stderr — if we don't drain it, the 4KB
        // OS pipe buffer fills up and ffmpeg blocks forever.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or caller cancellation — kill the process to release file locks
            try { process.Kill(entireProcessTree: true); } catch { }
            return string.Empty;
        }

        return await stdoutTask;
    }
}
