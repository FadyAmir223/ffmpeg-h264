using System.Diagnostics;
using System.IO.Compression;
using System.Media;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"h264-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporary);
            using var bundle = Assembly.GetExecutingAssembly().GetManifestResourceStream("bundle.zip")
                ?? throw new InvalidOperationException("Embedded converter bundle is missing.");
            ZipFile.ExtractToDirectory(bundle, temporary);

            var script = Path.Combine(temporary, "bin", "h264.ps1");
            if (args.Length == 2)
                return RunConverter(script, args[0], args[1]).GetAwaiter().GetResult();

            ApplicationConfiguration.Initialize();
            Application.Run(new ConverterForm(script));
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length != 2)
                MessageBox.Show(error.Message, "H.264 Converter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task<int> RunConverter(
        string script, string input, string output, Action<ConversionUpdate>? report = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var powershell = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var process = new ProcessStartInfo(powershell)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.ArgumentList.Add("-NoProfile");
        process.ArgumentList.Add("-ExecutionPolicy");
        process.ArgumentList.Add("Bypass");
        process.ArgumentList.Add("-File");
        process.ArgumentList.Add(script);
        process.ArgumentList.Add("-InputPath");
        process.ArgumentList.Add(input);
        process.ArgumentList.Add("-OutputPath");
        process.ArgumentList.Add(output);

        using var child = Process.Start(process)
            ?? throw new InvalidOperationException("Could not start the converter.");
        var temporaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("H264GUI:")) continue;
                var update = JsonSerializer.Deserialize<ConversionUpdate>(line[8..]);
                if (update is null) continue;
                if (!string.IsNullOrEmpty(update.Temporary)) temporaryFiles.Add(update.Temporary);
                report?.Invoke(update);
            }
            await child.WaitForExitAsync();
            await errors;
            cancellationToken.ThrowIfCancellationRequested();
            return child.ExitCode;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
                foreach (var path in temporaryFiles)
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed record ConversionUpdate(
    string Event, int Index, int Total, string Path, int Percent, string? Temporary = null);

internal sealed class ConverterForm : Form
{
    private readonly string _script;
    private readonly TextBox _from = FolderDropBox("Drop source folder here, or paste a path");
    private readonly TextBox _to = FolderDropBox("Drop destination folder here, or paste a path");
    private readonly Button _submit = new() { Text = "Convert", AutoSize = true };
    private readonly Button _sound = new() { Text = "🔊", Width = 36, Height = 29 };
    private readonly ToolTip _soundTip = new();
    private readonly Label _status = new() { Text = "Choose the source and destination folders.", AutoSize = true };
    private readonly Label _currentFile = new() { AutoEllipsis = true, Dock = DockStyle.Fill, Height = 24 };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private bool _running;
    private bool _closeWhenStopped;
    private bool _muted;
    private CancellationTokenSource? _cancellation;

