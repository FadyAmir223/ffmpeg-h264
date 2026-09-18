using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

var temporary = Path.Combine(Path.GetTempPath(), $"h264-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(temporary);
    using var bundle = Assembly.GetExecutingAssembly().GetManifestResourceStream("bundle.zip")
        ?? throw new InvalidOperationException("Embedded converter bundle is missing.");
    ZipFile.ExtractToDirectory(bundle, temporary);

    var process = new ProcessStartInfo("cmd.exe")
    {
        WorkingDirectory = Path.Combine(temporary, "bin"),
        UseShellExecute = false
    };
    process.ArgumentList.Add("/c");
    process.ArgumentList.Add("h264.bat");
    using var child = Process.Start(process) ?? throw new InvalidOperationException("Could not start h264.bat.");
    child.WaitForExit();
    return child.ExitCode;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally
{
    try { Directory.Delete(temporary, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}
