using System.Buffers.Binary;
using System.IO.Compression;
using TuxViewer.Models;

namespace TuxViewer.Services;

/// <summary>
/// Captures the selected window. X11 / XWayland uses XGetImage (exact window pixels);
/// Wayland uses the compositor's own screenshot command where one is available.
/// </summary>
public static class ScreenshotService
{
    public static async Task<ScreenshotResult> CaptureAsync(
        WindowInfo window, ProviderRegistry registry, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return Fail(LocalizationService.L("仅支持 Linux", "Linux only"));

        // X11 / XWayland: precise capture of the selected X11 window.
        Debug($"start source={window.SourceId} native={window.NativeId} x11={window.X11WindowId} linked={window.LinkedX11WindowId} x11available={registry.X11?.IsAvailable}");

        var x11Id = window.X11WindowId ?? window.LinkedX11WindowId;
        if (x11Id is not null && registry.X11?.IsAvailable == true)
        {
            var capture = registry.X11.Inspector.CaptureBgra(x11Id.Value, out var error);
            Debug($"x11 capture id=0x{x11Id:x} ok={capture is not null} error={error ?? "<null>"}");
            if (capture is { } pixels)
            {
                var png = EncodeBgraPng(pixels.Data, pixels.Width, pixels.Height);
                if (png is not null)
                    return new ScreenshotResult
                    {
                        Png = png,
                        Width = pixels.Width,
                        Height = pixels.Height,
                        Source = "X11 XGetImage",
                        Message = LocalizationService.F("已捕获 X11 窗口 0x{0:x}", "Captured X11 window 0x{0:x}", x11Id),
                    };
            }

            if (!string.IsNullOrWhiteSpace(error))
                return Fail(LocalizationService.F("X11 截图失败：{0}", "X11 screenshot failed: {0}", error));
        }

        // niri: first-class IPC screenshot.
        Debug($"niri? source={window.SourceId} native={window.NativeId} exists={CommandRunner.Exists("niri")}");
        if (string.Equals(window.SourceId, "niri", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(window.NativeId) &&
            CommandRunner.Exists("niri"))
        {
            var path = TempPath();
            try
            {
                var result = await CommandRunner.TryRunAsync(
                    "niri",
                    ["msg", "action", "screenshot-window", "--id", window.NativeId, "--path", path],
                    12_000, cancellationToken);
                Debug($"niri result={result?.ExitCode} exists={File.Exists(path)} stderr={result?.StdErr}");
                if (result?.Success == true)
                {
                    // niri acknowledges the action before the compositor writes the file.
                    for (var attempt = 0; attempt < 60 && !File.Exists(path); attempt++)
                        await Task.Delay(50, cancellationToken);
                }
                if (result?.Success == true && File.Exists(path))
                    return FromFile(path, "niri screenshot-window");
                if (!string.IsNullOrWhiteSpace(result?.StdErr))
                    return Fail(LocalizationService.F("niri 截图失败：{0}", "niri screenshot failed: {0}", result.StdErr));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Fail(LocalizationService.F("niri 截图失败：{0}", "niri screenshot failed: {0}", ex.Message)); }
            finally { TryDelete(path); }
        }

        // Sway / Hyprland / generic wlroots: grim with the window geometry.
        Debug($"geometry x={window.X} y={window.Y} w={window.Width} h={window.Height} grim={CommandRunner.Exists("grim")}");
        if (window.X is int x && window.Y is int y && window.Width is > 0 && window.Height is > 0)
        {
            var geometry = $"{x},{y} {window.Width}x{window.Height}";
            if (CommandRunner.Exists("grim"))
            {
                var path = TempPath();
                try
                {
                    var result = await CommandRunner.TryRunAsync(
                        "grim", ["-g", geometry, path], 10_000, cancellationToken);
                    if (result?.Success == true && File.Exists(path))
                        return FromFile(path, $"grim -g \"{geometry}\"");
                }
                catch (OperationCanceledException) { throw; }
                catch { /* try the next backend */ }
                finally { TryDelete(path); }
            }

            if (CommandRunner.Exists("gnome-screenshot"))
            {
                var path = TempPath();
                try
                {
                    var result = await CommandRunner.TryRunAsync(
                        "gnome-screenshot", ["-w", "-f", path], 12_000, cancellationToken);
                    if (result?.Success == true && File.Exists(path))
                        return FromFile(path, "gnome-screenshot -w");
                }
                catch (OperationCanceledException) { throw; }
                catch { /* ignore */ }
                finally { TryDelete(path); }
            }
        }

        return Fail(LocalizationService.L("当前后端无法截图：Wayland 原生窗口需要合成器 screenshot API（niri / grim / gnome-screenshot）", "This backend cannot take screenshots: Wayland-native windows need a compositor screenshot API (niri / grim / gnome-screenshot)"));
    }

    /// <summary>Encode BGRA8888 pixels as PNG without requiring an Avalonia platform.</summary>
    public static byte[]? EncodeBgraPng(byte[] bgra, int width, int height)
    {
        try
        {
            if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;

            using var raw = new MemoryStream();
            var row = new byte[1 + width * 4];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0; // filter: none
                var source = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var src = source + x * 4;
                    var dst = 1 + x * 4;
                    row[dst] = bgra[src + 2];     // R
                    row[dst + 1] = bgra[src + 1]; // G
                    row[dst + 2] = bgra[src];     // B
                    row[dst + 3] = bgra[src + 3]; // A
                }
                raw.Write(row, 0, row.Length);
            }

            raw.Position = 0;
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                raw.CopyTo(zlib);

            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 6;   // RGBA
            ihdr[10] = 0;  // compression
            ihdr[11] = 0;  // filter
            ihdr[12] = 0;  // interlace
            WriteChunk(png, "IHDR"u8, ihdr);
            WriteChunk(png, "IDAT"u8, compressed.ToArray());
            WriteChunk(png, "IEND"u8, []);

            return png.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        var crc = Crc32.Compute(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> type, byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in type) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            foreach (var value in data) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var i = 0u; i < 256; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }

    private static ScreenshotResult FromFile(string path, string source)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var size = ReadPngSize(bytes);
            return new ScreenshotResult
            {
                Png = bytes,
                Width = size.Width,
                Height = size.Height,
                Source = source,
                Message = LocalizationService.F("已通过 {0} 捕获", "Captured via {0}", source),
            };
        }
        catch (Exception ex)
        {
            return Fail(LocalizationService.F("读取截图失败：{0}", "Failed to read the screenshot: {0}", ex.Message));
        }
    }

    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        if (png.Length < 24) return (0, 0);
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }

    private static ScreenshotResult Fail(string message) => new() { Message = message };

    private static void Debug(string message)
    {
        if (Environment.GetEnvironmentVariable("TUXVIEWER_SHOT_DEBUG") == "1")
            Console.Error.WriteLine($"[shot] {message}");
    }

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"tuxviewer-shot-{Guid.NewGuid():N}.png");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignore */ }
    }
}