    internal ConverterForm(string script)
    {
        _script = script;
        Text = "H.264 Old Receiver Converter";
        ClientSize = new Size(620, 225);
        MinimumSize = new Size(520, 265);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AddFolderRow(layout, 0, "From:", _from);
        AddFolderRow(layout, 1, "To:", _to);
        _submit.Anchor = AnchorStyles.Left;
        _sound.Anchor = AnchorStyles.Right;
        layout.Controls.Add(_submit, 1, 2);
        layout.Controls.Add(_sound, 2, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.SetColumnSpan(_status, 3);
        layout.Controls.Add(_currentFile, 0, 4);
        layout.SetColumnSpan(_currentFile, 3);
        layout.Controls.Add(_progress, 0, 5);
        layout.SetColumnSpan(_progress, 3);
        Controls.Add(layout);

        AcceptButton = _submit;
        _submit.Click += Convert;
        _muted = ReadMuted();
        UpdateSoundButton();
        _sound.Click += (_, _) =>
        {
            _muted = !_muted;
            SaveMuted();
            UpdateSoundButton();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (!_running) return;
            eventArgs.Cancel = true;
            if (_closeWhenStopped) return;
            if (MessageBox.Show("Cancel the conversion and close? The unfinished output will be removed.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _closeWhenStopped = true;
            _status.Text = "Cancelling...";
            _cancellation?.Cancel();
        };
    }

    private void AddFolderRow(TableLayoutPanel layout, int row, string label, TextBox textBox)
    {
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = label == "From:" ? "Choose the source folder" : "Choose the destination folder",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(textBox.Text) ? textBox.Text : string.Empty
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                textBox.Text = dialog.SelectedPath;
        };
        textBox.DragEnter += (_, eventArgs) =>
        {
            if (eventArgs.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
                Directory.Exists(paths[0]))
                eventArgs.Effect = DragDropEffects.Copy;
        };
        textBox.DragDrop += (_, eventArgs) =>
        {
            if (eventArgs.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
                textBox.Text = paths[0];
        };

        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(textBox, 1, row);
        layout.Controls.Add(browse, 2, row);
    }

    private static TextBox FolderDropBox(string placeholder) => new()
    {
        Dock = DockStyle.Fill,
        AllowDrop = true,
        Multiline = true,
        Height = 52,
        PlaceholderText = placeholder
    };

    private static string MuteSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "H264OldReceiverConverter", "muted");

    private static bool ReadMuted()
    {
        try { return File.ReadAllText(MuteSettingsPath) == "1"; }
        catch { return false; }
    }

    private void SaveMuted()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MuteSettingsPath)!);
            File.WriteAllText(MuteSettingsPath, _muted ? "1" : "0");
        }
        catch { } // Settings must not prevent conversion.
    }

    private void UpdateSoundButton()
    {
        var action = _muted ? "Unmute completion sound" : "Mute completion sound";
        _sound.Text = _muted ? "🔇" : "🔊";
        _sound.AccessibleName = action;
        _soundTip.SetToolTip(_sound, action);
    }

    private async void Convert(object? sender, EventArgs eventArgs)
    {
        var input = _from.Text.Trim();
        var output = _to.Text.Trim();
        if (!Directory.Exists(input) || !Directory.Exists(output))
        {
            MessageBox.Show("Choose two existing folders.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.Equals(Path.GetFullPath(input).TrimEnd('\\'), Path.GetFullPath(output).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("The From and To folders must be different.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _running = true;
        _cancellation = new CancellationTokenSource();
        _submit.Enabled = _from.Enabled = _to.Enabled = false;
        _status.Text = "Finding videos...";
        _currentFile.Text = string.Empty;
        _progress.Value = 0;
        try
        {
            var exitCode = await Program.RunConverter(
                _script, input, output, ShowProgress, _cancellation.Token);
            _status.Text = exitCode == 0 ? "Conversion finished." : "Conversion finished with errors.";
            if (!_muted)
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("complete.wav");
                    using var player = new SoundPlayer(stream);
                    player.PlaySync();
                }
                catch { } // A missing audio device must not turn a successful conversion into a failure.
            }
            MessageBox.Show(_status.Text, Text, MessageBoxButtons.OK,
                MessageBoxIcon.None);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Conversion cancelled. Unfinished output was removed.";
            _currentFile.Text = string.Empty;
            _progress.Value = 0;
        }
        catch (Exception error)
        {
            _status.Text = "Conversion failed.";
            MessageBox.Show(error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _running = false;
            _submit.Enabled = _from.Enabled = _to.Enabled = true;
            if (_closeWhenStopped) Close();
        }
    }

    private void ShowProgress(ConversionUpdate update)
    {
        if (update.Event == "Temporary") return;
        if (update.Event == "Total")
        {
            _status.Text = update.Total == 0 ? "No videos found." : $"{update.Total} videos found.";
            return;
        }

        _status.Text = $"Video {update.Index} of {update.Total} — {update.Percent}%";
        _currentFile.Text = Path.GetFileName(update.Path) + (update.Event switch
        {
            "Done" => " — Done",
            "Skipped" => " — Skipped",
            "Failed" => " — Failed",
            _ => string.Empty
        });
        _progress.Value = Math.Clamp(update.Percent, 0, 100);
    }
}
